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
            // 关键：录制态就此作废，否则下一次捕获会与这次残留的状态串在一起。
            _facade.Hooks.CancelCapture();
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
        // 而钩子侧对鼠标不维护修饰键族集合（见 BuildBinding），
        // 不加这道守卫的话，任何一个鼠标单键都会命中下面这个分支、弹出与事实不符的提示。
        if (keyEvent.Keys.Length < 2 && PickMainKey(keyEvent.Keys).Mouse is null)
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

    /// <summary>
    /// 把钩子给出的按下集合折算成绑定。
    ///
    /// <para>
    /// <b>与设置页的 <c>BuildBinding</c> 是逐字副本</b>：两页的录制语义必须一致——
    /// 同一组按键在任务开关键与紧急停止键那里得不到同一个绑定，是用户无法理解的分歧。
    /// 改一必改二。
    /// </para>
    /// <para>
    /// 判定口径（设计文档 §5.2「一次捕获会话 → 一个绑定」）：集合里**第一个修饰键以外的键当主键**，
    /// **所有**修饰键（含主键所在族）进修饰集；一个非修饰键都没有时（用户就录了 `Ctrl+Shift`），
    /// 取固定族序里第一个命中的修饰键当主键，其余修饰键进修饰集。
    /// </para>
    /// </summary>
    private static KeyBinding BuildBinding(HookService.CapturedKeyEvent keyEvent)
    {
        // 先挑主键（口径见 PickMainKey）。与用户按下顺序无关——顺序无意义，绑定语义是「同时按」；
        // 遍历序固定（族序 Ctrl→Alt→Shift→Win，集合内按录制顺序），故同一组键每次折出的绑定相同。
        var mainKey = PickMainKey(keyEvent.Keys);

        // 鼠标键：组合形态仅键盘（§3.4 组合命中判定），故鼠标键不参与组合；
        // 钩子侧对鼠标也不维护修饰键族集合。因此直接按「鼠标单键」形态绑定。
        // 这里**不能**走 KeyboardVirtualKey：鼠标键的该字段为 null，取它就是崩。
        if (mainKey.Mouse is { } mouseKey)
        {
            return KeyBinding.FromMouse(mouseKey);
        }

        var virtualKey = mainKey.KeyboardVirtualKey
            ?? throw new InvalidOperationException(
                "捕获到的开关键既不是键盘键也不是鼠标键，无法构造绑定。");

        // 单键形态走这条："其余键"为空，修饰集自然是空的——**不需要**为它单开一条分支。
        // （旧实现另设 `Keys.Length < 2` 短路，正是因为那时口径反过来、"其余键"里装的是非修饰键。）
        var modifiers = CollectModifiers(keyEvent.Keys);

        // 无修饰时走 FromKey 而非"空修饰集的 FromCombo"：两者在绑定模型里
        // 是**两种形态**——单键形态与组合形态，下游（§3.4.1 的 IsCombo）据此分派。
        return modifiers.Count == 0
            ? KeyBinding.FromKey(virtualKey)
            : KeyBinding.FromCombo(modifiers, virtualKey);
    }

    /// <summary>修饰键族的固定序（与设计文档 §3.4 的组合命中顺序一致）。</summary>
    private static readonly string[] FamilyOrder = { "Ctrl", "Alt", "Shift", "Win" };

    /// <summary>
    /// 从捕获到的按下集合里挑主键：**第一个非修饰键**（按集合内的录制顺序）；
    /// 一个都没有时（用户就录了若干修饰键，如 `Ctrl+Shift`）取固定族序
    /// （Ctrl→Alt→Shift→Win）里第一个命中的修饰键**族代表键**。
    ///
    /// <para>
    /// <b>为什么主键取非修饰键</b>：主键是「这个绑定是哪一个键」的答案，
    /// 而修饰键是**以修饰身份**参与绑定的（进修饰集）。若把修饰键当主键，
    /// 被让位的那个真实按键反而要进修饰集——下游的修饰集只认修饰键族，
    /// 非修饰键会被整个丢掉（`TaskValidator.ModifierFamilies` / `ToggleSnapshot` 的族归一），
    /// 于是 `Ctrl+F8` 退化成裸 `Ctrl`。
    /// </para>
    /// <para>
    /// <b>修饰键当主键时为什么必须取族代表键</b>：主键是下游判等的键——
    /// 快照的 <c>ToggleSnapshot.Normalize</c> 与任务校验的 <c>ModifierFamilies</c>
    /// 都按**族代表键**归一（左右 Ctrl 视为同族）。若拿用户实按的那把（可能是右 Ctrl）当主键，
    /// 与族代表键比对会**查不中**，于是"钩子命中了、引擎却找不到任务"。
    /// 取族代表键即与归一口径一致。
    /// </para>
    /// <para>
    /// <b>派生形态对照</b>：<c>Ctrl+F8</c> → 主键 F8、修饰集 <c>[Ctrl]</c>；
    /// <c>Ctrl+Shift</c> → 主键 Ctrl、修饰集 <c>[Ctrl, Shift]</c>（见 <see cref="CollectModifiers"/>）；
    /// <c>F6</c> → 主键 F6、修饰集空（单键形态）。
    /// </para>
    /// </summary>
    private static PhysicalKey PickMainKey(IReadOnlyList<PhysicalKey> keys)
    {
        // 单趟扫出「第一个非修饰键」与「固定族序里第一个命中的修饰键代表键」。
        PhysicalKey? firstModifier = null;
        int firstModifierRank = int.MaxValue;

        foreach (var pressed in keys)
        {
            if (pressed.KeyboardVirtualKey is not { } virtualKey)
            {
                // 鼠标键没有修饰键族概念，天然是非修饰键——但也当不了组合的主键
                // （组合形态仅键盘，§3.4）。它只可能出现在单键捕获里，
                // 那种情形由 BuildBinding 的鼠标分支接手，走不到这里的主键语义。
                continue;
            }

            if (KeyCatalog.ModifierFamily(virtualKey) is not { } family)
            {
                return pressed;
            }

            var rank = System.Array.IndexOf(FamilyOrder, family);
            if (rank < firstModifierRank)
            {
                firstModifier = pressed;
                firstModifierRank = rank;
            }
        }

        if (firstModifier is not null)
        {
            // 手写分支（不用 `is { } x`）：`PhysicalKey` 是 struct，
            // 模式变量对可空值取的是**内部值**而非包好的可空量，`'return vk;` 会退回 WpfKey。
            return firstModifier.Value;
        }

        // 空集合不得取下标：钩子侧已经拦掉「只见到松开、没见到按下」的半截事件，
        // 但这里再采一道——BuildBinding 的调用方是 async void，一旦越界异常逃逸
        // 就是整个提权进程崩溃，不能把这条命根全押在上游。
        if (keys.Count == 0)
        {
            throw new InvalidOperationException("捕获结果为空，无法折出主键。");
        }

        // 只剩鼠标键（BuildBinding 的鼠标分支已先行接管，正常不可达）。
        return keys[0];
    }

    /// <summary>
    /// 把按下集合里的修饰键折成绑定用的修饰键列表：每族**至多一个代表键**
    /// （左右归一，见 KeyCatalog），按固定族序排列。
    ///
    /// <para>
    /// <b>主键所在的那个族也在内</b>：主键是修饰键时（用户只录了修饰键），
    /// 它同时是「按下的是哪个键」与「必须按着哪个修饰键」——这是同一件事的两个说法，
    /// 少写一半绑定就残了（`Ctrl+Shift` 会退化成裸 `Ctrl`）。
    /// 主键是非修饰键时它不在任何修饰键族里，本分支自然不命中。
    /// </para>
    /// <para>
    /// <b>非修饰键一律不进修饰集</b>：修饰集的每一项在命中判定（§3.4.1）与校验判等
    /// （`TaskValidator.ModifierFamilies`）里都被当作**修饰键族**归一——
    /// 塞进一个非修饰键（如 `Ctrl+F8` 里的 F8），它会在比对时被整个丢掉，
    /// 于是「按 Ctrl+F8」与「按 Ctrl」再也分不开。
    /// </para>
    /// </summary>
    private static List<WpfKey> CollectModifiers(IReadOnlyList<PhysicalKey> keys)
    {
        var modifiers = new List<WpfKey>();
        foreach (var family in FamilyOrder)
        {
            foreach (var pressed in keys)
            {
                if (pressed.KeyboardVirtualKey is not { } virtualKey)
                {
                    continue;
                }

                if (KeyCatalog.ModifierFamily(virtualKey) != family)
                {
                    continue;
                }

                modifiers.Add(KeyCatalog.RepresentativeOfFamily(virtualKey));
                break; // 每族一个代表键（修饰键按族归一）。
            }
        }

        return modifiers;
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
