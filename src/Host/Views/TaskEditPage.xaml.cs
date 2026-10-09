using System.Collections.ObjectModel;
using Core.Keys;
using Core.Model;
using Core.Validation;
using Host.Engine;
using Host.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WpfKey = System.Windows.Input.Key;

namespace Host.Views;

/// <summary>
/// 任务编辑页（设计文档 §6.2、§6.5.2）。
///
/// <para>
/// <b>草稿语义</b>：本页编辑的是 <see cref="TaskDraft"/> —— 一份从任务快照复制出来的
/// 副本，只有点「保存」才经校验器提交给门面。"取消"或直接离开页面都等于丢弃草稿，
/// 不留任何痕迹。这与列表页"改一下就立即生效"形成刻意的对照（§6.2），
/// 因为这里编辑的是一整份互相牵连的配置（名称 + 开关 + N 条命令），
/// 半截状态提交出去只会产生非法任务。
/// </para>
/// <para>
/// <b>固定取值一律用下拉/复选/数字框</b>（FR-UI-04 / AC-20）：
/// 类型、点按方式、鼠标按键、滚动方向是<b>下拉</b>；组合键修饰是<b>复选</b>；
/// 频率、坐标、格数是<b>数字框</b>。全页只有「任务名称」是自由文本。
/// </para>
/// </summary>
public sealed partial class TaskEditPage : Page
{
    /// <summary>组合根。由导航参数取出，早于导航不可用。</summary>
    private HostFacade _facade = null!;

    /// <summary>本次编辑的草稿。导航进入时建立，保存/取消后失效。</summary>
    private TaskDraft _draft = null!;

    /// <summary>命令行数据源（<c>ItemsControl</c> 绑定它，增删就地刷新）。</summary>
    private readonly ObservableCollection<CommandRow> _commands = new();

    /// <summary>捕获进行中的标志：防止连点两次「捕获按键」叠出两个等待者。</summary>
    private bool _capturing;

    /// <summary>
    /// 弹窗串行闸：同一 <c>XamlRoot</c> 只允许一个 <see cref="ContentDialog"/> 在屏，
    /// 并发 <c>ShowAsync()</c> 会抛 <c>COMException (0x80000019)</c>。
    ///
    /// <para>
    /// 本页唯一的弹窗点是 <see cref="ShowMessage"/>，但它可被多个异步流程并发触发
    /// （捕获失败、保存被拒）。经 <see cref="ShowDialogAsync"/> 串行化后，
    /// 后来的提示排队而非抛异常。锁只包住 <c>ShowAsync</c>，不包业务逻辑。
    /// </para>
    /// </summary>
    private readonly SemaphoreSlim _dialogGate = new(1, 1);

    /// <summary>构造编辑页。</summary>
    public TaskEditPage()
    {
        InitializeComponent();

        CommandList.ItemsSource = _commands;
        AddCommandButton.Click += OnAddCommandClick;
        SaveTaskButton.Click += OnSaveClick;
        CancelTaskButton.Click += OnCancelClick;
        CaptureToggleKeyButton.Click += OnCaptureToggleKeyClick;
        ResetToggleKeyButton.Click += OnResetToggleKeyClick;
    }

    /// <inheritdoc/>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not TaskEditArgs args)
        {
            // 导航调用方写错了。不静默兜底：本页没有可用数据，让它在最显眼处失败。
            throw new InvalidOperationException(
                $"{nameof(TaskEditPage)} 必须携带 {nameof(TaskEditArgs)} 导航进入。");
        }

        _facade = args.Facade;
        _draft = args.TaskId is { } id ? LoadExisting(id) : TaskDraft.CreateNew();

        HeaderText.Text = _draft.IsNew ? "新建任务" : "编辑任务";
        NameBox.Text = _draft.Name;
        EnabledSwitch.IsOn = _draft.Enabled;
        ConsumeToggleInputCheck.IsChecked = _draft.ConsumeToggleInput;
        ShowToggleKey();

        _commands.Clear();
        foreach (var row in _draft.Commands)
        {
            _commands.Add(row);
        }

        RefreshCommandCount();
    }

    /// <summary>
    /// 按 Id 取任务快照建草稿。任务在此期间被别处删掉时以新建兜底，
    /// 而不是抛异常——用户看到的是"进入编辑页接着编"，不会因一次删改丢掉手上的编辑。
    /// </summary>
    private TaskDraft LoadExisting(Guid id)
    {
        var source = _facade.Tasks.Snapshot().FirstOrDefault(task => task.Id == id);
        return source is null ? TaskDraft.CreateNew() : TaskDraft.FromDefinition(source);
    }

    // ── 命令列表 ──────────────────────────────────────────────────

    private void OnAddCommandClick(object sender, RoutedEventArgs e)
    {
        _commands.Add(CommandRow.FromDefinition(null));
        RefreshCommandCount();
    }

    private void OnDeleteCommandClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CommandRow row })
        {
            _commands.Remove(row);
            RefreshCommandCount();
        }
    }

    /// <summary>
    /// 命令为空时给一句提示。不禁止删除最后一条——那会让用户"想换一条命令"时
    /// 被迫先加后删；空列表在保存时由校验器报出来，比在界面上拦着更透明。
    /// </summary>
    private void RefreshCommandCount()
        => EmptyCommandHint.Visibility = _commands.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ── 开关按键捕获 ──────────────────────────────────────────────

    /// <summary>
    /// 捕获开关键：进入钩子录制态，在后台线等一个按键事件，回到 UI 线写入草稿。
    ///
    /// <para>
    /// 等待必须离开 UI 线程（<c>WaitForCapture</c> 是同步阻塞），
    /// 否则界面会在整个捕获期间冻住。
    /// </para>
    /// <para>
    /// <b>页面可能在等待期间被导航走</b>（用户按了取消、或去了别的页），
    /// 等后续 <c>await</c> 回来时本页已卸载——回写前先看
    /// <see cref="Microsoft.UI.Xaml.FrameworkElement.IsLoaded"/>，卸载了就整段丢弃；
    /// 提示走 <see cref="ShowMessage"/> 自己的 <c>XamlRoot</c> 空检查。
    /// </para>
    /// </summary>
    private async void OnCaptureToggleKeyClick(object sender, RoutedEventArgs e)
    {
        if (_capturing)
        {
            return;
        }

        if (!_facade.Hooks.BeginCapture())
        {
            await ShowMessage("无法捕获", "输入钩子当前不可用（可能尚未启动）。");
            return;
        }

        _capturing = true;
        CaptureToggleKeyButton.IsEnabled = false;
        ToggleKeyRing.IsActive = true;

        var captured = await Task.Run(() => _facade.Hooks.WaitForCapture(5000));

        // 页面已被导航走：控件都不在树上了，回写与提示均无意义（且提示会抛）。
        if (!IsLoaded)
        {
            return;
        }

        if (captured is not { } keyEvent)
        {
            // 超时或被取消——两者对用户是同一件事：没抓到，请重试。
            _capturing = false;
            CaptureToggleKeyButton.IsEnabled = true;
            ToggleKeyRing.IsActive = false;
            await ShowMessage("未捕获到按键", "等待超时（5 秒）。请重新点击「捕获按键」。");
            return;
        }

        _capturing = false;
        CaptureToggleKeyButton.IsEnabled = true;
        ToggleKeyRing.IsActive = false;

        _draft.ToggleKey = BuildBinding(keyEvent);
        ShowToggleKey();

        // 单键开关键（无修饰）会在运行时拦截该键的所有按下——§5.2 要求就此提示。
        // 非阻断：单键是合法配置，提示不得把用户按在原地，也不得阻保存。
        //
        // **鼠标键除外**：交互拦截只由键盘钩子实施（§3.4 组合命中判定亦仅键盘），
        // 鼠标键的原始输入不经过它，故"会被拦截"对鼠标键不成立；
        // 而钩子侧对鼠标不维护修饰键族集合（ModifierFamilies 恒为空，见 BuildBinding），
        // 不加这道守卫的话，任何一个鼠标单键都会命中下面这个分支、弹出与事实不符的提示。
        if (keyEvent.ModifierFamilies.Length == 0 && keyEvent.PrimaryKey.Mouse is null)
        {
            BareKeyHintBar.Message =
                $"「{KeyCatalog.Describe(_draft.ToggleKey)}」不含修饰键，"
                + "任务运行时该键的原始输入将被拦截；建议改配一个组合键。";
            BareKeyHintBar.IsOpen = true;
        }
        else
        {
            // 改配了组合键，原先的裸键提示就作废了。
            BareKeyHintBar.IsOpen = false;
        }
    }

    private void OnResetToggleKeyClick(object sender, RoutedEventArgs e)
    {
        // 取消可能仍在进行的捕获，避免它稍后回写把这次重置覆盖掉。
        _facade.Hooks.CancelCapture();

        // 提示只在**内容被改写**时才动它：开关键本来就是空的，这次点击什么也没改，
        // 用户可能正因为提示文本很长而滚上去还没读完，不能顺手把它抹掉。
        if (_draft.ToggleKey is not null)
        {
            BareKeyHintBar.IsOpen = false;
        }

        _draft.ToggleKey = null;
        ShowToggleKey();
    }

    /// <summary>把钩子给出的捕获事件折算成绑定（修饰族固定序：Ctrl → Alt → Shift → Win）。</summary>
    private static KeyBinding BuildBinding(HookService.CapturedKeyEvent keyEvent)
    {
        var modifiers = new List<WpfKey>();
        foreach (var family in keyEvent.ModifierFamilies)
        {
            foreach (var modifier in KeyCatalog.KeysOfFamily(family))
            {
                modifiers.Add(modifier);
                break; // 每族取一个代表键即可（修饰键按族归一，见 KeyCatalog）。
            }
        }

        // 鼠标键：组合形态仅键盘（§3.4 组合命中判定），故鼠标键不参与组合；
        // 钩子侧对鼠标也不维护修饰键族集合（HookService 中鼠标版 ModifierFamilies 恒为空）。
        // 因此直接按"鼠标单键"形态绑定。
        // 这里**不能**走 KeyboardVirtualKey：鼠标键的该字段为 null，取它就是崩。
        if (keyEvent.PrimaryKey.Mouse is { } mouseKey)
        {
            return KeyBinding.FromMouse(mouseKey);
        }

        var mainKey = keyEvent.PrimaryKey.KeyboardVirtualKey
            ?? throw new InvalidOperationException(
                "捕获到的开关键既不是键盘键也不是鼠标键，无法构造绑定。");
        return modifiers.Count == 0
            ? KeyBinding.FromKey(mainKey)
            : KeyBinding.FromCombo(modifiers, mainKey);
    }

    private void ShowToggleKey() => ToggleKeyBox.Text = KeyCatalog.Describe(_draft.ToggleKey);

    // ── 保存 / 取消 ───────────────────────────────────────────────

    /// <summary>
    /// 保存：把界面上的当前值收进草稿，折算成领域对象，过校验器后提交。
    ///
    /// <para>
    /// 名称与启用开关是即时读控件（它们不是命令行那样的可绑定对象），
    /// 命令行的每一行则早已双向绑定在自己的 <see cref="CommandRow"/> 上。
    /// 校验不通过时**不提交任何东西**，把问题原文摆给用户——
    /// 半截保存比不保存更难排查。
    /// </para>
    /// </summary>
    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        _draft.Name = NameBox.Text?.Trim() ?? string.Empty;
        _draft.Enabled = EnabledSwitch.IsOn;
        _draft.ConsumeToggleInput = ConsumeToggleInputCheck.IsChecked == true;

        _draft.Commands.Clear();
        _draft.Commands.AddRange(_commands);

        var task = _draft.ToDefinition();
        var result = _draft.IsNew ? _facade.AddTask(task) : _facade.UpdateTask(task);
        if (!result.IsValid)
        {
            await ShowMessage("无法保存", result.Summary);
            return;
        }

        // 新任务被门面分配了 Id，草稿就此作废——返回列表页（那里会重读全部任务）。
        GoBack();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => GoBack();

    /// <summary>离开前取消可能仍在进行的捕获，避免钩子的录制态挂着没人收。</summary>
    private void GoBack()
    {
        _facade.Hooks.CancelCapture();

        Navigator.ResolveFor(Frame).GoBackToTaskList();
    }

    // ── 工具 ──────────────────────────────────────────────────────

    /// <summary>
    /// 阻断式提示：编辑页的保存失败必须停在原地让用户读完再改。
    ///
    /// <para>
    /// <b>页面已卸载时直接返回</b>：本页的提示全部发生在 <c>await</c> 之后
    /// （后台等捕获、保存回写），此时用户可能已经保存/取消导航走了。页面一离开
    /// 视觉树，<c>XamlRoot</c> 就变成 <c>null</c>，再拿它构造 <c>ContentDialog</c>
    /// 会抛异常——而调用方是 <c>async void</c>，异常直接成为进程级未处理异常。
    /// 没人再看这一页，静默丢弃正是正确的。
    /// </para>
    /// <para>
    /// 经 <see cref="ShowDialogAsync"/> 串行化：本页提示可由多条异步流程并发触发
    /// （捕获失败、保存被拒），同 <c>XamlRoot</c> 不允许两个 <c>ContentDialog</c> 在屏。
    /// </para>
    /// </summary>
    private async Task ShowMessage(string title, string? message)
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

        await ShowDialogAsync(dialog);
    }

    /// <summary>
    /// 弹窗统一入口：本页所有 <c>ContentDialog.ShowAsync()</c> 的唯一调用点。
    /// 用 <see cref="_dialogGate"/> 把并发弹窗串行化（同 <c>XamlRoot</c> 不允许两个在屏）。
    /// 锁只包住 <c>ShowAsync</c>；<c>finally</c> 保证信号量归还，不会把页面锁死。
    /// </summary>
    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        await _dialogGate.WaitAsync();
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            _dialogGate.Release();
        }
    }
}
