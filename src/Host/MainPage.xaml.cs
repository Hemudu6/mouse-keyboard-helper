using Host.Engine;
using Host.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Host;

/// <summary>
/// 主页面：**导航宿主**。
///
/// <para>
/// 它本身不显示业务内容——只把 <see cref="HostFacade"/> 转交给三个子页面
/// （任务列表 / 任务编辑 / 设置），并负责它们之间的跳转。
/// </para>
/// <para>
/// <b>门面为什么走导航参数</b>：<see cref="MainWindow"/> 用
/// <c>RootFrame.Navigate(typeof(MainPage), facade)</c> 把它交给本页。
/// 子页面同样由本页 <c>Navigate(type, facade)</c> 拿到——这样每个页面都
/// 只依赖"导航参数里有个门面"，而不需要知道窗口类、更不需要全局单例。
/// </para>
/// </summary>
public sealed partial class MainPage : Page
{
    /// <summary>
    /// 组合根。三个子页面共用同一个（进程内唯一，见 <see cref="HostFacade"/> 类注释）。
    ///
    /// <para>它只在 <see cref="OnNavigatedTo"/> 里被赋值，故早于导航不可用。</para>
    /// </summary>
    internal HostFacade Facade { get; private set; } = null!;

    /// <summary>本次导航是否已经把"任务列表"作为着陆页推过一遍（防止重复导航叠栈）。</summary>
    private bool _landed;

    /// <summary>构造导航宿主。</summary>
    public MainPage()
    {
        InitializeComponent();
    }

    /// <inheritdoc/>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not HostFacade facade)
        {
            // 走到这里说明导航调用方写错了（只有 MainWindow 会导航本页）。
            // 不静默兜底：界面此时无法工作，直接把问题摆在明面上。
            throw new InvalidOperationException("主页面必须由 MainWindow 携带 HostFacade 导航进入。");
        }

        Facade = facade;

        if (_landed)
        {
            return;
        }

        _landed = true;
        ContentFrame.Navigate(typeof(TaskListPage), facade);
    }

    /// <summary>左侧导航切换：任务列表 / 设置。</summary>
    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag })
        {
            switch (tag)
            {
                case "tasks":
                    // 从设置页回到列表页：若栈顶已是编辑页，先退出去（否则会叠出一个新列表页，
                    // 用户按返回时看到的是旧页面）。
                    while (ContentFrame.CanGoBack)
                    {
                        ContentFrame.GoBack();
                    }

                    if (ContentFrame.CurrentSourcePageType != typeof(TaskListPage))
                    {
                        ContentFrame.Navigate(typeof(TaskListPage), Facade);
                    }

                    break;

                case "settings":
                    if (ContentFrame.CurrentSourcePageType != typeof(SettingsPage))
                    {
                        ContentFrame.Navigate(typeof(SettingsPage), Facade);
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// 供子页面调用：跳到任务编辑页。
    ///
    /// <para>
    /// 「新建」与「编辑」共用同一个页面，靠导航参数区分：
    /// 传 <c>null</c> 是新建，传 Id 是编辑既有任务。
    /// </para>
    /// <para>
    /// 子页面通过 <see cref="Navigator.ResolveFor(Frame)"/> 拿到本页再调用——
    /// 不直接走 <c>Frame.Navigate</c>，是因为「退栈」语义（下面 <see cref="GoBackToTaskList"/>）
    /// 需要宿主这边统一把关，散到三个页面里迟早会各写一套。
    /// </para>
    /// </summary>
    /// <param name="taskId">要编辑的任务 Id；<c>null</c> = 新建。</param>
    internal void GoToTaskEditor(Guid? taskId)
    {
        ContentFrame.Navigate(typeof(TaskEditPage), new TaskEditArgs(Facade, taskId));
    }

    /// <summary>供子页面调用：返回任务列表页。</summary>
    internal void GoBackToTaskList()
    {
        while (ContentFrame.CanGoBack)
        {
            ContentFrame.GoBack();
        }

        if (ContentFrame.CurrentSourcePageType != typeof(TaskListPage))
        {
            ContentFrame.Navigate(typeof(TaskListPage), Facade);
        }
    }
}
