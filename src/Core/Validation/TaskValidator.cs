using Core.Keys;
using Core.Model;
using Core.Scheduling;

namespace Core.Validation;

/// <summary>
/// 校验结果：全部问题清单 + 一票否决判定（设计文档 §5.3）。
///
/// "拒绝保存"的语义是**全通过才落盘**，但界面还需要**一次看全**所有问题——
/// 逐条报错会让用户改一条、存一次、再被打回，体验很差。
/// 故本类型同时携带"全部问题"与"是否通过"，而不是遇到第一个问题就抛出。
/// </summary>
/// <param name="Issues">命中的全部问题；为空表示通过。</param>
public sealed record ValidationResult(IReadOnlyList<ValidationIssue> Issues)
{
    /// <summary>校验通过（可以落盘）。</summary>
    public bool IsValid => Issues.Count == 0;

    /// <summary>通过的空结果（用于短路与默认值）。</summary>
    public static ValidationResult Valid { get; } = new(Array.Empty<ValidationIssue>());

    /// <summary>
    /// 面向用户的汇总提示：至少包含首条问题的正文。
    /// 多问题时逐行列出（界面提示框按行展示）。
    /// </summary>
    public string Summary
        => Issues.Count switch
        {
            0 => "校验通过。",
            1 => Issues[0].Message,
            _ => string.Join(Environment.NewLine, Issues.Select(i => "· " + i.Message)),
        };
}

/// <summary>
/// 任务校验器——保存时的**集中校验点**（设计文档 §5.3，需求 FR 多条"拒绝保存"）。
///
/// 规则集中在这一个模块，而不是散落到界面各处：
/// 界面只负责"把当前编辑中的对象交给它、按结果提示"，不自己判规则。
/// 这样规则可以被单测逐条穷举（AC-05 / AC-05a / AC-14 / AC-28 / AC-31 等），
/// 也不会出现"编辑页拦住了、复制任务绕过去了"这类漏网（FR-TASK-04 明确
/// 副本必须重新过一遍全校验）。
///
/// **本类不抛异常**：校验的对象来自用户界面与配置文件，都可能畸形
/// （AC-21 要求投毒文件不崩溃）。畸形一律转化为"校验不通过"的问题项。
/// </summary>
public static class TaskValidator
{
    /// <summary>
    /// 滚轮单次格数上限。取 100 是工程取值：
    /// 一次滚 100 格以上在实用中毫无意义（且高频下会把前台程序淹掉），
    /// 需要更大滚动量应提高频率而不是把单次格数调到荒谬的数值。
    /// </summary>
    public const int MaxScrollNotches = 100;

    /// <summary>
    /// 全校验（设计文档 §5.3 七条 + 领域形状规则）。
    /// </summary>
    /// <param name="candidate">待保存的任务（新建或编辑后的对象）。</param>
    /// <param name="existing">
    /// 当前列表中的**其他**任务。可以包含 <paramref name="candidate"/> 自己
    /// （按 <see cref="TaskDefinition.Id"/> 排除），传"整个列表"最省心也最不容易漏。
    /// </param>
    /// <param name="emergencyStopKey">
    /// 紧急停止快捷键的绑定形态（FR-RUN-02 有默认值，故非空）。
    /// 与任何任务开关按键冲突都拒绝（FR-RUN-03）。
    /// </param>
    public static ValidationResult Validate(
        TaskDefinition? candidate,
        IEnumerable<TaskDefinition>? existing,
        KeyBinding emergencyStopKey)
    {
        var issues = new List<ValidationIssue>();

        if (candidate is null)
        {
            // 不可达的防御：没有待保存对象就无从校验。
            // 报"命令集合为空"而不是抛异常——界面上等价于"什么都没有，不能保存"。
            issues.Add(new ValidationIssue(ValidationRule.CommandsNotEmpty, "没有可保存的任务。"));
            return new ValidationResult(issues);
        }

        var others = (existing ?? Array.Empty<TaskDefinition>())
            .Where(t => t is not null && t.Id != candidate.Id)
            .ToList();

        ValidateName(candidate, others, issues);
        ValidateToggleKeyRequired(candidate, issues);
        ValidateToggleKeyTaken(candidate, others, issues);
        ValidateEmergencyStopConflict(candidate, emergencyStopKey, issues);
        ValidateCommands(candidate, issues);

        return new ValidationResult(issues);
    }

    /// <summary>校验任务名：非空且在其他任务中唯一（FR-TASK-06）。</summary>
    private static void ValidateName(
        TaskDefinition candidate,
        IReadOnlyList<TaskDefinition> others,
        List<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(candidate.Name))
        {
            issues.Add(new ValidationIssue(
                ValidationRule.TaskNameRequiredAndUnique,
                "任务名不能为空，请填写一个名称。"));
            return;
        }

        // 首尾空白不参与唯一性判定："任务 A" 与 "任务 A " 在界面上
        // 看起来完全一样，判为不同会让用户怎么也找不出重名在哪里。
        var normalized = candidate.Name.Trim();
        var duplicated = others.Any(o =>
            !string.IsNullOrWhiteSpace(o.Name)
            && string.Equals(o.Name.Trim(), normalized, StringComparison.OrdinalIgnoreCase));

        if (duplicated)
        {
            issues.Add(new ValidationIssue(
                ValidationRule.TaskNameRequiredAndUnique,
                $"任务名“{normalized}”已被其他任务使用，请换一个名称。"));
        }
    }

    /// <summary>校验开关按键必填（FR-TOGGLE-01）。</summary>
    private static void ValidateToggleKeyRequired(
        TaskDefinition candidate,
        List<ValidationIssue> issues)
    {
        // 开关按键是任务的启用入口（FR-TOGGLE-01）：缺失即无从启用。
        // 界面上正常选不出来，但投毒文件中 "toggleKey": null 是合法 JSON，
        // 而 LoadTasks 不做归一——不补这条会让一个无法启用的任务通过校验。
        if (candidate.ToggleKey is null)
        {
            issues.Add(new ValidationIssue(
                ValidationRule.ToggleKeyRequired,
                "尚未指定开关按键，请为任务选择一个开关按键。"));
        }
    }

    /// <summary>
    /// 校验开关按键的**主键**未被其他任务占用（FR-TOGGLE-05, AC-05）。
    ///
    /// 判据是**主键**而不是完整绑定：一个主键至多服务一个任务，
    /// 配 <c>F8</c> 与 <c>Ctrl+F8</c> 与 <c>Alt+F8</c> 三者互相冲突。
    /// 理由有二：
    ///   1. 主键相同即会在同一次点按里命中两条条目，同一次输入启停两个任务，用户无法预期；
    ///   2. 修饰键按下状态的漏同步（如钩子重装窗口内错过的 key-up）会直接把
    ///      本应“需要 Ctrl”的组合降级为裸键命中——留一条同主键的合法绑定
    ///      等于给这个漏洞留了一个可踩的入口。
    /// 驳回“完整绑定判等”（`Ctrl+F8` 与 `Alt+F8` 共存）的理由即第 2 条：
    /// 漏同步后的 `Alt+F8` 与无修饰键的 `F8` 无法区分。
    /// </summary>
    private static void ValidateToggleKeyTaken(
        TaskDefinition candidate,
        IReadOnlyList<TaskDefinition> others,
        List<ValidationIssue> issues)
    {
        var taken = others.FirstOrDefault(o => IsSameToggleKey(o.ToggleKey, candidate.ToggleKey));
        if (taken is null)
        {
            return;
        }

        issues.Add(new ValidationIssue(
            ValidationRule.ToggleKeyNotTaken,
            $"开关按键 {DescribeBindingForMessage(candidate.ToggleKey)} 与任务“{taken.DisplayName}”的开关按键 {DescribeBindingForMessage(taken.ToggleKey)} 使用了同一个主键，请为当前任务指定其他按键。"));
    }

    /// <summary>
    /// 报错文本里的绑定描述——绑定可能为 null（投毒文件/外部构造），
    /// 描述文本不该成为崩溃点（AC-21）。
    /// </summary>
    private static string DescribeBindingForMessage(KeyBinding? binding)
        => binding is null ? "（未设置）" : KeyCatalog.Describe(binding);


    /// <summary>校验紧急停止快捷键不与任何任务开关按键冲突（FR-RUN-03, AC-15）。</summary>
    private static void ValidateEmergencyStopConflict(
        TaskDefinition candidate,
        KeyBinding emergencyStopKey,
        List<ValidationIssue> issues)
    {
        if (!IsSameBinding(candidate.ToggleKey, emergencyStopKey))
        {
            return;
        }

        issues.Add(new ValidationIssue(
            ValidationRule.EmergencyStopKeyNotConflicting,
            $"开关按键 {KeyCatalog.Describe(candidate.ToggleKey)} 与紧急停止快捷键冲突，请换一个按键。"));
    }

    /// <summary>
    /// 校验命令集合：非空（FR-SEQ-08）+ 逐条领域形状 + 跨命令的按键冲突（FR-TOGGLE-07 / FR-SEQ-10）。
    /// </summary>
    private static void ValidateCommands(TaskDefinition candidate, List<ValidationIssue> issues)
    {
        var commands = candidate.Commands;
        if (commands is null || commands.Count == 0)
        {
            issues.Add(new ValidationIssue(
                ValidationRule.CommandsNotEmpty,
                "任务至少需要一条命令，否则点按开关按键不会有任何反应。"));
            return;
        }

        // 防御：列表中可能混入 null（配置文件被外部改坏时）。
        // 先剔掉再逐条校验，避免后续遍历里到处判空。
        var present = commands.Where(c => c is not null).ToList();
        if (present.Count == 0)
        {
            issues.Add(new ValidationIssue(
                ValidationRule.CommandsNotEmpty,
                "任务至少需要一条命令，否则点按开关按键不会有任何反应。"));
            return;
        }

        foreach (var command in present)
        {
            ValidateCommandShape(command, issues);
        }

        TableKeyOccupancy(present, out var hostByPhysicalKey, out var tapModesByPhysicalKey);

        ValidateToggleKeyOverlapWithOwnCommands(candidate, hostByPhysicalKey, issues);
        ValidateNoConflictingTapModes(tapModesByPhysicalKey, issues);
    }

    /// <summary>单条命令的领域形状校验（频率区间、执行形态、滚轮格数、组合键形状）。</summary>
    private static void ValidateCommandShape(CommandDefinition command, List<ValidationIssue> issues)
    {
        // 频率区间对**所有**命令生效——鼠标移动 / 滚轮同样按频率重复（FR-TAP-04）。
        if (command.Frequency < TapSchedule.MinFrequency || command.Frequency > TapSchedule.MaxFrequency)
        {
            issues.Add(new ValidationIssue(
                ValidationRule.FrequencyInRange,
                $"“{SafeDisplayName(command)}”的频率 {command.Frequency} 超出合法范围（{TapSchedule.MinFrequency}~{TapSchedule.MaxFrequency} 次/秒）。",
                command));
        }

        switch (command)
        {
            case KeyboardKeyCommand:
            case MouseButtonCommand:
                // 按键类命令必须有明确的执行形态：默认值（Tap）与用户显式选择不可区分，
                // 但 None 一定是错的——那意味着它既不会点按也不会按住，执行时无事可做。
                if (command.TapMode == TapMode.None)
                {
                    issues.Add(new ValidationIssue(
                        ValidationRule.TapModeRequiredForCommand,
                        $"“{SafeDisplayName(command)}”需要选择执行形态（点按或按住）。",
                        command));
                }

                break;

            case KeyboardComboCommand combo:
                ValidateComboShape(combo, issues);
                break;

            case MouseScrollCommand scroll:
                if (scroll.Notches < 1 || scroll.Notches > MaxScrollNotches)
                {
                    issues.Add(new ValidationIssue(
                        ValidationRule.ScrollNotchesInRange,
                        $"“{SafeDisplayName(scroll)}”的滚动格数 {scroll.Notches} 超出合法范围（1~{MaxScrollNotches} 格）。",
                        scroll));
                }

                ValidateTapModeNotApplicable(scroll, TapMode.None, issues);
                break;

            case MouseMoveCommand move:
                ValidateTapModeNotApplicable(move, TapMode.None, issues);
                break;
        }
    }

    /// <summary>
    /// 鼠标移动 / 滚轮没有"按住"的物理含义（设计文档 §5.1 标注"无 TapMode"），
    /// 其执行形态必须是 <see cref="TapMode.None"/>。
    ///
    /// 需求 FR-TAP-01 只说了"键盘/鼠标按键命令有执行形态"，未直接说另外两类必须没有；
    /// 但允许它们带着"按住"意味着运行期要定义"按住滚轮"是什么——
    /// 那是需求未定义的状态，宁可保存时拒绝。
    /// </summary>
    private static void ValidateTapModeNotApplicable(
        CommandDefinition command,
        TapMode expected,
        List<ValidationIssue> issues)
    {
        if (command.TapMode != expected)
        {
            issues.Add(new ValidationIssue(
                ValidationRule.TapModeNotApplicableForCommand,
                $"“{SafeDisplayName(command)}”不支持执行形态设置。",
                command));
        }
    }

    /// <summary>
    /// 组合键形状：至少一个修饰键，且主键本身不是修饰键（FR-CMD-K2）。
    ///
    /// 两条都是形状性的：
    ///   - 无修饰键的"组合"等于单键命令，用户该选"键盘键"类型，而不是留一个语义为空的组合；
    ///   - 主键是修饰键（如 Ctrl+Ctrl）在执行时按下两次同一个键，
    ///     松开的配对关系会变得不可判定（哪一次松开对应哪一次按下）。
    /// </summary>
    private static void ValidateComboShape(KeyboardComboCommand combo, List<ValidationIssue> issues)
    {
        if (combo.ModifierKeys is null || combo.ModifierKeys.Count == 0)
        {
            issues.Add(new ValidationIssue(
                ValidationRule.ComboShapeValid,
                $"“{SafeDisplayName(combo)}”缺少修饰键；若只想按一个键，请改用“键盘键”命令。",
                combo));
        }

        if (KeyCatalog.IsModifier(combo.VirtualKey))
        {
            issues.Add(new ValidationIssue(
                ValidationRule.ComboShapeValid,
                $"“{SafeDisplayName(combo)}”的主键不能是修饰键，请选择普通按键。",
                combo));
        }

        if (combo.TapMode == TapMode.None)
        {
            issues.Add(new ValidationIssue(
                ValidationRule.TapModeRequiredForCommand,
                $"“{SafeDisplayName(combo)}”需要选择执行形态（点按或按住）。",
                combo));
        }
    }

    /// <summary>
    /// 开关按键不得与本任务命令集合内的实体键重叠（FR-TOGGLE-07, AC-05a）。
    ///
    /// 组合键命令占用的是**多个**实体键（Ctrl+C 同时占用 Ctrl 与 C），
    /// 故这里是集合语义而非单键比对。
    /// </summary>
    private static void ValidateToggleKeyOverlapWithOwnCommands(
        TaskDefinition candidate,
        IReadOnlyDictionary<PhysicalKey, CommandDefinition> hostByPhysicalKey,
        List<ValidationIssue> issues)
    {
        var reported = new HashSet<CommandDefinition>();
        foreach (var physicalKey in BindingPhysicalKeys(candidate.ToggleKey))
        {
            // 查表前同样归一：占用表的键已在 ValidateCommands 里归一到族代表，
            // 这里若用裸 PhysicalKey 查询，开关按键 = 右 Ctrl 就查不到表里的左 Ctrl。
            if (!hostByPhysicalKey.TryGetValue(NormalizeForOccupancy(physicalKey), out var host))
            {
                continue;
            }

            // 一个绑定可占用多个实体键（Ctrl+C 开关占 Ctrl 与 C），它们可能同属一条命令。
            // 按命令去重，避免同一条冲突被不同键重复报两遍。
            if (!reported.Add(host))
            {
                continue;
            }

            issues.Add(new ValidationIssue(
                ValidationRule.ToggleKeyNotOverlappingOwnCommands,
                $"开关按键 {DescribePhysicalKey(physicalKey)} 同时被本任务的命令“{SafeDisplayName(host)}”使用，两者会互相干扰。",
                host));
        }
    }

    /// <summary>
    /// 同一实体键不得同时存在"按住"与"点按"两条命令（FR-SEQ-10, AC-31）。
    ///
    /// 判据是**形态集合的势**，而不是"两条命令"：三条同为"按住"的命令
    /// 并不自相矛盾（都是按下不松，松开一次即可），不构成拒绝理由；
    /// 而"点按"与"按住"同时存在时，前者会替后者把键松开，
    /// 后者的"按住"随即失效——这才是需求要挡的自相矛盾。
    /// </summary>
    private static void ValidateNoConflictingTapModes(
        IReadOnlyDictionary<PhysicalKey, HashSet<TapMode>> tapModesByPhysicalKey,
        List<ValidationIssue> issues)
    {
        foreach (var (physicalKey, modes) in tapModesByPhysicalKey)
        {
            if (modes.Count < 2)
            {
                continue;
            }

            issues.Add(new ValidationIssue(
                ValidationRule.NoConflictingTapModesOnSameKey,
                $"实体键 {DescribePhysicalKey(physicalKey)} 同时配置了“点按”与“按住”命令，二者会互相打断，请只保留一种。"));
        }
    }

    /// <summary>
    /// 建立"实体键 → 使用它的命令"与"实体键 → 出现过的执行形态集合"两张表。
    ///
    /// 一次遍历同时产出两张表：两条规则（FR-TOGGLE-07 / FR-SEQ-10）都基于
    /// "某实体键被哪些命令占用"，各扫一遍列表纯属重复劳动。
    ///
    /// "首发者优先"：同一实体键出现在多条命令上时保留**第一条**作为报错指认对象。
    /// 命令集合是集合语义（FR-SEQ-01 明示无顺序），报哪一条都对；
    /// 定住"第一条"只是为了让提示稳定可复现，而不是随机指认。
    /// </summary>
    private static void TableKeyOccupancy(
        IReadOnlyList<CommandDefinition> commands,
        out Dictionary<PhysicalKey, CommandDefinition> hostByPhysicalKey,
        out Dictionary<PhysicalKey, HashSet<TapMode>> tapModesByPhysicalKey)
    {
        hostByPhysicalKey = new Dictionary<PhysicalKey, CommandDefinition>();
        tapModesByPhysicalKey = new Dictionary<PhysicalKey, HashSet<TapMode>>();

        foreach (var command in commands)
        {
            var modes = TapModeOf(command);
            if (modes is null)
            {
                // 鼠标移动 / 滚轮不占用任何按键，也不参与形态冲突判定。
                continue;
            }

            foreach (var physicalKey in CommandPhysicalKeys(command))
            {
                // 归一化后再入表：左 Ctrl 与右 Ctrl 在用户眼里是同一个键
                // （同 IsSamePhysicalKey / KeyCatalog.ContainsModifier 的口径）。
                // 若用裸 PhysicalKey 值相等，开关键 LeftCtrl + 组合键 RightCtrl
                // 会被 IsSameBinding 判为同一绑定、却被本表判为不同实体键，
                // 于是 FR-TOGGLE-07 / FR-SEQ-10 会在混合左右时**漏报冲突**。
                var key = NormalizeForOccupancy(physicalKey);
                hostByPhysicalKey.TryAdd(key, command);

                if (!tapModesByPhysicalKey.TryGetValue(key, out var set))
                {
                    set = new HashSet<TapMode>();
                    tapModesByPhysicalKey[key] = set;
                }

                foreach (var mode in modes)
                {
                    set.Add(mode);
                }
            }
        }
    }

    /// <summary>
    /// 实体键在"占用比对"语义下归一化：修饰键取所属族的代表键（左 Ctrl → LeftCtrl），
    /// 非修饰键原样返回。鼠标键原样返回。
    ///
    /// 目的：让占用表与 <see cref="IsSamePhysicalKey"/> 用**同一套等价口径**。
    /// 两套口径共存会让同一条冲突规则在混合左右修饰键时得出相反结论。
    /// </summary>
    private static PhysicalKey NormalizeForOccupancy(PhysicalKey physicalKey)
    {
        if (physicalKey.KeyboardVirtualKey is not { } key)
        {
            return physicalKey;
        }

        return KeyCatalog.ModifierFamily(key) is null
            ? physicalKey
            : PhysicalKey.FromKeyboard(KeyCatalog.RepresentativeOfFamily(key));
    }

    /// <summary>
    /// 命令占用的实体键集合：按键类 1 个，组合键 = 修饰键 + 主键（多个）。
    /// 鼠标移动 / 滚轮返回空——它们不占用任何实体键。
    /// </summary>
    public static IEnumerable<PhysicalKey> CommandPhysicalKeys(CommandDefinition command)
        => command switch
        {
            KeyboardKeyCommand key => new[] { key.PhysicalKey },
            MouseButtonCommand mouse => new[] { mouse.PhysicalKey },
            KeyboardComboCommand combo => combo.PhysicalKeys,

            // 鼠标移动 / 滚轮：不占用实体键，故不与开关按键冲突，也不与别的命令冲突。
            _ => Array.Empty<PhysicalKey>(),
        };

    /// <summary>
    /// 命令参与"按住 / 点按"冲突判定时贡献的形态集合。
    /// 不适用执行形态的命令（鼠标移动 / 滚轮）返回 <c>null</c>，表示**不参与**本判定。
    /// </summary>
    private static IEnumerable<TapMode>? TapModeOf(CommandDefinition command)
        => command switch
        {
            KeyboardKeyCommand or MouseButtonCommand or KeyboardComboCommand
                => TapMode.None == command.TapMode ? null : new[] { command.TapMode },
            _ => null,
        };

    /// <summary>
    /// 开关按键绑定所占用的实体键集合。
    /// 单键 / 鼠标键为 1 个；键盘组合为"修饰键 + 主键"多个
    /// （Ctrl+C 作开关时，C 键本身也被占用）。
    /// </summary>
    public static IEnumerable<PhysicalKey> BindingPhysicalKeys(KeyBinding binding)
    {
        // KeyBinding 可被外部代码/反序列化构造出 null——不抛异常，只当作"无绑定"（AC-21）。
        if (binding is null)
        {
            yield break;
        }

        if (binding.Trigger.IsMouse)
        {
            yield return binding.Trigger;
            yield break;
        }

        // binding.Modifiers 理论上非空（record 主属性 + ModifiersForJson 读出时归一），
        // 但 KeyBinding 可被外部代码/反序列化构造出 null——遍历前兜底，
        // 兑换本类"不抛异常"的承诺与 AC-21（投毒文件不崩溃）。
        foreach (var modifier in binding.Modifiers ?? (IReadOnlySet<System.Windows.Input.Key>)new HashSet<System.Windows.Input.Key>())
        {
            yield return PhysicalKey.FromKeyboard(modifier);
        }

        yield return binding.Trigger;
    }

    /// <summary>
    /// 两个开关按键是否**共用同一个主键**（FR-TOGGLE-05 的占用判据）。
    ///
    /// 与 <see cref="IsSameBinding"/> 的区别：本方法**忽略修饰集**，只比主键。
    /// 占用校验用本方法（一个主键只能服务一个任务），
    /// 紧急停止冲突用 <see cref="IsSameBinding"/>（完整绑定，两者修饰集相同才算冲突）。
    ///
    /// 主键比对同样做**左右修饰键归一**——主键若是 Ctrl 这类修饰键
    /// （畸形或边界配置），左 Ctrl 与右 Ctrl 在用户眼里仍是同一个键。
    /// </summary>
    public static bool IsSameToggleKey(KeyBinding a, KeyBinding b)
    {
        // 对外公开的 API，可被 null 喂进（投毒文件）——不抛异常（AC-21）。
        if (a is null || b is null)
        {
            return false;
        }

        return IsSamePhysicalKey(a.Trigger, b.Trigger);
    }

    /// <summary>
    /// 两个开关按键绑定是否是"同一个按键"（**完整**判等：主键 + 修饰集）。
    ///
    /// 判等口径有三点，都是为了贴合用户心智而不是贴合数据结构：
    ///   1. **左右修饰键归一**：左 Ctrl 与右 Ctrl 在用户眼里都是"Ctrl"，
    ///      判为不同会让"我明明换了个键"却仍报冲突（或反之漏报）。
    ///   2. **修饰键无序**：Ctrl+Alt 与 Alt+Ctrl 是同一个按键。
    ///   3. **主键严格**：主键不同就是不同的开关按键，不做任何模糊。
    ///
    /// 用途：**紧急停止快捷键**的冲突判定（FR-RUN-03）——
    /// 只有当两者的完整绑定相同时，同一次点按才会重叠，才是真冲突。
    /// 任务间的开关按键占用**不用**本方法（那里是主键口径，见 <see cref="IsSameToggleKey"/>）。
    /// </summary>
    public static bool IsSameBinding(KeyBinding a, KeyBinding b)
    {
        // 本方法是对外公开的 API，且能被 "toggleKey": null 的投毒文件喂进 null——
        // 不抛异常，只当作"无绑定"处理（AC-21）。
        if (a is null || b is null)
        {
            return false;
        }

        if (!IsSamePhysicalKey(a.Trigger, b.Trigger))
        {
            return false;
        }

        return ModifierFamilies(a.Modifiers).SetEquals(ModifierFamilies(b.Modifiers));
    }

    /// <summary>两个实体键在"开关按键"语义下是否等价（仅修饰键左右归一）。</summary>
    private static bool IsSamePhysicalKey(PhysicalKey a, PhysicalKey b)
    {
        if (a.IsMouse || b.IsMouse)
        {
            return a.IsMouse && b.IsMouse && a.Mouse == b.Mouse;
        }

        if (a.KeyboardVirtualKey is not { } keyA || b.KeyboardVirtualKey is not { } keyB)
        {
            // 两个都既非鼠标也无键盘键：结构上不可达（PhysicalKey 必然二选一）。
            // 返回 false 而非抛异常——判等函数不该成为崩溃点。
            return false;
        }

        return keyA == keyB || KeyCatalog.IsSameModifier(keyA, keyB);
    }

    /// <summary>修饰键集合按"族名"归一后的集合（左右 Ctrl 视为同一项）。</summary>
    private static HashSet<string> ModifierFamilies(IReadOnlySet<System.Windows.Input.Key>? modifiers)
    {
        var families = new HashSet<string>(StringComparer.Ordinal);
        if (modifiers is null)
        {
            return families;
        }

        foreach (var modifier in modifiers)
        {
            var family = KeyCatalog.ModifierFamily(modifier);
            if (family is not null)
            {
                families.Add(family);
            }
        }

        return families;
    }

    /// <summary>
    /// 实体键的中文描述，用于提示文案。
    /// 键盘键回退到枚举名（与 <see cref="KeyCatalog.Describe(System.Windows.Input.Key)"/> 同口径），
    /// 不返回空白——空白会让用户看不出到底是哪个键冲突。
    /// </summary>
    private static string DescribePhysicalKey(PhysicalKey physicalKey)
    {
        if (physicalKey.Mouse is { } mouseKey)
        {
            return KeyCatalog.Describe(mouseKey);
        }

        return physicalKey.KeyboardVirtualKey is { } virtualKey
            ? KeyCatalog.Describe(virtualKey)
            : "（未知按键）";
    }

    /// <summary>
    /// 取命令的显示名用于提示。显示名由类型与字段算出，畸形对象可能抛异常
    /// （如组合键的显示名要遍历修饰键集合）——提示文案的取值不该让整个校验崩掉，
    /// 故此处吞下异常并退回类型名。
    /// </summary>
    private static string SafeDisplayName(CommandDefinition command)
    {
        try
        {
            var name = command.DisplayName;
            return string.IsNullOrWhiteSpace(name) ? command.GetType().Name : name;
        }
        catch (Exception)
        {
            return command.GetType().Name;
        }
    }
}
