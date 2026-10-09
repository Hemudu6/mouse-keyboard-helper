using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Input;
using Core.Input;
using Core.Keys;

namespace Host.Input;

/// <summary>
/// <see cref="IInputSink"/> 的 Windows 实现——本工程中 **<c>SendInput</c> 的唯一调用点**
/// （设计文档 §3.1：<c>SendInput</c>(user32.dll) 是唯一的输入模拟出口）。
///
/// <para>
/// 之所以单独成一个薄类：注入语义（引用计数、组合键顺序、日志聚合）全部在
/// <see cref="Core.Input.InputSimulator"/> 里做成纯逻辑，可被单测穷举；
/// 剩下这层"把结构体塞进 user32"没有任何可测逻辑，只有平台调用。
/// 宿主不参与单测工程引用（<c>docs/README.md:47</c>：
/// SendInput 类验收不进单测），故此文件不进测试。
/// </para>
/// <para>
/// <b>失败语义</b>：<c>SendInput</c> 失败时返回已成功插入的事件数（可能少于请求数、
/// 可能为 0），并且**不抛异常**（<c>GetLastError</c> 才是错误来源）。
/// 这里**如实返回**注入成功的事件数，交给调用方判定——
/// 适配器不吞错、不转换、不抛异常（设计文档 §3.1.2 的裁决前提）。
/// </para>
/// <para>
/// <b>不做反检测 / 不尝试绕过 UIPI</b>（设计文档 §3.1.2 明确不做的部分）。
/// </para>
/// </summary>
internal sealed class SendInputSink : IInputSink
{
    /// <summary>
    /// 最近一次 <c>SendInput</c> 的 Win32 错误码（<see cref="IInputSink.LastInjectionError"/>）。
    /// <c>0</c> = 无失败或无可用原因。
    /// </summary>
    private int _lastError;

    /// <inheritdoc/>
    public int LastInjectionError => Volatile.Read(ref _lastError);

    /// <summary>虚拟键码：<see cref="Key"/> 的枚举值即 VK 码，此处只做显式转换以表明契约。</summary>
    private static ushort VirtualKeyOf(Key key) => (ushort)KeyInterop.VirtualKeyFromKey(key);

    /// <summary>
    /// 调用 <c>SendInput</c> 并**同一次**取回 Win32 错误码。
    ///
    /// <para>
    /// 必须收在这里：失败原因只对**紧接调用之后**的 <c>GetLastError</c> 有效，
    /// 中途插入任何别的 Win32 调用（如 <c>GetSystemMetrics</c>）都可能把它冲掉。
    /// </para>
    /// </summary>
    private int Inject(INPUT input)
    {
        var injected = SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());

        // 只在“没全发出去”时读错误：成功后 GetLastError 的值无意义（可能是上次残留的），
        // 记下来只会把陈旧的错误码当成新失败的“失败类型”。
        Volatile.Write(ref _lastError, injected == 1 ? 0 : Marshal.GetLastWin32Error());

        return injected;
    }

    /// <inheritdoc/>
    public int KeyDown(Key virtualKey) => SendKeyboard(VirtualKeyOf(virtualKey), keyUp: false);

    /// <inheritdoc/>
    public int KeyUp(Key virtualKey) => SendKeyboard(VirtualKeyOf(virtualKey), keyUp: true);

    /// <inheritdoc/>
    public int MouseDown(MouseKey button) => SendMouseButton(button, up: false);

    /// <inheritdoc/>
    public int MouseUp(MouseKey button) => SendMouseButton(button, up: true);

    /// <inheritdoc/>
    public int MoveAbsolute(int x, int y)
    {
        // 绝对坐标注入：MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_MOVE，
        // 坐标必须由"像素"归一化到 0..65535 的虚拟桌面坐标系后再写入 dx/dy——
        // 直接把像素写进去会让指针落在完全无关的位置（多屏下尤其明显）。
        var virtualLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var virtualTop = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var virtualWidth = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var virtualHeight = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        if (virtualWidth <= 0 || virtualHeight <= 0)
        {
            // 未调用 SendInput：不是“注入被拒”，不该留下错误码。
            Volatile.Write(ref _lastError, 0);
            return 0;
        }

        // 归一化到 0..65535（含，且不落到 65535 这一越界端点上）。
        var normalizedX = (int)Math.Round((double)(x - virtualLeft) * 65535 / virtualWidth);
        var normalizedY = (int)Math.Round((double)(y - virtualTop) * 65535 / virtualHeight);

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = normalizedX,
                    dy = normalizedY,
                    mouseData = 0,
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                    time = 0,
                    dwExtraInfo = InjectionMarker,
                },
            },
        };

        return Inject(input);
    }

    /// <inheritdoc/>
    public int Scroll(bool vertical, int notches)
    {
        // WHEEL_DELTA = 120：一格 = 120。正数 = 上/右，负数 = 下/左（与约定一致）。
        // 用 long 中转再钳制：notches 来自公开 API，可为任意 int，直接 int * 120 会静默溢出。
        var data = (int)Math.Clamp((long)notches * WHEEL_DELTA, int.MinValue, int.MaxValue);
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = 0,
                    dy = 0,
                    mouseData = data,
                    dwFlags = vertical ? MOUSEEVENTF_WHEEL : MOUSEEVENTF_HWHEEL,
                    time = 0,
                    dwExtraInfo = InjectionMarker,
                },
            },
        };

        return Inject(input);
    }

    private int SendKeyboard(ushort virtualKey, bool keyUp)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = virtualKey,
                    wScan = 0,
                    // 扩展键（如右 Alt、方向键）必须带 KEYEVENTF_EXTENDEDKEY，
                    // 否则目标程序收到的键码会与用户预期不符。
                    dwFlags = (keyUp ? KEYEVENTF_KEYUP : 0u)
                              | (IsExtendedKey(virtualKey) ? KEYEVENTF_EXTENDEDKEY : 0u),
                    time = 0,
                    dwExtraInfo = InjectionMarker,
                },
            },
        };

        return Inject(input);
    }

    private int SendMouseButton(MouseKey button, bool up)
    {
        uint flag;
        uint data = 0;

        switch (button)
        {
            case MouseKey.Left:
                flag = up ? MOUSEEVENTF_LEFTUP : MOUSEEVENTF_LEFTDOWN;
                break;
            case MouseKey.Right:
                flag = up ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_RIGHTDOWN;
                break;
            case MouseKey.Middle:
                flag = up ? MOUSEEVENTF_MIDDLEUP : MOUSEEVENTF_MIDDLEDOWN;
                break;
            case MouseKey.X1:
            case MouseKey.X2:
                // 侧键共用 XDOWN/XUP，靠 mouseData 区分是哪一个。
                flag = up ? MOUSEEVENTF_XUP : MOUSEEVENTF_XDOWN;
                data = button == MouseKey.X1 ? XBUTTON1 : XBUTTON2;
                break;
            default:
                // 未知鼠标键：不发事件，也不留错误码（同上，不是“注入被拒”）。
                Volatile.Write(ref _lastError, 0);
                return 0;
        }

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = 0,
                    dy = 0,
                    mouseData = (int)data,
                    dwFlags = flag,
                    time = 0,
                    dwExtraInfo = InjectionMarker,
                },
            },
        };

        return Inject(input);
    }

    private static bool IsExtendedKey(ushort virtualKey)
        => virtualKey is VK_RMENU or VK_RCONTROL or VK_INSERT or VK_DELETE or VK_HOME or VK_END
            or VK_PRIOR or VK_NEXT or VK_LEFT or VK_RIGHT or VK_UP or VK_DOWN
            or VK_NUMLOCK or VK_DIVIDE or VK_LWIN or VK_RWIN or VK_APPS or VK_SNAPSHOT;

    // ---- P/Invoke ----

    /// <summary>
    /// 标记本进程注入的事件（写入 <c>dwExtraInfo</c>）。
    /// 设计文档 §3.3 的自注入过滤在钩子侧按 <c>LLKHF_INJECTED</c> 标志识别，
    /// 本标记不承担过滤职责，只作为诊断线索保留。
    /// </summary>
    private static readonly UIntPtr InjectionMarker = new(0x574B4D41u /* "WKMA" */);

    private const int INPUT_MOUSE = 0;
    private const int INPUT_KEYBOARD = 1;

    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_XDOWN = 0x0080;
    private const uint MOUSEEVENTF_XUP = 0x0100;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_HWHEEL = 0x1000;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    private const int WHEEL_DELTA = 120;
    private const uint XBUTTON1 = 0x0001;
    private const uint XBUTTON2 = 0x0002;

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private const ushort VK_RMENU = 0xA5;
    private const ushort VK_RCONTROL = 0xA3;
    private const ushort VK_INSERT = 0x2D;
    private const ushort VK_DELETE = 0x2E;
    private const ushort VK_HOME = 0x24;
    private const ushort VK_END = 0x23;
    private const ushort VK_PRIOR = 0x21;
    private const ushort VK_NEXT = 0x22;
    private const ushort VK_LEFT = 0x25;
    private const ushort VK_RIGHT = 0x27;
    private const ushort VK_UP = 0x26;
    private const ushort VK_DOWN = 0x28;
    private const ushort VK_NUMLOCK = 0x90;
    private const ushort VK_DIVIDE = 0x6F;
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_RWIN = 0x5C;
    private const ushort VK_APPS = 0x5D;
    private const ushort VK_SNAPSHOT = 0x2C;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SendInput(int count, INPUT[] inputs, int sizeOfInput);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;

        [FieldOffset(0)]
        public KEYBDINPUT ki;

        [FieldOffset(0)]
        public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public int mouseData;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }
}
