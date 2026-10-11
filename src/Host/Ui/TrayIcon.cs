using System.Runtime.InteropServices;

namespace Host.Ui;

/// <summary>
/// 托盘图标：把图标挂到 **WinUI 主窗口的 HWND** 上，并在子类化后的窗口过程里
/// 接住回调消息（设计文档 §6.3、§6.5.5）。
///
/// <para>
/// <b>子类化的两条纪律</b>（§6.3，违反任一条都会让 WinUI 出现难以归因的故障）：
/// </para>
/// <list type="number">
/// <item><b>保存原窗口过程并在默认分支转调它。</b>主窗口承载 WinUI 3 自身的全部消息
///       （布局、DPI、激活、输入），本类只**识别并吞掉**自己的自定义消息，
///       其余一律 <c>CallWindowProc(原过程, …)</c>。</item>
/// <item><b>自定义消息各占一个 <c>WM_APP + n</c>。</b>见 <see cref="AppMessages"/>。</item>
/// </list>
///
/// <para>
/// <b>失败不拒绝启动</b>（§6.3）：<c>NIM_ADD</c> 失败（如任务栏不可用、图标资源缺失）
/// 时本类只把失败如实报出去，程序继续运行——只是托盘不可达，关窗后需经任务管理器结束。
/// 把"托盘装不上"升级成"拒绝启动"会让一个次要能力的故障瘫痪整个工具。
/// </para>
///
/// <para>
/// <b>线程</b>：托盘消息在窗口过程（UI 线程）上到达，菜单回调也在 UI 线程上执行，
/// 故不需要额外同步；<see cref="Dispose"/> 同样要求从 UI 线程调用。
/// </para>
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    /// <summary>托盘图标的逻辑 Id（同一 HWND 下可挂多个，本工程只一个）。</summary>
    private const int IconId = 1;

    /// <summary>菜单项 Id：显示主窗口。</summary>
    private const int MenuShowWindow = 1001;

    /// <summary>菜单项 Id：停止全部任务。</summary>
    private const int MenuStopAll = 1002;

    /// <summary>菜单项 Id：退出。</summary>
    private const int MenuExit = 1003;

    /// <summary>
    /// 保存原窗口过程（纪律①）。必须是**静态字段**：窗口过程是交给系统的函数指针，
    /// 系统在消息到达时按裸地址调用它——若这段代码被 GC 当作垃圾回收走，
    /// 进程会在下一次消息投递时直接崩溃（这一条与 <see cref="HookService"/> 里
    /// 静态保存钩子回调是同一个理由）。
    /// </summary>
    private static IntPtr _originalWndProc;

    /// <summary>窗口过程的托管引用（防止委托被 GC 回收，理由同 <see cref="_originalWndProc"/>）。</summary>
    private static WndProcDelegate? _wndProcKeepAlive;

    /// <summary>当前实例——窗口过程是静态的，靠这个字段路由回实例。</summary>
    private static TrayIcon? _current;

    /// <summary>主窗口句柄。</summary>
    private readonly IntPtr _hwnd;

    /// <summary>托盘图标句柄（<see cref="Dispose"/> 时要销毁，否则泄漏 GDI 对象）。</summary>
    private readonly IntPtr _icon;

    /// <summary>
    /// <see cref="_icon"/> 是否为**系统共享图标**。
    ///
    /// <para>
    /// <c>LoadIconW</c> 返回的是系统预置的共享句柄，调用方**不得**销毁它
    /// （销毁共享图标会影响同进程内其它使用方，且系统不保证其后再取仍有效）；
    /// 而 <c>ExtractIcon</c> 取来的是本进程拥有的句柄，不销毁就是泄漏。
    /// 两者的销毁语义相反，故必须记下来源。
    /// </para>
    /// </summary>
    private readonly bool _iconIsShared;

    /// <summary>是否已加入托盘（决定 <see cref="Dispose"/> 要不要发 <c>NIM_DELETE</c>）。</summary>
    private bool _added;

    /// <summary>是否已子类化（决定 <see cref="Dispose"/> 要不要还原窗口过程）。</summary>
    private bool _subclassed;

    private bool _disposed;

    /// <summary>
    /// 菜单项被选中时触发。参数是菜单项语义：
    /// <c>ShowWindow</c> / <c>StopAll</c> / <c>Exit</c> 三者之一。
    /// </summary>
    internal event Action<TrayCommand>? CommandInvoked;

    /// <summary>托盘相关的失败信息（<c>null</c> = 一切正常）。供 UI 决定是否提示用户。</summary>
    internal string? FailureMessage { get; private set; }

    /// <summary>
    /// 在主窗口上创建托盘图标并开始接管回调消息。
    /// </summary>
    /// <param name="hwnd">
    /// **WinUI 主窗口**的 HWND。<b>在窗口 <c>Activated</c> 之后取</b>（§6.3、§6.5.5）——
    /// 句柄自窗口构造起即有效，此处选在 <c>Activated</c> 后是为了“窗口完全就绪再挂托盘”。
    /// </param>
    internal TrayIcon(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            throw new ArgumentException("主窗口句柄无效（应为窗口 Activated 之后经 GetWindowHandle 取得）。", nameof(hwnd));
        }

        _hwnd = hwnd;
        _current = this;

        SubclassWindow();
        _icon = LoadIcon(out _iconIsShared);
        AddIcon();
    }

    // ── 安装 ──────────────────────────────────────────────────────

    /// <summary>
    /// 子类化主窗口过程（纪律①：存原过程、默认分支转调）。
    ///
    /// <para>
    /// 32/64 位下都必须用 <c>SetWindowLongPtr</c>——本工程只出 x64，
    /// 但仍走 Ptr 版本以与文档一致。
    /// </para>
    /// </summary>
    private void SubclassWindow()
    {
        _wndProcKeepAlive = WindowProc;
        _originalWndProc = SetWindowLongPtr(_hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive));
        _subclassed = _originalWndProc != IntPtr.Zero;
    }

    /// <summary>
    /// 取应用图标：优先用宿主 exe 自身的图标资源，取不到则退回系统默认图标。
    ///
    /// <para>
    /// <b>两个调用方共用这一处实现</b>：托盘图标（本类构造函数）与窗口/任务栏图标
    /// （<c>MainWindow</c> 经 <c>WM_SETICON</c>）——两处必须同源，
    /// 否则换了图标会出现"托盘变了、任务栏没变"的不一致（这正是曾经的缺陷）。
    /// </para>
    ///
    /// <para>
    /// <b>取自 exe 的 PE 资源</b>，不走文件路径——因为发布形态是单文件自包含，
    /// 产物目录里没有 <c>Assets\</c> 文件夹，任何指向 <c>Assets/AppIcon.ico</c>
    /// 的相对路径在发布产物里都解析不到。
    /// </para>
    /// </summary>
    /// <param name="isShared">
    /// 输出：所取句柄是否为系统共享图标（决定 <see cref="Dispose"/> 能否销毁它）。
    /// </param>
    internal static IntPtr LoadIcon(out bool isShared)
    {
        var module = GetModuleHandle(null);
        var icon = ExtractIcon(module, Environment.ProcessPath ?? string.Empty, 0);

        // ExtractIcon 返回 1 表示"文件里没有图标"（这是个特殊哨兵值，不是句柄）。
        if (icon == IntPtr.Zero || icon == new IntPtr(1))
        {
            // 没有图标资源也要能把托盘装上：用系统的默认应用图标兜底。
            isShared = true;
            return LoadSystemIcon();
        }

        isShared = false;
        return icon;
    }

    /// <summary>兜底图标：系统默认的应用程序图标。</summary>
    private static IntPtr LoadSystemIcon()
    {
        // IDI_APPLICATION = 32512（MAKEINTRESOURCE 形式）。
        return LoadIconW(IntPtr.Zero, new IntPtr(32512));
    }

    /// <summary>把图标加入托盘通知区。失败只记录，不抛（§6.3）。</summary>
    private void AddIcon()
    {
        var data = CreateNotifyIconData();
        if (Shell_NotifyIconW(NIM_ADD_CALLBACK, ref data))
        {
            _added = true;
            return;
        }

        FailureMessage = $"托盘图标添加失败（错误码 {Marshal.GetLastWin32Error()}）。程序仍可使用，但托盘菜单不可达。";
    }

    /// <summary>组装 <c>NOTIFYICONDATA</c>：挂消息、挂图标、写提示文案。</summary>
    private TrayNative.NOTIFYICONDATA CreateNotifyIconData()
    {
        return new TrayNative.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<TrayNative.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = IconId,
            uFlags = TrayNative.NIF_MESSAGE | TrayNative.NIF_ICON | TrayNative.NIF_TIP,
            uCallbackMessage = AppMessages.TrayCallback,
            hIcon = _icon,
            szTip = "鼠标键盘辅助",
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };
    }

    // ── 窗口过程 ──────────────────────────────────────────────────

    /// <summary>
    /// 子类化后的窗口过程（在 UI 线程上被系统调用）。
    ///
    /// <para>
    /// <b>只识别自己的消息</b>：命中 <see cref="AppMessages.TrayCallback"/> 就处理并返回 0；
    /// 其余一律交给原过程（纪律①）。这里**不**做任何耗时工作，也不碰业务对象——
    /// 菜单弹栈虽重，但它是用户主动触发的偶发操作，阻塞几毫秒无妨；
    /// 真正需要避免的是把业务逻辑搬到这里（业务由 <see cref="CommandInvoked"/> 的订阅方做）。
    /// </para>
    /// </summary>
    private static IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == AppMessages.TrayCallback)
        {
            var instance = _current;
            if (instance is not null && !instance._disposed)
            {
                instance.HandleTrayMessage(lParam);
            }

            return IntPtr.Zero;
        }

        // 纪律①：其余消息原样交还原过程，WinUI 才能正常工作。
        return CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);
    }

    /// <summary>处理托盘消息：右键弹菜单，左键单击唤起主窗口。</summary>
    private void HandleTrayMessage(IntPtr lParam)
    {
        var activity = lParam.ToInt32();

        switch (activity)
        {
            case TrayNative.WM_RBUTTONUP:
                ShowContextMenu();
                break;

            case TrayNative.WM_LBUTTONUP:
                // 左键单击直达"显示主窗口"——等价于菜单第一项，省去用户两步操作。
                CommandInvoked?.Invoke(TrayCommand.ShowWindow);
                break;
        }
    }

    /// <summary>
    /// 弹出右键菜单（§6.3）。
    ///
    /// <para>
    /// 两个细节是必需的，不是风格问题：
    /// </para>
    /// <list type="bullet">
    /// <item><b>弹出前 <c>SetForegroundWindow</c></b>——不这么做，菜单弹出后
    ///       点击别处**菜单不会消失**（这是 Win32 托盘菜单的经典坑，
    ///       系统是靠前台窗口变化来结束菜单模态循环的）。</item>
    /// <item><b><c>TPM_RETURNCMD | TPM_NONOTIFY</c></b>——让
    ///       <c>TrackPopupMenu</c> 直接返回被选项的 Id，而不是发
    ///       <c>WM_COMMAND</c> 到窗口。菜单挂在 WinUI 主窗口的 HWND 上，
    ///       返回命令码能彻底绕开"消息能否穿透 WinUI 自己的消息处理"这个不确定环节。</item>
    /// </list>
    /// </summary>
    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            AppendMenuW(menu, TrayNative.MF_STRING, new IntPtr(MenuShowWindow), "显示主窗口");
            AppendMenuW(menu, TrayNative.MF_STRING, new IntPtr(MenuStopAll), "停止全部任务");
            AppendMenuW(menu, TrayNative.MF_STRING, new IntPtr(MenuExit), "退出");

            GetCursorPos(out var cursor);

            // 先成为前台窗口，菜单才会在点击别处时正常消失（见上面的说明）。
            SetForegroundWindow(_hwnd);

            var chosen = TrackPopupMenuEx(
                menu,
                TrayNative.TPM_RETURNCMD | TrayNative.TPM_NONOTIFY,
                cursor.X,
                cursor.Y,
                _hwnd,
                IntPtr.Zero);

            // TrackPopupMenu 之后 Win32 文档要求补一条空消息，否则菜单可能残留；
            // 这里用一个 PostMessage(WM_NULL) 完成该收尾。
            PostMessageW(_hwnd, 0x0000 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero);

            Dispatch(chosen);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    /// <summary>把选中的菜单项翻译成语义命令并抛给订阅方。</summary>
    private void Dispatch(int command)
    {
        switch (command)
        {
            case MenuShowWindow:
                CommandInvoked?.Invoke(TrayCommand.ShowWindow);
                break;

            case MenuStopAll:
                CommandInvoked?.Invoke(TrayCommand.StopAll);
                break;

            case MenuExit:
                CommandInvoked?.Invoke(TrayCommand.Exit);
                break;

            // 0 = 用户没选（点到别处/按 Esc 取消），什么都不做。
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_added)
        {
            var data = CreateNotifyIconData();
            Shell_NotifyIconW(NIM_DELETE_CALLBACK, ref data);
            _added = false;
        }

        if (_icon != IntPtr.Zero && !_iconIsShared)
        {
            DestroyIcon(_icon);
        }

        if (_subclassed && _originalWndProc != IntPtr.Zero)
        {
            // 还原原窗口过程：不还原就把 WinUI 的窗口过程永久替换成了我们的托管委托，
            // 窗口销毁时框架的清理路径会走到一个已经失效的指针上。
            SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _originalWndProc);
            _subclassed = false;
            _originalWndProc = IntPtr.Zero;
        }

        if (ReferenceEquals(_current, this))
        {
            _current = null;
        }
    }

    // ── P/Invoke ──────────────────────────────────────────────────

    private const int GWLP_WNDPROC = -4;
    private const int NIM_ADD_CALLBACK = TrayNative.NIM_ADD;
    private const int NIM_DELETE_CALLBACK = TrayNative.NIM_DELETE;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SendMessageW", SetLastError = true)]
    internal static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW", SetLastError = true)]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(int dwMessage, ref TrayNative.NOTIFYICONDATA lpData);

    [DllImport("user32.dll", EntryPoint = "CreatePopupMenu", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(IntPtr hMenu, int uFlags, IntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll", EntryPoint = "DestroyMenu", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", EntryPoint = "TrackPopupMenuEx", SetLastError = true)]
    private static extern int TrackPopupMenuEx(IntPtr hMenu, int fuFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);

    [DllImport("user32.dll", EntryPoint = "GetCursorPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out TrayNative.POINT lpPoint);

    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "DestroyIcon", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", EntryPoint = "LoadIconW", SetLastError = true)]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("shell32.dll", EntryPoint = "ExtractIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr ExtractIcon(IntPtr hInst, string lpszExeFileName, int nIconIndex);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}

/// <summary>托盘菜单项的语义（把 Win32 的整数命令码挡在 UI 之外）。</summary>
internal enum TrayCommand
{
    /// <summary>显示并激活主窗口（FR-UI-01）。</summary>
    ShowWindow,

    /// <summary>停止全部任务，进程继续驻留（FR-RUN-04）。</summary>
    StopAll,

    /// <summary>停止全部任务并结束进程（FR-UI-02、AC-32）。</summary>
    Exit,
}
