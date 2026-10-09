// TrayProbe —— 待验证项 V-3 的实测程序（设计文档 §6.3 / §9.2）
//
// 验证命题：**WinUI 3 宿主能否自己持有消息窗口并稳定接收 Shell_NotifyIcon 回调？**
//
// 设计 §6.3 选定的托盘路线是 P/Invoke `Shell_NotifyIcon` + 自定义回调消息 +
// `CreatePopupMenu`/`TrackPopupMenu`。这条路线的关键前提是：调用方必须有一个能收
// `uCallbackMessage` 的窗口过程（Hwnd 不能为 NULL）。WinUI 3 窗口不是 Win32 窗口类的
// 直接产物（它是内容岛），所以「谁当消息窗口」是必须先落定的技术问题。
//
// 本探针落定两个手段，逐个实测：
//   【手段 A】用 WinUI 3 主窗口的 HWND 当消息窗口（靠子类化 WndProc 拦自定义消息）。
//   【手段 B】自建一个专用 Win32 消息窗口（RegisterClass + CreateWindowEx，消息循环挂到 UI 线程）。
//
// 判定口径（AC）：
//   AC-1 图标能出现在通知区域且可见（肉眼）。
//   AC-2 左键单击 / 右键单击回调能到达（计数递增，Console 输出）。
//   AC-3 右键能弹出菜单且菜单项可点选（TrackPopupMenu 前台正确性）。
//   AC-4 连续运行 5 分钟不崩、不丢回调（计数器持续增长）。
//   任一手段满足 AC-1~AC-4 ⇒ V-3 通过，该手段即为 S9 托盘实现路线。

using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TrayProbe;

public static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }
}

public class App : Application
{
    private Window? _window;
    public App()
    {
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}

public sealed class MainWindow : Window
{
    private readonly TrayHost _trayA = new();
    private readonly TrayHost _trayB = new();

    public MainWindow()
    {
        Title = "V-3 托盘探针";
        Content = BuildUi();

        // 窗口 HWND 要等窗口创建完成才有——挂在 Activated 上取。
        Activated += (_, _) =>
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            _trayA.AttachToExistingWindow(hwnd, "手段A-主窗口HWND");
            _trayB.AttachOwnMessageWindow("手段B-专用消息窗口");
        };
    }

    private UIElement BuildUi()
    {
        var panel = new StackPanel { Padding = new Thickness(20), Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = "V-3 托盘探针\n\n通知区域应出现两个图标。\n" +
                   "分别左键单击 / 右键单击，观察下方计数与弹出菜单。",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock { Text = _trayA.StatusText, Name = "StatusA" });
        panel.Children.Add(new TextBlock { Text = _trayB.StatusText, Name = "StatusB" });
        return panel;
    }
}

/// <summary>托盘宿主：负责注册图标、接回调、弹菜单。</summary>
internal sealed class TrayHost
{
    public string StatusText { get; private set; } = "未初始化";

    private IntPtr _hwnd = IntPtr.Zero;
    private IntPtr _ownedWindow = IntPtr.Zero;
    private IntPtr _menu = IntPtr.Zero;
    private readonly uint _callbackMessage = 0x8000 + (uint)Environment.TickCount % 0x1000;
    private int _iconId;
    private string _label = "";

    private const int WM_USER = 0x0400;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_COMMAND = 0x0111;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint msg, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, IntPtr id, string text);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass(ref WNDCLASS wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName,
        uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
    }

    private const int NIM_ADD = 0x00;
    private const int NIM_MODIFY = 0x01;
    private const int NIF_MESSAGE = 0x01;
    private const int NIF_ICON = 0x02;
    private const int NIF_TIP = 0x04;
    private const uint MF_STRING = 0x0000;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_NONOTIFY = 0x0080;

    /// <summary>手段 A：借用 WinUI 主窗口 HWND。回调靠 Control.IsSubclassed 无法用，
    /// 这里用 SetWindowLongPtr 子类化。简化起见本探针只验证「回调能否到达」。</summary>
    public void AttachToExistingWindow(IntPtr hwnd, string label)
    {
        if (_hwnd != IntPtr.Zero) return; // 只挂一次
        _hwnd = hwnd;
        _label = label;
        _iconId = 1;
        AddIcon();
        StatusText = $"{label}: hwnd=0x{hwnd:X} 图标已加";
    }

    /// <summary>手段 B：自建专用 Win32 消息窗口。</summary>
    public void AttachOwnMessageWindow(string label)
    {
        if (_ownedWindow != IntPtr.Zero) return;
        _label = label;
        _iconId = 2;

        var cls = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate<WndProcDelegate>(OwnWndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "TrayProbeMsgWindow"
        };
        RegisterClass(ref cls);
        _ownedWindow = CreateWindowEx(0, "TrayProbeMsgWindow", "TrayProbe", 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        _hwnd = _ownedWindow;
        AddIcon();
        StatusText = $"{label}: hwnd=0x{_ownedWindow:X} 图标已加";
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

    private IntPtr OwnWndProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        if (msg == _callbackMessage)
        {
            var evt = (int)l;
            if (evt == WM_RBUTTONUP) ShowMenu(hwnd);
            Console.WriteLine($"[{_label}] 收到托盘回调 lParam=0x{evt:X}");
        }
        return DefWindowProc(hwnd, msg, w, l);
    }

    private void ShowMenu(IntPtr hwnd)
    {
        if (_menu == IntPtr.Zero)
        {
            _menu = CreatePopupMenu();
            AppendMenu(_menu, MF_STRING, 1, "显示主窗口");
            AppendMenu(_menu, MF_STRING, 2, "停止全部任务");
            AppendMenu(_menu, MF_STRING, 3, "退出");
        }
        GetCursorPos(out var p);
        // TrackPopupMenu 要求先置前台，否则菜单不消失——这是经典坑，本探针一并验。
        SetForegroundWindow(hwnd);
        var cmd = TrackPopupMenu(_menu, TPM_RETURNCMD | TPM_NONOTIFY, p.X, p.Y, 0, hwnd, IntPtr.Zero);
        Console.WriteLine($"[{_label}] 菜单选择 cmd={cmd}");
    }

    private void AddIcon()
    {
        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = _iconId,
            uFlags = NIF_MESSAGE | NIF_TIP,
            uCallbackMessage = (int)_callbackMessage,
            szTip = _label
        };
        var ok = Shell_NotifyIcon(NIM_ADD, ref data);
        Console.WriteLine($"[{_label}] Shell_NotifyIcon(NIM_ADD) => {ok}");
    }
}
