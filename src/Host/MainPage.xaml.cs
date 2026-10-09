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

    /// <summary>
    /// 是否正在由代码回写 <c>Nav.SelectedItem</c>。
    ///
    /// <para>
    /// 回写会再触发一次 <see cref="OnNavSelectionChanged"/>——而那段逻辑会
    /// <b>退掉整条导航栈</b>。由「编辑页返回列表」这条路径进到这里时，栈里已经没有可退的页面，
    /// 再跑一遍虽因幂等而不会出错，但那只是巧合（依赖 <c>CurrentSourcePageType</c> 守卫）；
    /// 若将来有人改动那段逻辑，这个巧合立刻变成”返回时把列表页也退掉”的缺陷。
    /// 故用显式标志把这次自赋值产生的回调吞掉，不去赌幂等。
    /// </para>
    /// </summary>
    private bool _syncingNavSelection;

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
        // 这次选中态是代码回写的，不是用户点的——回写点负责把内容帧摆正，这里不再跑一遍。
        if (_syncingNavSelection)
        {
            return;
        }

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

        SyncNavSelection("tasks");
    }

    /// <summary>
    /// 把左侧导航的选中态拉回与内容帧一致。
    ///
    /// <para>
    /// 内容帧可以不走 <see cref="OnNavSelectionChanged"/> 就被改动（<see cref="GoBackToTaskList"/> 的
    /// 退栈回退、子页面发起的导航），此时不做同步就会出现”内容已经是任务列表、
    /// 左侧还高亮着设置“的错配——用户看到的是两个互相矛盾的信号。
    /// </para>
    /// </summary>
    /// <param name="tag">要选中的导航项 <c>Tag</c>。</param>
    private void SyncNavSelection(string tag)
    {
        // 导航控件可能尚未就绪（初始化早期）；此时不同步即可，绝不能抛。
        foreach (var item in Nav.MenuItems)
        {
            if (item is NavigationViewItem { Tag: string itemTag } && itemTag == tag)
            {
                if (ReferenceEquals(Nav.SelectedItem, item))
                {
                    return; // 已经选中的就是它，不必再走一次赋值/回调。
                }

                _syncingNavSelection = true;
                try
                {
                    Nav.SelectedItem = item;
                }
                finally
                {
                    _syncingNavSelection = false;
                }

                return;
            }
        }
    }
}
