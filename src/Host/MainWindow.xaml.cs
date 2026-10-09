using Host.Engine;
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

        AppWindow.SetIcon("Assets/AppIcon.ico");

        // 页面用 Frame.Navigate 的第二个参数接收门面（见 MainPage.OnNavigatedTo）。
        RootFrame.Navigate(typeof(MainPage), facade);
    }
}
