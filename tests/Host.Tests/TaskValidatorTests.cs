using Core.Keys;
using Core.Model;
using Core.Validation;
using System.Windows.Input;
using KeyBinding = Core.Keys.KeyBinding;
using MouseButton = Core.Keys.MouseKey;
using WpfKey = System.Windows.Input.Key;

namespace Host.Tests;

/// <summary>
/// 任务校验器的单测（设计文档 §5.3，需求 FR-TASK-06 / FR-SEQ-08/10 / FR-TAP-05 /
/// FR-TOGGLE-05/07 / FR-RUN-03）。
///
/// 断言的粒度是 <see cref="ValidationRule"/> 枚举，不是提示文案：
/// 文案会随界面打磨而改，规则标识不会。这样测试锁住的是"哪条规则被命中"，
/// 而不是"提示最后有没有句号"。
///
/// 覆盖的验收标准：
///   AC-05（开关按键被两个任务占用）、AC-05a（开关键与本任务命令实体键重叠）、
///   AC-14（频率越界）、AC-15（紧急停止快捷键与开关键冲突）、
///   AC-28（空命令集合）、AC-31（同一实体键按住+点按）。
/// </summary>
public class TaskValidatorTests
{
    private const int LegalFrequency = 10;

    /// <summary>紧急停止快捷键默认值（需求 FR-RUN-02：Ctrl+Alt+F12）。</summary>
    private static KeyBinding EmergencyStop()
        => KeyBinding.FromCombo(new[] { WpfKey.LeftCtrl, WpfKey.LeftAlt }, WpfKey.F12);

    /// <summary>造一个"形状合法"的任务：有名、有键、有一条合法命令。</summary>
    private static TaskDefinition NewTask(
        string name = "任务",
        KeyBinding? toggleKey = null,
        params CommandDefinition[] commands)
    {
        var task = new TaskDefinition
        {
            Name = name,
            ToggleKey = toggleKey ?? KeyBinding.FromKey(WpfKey.F6),
        };

        task.Commands.AddRange(commands.Length > 0
            ? commands
            : new CommandDefinition[] { KeyCommand(WpfKey.A) });

        return task;
    }

    private static KeyboardKeyCommand KeyCommand(WpfKey key, TapMode mode = TapMode.Tap, int frequency = LegalFrequency)
        => new() { VirtualKey = key, TapMode = mode, Frequency = frequency };

    private static MouseButtonCommand MouseCommand(MouseButton button, TapMode mode = TapMode.Tap, int frequency = LegalFrequency)
        => new() { Button = button, TapMode = mode, Frequency = frequency };

    private static KeyboardComboCommand ComboCommand(
        IEnumerable<WpfKey> modifiers,
        WpfKey main,
        TapMode mode = TapMode.Tap,
        int frequency = LegalFrequency)
        => new() { ModifierKeys = modifiers.ToList(), VirtualKey = main, TapMode = mode, Frequency = frequency };

    private static MouseMoveCommand MoveCommand(int x = 100, int y = 200, int frequency = LegalFrequency)
        => new() { X = x, Y = y, Frequency = frequency, TapMode = TapMode.None };

    private static MouseScrollCommand ScrollCommand(
        ScrollDirection direction = ScrollDirection.Up,
        int notches = 3,
        int frequency = LegalFrequency)
        => new() { Direction = direction, Notches = notches, Frequency = frequency, TapMode = TapMode.None };

    private static ValidationResult Validate(TaskDefinition? candidate, params TaskDefinition[] others)
        => TaskValidator.Validate(candidate, others, EmergencyStop());

    /// <summary>断言结果不通过，且命中了指定的规则（可多条）。</summary>
    private static void AssertViolations(ValidationResult result, params ValidationRule[] expected)
    {
        Assert.False(result.IsValid, "期望校验不通过，但实际通过了。结果：" + result.Summary);

        var actual = result.Issues.Select(i => i.Rule).ToHashSet();
        foreach (var rule in expected)
        {
            Assert.True(actual.Contains(rule), $"期望命中规则 {rule}，实际命中：{string.Join("、", actual)}");
        }
    }

    /// <summary>断言结果通过。</summary>
    private static void AssertValid(ValidationResult result)
        => Assert.True(result.IsValid, "期望校验通过，实际问题：" + result.Summary);

    // ────────────────────────────────────────────────────────────────
    // 第 1 条：任务名非空且唯一（FR-TASK-06）
    // ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void 任务名为空或全空白时拒绝保存(string name)
    {
        AssertViolations(Validate(NewTask(name)), ValidationRule.TaskNameRequiredAndUnique);
    }

    [Fact]
    public void 任务名与其他任务重名时拒绝保存()
    {
        var candidate = NewTask("刷副本");
        var other = NewTask("刷副本");

        AssertViolations(Validate(candidate, other), ValidationRule.TaskNameRequiredAndUnique);
    }

    [Fact]
    public void 任务名唯一时通过()
    {
        // 两个任务的开关按键必须不同，否则会额外命中"开关键被占用"——这里只想验名称唯一性。
        AssertValid(Validate(NewTask("刷副本"), NewTask("挂机采集", KeyBinding.FromKey(WpfKey.F8))));
    }

    [Fact]
    public void 任务名首尾空白不影响唯一性判定()
    {
        // 用户看不出"刷副本"与"刷副本 "的差别，判为不同会让重名冲突无从排查。
        var candidate = NewTask("  刷副本  ");
        var other = NewTask("刷副本");

        AssertViolations(Validate(candidate, other), ValidationRule.TaskNameRequiredAndUnique);
    }

    [Fact]
    public void 编辑既有任务时不与自己重名()
    {
        // 编辑场景：候选对象与列表中的自己是同一个 Id，不得报"名字被自己占用"。
        var existing = NewTask("刷副本");
        var edited = new TaskDefinition
        {
            Id = existing.Id,
            Name = "刷副本（改）",
            ToggleKey = KeyBinding.FromKey(WpfKey.F7),
        };
        edited.Commands.Add(KeyCommand(WpfKey.A));

        AssertValid(TaskValidator.Validate(edited, new[] { existing }, EmergencyStop()));
    }

    // ────────────────────────────────────────────────────────────────
    // 第 2 条：命令集合非空（FR-SEQ-08 / AC-28）
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void 命令集合为空时拒绝保存()
    {
        var task = NewTask();
        task.Commands.Clear();

        AssertViolations(Validate(task), ValidationRule.CommandsNotEmpty);
    }

    [Fact]
    public void 命令集合内混入空引用视为命令集合为空()
    {
        // 配置文件被外部改坏时列表里可能出现 null；不能因此抛异常（AC-21 的不崩溃口径）。
        var task = NewTask();
        task.Commands.Clear();
        task.Commands.Add(null!);

        AssertViolations(Validate(task), ValidationRule.CommandsNotEmpty);
    }

    // ────────────────────────────────────────────────────────────────
    // 第 3 条：频率 1~1000（FR-TAP-05 / AC-14）
    // ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1001)]
    [InlineData(100_000)]
    public void 频率越界时拒绝保存(int frequency)
    {
        var task = NewTask(commands: KeyCommand(WpfKey.A, frequency: frequency));

        AssertViolations(Validate(task), ValidationRule.FrequencyInRange);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(1000)]
    public void 频率在闭区间边界内时通过(int frequency)
    {
        var task = NewTask(commands: KeyCommand(WpfKey.A, frequency: frequency));

        AssertValid(Validate(task));
    }

    [Fact]
    public void 频率校验对鼠标移动与滚轮同样生效()
    {
        // 需求 FR-TAP-07：这两类命令虽无点按形态，但同样"按自身重复频率执行"，
        // 频率区间口径必须一致——否则会出现"改类型绕开频率校验"的漏洞。
        var move = MoveCommand(frequency: 0);
        var scroll = ScrollCommand(frequency: 5000);

        var result = Validate(NewTask(commands: new CommandDefinition[] { move, scroll }));

        AssertViolations(result, ValidationRule.FrequencyInRange);
        Assert.Equal(2, result.Issues.Count(i => i.Rule == ValidationRule.FrequencyInRange));
    }

    [Fact]
    public void 频率越界的问题定位到具体命令()
    {
        var bad = KeyCommand(WpfKey.A, frequency: 0);

        var result = Validate(NewTask(commands: new CommandDefinition[] { KeyCommand(WpfKey.B), bad }));

        var issue = Assert.Single(result.Issues, i => i.Rule == ValidationRule.FrequencyInRange);
        Assert.Same(bad, issue.Command);
    }

    // ────────────────────────────────────────────────────────────────
    // 第 4 条：开关按键未被其他任务占用（FR-TOGGLE-05 / AC-05）
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void 开关按键被其他任务占用时拒绝保存()
    {
        var candidate = NewTask("任务甲", KeyBinding.FromKey(WpfKey.F7));
        var other = NewTask("任务乙", KeyBinding.FromKey(WpfKey.F7));

        AssertViolations(Validate(candidate, other), ValidationRule.ToggleKeyNotTaken);
    }

    [Fact]
    public void 开关按键互不相同时通过()
    {
        var candidate = NewTask("任务甲", KeyBinding.FromKey(WpfKey.F7));
        var other = NewTask("任务乙", KeyBinding.FromKey(WpfKey.F8));

        AssertValid(Validate(candidate, other));
    }

    [Fact]
    public void 编辑既有任务时开关按键不算被自己占用()
    {
        var existing = NewTask("刷副本", KeyBinding.FromKey(WpfKey.F7));
        var edited = new TaskDefinition
        {
            Id = existing.Id,
            Name = "刷副本",
            ToggleKey = KeyBinding.FromKey(WpfKey.F7),
        };
        edited.Commands.Add(KeyCommand(WpfKey.A));

        AssertValid(TaskValidator.Validate(edited, new[] { existing }, EmergencyStop()));
    }

    [Fact]
    public void 鼠标键作开关时占用判定按鼠标键本身()
    {
        var candidate = NewTask("任务甲", KeyBinding.FromMouse(MouseButton.X1));
        var other = NewTask("任务乙", KeyBinding.FromMouse(MouseButton.X1));

        AssertViolations(Validate(candidate, other), ValidationRule.ToggleKeyNotTaken);

        var different = NewTask("任务丙", KeyBinding.FromMouse(MouseButton.X2));
        AssertValid(Validate(candidate, different));
    }

    [Fact]
    public void 左右修饰键视为同一个开关按键()
    {
        // 用户眼里左 Ctrl 与右 Ctrl 都是"Ctrl"；判为不同会让冲突漏报。
        var candidate = NewTask("任务甲", KeyBinding.FromCombo(new[] { WpfKey.LeftCtrl }, WpfKey.C));
        var other = NewTask("任务乙", KeyBinding.FromCombo(new[] { WpfKey.RightCtrl }, WpfKey.C));

        AssertViolations(Validate(candidate, other), ValidationRule.ToggleKeyNotTaken);
    }

    [Fact]
    public void 修饰键顺序不同的同一组合视为同一个开关按键()
    {
        var candidate = NewTask("任务甲", KeyBinding.FromCombo(new[] { WpfKey.LeftCtrl, WpfKey.LeftAlt }, WpfKey.F9));
        var other = NewTask("任务乙", KeyBinding.FromCombo(new[] { WpfKey.LeftAlt, WpfKey.LeftCtrl }, WpfKey.F9));

        AssertViolations(Validate(candidate, other), ValidationRule.ToggleKeyNotTaken);
    }

    [Fact]
    public void 主键相同的不同组合仍被视为同一个开关按键()
    {
        // FR-TOGGLE-05 取**主键**口径：一个主键至多服务一个任务。
        // Ctrl+C 与 Ctrl+Shift+C 共用主键 C，用户按下 C 时两者都会被命中，
        // 同一次输入启停两个任务不是用户能预期的，故拒绝保存。
        var candidate = NewTask("任务甲", KeyBinding.FromCombo(new[] { WpfKey.LeftCtrl }, WpfKey.C));
        var other = NewTask("任务乙", KeyBinding.FromCombo(new[] { WpfKey.LeftCtrl, WpfKey.LeftShift }, WpfKey.C));

        AssertViolations(Validate(candidate, other), ValidationRule.ToggleKeyNotTaken);
    }

    [Fact]
    public void 修饰集不同的同主键组合冲突()
    {
        // “Ctrl+F8 与 Alt+F8 算不算重复”的裁定结论：**算**（主键口径）。
        // 裁定理由见 TaskValidator.IsSameToggleKey 的注释。
        var candidate = NewTask("任务甲", KeyBinding.FromCombo(new[] { WpfKey.LeftCtrl }, WpfKey.F8));
        var other = NewTask("任务乙", KeyBinding.FromCombo(new[] { WpfKey.LeftAlt }, WpfKey.F8));

        AssertViolations(Validate(candidate, other), ValidationRule.ToggleKeyNotTaken);
    }

    [Fact]
    public void 单键开关占用同名组合开关()
    {
        // 裸 F7 与 Ctrl+F7 共用主键 F7：保存时拒绝。
        // 这条与 §5.2 的“裸键误触提示”同源——裸键会吞掉同主键组合键的点按，
        // 两者指向同一个风险，只是一处在保存期拦、一处在编辑期提示。
        var candidate = NewTask("任务甲", KeyBinding.FromKey(WpfKey.F7));
        var other = NewTask("任务乙", KeyBinding.FromCombo(new[] { WpfKey.LeftCtrl }, WpfKey.F7));

        AssertViolations(Validate(candidate, other), ValidationRule.ToggleKeyNotTaken);
    }

    [Fact]
    public void 已禁用任务的开关按键同样被占用()
    {
        // 禁用任务可随时启用，放过它的开关键等于埋雷：
        // 启用那一刻才会弹出冲突，而用户已经忘了自己配过什么。
        var candidate = NewTask("任务甲", KeyBinding.FromKey(WpfKey.F7));
        var disabled = NewTask("任务乙", KeyBinding.FromKey(WpfKey.F7));
        disabled.Enabled = false;

        AssertViolations(Validate(candidate, disabled), ValidationRule.ToggleKeyNotTaken);
    }

    [Fact]
    public void 单键开关与本任务命令的异侧修饰键视为同一实体键()
    {
        // D 回归：占用表原先按 PhysicalKey 精确比对，LeftCtrl 与 RightCtrl 被判为两个实体键，
        // 于是"开关按键 = 右 Ctrl"能穿过"开关不得与本任务命令实体键重叠"这条校验，
        // 与 FR-TOGGLE-07 的语义（用户眼里都是 Ctrl）相悖。
        var task = NewTask("任务甲", KeyBinding.FromKey(WpfKey.RightCtrl),
            commands: ComboCommand(new[] { WpfKey.LeftCtrl }, WpfKey.C));

        AssertViolations(Validate(task), ValidationRule.ToggleKeyNotOverlappingOwnCommands);
    }

    [Fact]
    public void 异侧修饰键的按住与点按命令互斥()
    {
        // D 回归：同一条 FR-SEQ-10 校验，原先只对完全相同的键报冲突。
        var task = NewTask("任务甲", commands: new CommandDefinition[]
        {
            KeyCommand(WpfKey.LeftShift, TapMode.Hold),
            KeyCommand(WpfKey.RightShift, TapMode.Tap),
        });

        AssertViolations(Validate(task), ValidationRule.NoConflictingTapModesOnSameKey);
    }


    // ────────────────────────────────────────────────────────────────
    // 第 5 条：开关按键不与本任务命令实体键重叠（FR-TOGGLE-07 / AC-05a）
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void 开关键与本任务键盘命令实体键重叠时拒绝保存()
    {
        var task = NewTask("任务", KeyBinding.FromKey(WpfKey.X), KeyCommand(WpfKey.X, TapMode.Hold));

        AssertViolations(Validate(task), ValidationRule.ToggleKeyNotOverlappingOwnCommands);
    }

    [Fact]
    public void 开关键与本任务鼠标命令实体键重叠时拒绝保存()
    {
        // AC-05a 的原始场景：开关按键 X1 + 本任务内 `鼠标X1·按住` 命令。
        var task = NewTask("任务", KeyBinding.FromMouse(MouseButton.X1), MouseCommand(MouseButton.X1, TapMode.Hold));

        AssertViolations(Validate(task), ValidationRule.ToggleKeyNotOverlappingOwnCommands);
    }

    [Fact]
    public void 开关键与本任务命令实体键不同时通过()
    {
        var task = NewTask("任务", KeyBinding.FromKey(WpfKey.F6), KeyCommand(WpfKey.X));

        AssertValid(Validate(task));
    }

    [Fact]
    public void 开关键与组合键命令的修饰键重叠时拒绝保存()
    {
        // Ctrl+C 这条命令占用了 Ctrl 与 C 两个实体键，开关按键设为 Ctrl 同样是冲突。
        var task = NewTask(
            "任务",
            KeyBinding.FromKey(WpfKey.LeftCtrl),
            ComboCommand(new[] { WpfKey.LeftCtrl }, WpfKey.C));

        AssertViolations(Validate(task), ValidationRule.ToggleKeyNotOverlappingOwnCommands);
    }

    [Fact]
    public void 开关键与组合键命令的主键重叠时拒绝保存()
    {
        var task = NewTask(
            "任务",
            KeyBinding.FromKey(WpfKey.C),
            ComboCommand(new[] { WpfKey.LeftCtrl }, WpfKey.C));

        AssertViolations(Validate(task), ValidationRule.ToggleKeyNotOverlappingOwnCommands);
    }

    [Fact]
    public void 开关键与鼠标移动滚轮命令不构成重叠()
    {
        // 移动 / 滚轮不占用任何实体键，无论怎么配都不该报重叠。
        var task = NewTask(
            "任务",
            KeyBinding.FromKey(WpfKey.F6),
            MoveCommand(),
            ScrollCommand());

        AssertValid(Validate(task));
    }

    [Fact]
    public void 组合开关按键逐键比对命令实体键()
    {
        // 组合开关 Ctrl+F6：F6 出现在命令里 → 冲突（Ctrl 未出现在命令里也不影响判定）。
        var task = NewTask(
            "任务",
            KeyBinding.FromCombo(new[] { WpfKey.LeftCtrl }, WpfKey.F6),
            KeyCommand(WpfKey.F6, TapMode.Hold));

        AssertViolations(Validate(task), ValidationRule.ToggleKeyNotOverlappingOwnCommands);
    }

    // ────────────────────────────────────────────────────────────────
    // 第 6 条：同一实体键不同时"按住"+"点按"（FR-SEQ-10 / AC-31）
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void 同一实体键同时配按住与点按时拒绝保存()
    {
        var task = NewTask(
            "任务",
            commands: new CommandDefinition[] { KeyCommand(WpfKey.A, TapMode.Hold), KeyCommand(WpfKey.A, TapMode.Tap) });

        AssertViolations(Validate(task), ValidationRule.NoConflictingTapModesOnSameKey);
    }

    [Fact]
    public void 同一实体键配置两条按住命令时通过()
    {
        // 两条同为"按住"并不自相矛盾（都是按下不松），需求要挡的是"点按替按住把键松开"。
        var task = NewTask(
            "任务",
            commands: new CommandDefinition[] { KeyCommand(WpfKey.A, TapMode.Hold), KeyCommand(WpfKey.A, TapMode.Hold) });

        AssertValid(Validate(task));
    }

    [Fact]
    public void 不同实体键的按住与点按可以共存()
    {
        var task = NewTask(
            "任务",
            commands: new CommandDefinition[] { KeyCommand(WpfKey.A, TapMode.Hold), KeyCommand(WpfKey.B, TapMode.Tap) });

        AssertValid(Validate(task));
    }

    [Fact]
    public void 鼠标键的按住点按冲突同样被拦下()
    {
        var task = NewTask(
            "任务",
            commands: new CommandDefinition[]
            {
                MouseCommand(MouseButton.Left, TapMode.Hold),
                MouseCommand(MouseButton.Left, TapMode.Tap),
            });

        AssertViolations(Validate(task), ValidationRule.NoConflictingTapModesOnSameKey);
    }

    [Fact]
    public void 组合键与单键共用同一实体键时的冲突被拦下()
    {
        // Ctrl+C 的"点按"与 C 的"按住"落在同一实体键 C 上 → 冲突。
        var task = NewTask(
            "任务",
            commands: new CommandDefinition[]
            {
                ComboCommand(new[] { WpfKey.LeftCtrl }, WpfKey.C, TapMode.Tap),
                KeyCommand(WpfKey.C, TapMode.Hold),
            });

        AssertViolations(Validate(task), ValidationRule.NoConflictingTapModesOnSameKey);
    }

    [Fact]
    public void 同一实体键的两条点按命令互不冲突()
    {
        var task = NewTask(
            "任务",
            commands: new CommandDefinition[] { KeyCommand(WpfKey.A, TapMode.Tap), KeyCommand(WpfKey.A, TapMode.Tap) });

        AssertValid(Validate(task));
    }

    [Fact]
    public void 组合键内部形态不冲突时通过()
    {
        // Ctrl+Shift+C 一条命令同时"占用"三个实体键，但它只贡献一个形态，不构成冲突。
        var task = NewTask("任务", commands: ComboCommand(new[] { WpfKey.LeftCtrl, WpfKey.LeftShift }, WpfKey.C));

        AssertValid(Validate(task));
    }

    // ────────────────────────────────────────────────────────────────
    // 第 7 条：紧急停止快捷键不与任何任务开关键冲突（FR-RUN-03 / AC-15）
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void 开关键与紧急停止快捷键相同时拒绝保存()
    {
        var task = NewTask(
            "任务",
            KeyBinding.FromCombo(new[] { WpfKey.LeftCtrl, WpfKey.LeftAlt }, WpfKey.F12));

        AssertViolations(Validate(task), ValidationRule.EmergencyStopKeyNotConflicting);
    }

    [Fact]
    public void 紧急停止快捷键不与任何任务开关键冲突时通过()
    {
        var task = NewTask("任务", KeyBinding.FromKey(WpfKey.F6));

        AssertValid(Validate(task));
    }

    [Fact]
    public void 紧急停止快捷键只与开关按键比对不碰命令实体键()
    {
        // FR-RUN-03 只约束"与开关按键冲突"；紧急停止键与某条命令用同一个键
        // 并不构成冲突（命令键是点按时才按下，紧急停止是全程监听）。
        var task = NewTask(
            "任务",
            KeyBinding.FromKey(WpfKey.F6),
            KeyCommand(WpfKey.F12));

        AssertValid(Validate(task));
    }

    [Fact]
    public void 紧急停止键的左右修饰键归一同样生效()
    {
        var task = NewTask(
            "任务",
            KeyBinding.FromCombo(new[] { WpfKey.RightCtrl, WpfKey.RightAlt }, WpfKey.F12));

        AssertViolations(Validate(task), ValidationRule.EmergencyStopKeyNotConflicting);
    }

    // ────────────────────────────────────────────────────────────────
    // 领域形状规则（不属于 §5.3 七条，但同属"拒绝保存"的形态约束）
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void 键盘命令的执行形态为不适用时拒绝保存()
    {
        var task = NewTask(commands: KeyCommand(WpfKey.A, TapMode.None));

        AssertViolations(Validate(task), ValidationRule.TapModeRequiredForCommand);
    }

    [Fact]
    public void 鼠标按键命令的执行形态为不适用时拒绝保存()
    {
        var task = NewTask(commands: MouseCommand(MouseButton.Left, TapMode.None));

        AssertViolations(Validate(task), ValidationRule.TapModeRequiredForCommand);
    }

    [Fact]
    public void 鼠标移动与滚轮带执行形态时拒绝保存()
    {
        var move = MoveCommand();
        move.TapMode = TapMode.Hold;
        var scroll = ScrollCommand();
        scroll.TapMode = TapMode.Tap;

        var result = Validate(NewTask(commands: new CommandDefinition[] { move, scroll }));

        AssertViolations(result, ValidationRule.TapModeNotApplicableForCommand);
        Assert.Equal(2, result.Issues.Count(i => i.Rule == ValidationRule.TapModeNotApplicableForCommand));
    }

    [Fact]
    public void 鼠标移动与滚轮的默认形态通过()
    {
        var task = NewTask(commands: new CommandDefinition[] { MoveCommand(), ScrollCommand() });

        AssertValid(Validate(task));
    }

    [Fact]
    public void 滚轮格数越界时拒绝保存()
    {
        var zero = ScrollCommand(notches: 0);
        var tooMany = ScrollCommand(notches: TaskValidator.MaxScrollNotches + 1);

        var result = Validate(NewTask(commands: new CommandDefinition[] { zero, tooMany }));

        AssertViolations(result, ValidationRule.ScrollNotchesInRange);
        Assert.Equal(2, result.Issues.Count(i => i.Rule == ValidationRule.ScrollNotchesInRange));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public void 滚轮格数为正且不超上限时通过(int notches)
    {
        var task = NewTask(commands: ScrollCommand(notches: notches));

        AssertValid(Validate(task));
    }

    [Fact]
    public void 组合键无修饰键时拒绝保存()
    {
        var combo = ComboCommand(Array.Empty<WpfKey>(), WpfKey.C);

        AssertViolations(Validate(NewTask(commands: combo)), ValidationRule.ComboShapeValid);
    }

    [Fact]
    public void 组合键主键为修饰键时拒绝保存()
    {
        var combo = ComboCommand(new[] { WpfKey.LeftCtrl }, WpfKey.LeftShift);

        AssertViolations(Validate(NewTask(commands: combo)), ValidationRule.ComboShapeValid);
    }

    [Fact]
    public void 组合键执行形态为不适用时拒绝保存()
    {
        // 需求 FR-TAP-06 规定组合键按点按反复执行；形态为"不适用"意味着无事可做。
        var combo = ComboCommand(new[] { WpfKey.LeftCtrl }, WpfKey.C, TapMode.None);

        AssertViolations(Validate(NewTask(commands: combo)), ValidationRule.TapModeRequiredForCommand);
    }

    [Fact]
    public void 合法组合键通过()
    {
        var combo = ComboCommand(new[] { WpfKey.LeftCtrl, WpfKey.LeftShift }, WpfKey.C);

        AssertValid(Validate(NewTask(commands: combo)));
    }

    // ────────────────────────────────────────────────────────────────
    // 结果结构与鲁棒性
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void 一次返回全部违规而不是首错即返()
    {
        // UI 需要"一次看全"：逐条报错会让用户改一条、存一次、再被打回。
        var task = new TaskDefinition
        {
            Name = "   ",                                            // 第 1 条：任务名
            ToggleKey = KeyBinding.FromKey(WpfKey.F6),               // 第 4 条：开关键被下面那个任务占用
        };
        task.Commands.Add(KeyCommand(WpfKey.A, TapMode.Hold, frequency: 0)); // 第 3 条：频率
        task.Commands.Add(KeyCommand(WpfKey.A, TapMode.Tap));               // 第 6 条：形态冲突

        var other = NewTask("任务乙", KeyBinding.FromKey(WpfKey.F6));

        var result = Validate(task, other);

        AssertViolations(
            result,
            ValidationRule.TaskNameRequiredAndUnique,
            ValidationRule.FrequencyInRange,
            ValidationRule.ToggleKeyNotTaken,
            ValidationRule.NoConflictingTapModesOnSameKey);
        Assert.Equal(4, result.Issues.Count);
    }

    [Fact]
    public void 命令集合为空时只报命令为空不误报其他命令级问题()
    {
        var task = NewTask();
        task.Commands.Clear();

        var result = Validate(task);

        AssertViolations(result, ValidationRule.CommandsNotEmpty);
        Assert.DoesNotContain(result.Issues, i => i.Rule == ValidationRule.FrequencyInRange);
        Assert.DoesNotContain(result.Issues, i => i.Rule == ValidationRule.NoConflictingTapModesOnSameKey);
    }

    [Fact]
    public void 其他任务列表为空时仍能正常校验()
    {
        AssertValid(TaskValidator.Validate(NewTask(), null, EmergencyStop()));
    }

    [Fact]
    public void 待校验任务为空引用时不抛异常()
    {
        var result = TaskValidator.Validate(null, Array.Empty<TaskDefinition>(), EmergencyStop());

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Rule == ValidationRule.CommandsNotEmpty);
    }

    [Fact]
    public void 开关按键为null时不抛异常()
    {
        // AC-21 回归：投毒文件里 "toggleKey": null 是合法 JSON，
        // 而 LoadTasks 不做归一——null 绑定会一路走到校验器。
        // 校验器承诺"不抛异常"，此处必须退化为可读的校验结果而非 NRE。
        var poisoned = NewTask("任务甲");
        poisoned.ToggleKey = null!;

        var alone = TaskValidator.Validate(poisoned, Array.Empty<TaskDefinition>(), EmergencyStop());
        Assert.Contains(alone.Issues, i => i.Rule == ValidationRule.ToggleKeyRequired);

        // 与另一条任务同帧校验：ValidateToggleKeyTaken / 报错文本两条路径都不得崩。
        var other = NewTask("任务乙", KeyBinding.FromKey(WpfKey.F7));
        var together = TaskValidator.Validate(poisoned, new[] { other }, EmergencyStop());
        Assert.Contains(together.Issues, i => i.Rule == ValidationRule.ToggleKeyRequired);
    }

    [Fact]
    public void 非法执行形态的命令不参与点按按住冲突判定()
    {
        // 🟡 回归：TapMode.None 的命令已由 TapModeRequiredForCommand 单独报错，
        // 不该再在 FR-SEQ-10 里冒充"点按"，否则会多报一条且文案谎称存在"按住"命令。
        var task = NewTask("任务甲", commands: new CommandDefinition[]
        {
            KeyCommand(WpfKey.A, TapMode.None),
            KeyCommand(WpfKey.A, TapMode.Tap),
        });

        var result = Validate(task);

        Assert.Contains(result.Issues, i => i.Rule == ValidationRule.TapModeRequiredForCommand);
        Assert.DoesNotContain(result.Issues, i => i.Rule == ValidationRule.NoConflictingTapModesOnSameKey);
    }

    [Fact]
    public void 同一命令占用开关绑定的多个键时只报一次()
    {
        // 🔵 回归：Ctrl+C 作开关会占用 Ctrl 与 C 两个实体键，若它们同属一条命令，
        // 不该把同一条冲突报两遍。
        var task = NewTask("任务甲", KeyBinding.FromCombo(new[] { WpfKey.LeftCtrl }, WpfKey.C),
            commands: ComboCommand(new[] { WpfKey.LeftCtrl }, WpfKey.C));

        var result = Validate(task);

        Assert.Equal(1, result.Issues.Count(i => i.Rule == ValidationRule.ToggleKeyNotOverlappingOwnCommands));
    }


    [Fact]
    public void 结果汇总文案在多问题时逐行列出()
    {
        var task = NewTask();
        task.Commands.Clear();

        var result = Validate(task);

        Assert.Contains("命令", result.Summary);
        Assert.NotEmpty(result.Summary);
    }

    [Fact]
    public void 通过的结果汇总为校验通过文案()
    {
        var result = Validate(NewTask("任务", KeyBinding.FromKey(WpfKey.F6)));

        AssertValid(result);
        Assert.Equal(ValidationResult.Valid.Summary, result.Summary);
    }

    [Fact]
    public void 开关按键判等口径的单元验证()
    {
        // IsSameBinding  = **完整**绑定判等（主键 + 修饰集），用于**紧急停止冲突**。
        // IsSameToggleKey = **仅主键**判等，用于**任务间占用**（FR-TOGGLE-05）。
        // 两个口径分开锁，避免日后被“统一成一个”而默默改变某条规则的范围。
        Assert.True(TaskValidator.IsSameBinding(KeyBinding.FromKey(WpfKey.F6), KeyBinding.FromKey(WpfKey.F6)));
        Assert.False(TaskValidator.IsSameBinding(KeyBinding.FromKey(WpfKey.F6), KeyBinding.FromKey(WpfKey.F7)));
        Assert.False(TaskValidator.IsSameBinding(KeyBinding.FromMouse(MouseButton.Left), KeyBinding.FromKey(WpfKey.F6)));
        Assert.True(TaskValidator.IsSameBinding(KeyBinding.FromMouse(MouseButton.X1), KeyBinding.FromMouse(MouseButton.X1)));
        Assert.False(TaskValidator.IsSameBinding(KeyBinding.FromMouse(MouseButton.X1), KeyBinding.FromMouse(MouseButton.X2)));

        // 完整判等：修饰集不同 → 不是同一个绑定。
        // 这保证“紧急停止 = Ctrl+F8”不会与“任务开关 = Alt+F8”互相误报冲突。
        Assert.False(TaskValidator.IsSameBinding(
            KeyBinding.FromKey(WpfKey.F8),
            KeyBinding.FromCombo(new[] { WpfKey.LeftCtrl }, WpfKey.F8)));
        Assert.False(TaskValidator.IsSameBinding(
            KeyBinding.FromCombo(new[] { WpfKey.LeftCtrl }, WpfKey.F8),
            KeyBinding.FromCombo(new[] { WpfKey.LeftAlt }, WpfKey.F8)));

        // 仅主键判等：只要主键相同就是冲突，与修饰集无关。
        Assert.True(TaskValidator.IsSameToggleKey(
            KeyBinding.FromKey(WpfKey.F8),
            KeyBinding.FromCombo(new[] { WpfKey.LeftCtrl }, WpfKey.F8)));
        Assert.True(TaskValidator.IsSameToggleKey(
            KeyBinding.FromCombo(new[] { WpfKey.LeftCtrl }, WpfKey.F8),
            KeyBinding.FromCombo(new[] { WpfKey.LeftAlt }, WpfKey.F8)));
        Assert.False(TaskValidator.IsSameToggleKey(KeyBinding.FromKey(WpfKey.F8), KeyBinding.FromKey(WpfKey.F9)));

        // 主键判等同样做左右修饰键归一（主键为修饰键的边界配置）。
        Assert.True(TaskValidator.IsSameToggleKey(KeyBinding.FromKey(WpfKey.LeftCtrl), KeyBinding.FromKey(WpfKey.RightCtrl)));

        // null 不作崩溃点（AC-21）：两个口径都返回 false。
        Assert.False(TaskValidator.IsSameToggleKey(null!, KeyBinding.FromKey(WpfKey.F6)));
        Assert.False(TaskValidator.IsSameBinding(null!, KeyBinding.FromKey(WpfKey.F6)));
    }

    [Fact]
    public void 命令占用实体键集合的口径()
    {
        // CommandPhysicalKeys 是 FR-TOGGLE-07 与 FR-SEQ-10 两张占用表的唯一来源，
        // 它的口径直接决定这两条规则的范围。
        Assert.Equal(
            new[] { PhysicalKey.FromKeyboard(WpfKey.A) },
            TaskValidator.CommandPhysicalKeys(KeyCommand(WpfKey.A)).ToArray());

        Assert.Equal(
            new[] { PhysicalKey.FromMouse(MouseButton.Left) },
            TaskValidator.CommandPhysicalKeys(MouseCommand(MouseButton.Left)).ToArray());

        Assert.Empty(TaskValidator.CommandPhysicalKeys(MoveCommand()));
        Assert.Empty(TaskValidator.CommandPhysicalKeys(ScrollCommand()));

        var comboKeys = TaskValidator.CommandPhysicalKeys(
            ComboCommand(new[] { WpfKey.LeftCtrl, WpfKey.LeftShift }, WpfKey.C)).ToArray();
        Assert.Equal(3, comboKeys.Length);
        Assert.Contains(PhysicalKey.FromKeyboard(WpfKey.LeftCtrl), comboKeys);
        Assert.Contains(PhysicalKey.FromKeyboard(WpfKey.LeftShift), comboKeys);
        Assert.Contains(PhysicalKey.FromKeyboard(WpfKey.C), comboKeys);
    }

    [Fact]
    public void 开关按键绑定占用实体键集合的口径()
    {
        Assert.Equal(
            new[] { PhysicalKey.FromKeyboard(WpfKey.F6) },
            TaskValidator.BindingPhysicalKeys(KeyBinding.FromKey(WpfKey.F6)).ToArray());

        Assert.Equal(
            new[] { PhysicalKey.FromMouse(MouseButton.X1) },
            TaskValidator.BindingPhysicalKeys(KeyBinding.FromMouse(MouseButton.X1)).ToArray());

        // 组合开关占用修饰键 + 主键：Ctrl+C 作开关时，C 键本身也被视为被开关占用，
        // 故它同样会与本任务内使用 C 的命令（如 Ctrl+C 命令）冲突。
        var comboBindingKeys = TaskValidator.BindingPhysicalKeys(
            KeyBinding.FromCombo(new[] { WpfKey.LeftCtrl }, WpfKey.C)).ToArray();
        Assert.Equal(2, comboBindingKeys.Length);
        Assert.Contains(PhysicalKey.FromKeyboard(WpfKey.LeftCtrl), comboBindingKeys);
        Assert.Contains(PhysicalKey.FromKeyboard(WpfKey.C), comboBindingKeys);
    }

    [Fact]
    public void 组合开关与本任务内使用主键的命令冲突()
    {
        // 这是"逐键比对"的直接后果，锁住它以免日后被改回"只比主键"。
        var task = NewTask(
            "任务",
            KeyBinding.FromCombo(new[] { WpfKey.LeftCtrl }, WpfKey.C),
            KeyCommand(WpfKey.C, TapMode.Hold));

        AssertViolations(Validate(task), ValidationRule.ToggleKeyNotOverlappingOwnCommands);
    }
}
