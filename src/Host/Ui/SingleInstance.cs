using System.Runtime.InteropServices;
using System.Threading;

namespace Host.Ui;

/// <summary>
/// 单实例约束 + 第二实例的焦点移交（设计文档 §6.4，NFR-09、AC-30）。
///
/// <para>
/// <b>两件事，缺一不可</b>：
/// </para>
/// <list type="number">
/// <item><b>命名互斥体</b>回答"是不是已有实例"——这是 AC-30 的硬判据
///       （不产生第二个实例 + 给出提示）。</item>
/// <item><b>焦点移交</b>回答"已有实例在哪儿"——互斥体答不了这个（两个进程之间
///       没有任何共享窗口句柄），故用 <c>FindWindow</c> + <c>PostMessage</c>。
///       这是**尽力而为的增强**，不是 AC 判据（§6.4 边界 2）。</item>
/// </list>
///
/// <para>
/// <b>稳定标识的选择（§6.4 边界 1 的实现）</b>：设计稿硬要求"必须有一个不随 UI 文案
/// 漂移的稳定标识"，形态由实现期定。这里选 <b>自己的窗口类名</b>——首实例专门注册一个
/// 消息专用窗口（message-only window），类名是本进程写死的常量。
/// </para>
/// <para>
/// 之所以**不**用主窗口的标题：标题会随产品文案变（今天是"鼠标键盘辅助"，
/// 明天可能带版本号），而 <c>FindWindow</c> 的标题匹配是精确匹配，
/// 文案一改焦点移交就静默失效。也**不**用 WinUI 主窗口的类名：那是框架生成的，
/// 不同 Windows App SDK 版本下可能不同。自己注册的类名两个问题都没有。
/// </para>
/// <para>
/// 消息专用窗口（父窗口 <c>HWND_MESSAGE</c>）不出现在任务栏、不被用户看见，
/// 只承担"接住第二实例的消息"这一件事，代价极小。
/// </para>
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    /// <summary>
    /// 互斥体名。用 <c>Global\</c> 前缀：多个用户会话下也只有一个实例能跑
    /// （本工具装全局键盘钩子、注入全局输入，"每个会话一个"没有意义且互相干扰）。
    /// </summary>
    private const string MutexName = @"Global\鼠标键盘辅助.SingleInstance.9F2A7C41";

    /// <summary>
    /// 消息专用窗口的**稳定类名**（§6.4 边界 1 要求的"不随文案漂移的标识"）。
    /// 类名注册后即由系统持有，第二实例按此名 <c>FindWindow</c> 即可定位。
    /// </summary>
    private const string WindowClassName = "MouseKeyboardAssistant.SingleInstanceWindow";

    /// <summary>消息专用窗口的标题（无语义，仅为满足 <c>CreateWindowEx</c> 的参数要求）。</summary>
    private const string WindowTitle = WindowClassName;

    /// <summary>
    /// <c>FindWindow</c> 的 <c>lpClassName</c> 实参。
    /// <b>必须带 <c>Atom</c> 而非裸字符串</b>：类名由 <see cref="RegisterClassEx"/> 注册后
    /// 系统返回的是局部原子，<c>FindWindow</c> 按类名查找时用的就是这个原子；
    /// 用 <c>MAKEINTATOM</c> 包装是 Win32 的既定用法（见 MSDN <c>FindWindow</c> 备注）。
    /// </summary>
    private static readonly IntPtr ClassAtom = new(0xC000 | 1);

    /// <summary>本实例持有的互斥体（<c>null</c> = 本进程是第二实例）。</summary>
    private Mutex? _mutex;

    /// <summary>消息专用窗口句柄（首实例才有）。</summary>
    private IntPtr _hwnd;

    /// <summary>注册的窗口类名原子（<see cref="Dispose"/> 时 <c>UnregisterClass</c> 用）。</summary>
    private ushort _classAtom;

    /// <summary>窗口过程的原生委托引用（防止 GC 回收，理由同 <see cref="TrayIcon"/>）。</summary>
    private WndProcDelegate? _wndProcKeepAlive;

    /// <summary>
    /// 本进程是**首实例**时为 <c>true</c>；<c>false</c> 表示已有实例在跑，
    /// 调用方应提示"程序已在运行"并立即退出（AC-30）。
    /// </summary>
    internal bool IsFirstInstance { get; private set; }

    /// <summary>
    /// 首实例收到"第二实例要来抢焦点"时触发（在 UI 线程外的消息线程上）。
    /// 订阅方须把"显示主窗口"这一动作封送到界面线程。
    /// </summary>
    internal event Action? ActivationRequested;

    /// <summary>
    /// 尝试成为首实例。
    ///
    /// <para>
    /// <b>必须在主窗口创建之前调用</b>（§6.5.5）：第二实例判定成功后要立刻退出，
    /// 若先建了主窗口再判定，第二实例会闪现一个窗口才消失；更糟的是两个实例
    /// 都装上了全局钩子，在退出的瞬间争抢输入。
    /// </para>
    /// </summary>
    internal void TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: false, MutexName, out var createdNew);
        IsFirstInstance = createdNew;

        if (!IsFirstInstance)
        {
            // 第二实例：互斥体已经存在。通知首实例（尽力而为），然后由调用方退出。
            NotifyExistingInstance();
            return;
        }

        CreateMessageWindow();
    }

    /// <summary>
    /// 通知已有实例把窗口拉到前台（§6.4）。
    ///
    /// <para>
    /// <b>找不到就到此为止，不重试、不轮询</b>（§6.4 边界 2）：首实例可能还在初始化
    /// （互斥体已建、消息窗口尚未创建），此时 <c>FindWindow</c> 返回 <c>NULL</c>。
    /// AC-30 只要求"不产生第二个实例 + 给出提示"，焦点移交是增强项，
    /// 为它加超时重试会把"启动第二实例"拖成一次带等待的操作——收益不抵代价。
    /// </para>
    /// </summary>
    private static void NotifyExistingInstance()
    {
        var hwnd = FindWindow(ClassAtom, WindowTitle);
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        // PostMessage 是异步入队：发完即返回，不等对方处理——
        // 第二实例因此可以立刻退出，不必等首实例把窗口拉起来。
        PostMessage(hwnd, AppMessages.SecondInstanceActivate, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>注册窗口类并创建消息专用窗口（首实例专用）。</summary>
    private void CreateMessageWindow()
    {
        _wndProcKeepAlive = WindowProc;

        var wndClass = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive),
            hInstance = GetModuleHandle(null),
            lpszClassName = WindowClassName,
        };

        _classAtom = RegisterClassEx(ref wndClass);
        if (_classAtom == 0)
        {
            // 注册失败（极罕见）：放弃焦点移交通道，但**不影响单实例约束**——
            // 互斥体已经建好，AC-30 的硬判据仍然成立。
            return;
        }

        // 父窗口传 HWND_MESSAGE(-3)：消息专用窗口，不进任务栏、不参与 Z 序。
        _hwnd = CreateWindowEx(
            0,
            WindowClassName,
            WindowTitle,
            0,
            0, 0, 0, 0,
            new IntPtr(-3),
            IntPtr.Zero,
            GetModuleHandle(null),
            IntPtr.Zero);
    }

    /// <summary>
    /// 消息专用窗口的窗口过程：只认"第二实例唤起"，其余走系统默认处理。
    /// </summary>
    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == AppMessages.SecondInstanceActivate)
        {
            ActivationRequested?.Invoke();
            return IntPtr.Zero;
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        if (_classAtom != 0)
        {
            UnregisterClass(WindowClassName, GetModuleHandle(null));
            _classAtom = 0;
        }

        if (_mutex is not null)
        {
            _mutex.Dispose();
            _mutex = null;
        }
    }

    // ── P/Invoke ──────────────────────────────────────────────────

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        internal int cbSize;
        internal int style;
        internal IntPtr lpfnWndProc;
        internal int cbClsExtra;
        internal int cbWndExtra;
        internal IntPtr hInstance;
        internal IntPtr hIcon;
        internal IntPtr hCursor;
        internal IntPtr hbrBackground;
        internal string? lpszMenuName;
        internal string lpszClassName;
        internal IntPtr hIconSm;
    }

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW", SetLastError = true)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(IntPtr lpClassName, string? lpWindowName);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
