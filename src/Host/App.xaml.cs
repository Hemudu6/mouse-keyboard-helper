using System.Runtime.InteropServices;
using Host.Engine;
using Host.Ui;
using Microsoft.UI.Xaml;

namespace Host;

/// <summary>
/// 应用入口：装配顺序的唯一落点。
///
/// <para>
/// <b>顺序是有理由的，不是随手排的</b>：
/// </para>
/// <list type="number">
/// <item><b>单实例判定最先</b>（§6.5.5）：第二实例要"发完即退"，若先建主窗口再判定，
///       第二实例会先闪一个窗口才消失；更糟的是两个实例都装上了全局钩子，
///       在退出瞬间争抢输入。判定失败就提示并直接退出，不进入后续任何一步。</item>
/// <item><b>门面构造</b>：构造即完成接线（任务载入、快照就绪），但**不装钩子**。</item>
/// <item><b>主窗口创建并 <c>Activate</c></b>，托盘与“第二实例唤起”的 HWND 在 <c>Activated</c>
///       之后取（§6.3）——句柄自窗口构造起即有效，选在 Activated 后是为了“窗口完全就绪
///       再挂托盘”这一时序干净，不当依赖为硬件限制。</item>
/// <item><b>门面 <c>Start()</c> 装钩子</b>：放在窗口就绪之后，此时错误提示有地方可显示。</item>
/// </list>
/// </summary>
public partial class App : Application
{
    /// <summary>主窗口（唯一）。</summary>
    private MainWindow? _window;

    /// <summary>组合根（进程内唯一装配点）。</summary>
    private HostFacade? _facade;

    /// <summary>托盘图标（窗口就绪后创建；创建失败时保持 <c>null</c>，程序照常运行）。</summary>
    private TrayIcon? _tray;

    /// <summary>单实例约束（互斥体 + 消息专用窗口）。</summary>
    private SingleInstance? _singleInstance;

    /// <summary>本进程是否已开始退出（挡住"托盘退出"与"关窗"同时触发两次收尾）。</summary>
    private bool _exiting;

    /// <summary>
    /// 初始化单例应用对象。这是第一行被执行的授权代码。
    /// </summary>
    public App()
    {
        InitializeComponent();
    }

    /// <summary>应用启动入口。</summary>
    /// <param name="args">启动请求的细节。</param>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // ── ① 单实例判定（必须先于任何窗口创建）────────────────────
        _singleInstance = new SingleInstance();
        _singleInstance.TryAcquire();

        if (!_singleInstance.IsFirstInstance)
        {
            // AC-30：第二实例只提示"程序已在运行"并退出。
            // 用原生 MessageBox 而非 WinUI 对话框——此刻还没有 XAML 窗口，
            // 弹一个 ContentDialog 需要先有 XamlRoot，条件不具备。
            ShowAlreadyRunningNotice();
            Exit();
            return;
        }

        // 首实例：把"第二实例要来抢焦点"接到"显示主窗口"（与托盘第一项同一条路径）。
        _singleInstance.ActivationRequested += BringMainWindowToFront;

        // ── ② 组合根（构造即接线，此时尚未装钩子）──────────────────
        _facade = new HostFacade();

        // ── ③ 主窗口 ──────────────────────────────────────────────
        _window = new MainWindow(_facade);
        _window.Activated += OnWindowActivated;
        _window.AppWindow.Closing += OnAppWindowClosing;
        _window.Activate();

        // ── ④ 装钩子（窗口已就绪，错误有地方提示）──────────────────
        _facade.Start();

        // ── ⑤ 配置载入异常提示（AC-21 / FR-CFG-02，非阻塞）─────────
        ShowLoadNoticeIfAny(_facade.LoadNotice);
    }

    /// <summary>
    /// 窗口首次激活后建托盘（§6.3：HWND 须在 <c>Activated</c> 之后取）。
    /// </summary>
    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        // 只建一次：Activated 会在每次窗口获得焦点时重复触发。
        _window!.Activated -= OnWindowActivated;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        try
        {
            _tray = new TrayIcon(hwnd);
            _tray.CommandInvoked += OnTrayCommand;
        }
        catch (Exception ex)
        {
            // 托盘建不上**不拒绝启动**（§6.3）：程序仍可使用，只是关窗后需经任务管理器结束。
            ShowNotice("托盘不可用", $"托盘图标创建失败，程序仍可使用。\n\n{ex.Message}");
        }
    }

    /// <summary>托盘菜单三项的分发（§6.3 表）。</summary>
    private void OnTrayCommand(TrayCommand command)
    {
        switch (command)
        {
            case TrayCommand.ShowWindow:
                BringMainWindowToFront();
                break;

            case TrayCommand.StopAll:
                // 「停止全部任务」**不退出进程**（FR-RUN-04）：只清调度表。
                _facade?.Scheduler.StopAll();
                break;

            case TrayCommand.Exit:
                ExitApplication();
                break;
        }
    }

    /// <summary>
    /// 关闭主窗口 = **隐藏而非退出**（FR-UI-01）。
    ///
    /// <para>
    /// 取消系统默认的关闭动作，改为隐藏窗口：本工具是常驻的辅助程序，
    /// 关窗只是"把界面收起来"，任务该跑还得跑。
    /// </para>
    /// </summary>
    private void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_exiting)
        {
            // 走的是"托盘 → 退出"路径：收尾已经做过，放行这次关闭。
            return;
        }

        args.Cancel = true;
        sender.Hide();
    }

    /// <summary>
    /// 真正退出：收尾 → 关托盘 → 关窗 → 结束进程。
    ///
    /// <para>
    /// <b>收尾的唯一入口是门面的 <c>Shutdown</c></b>（§6.5.5 明文：托盘退出、窗口关闭、
    /// 异常结束路径都收敛到 <c>Scheduler.Shutdown()</c>，不得各自写一套停任务逻辑）。
    /// 它内含停任务 + 补发松开两层保障（AC-32）。
    /// </para>
    /// </summary>
    private void ExitApplication()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;

        // ① 停止全部任务 + 补发所有按住的键（AC-32）——必须在进程结束前完成。
        _facade?.Shutdown();

        // ② 摘掉托盘图标（不摘的话图标会残留在通知区，直到用户划过它才消失）。
        _tray?.Dispose();
        _tray = null;

        // ③ 关窗（此时 _exiting 已置位，关闭不再被取消）。
        _window?.Close();

        // ④ 收掉互斥体与消息专用窗口。
        _singleInstance?.Dispose();
        _singleInstance = null;

        // ⑤ 结束进程。用 Exit() 而非 Environment.Exit：让 WinUI 走自己的关闭流程，
        //    给框架一个收尾的机会（预设在后面兜底，若框架未退出则由 Exit() 收口）。
        Exit();
    }

    /// <summary>把主窗口显示出来并抢到前台（托盘第一项与单实例唤起共用）。</summary>
    private void BringMainWindowToFront()
    {
        if (_window is null)
        {
            return;
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);

        // 窗口可能是隐藏的（关窗隐藏后）——ShowWindow 负责显示，SetForegroundWindow 负责抢焦点，
        // 两者缺一不可：只显示不抢焦点的话，窗口会出现在别的窗口后面，用户看不到。
        ShowWindow(hwnd, SW_SHOW);
        SetForegroundWindow(hwnd);
    }

    /// <summary>第二实例的提示（AC-30）。</summary>
    private static void ShowAlreadyRunningNotice()
    {
        MessageBox(
            IntPtr.Zero,
            "程序已在运行。",
            "鼠标键盘辅助",
            MB_OK | MB_ICONINFORMATION | MB_SETFOREGROUND);
    }

    /// <summary>提示配置载入时发现的问题（AC-21 / FR-CFG-02）。</summary>
    private static void ShowLoadNoticeIfAny(string? notice)
    {
        if (string.IsNullOrWhiteSpace(notice))
        {
            return;
        }

        ShowNotice("配置载入提示", notice);
    }

    /// <summary>原生提示框（在 WinUI 窗口完全就绪前也能用）。</summary>
    private static void ShowNotice(string title, string message)
    {
        MessageBox(IntPtr.Zero, message, $"鼠标键盘辅助 — {title}", MB_OK | MB_ICONWARNING);
    }

    // ── P/Invoke ──────────────────────────────────────────────────

    private const int SW_SHOW = 5;
    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONINFORMATION = 0x00000040;
    private const uint MB_ICONWARNING = 0x00000030;
    private const uint MB_SETFOREGROUND = 0x00010000;

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    [DllImport("user32.dll", EntryPoint = "ShowWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
