using Core.Config;
using Core.Keys;
using Host.Engine;
using Host.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WpfKey = System.Windows.Input.Key;

namespace Host.Views;

/// <summary>
/// 设置页（设计文档 §6.5.5）。
///
/// <para>
/// <b>为什么持有副本</b>：<c>HostFacade.CurrentSettings</c> 每次返回的都是**同一个实例**
/// （<c>Settings.Value</c>），直接改它等于在用户点「保存」之前就把生效配置改了。
/// 本页因此在导航进入时复制一份自持，只有保存那一步才把它交给门面下盘。
/// </para>
/// <para>
/// <b>本页没有"退出程序"按钮</b>（§6.5.5）：退出走托盘菜单，
/// 设置的职责只是改配置，不承担进程生命周期。
/// </para>
/// </summary>
public sealed partial class SettingsPage : Page
{
    /// <summary>组合根。由导航参数取出（裸 <see cref="HostFacade"/>），早于导航不可用。</summary>
    private HostFacade _facade = null!;

    /// <summary>本页自持的设置副本（见类型注释：绝不能直接改门面里的那一份）。</summary>
    private AppSettings _settings = new();

    /// <summary>捕获进行中的标志：防止连点两次「捕获按键」叠出两个等待者。</summary>
    private bool _capturing;

    /// <summary>
    /// 弹窗串行闸：同一 <c>XamlRoot</c> 只允许一个 <see cref="ContentDialog"/> 在屏，
    /// 并发 <c>ShowAsync()</c> 会抛 <c>COMException (0x80000019)</c>。
    /// <para>
    /// 本页唯一的弹窗点是 <see cref="ShowMessage"/>，但捕获失败（钩子不可用 / 超时 /
    /// 按键被占）与保存被拒可能并发触发。经 <see cref="ShowDialogAsync"/> 串行化后，
    /// 后来的提示排队而非抛异常。
    /// </para>
    /// </summary>
    private readonly SemaphoreSlim _dialogGate = new(1, 1);

    /// <summary>构造设置页。</summary>
    public SettingsPage()
    {
        InitializeComponent();

        CaptureEmergencyStopButton.Click += OnCaptureClick;
        ResetEmergencyStopButton.Click += OnResetClick;
        SaveSettingsButton.Click += OnSaveClick;
        CancelSettingsButton.Click += OnCancelClick;
    }

    /// <inheritdoc/>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not HostFacade facade)
        {
            // 导航调用方写错了。不静默兜底。
            throw new InvalidOperationException(
                $"{nameof(SettingsPage)} 必须携带 HostFacade 导航进入。");
        }

        _facade = facade;

        _settings = CopyOf(facade.CurrentSettings);
        ShowEmergencyStopKey();
    }

    /// <summary>
    /// 复制一份设置。逐字段复制而不是序列化往返：字段少、语义清晰，
    /// 也不会把"保存失败"这种运行态意外卷进副本。
    /// </summary>
    private static AppSettings CopyOf(AppSettings source) => new()
    {
        EmergencyStopKey = source.EmergencyStopKey,
        SchemaVersion = source.SchemaVersion,
    };

    // ── 紧急停止键 ────────────────────────────────────────────────

    /// <summary>
    /// 捕获紧急停止键：进钩子录制态、后台线等待、回 UI 线写入副本。
    ///
    /// <para>
    /// <b>"注册即注销"探测</b>：紧急停止键本身由低级键盘钩子监听生效（设计文档 §3.2），
    /// <b>不依赖</b>系统级热键注册。这里试注册一次再立刻注销，是为了回答另一个问题——
    /// **这个键有没有被别的程序用系统热键占着**，好让用户尽早知道，
    /// 而不是等到真要紧急停止时才发现该组合已被别的软件截获。
    /// 探测失败只提示、不阻止保存：占用是运行环境的事，用户可能就是要先存下来、
    /// 回头关掉占用的程序。
    /// </para>
    /// </summary>
    private async void OnCaptureClick(object sender, RoutedEventArgs e)
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
        CaptureEmergencyStopButton.IsEnabled = false;
        CaptureRing.IsActive = true;

        var captured = await Task.Run(() => _facade.Hooks.WaitForCapture(5000));

        // 页面已在等待期间被导航走：控件不在树上，回写与提示均无意义（且提示会抛）。
        if (!IsLoaded)
        {
            return;
        }

        _capturing = false;
        CaptureEmergencyStopButton.IsEnabled = true;
        CaptureRing.IsActive = false;

        if (captured is not { } keyEvent)
        {
            // 超时取消 / 用户中途改主意。等下一次重新捕获时兜底，见设计文档 §5.2 的键集合纪律。
            _facade.Hooks.CancelCapture();
            await ShowMessage("未捕获到按键", "等待超时（5 秒）。请重新点击「捕获按键」。");
            return;
        }

        _settings.EmergencyStopKey = BuildBinding(keyEvent);
        ShowEmergencyStopKey();

        // 探测结果一律告知（设计文档 §6.1）：失败说明被别的进程占着，成功也不能当作没事——
        // 本工具的紧急停止**用低级钩子监听**而非 RegisterHotKey（§3.2），所以即使这个组合
        // 空闲，它仍可能与系统快捷键语义重叠（如 Win+L 锁屏），届时两边都会响应。
        // 探测失败只提示、不拦保存：占用是运行环境的事，用户可能就是要先存下来、回头再改。
        var probeOk = HotKeyProbe.TryProbe(_settings.EmergencyStopKey, out var failureMessage);
        if (probeOk)
        {
            await ShowMessage(
                "该键可用",
                "该键当前未被其他程序占用。\n\n注意：本工具的紧急停止通过全局键盘钩子监听，"
                    + "因此该组合即使无人注册，仍可能与系统快捷键语义重叠（例如 Win 系组合），"
                    + "届时两边都会响应。");
        }
        else
        {
            await ShowMessage("该键可能不可用", failureMessage);
        }
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        _facade.Hooks.CancelCapture();
        _settings.EmergencyStopKey = new AppSettings().EmergencyStopKey;
        ShowEmergencyStopKey();
    }

    /// <summary>
    /// 把钩子给出的按下集合折算成绑定。
    ///
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
                "捕获到的紧急停止键既不是键盘键也不是鼠标键，无法构造绑定。");

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

    private void ShowEmergencyStopKey()
        => EmergencyStopBox.Text = KeyCatalog.Describe(_settings.EmergencyStopKey);

    // ── 保存 / 取消 ───────────────────────────────────────────────

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        // 落盘 + 应急停止键生效（含）由门面一次做完（§3.5.4）：
        // SaveSettings 成功时会在内部重建并重推快照，本页**不必**也不能再推一次——
        // 快照构建依赖任务列表与设置的全局视图，那是 Engine 的职责，
        // UI 重推会多走一次构建、且容易与门面内部的状态脱节（§6.5.1 单向推送）。
        var result = _facade.SaveSettings(_settings);
        if (!result.Succeeded)
        {
            await ShowMessage("无法保存", result.Message);
            return;
        }

        GoBack();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => GoBack();

    private void GoBack()
    {
        _facade.Hooks.CancelCapture();

        Navigator.ResolveFor(Frame).GoBackToTaskList();
    }

    // ── 工具 ──────────────────────────────────────────────────────

    /// <summary>
    /// 阻断式提示：保存失败必须停在原地让用户读完再改。
    ///
    /// <para>
    /// <b>页面已卸载时直接返回</b>：本页的提示都发生在 <c>await</c> 之后（后台等捕获、保存回写），
    /// 此时用户可能已经保存/取消导航走了。页面一离开视觉树，<c>XamlRoot</c> 就变成 <c>null</c>，
    /// 再拿它构造 <c>ContentDialog</c> 会抛异常——而调用方是 <c>async void</c>，
    /// 异常直接成为进程级未处理异常。没人再看这一页，静默丢弃正是正确的。
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
