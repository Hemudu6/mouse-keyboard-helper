using Host.Engine;
using Host.Ui;
using Microsoft.UI.Xaml;

namespace Host;

/// <summary>
/// 主窗口：承载界面框架（<c>Frame</c>）与标题栏。
///
/// <para>
/// 窗口本身**不持有业务逻辑**——它只把组合根 <see cref="HostFacade"/> 传给页面，
/// 由页面自己去门面取任务列表、订阅运行态。这样换页面（列表/编辑/设置）时，
/// 窗口不需要跟着改。
/// </para>
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>
    /// 组合根。页面通过 <see cref="MainWindow.Facade"/> 取用。
    ///
    /// <para>
    /// 门面由 <see cref="App"/> 构造并注入（而非在此自建）：它的生命周期长于窗口
    /// （关窗只隐藏、进程还活着），且**全进程只应有一个**——
    /// 在这里 <c>new</c> 会让"窗口重建"变成"钩子重装、快照重建"，那是两套并存的状态。
    /// </para>
    /// </summary>
    internal HostFacade Facade { get; }

    /// <summary>构造主窗口并导航到主页面。</summary>
    /// <param name="facade">进程内唯一的组合根。</param>
    public MainWindow(HostFacade facade)
    {
        Facade = facade;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        ApplyWindowIcon();

        // 页面用 Frame.Navigate 的第二个参数接收门面（见 MainPage.OnNavigatedTo）。
        RootFrame.Navigate(typeof(MainPage), facade);
    }

    /// <summary><c>WM_SETICON</c>：设置窗口的大/小图标（标题栏与任务栏按钮共用）。</summary>
    private const uint WM_SETICON = 0x0080;

    /// <summary><c>WM_SETICON</c> 的 <c>ICON_SMALL</c>：标题栏、任务栏的小图标（16×16）。</summary>
    private const int IconSmall = 0;

    /// <summary><c>WM_SETICON</c> 的 <c>ICON_BIG</c>：Alt+Tab 与任务栏的大图标（32×32）。</summary>
    private const int IconBig = 1;

    /// <summary>
    /// 设置窗口图标（标题栏 + 任务栏按钮共用这一个来源）。
    ///
    /// <para>
    /// <b>为什么不用 <c>AppWindow.SetIcon</c></b>：它按文件路径取图标，而发布形态是单文件
    /// 自包含——产物目录里没有 Assets 文件夹，相对路径 "Assets/AppIcon.ico" 在发布产物里
    /// 解析不到，窗口于是退回 WinUI 的默认图标：现象就是「托盘图标换了、任务栏按钮没换」
    /// （托盘走 exe 的 PE 资源，不受此影响）。
    /// </para>
    ///
    /// <para>
    /// 改为<b>从自身 exe 的 PE 资源取图标</b>（<see cref="TrayIcon.LoadIcon"/>，与托盘同源），
    /// 再经 <c>WM_SETICON</c> 挂到窗口上——小图标（16×16）与大图标（32×32）各设一次。
    /// 这条路径不依赖任何随包文件。
    /// </para>
    ///
    /// <para>
    /// <b>两档共用同一个 32×32 句柄</b>：单文件发布没有散装文件，<c>LoadImage</c> 的
    /// <c>LR_LOADFROMFILE</c> 取不到东西（实测返回 0），拿不到原生 16×16 那一帧；
    /// 交给系统降采样到 16×16 是可用的近似。
    /// </para>
    ///
    /// <para>
    /// <b>句柄在挂上之后立即销毁</b>：窗口在 <c>WM_SETICON</c> 时已持有自己的引用
    /// （实测：销毁原句柄后 <c>WM_GETICON</c> 回读仍非 0），不销毁就是 GDI 泄漏。
    /// 系统共享图标（<c>LoadSystemIcon</c> 兜底来的）不属本进程，不能销毁。
    /// </para>
    ///
    /// <para>
    /// <b>失败不拒绝启动</b>：取 HWND 或取图标失败时只放弃设置，窗口照常显示
    /// （沿用 <see cref="TrayIcon"/> 的口径——次要能力故障不瘫痪主功能）。
    /// </para>
    /// </summary>
    private void ApplyWindowIcon()
    {
        IntPtr hwnd;
        try
        {
            hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        }
        catch (Exception)
        {
            // 取句柄失败（异常仅作兜底）：窗口图标保持默认，不影响功能。
            return;
        }

        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var icon = TrayIcon.LoadIcon(out var isShared);
        if (icon == IntPtr.Zero)
        {
            return;
        }

        TrayIcon.SendMessage(hwnd, WM_SETICON, new IntPtr(IconSmall), icon);
        TrayIcon.SendMessage(hwnd, WM_SETICON, new IntPtr(IconBig), icon);

        // 窗口已持有自己的引用，此处释放本方法这一份，避免 GDI 句柄泄漏。
        // 共享图标（系统预置）不归本进程所有，销毁会波及同进程其它使用方。
        if (!isShared)
        {
            TrayIcon.DestroyIcon(icon);
        }
    }
}
