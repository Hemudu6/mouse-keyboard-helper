using Core.Keys;
using Core.Model;
using Host.Engine;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Host.Views;

/// <summary>
/// 任务列表页（设计文档 §6.1、§6.2、§6.5.3）。
///
/// <para>
/// <b>基类为什么是 <see cref="Page"/> 而不是 <see cref="FacadePage"/></b>：
/// XAML 编译器为每个 .xaml 生成的 <c>.g.i.cs</c> 都固定声明
/// <c>partial class … : global::Microsoft.UI.Xaml.Controls.Page</c>，
/// 而 C# 要求部分声明的基类完全一致——所以任何带自定义基类的页面都必须在
/// 生成的 C# 里显式写 <c>&lt;Page&gt;</c> 才能两半对上。三个业务页采用同一形态：
/// 直接继承 <see cref="Page"/>，门面由导航参数取出（与 <see cref="MainPage"/> 一致）。
/// </para>
/// <para>
/// <b>本页的改动立即生效</b>：新建 / 复制 / 删除 / 启用开关都直接落库落盘
/// （<c>HostFacade.AddTask</c> 等），不存在"改完要不要保存"的疑问——
/// 那是编辑页的语义（编辑页改草稿副本）。两页的差别是刻意的：列表上做的都是
/// 一步到位的动作，编辑页里改的是多变少、必须能反悔的一整份任务。
/// </para>
/// <para>
/// <b>运行态只订阅不轮询</b>（§6.5.3）：<c>Scheduler.RunStateChanged</c> →
/// <c>DispatcherQueue.TryEnqueue</c> → 重读 <c>Scheduler.IsRunning</c>。
/// 事件本身只当"该重读一下"的信号——它不保证事件之间的先后因果，
/// 所以本页从不据事件参数直接改显示，一律重读。
/// </para>
/// </summary>
public sealed partial class TaskListPage : Page
{
    /// <summary>
    /// 组合根。由 <see cref="OnNavigatedTo"/> 从导航参数取出，故**早于导航不可用**。
    /// </summary>
    private HostFacade _facade = null!;

    /// <summary>是否已订阅调度器事件（防止导航进出时重复挂钩）。</summary>
    private bool _subscribed;

    /// <summary>列表数据源。每项是**显示用的快照行**，不是任务对象本身。</summary>
    private readonly List<TaskListEntry> _entries = new();

    /// <summary>构造列表页。</summary>
    public TaskListPage()
    {
        InitializeComponent();
    }

    private HostFacade Facade => _facade;

    private Scheduler Scheduler => _facade.Scheduler;

    /// <inheritdoc/>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not HostFacade facade)
        {
            // 走到这里说明导航调用方写错了。不静默兜底：界面此时无法工作，
            // 把问题摆在明面上，而不是让它在后面以空引用崩溃的形式冒出来。
            throw new InvalidOperationException(
                $"{nameof(TaskListPage)} 必须携带 HostFacade 导航进入。");
        }

        _facade = facade;

        // 载入提示（AC-37）：配置文件有问题时**必须**让用户看见，不能只在日志里。
        if (!string.IsNullOrWhiteSpace(facade.LoadNotice))
        {
            LoadNoticeBar.Message = facade.LoadNotice;
            LoadNoticeBar.IsOpen = true;
        }

        SubscribeRunState();

        // 每次导航进来都重读：期间任务可能被别的入口（编辑页、托盘）改动过。
        ReloadTasks();
    }

    // ── 运行态 ────────────────────────────────────────────────────

    /// <summary>
    /// 订阅调度器的运行态变化（§6.5.3）。
    ///
    /// <para>
    /// 事件在**调度线程**上触发，直接改 <c>ItemsControl</c> 的数据源会跨线程踩 UI——
    /// 故一律经 <c>DispatcherQueue.TryEnqueue</c> 回到 UI 线程再重读。
    /// 用 <c>TryEnqueue</c> 而非 <c>Enqueue</c>：关窗后队列可能已不可用，
    /// 此时静默丢弃是正确的（窗口都没了，没人再看状态）。
    /// </para>
    /// <para>
    /// <b>为什么由本页负责摘钩子</b>：门面在进程内是唯一的、活到进程结束，
    /// 而页面会被导航反复创建销毁——订阅挂在门面上就必须在离开时摘掉，
    /// 否则每进一次列表页就多一份处理器，事件一发就刷屏且拖住已死页面。
    /// </para>
    /// </summary>
    private void SubscribeRunState()
    {
        if (_subscribed)
        {
            return;
        }

        _subscribed = true;
        Scheduler.RunStateChanged += OnRunStateChanged;
        Unloaded += OnPageUnloaded;
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            return;
        }

        _subscribed = false;
        Scheduler.RunStateChanged -= OnRunStateChanged;
        Unloaded -= OnPageUnloaded;
    }

    private void OnRunStateChanged(Guid taskId, bool isRunning)
    {
        // 参数不直接用：事件只代表"调度器对表内有无此任务的观测"，
        // 且 false 的发出时机随停止路径而异。一律重读，避免把事件顺序当成事实。
        _ = taskId;
        _ = isRunning;

        // 队列不可用时静默丢弃（页面已销毁 / 应用正在退出）——无人再看状态。
        _ = DispatcherQueue.TryEnqueue(RefreshRunStates);
    }

    /// <summary>重读每个任务的运行态并刷新卡片（UI 线程）。</summary>
    private void RefreshRunStates()
    {
        var running = new HashSet<Guid>(Scheduler.RunningTaskIds);

        foreach (var entry in _entries)
        {
            entry.IsRunning = running.Contains(entry.Id);
        }
    }

    // ── 列表装载 ──────────────────────────────────────────────────

    /// <summary>
    /// 从门面重读任务列表并重建卡片。
    ///
    /// <para>
    /// 用 <c>Tasks.Snapshot()</c> 取**脱库的副本**：列表页只读不写任务内容，
    /// 但显示用的对象若直接引用注册表内部实例，任何一处误改都会绕过校验器
    /// 悄悄改掉正在跑的任务。
    /// </para>
    /// </summary>
    private void ReloadTasks()
    {
        var running = new HashSet<Guid>(Scheduler.RunningTaskIds);
        var snapshots = Facade.Tasks.Snapshot();

        _entries.Clear();
        foreach (var task in snapshots)
        {
            _entries.Add(TaskListEntry.From(task, running.Contains(task.Id)));
        }

        TaskList.ItemsSource = _entries;
        EmptyHint.Visibility = _entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StopAllButton.IsEnabled = Scheduler.RunningCount > 0;
    }

    // ── 卡片动作 ──────────────────────────────────────────────────

    private void OnNewTaskClick(object sender, RoutedEventArgs e)
        => NavigateToEditor(null);

    private void OnEditTaskClick(object sender, RoutedEventArgs e)
    {
        if (TagAsGuid(sender) is { } id)
        {
            NavigateToEditor(id);
        }
    }

    /// <summary>
    /// 复制任务（FR-TASK-04）。
    ///
    /// <para>
    /// 副本必须**重新过一遍全校验**——这一点由门面保证（<c>DuplicateTask</c> 内部
    /// 走的就是新建那条校验路径），本页只负责把结果如实报出来，不自己判规则。
    /// </para>
    /// </summary>
    private void OnDuplicateTaskClick(object sender, RoutedEventArgs e)
    {
        if (TagAsGuid(sender) is not { } id)
        {
            return;
        }

        var (copy, result) = Facade.DuplicateTask(id);
        if (copy is null || !result.IsValid)
        {
            ShowMessage("复制失败", result.Summary);
            return;
        }

        ReloadTasks();
        ShowMessage("已复制", $"已创建「{copy.DisplayName}」。");
    }

    /// <summary>
    /// 删除任务（§6.5.3 要求二次确认）。
    ///
    /// <para>
    /// 先停任务再删：运行中的任务被删掉后，它的循环仍在跑但表里已经没有它，
    /// 用户既看不到也点不停（紧急停止快捷键还能兜住，但那是最后一道防线）。
    /// </para>
    /// </summary>
    private async void OnDeleteTaskClick(object sender, RoutedEventArgs e)
    {
        if (TagAsGuid(sender) is not { } id)
        {
            return;
        }

        var name = _entries.FirstOrDefault(entry => entry.Id == id)?.Name ?? "该任务";

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除任务",
            Content = $"确定要删除「{name}」吗？此操作不可撤销。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        // 只调 Facade.RemoveTask：它内部已经在 _wiringGate 内做了 Scheduler.Stop
        // （"先停再删"是门面的语义，见 HostFacade.RemoveTask）。页面再补一次 Stop
        // 是绕过门面串行化的重复调用——将来停止时机一变，这条路径会静默偏离。
        // 删除可能抛（Store 落盘失败等），用 finally 保证列表一定重载：
        // 否则界面会停留在"已删除"的陈旧视图上，而库里其实还在。
        try
        {
            if (Facade.RemoveTask(id) is null)
            {
                ShowMessage("删除失败", $"「{name}」不在任务列表中。");
            }
        }
        catch (Exception ex)
        {
            ShowMessage("删除失败", ex.Message);
        }
        finally
        {
            ReloadTasks();
        }
    }

    /// <summary>
    /// 启用 / 禁用开关（FR-TASK-07、AC-02）。
    ///
    /// <para>
    /// 立即落库。禁用中的任务必须同时停掉——否则会出现"界面显示已禁用、
    /// 任务却还在按"的矛盾状态。开关键在钩子侧也不再被消费（快照里随之更新）。
    /// </para>
    /// <para>
    /// 开关是**单向绑定**（<c>Mode=OneWay</c>）：被拒时直接改 <c>IsOn</c> 会被
    /// 单向绑定在下一次刷新时又拨回去，故一律走"落库 → 重载列表"这条路径，
    /// 让显示回到与库一致的状态。
    /// </para>
    /// </summary>
    private void OnEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle || TagAsGuid(sender) is not { } id)
        {
            return;
        }

        var wanted = toggle.IsOn;

        // 必须**先拷一份**再改：Snapshot() 复制的只是数组，元素仍是注册表的
        // 内部引用（TaskRegistry.Snapshot 的实现如此）。直接在取到的对象上赋
        // Enabled，等于就地改生效中的任务——校验一旦不过，对象已被改脏，
        // 接下来 ReloadTasks() 读到的正是脏值：界面显示"已启用"，而这次保存
        // 其实被拒、磁盘也没落。三方不一致且要到重启才对齐。
        var current = Facade.Tasks.Snapshot().FirstOrDefault(t => t.Id == id);
        if (current is null || current.Enabled == wanted)
        {
            return;
        }

        var edited = TaskDraft.FromDefinition(current).ToDefinition();
        edited.Enabled = wanted;

        var result = Facade.UpdateTask(edited);
        if (!result.IsValid)
        {
            // 保存被拒：重载列表把开关拨回原位，并把原因告诉用户（不静默）。
            ReloadTasks();
            ShowMessage("未能修改", result.Summary);
            return;
        }

        if (!wanted)
        {
            // 禁用即停止：禁用态的任务不该继续按。
            Scheduler.Stop(id);
        }

        ReloadTasks();
    }

    /// <summary>停止全部任务（FR-RUN-04）。进程继续驻留，只停任务（§6.3）。</summary>
    private void OnStopAllClick(object sender, RoutedEventArgs e)
    {
        Scheduler.StopAll();
        ReloadTasks();
    }

    // ── 工具 ──────────────────────────────────────────────────────

    private void NavigateToEditor(Guid? taskId)
    {
        Navigator.ResolveFor(Frame).GoToTaskEditor(taskId);
    }

    private static Guid? TagAsGuid(object sender)
        => sender is FrameworkElement { Tag: Guid id } ? id : null;

    /// <summary>
    /// 非阻断提示：列表页的动作没有"取消"语义，弹窗只用来告知结果。
    ///
    /// <para>
    /// <b>页面已卸载时直接返回</b>：本方法在 <c>await</c> 之后才构造弹窗，
    /// 此时用户可能已经导航走了。页面一离开视觉树，<c>XamlRoot</c> 就变成 <c>null</c>，
    /// 再拿它构造 <c>ContentDialog</c> 会抛异常——而本方法是 <c>async void</c>，
    /// 异常直接成为进程级未处理异常（没有调用方会观察到它）。
    /// 没人再看这一页，静默丢弃正是正确的。
    /// </para>
    /// </summary>
    private async void ShowMessage(string title, string? message)
    {
        if (XamlRoot is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message ?? string.Empty,
            CloseButtonText = "知道了",
        };

        await dialog.ShowAsync();
    }
}

/// <summary>
/// 列表卡片的数据行：**任务快照 + 运行态的显示投影**。
///
/// <para>
/// 不直接把 <see cref="TaskDefinition"/> 绑到卡片上，是因为卡片还要显示
/// 运行中 / 已停止这种"不属于任务、属于调度器"的状态；把它们合成一个只读投影，
/// 卡片模板就只需要认这一个类型（<c>x:DataType</c> 也跟着简单）。
/// </para>
/// <para>
/// 运行态是可变的（<c>Scheduler.RunStateChanged</c> 会改它），故实现
/// <see cref="System.ComponentModel.INotifyPropertyChanged"/> 让状态徽标就地刷新，
/// 而不是整表重建——整表重建会让用户正在看的位置跳掉。
/// </para>
/// </summary>
public sealed class TaskListEntry : System.ComponentModel.INotifyPropertyChanged
{
    private bool _isRunning;

    private TaskListEntry(TaskDefinition task, bool isRunning)
    {
        Id = task.Id;
        Name = task.DisplayName;
        Enabled = task.Enabled;
        ToggleKeyText = KeyCatalog.Describe(task.ToggleKey);
        CommandsText = DescribeCommands(task);
        _isRunning = isRunning;
    }

    /// <inheritdoc/>
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>任务 Id（卡片按钮靠它定位任务）。</summary>
    public Guid Id { get; }

    /// <summary>任务名。</summary>
    public string Name { get; }

    /// <summary>是否启用（单向绑定到卡片上的开关；实际修改走 <c>OnEnabledToggled</c>）。</summary>
    public bool Enabled { get; }

    /// <summary>开关按键的中文描述。</summary>
    public string ToggleKeyText { get; }

    /// <summary>命令摘要（「键盘 A · 点按」这样的短名逐条列出）。</summary>
    public string CommandsText { get; }

    /// <summary>禁用徽标是否可见。</summary>
    public Visibility DisabledBadgeVisibility => Enabled ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>此刻是否在运行。</summary>
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (_isRunning == value)
            {
                return;
            }

            _isRunning = value;
            Raise(nameof(IsRunning));
            Raise(nameof(StateText));
            Raise(nameof(StateBackground));
            Raise(nameof(StateDot));
        }
    }

    /// <summary>状态徽标上的文字。</summary>
    public string StateText => IsRunning ? "运行中" : "已停止";

    /// <summary>状态徽标的底色。</summary>
    public SolidColorBrush StateBackground => IsRunning
        ? new SolidColorBrush(ColorHelper.FromArgb(48, 16, 124, 16))
        : new SolidColorBrush(ColorHelper.FromArgb(32, 128, 128, 128));

    /// <summary>状态徽标前的小圆点。</summary>
    public SolidColorBrush StateDot => IsRunning
        ? new SolidColorBrush(ColorHelper.FromArgb(255, 16, 124, 16))
        : new SolidColorBrush(ColorHelper.FromArgb(255, 128, 128, 128));

    /// <summary>由任务对象构造显示行（构造时即定下运行态）。</summary>
    public static TaskListEntry From(TaskDefinition task, bool isRunning) => new(task, isRunning);

    /// <summary>
    /// 命令摘要。
    ///
    /// <para>
    /// 空命令列表是可能的（新建后还没加命令的畸形任务，或手工改坏的文件）；
    /// 这里给一句提示而不是空字符串——空白会让人以为界面坏了。
    /// </para>
    /// </summary>
    private static string DescribeCommands(TaskDefinition task)
    {
        var commands = task.Commands;
        if (commands is null || commands.Count == 0)
        {
            return "（尚无命令）";
        }

        return string.Join("　|　", commands.Select(command => command.DisplayName));
    }

    private void Raise(string propertyName)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
}
