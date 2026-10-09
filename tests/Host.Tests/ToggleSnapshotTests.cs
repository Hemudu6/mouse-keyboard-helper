using System.Windows.Input;
using Core.Input;
using Core.Keys;
using Core.Model;

// System.Windows.Input 与 Core.Keys 都有 KeyBinding —— 本文件要领域模型那个，显式别名消歧。
using KeyBinding = Core.Keys.KeyBinding;

namespace Host.Tests;

/// <summary>
/// 开关按键快照的单测（设计文档 §3.4 / §3.4.1）。
///
/// <para>
/// <b>为什么这块逻辑必须在 Core 且必须单测</b>：钩子回调内"吞掉 / 照常"的判定
/// 完全由这张快照决定，而回调本身无法在测试进程里被系统调用（需真实桌面环境）。
/// 把判定逻辑抽到 Core 的纯逻辑类后，**唯一可测、也最该测**的部分就被覆盖住了；
/// 剩下的只有 P/Invoke 外壳。
/// </para>
/// <para>
/// 覆盖的需求判据：§3.4 逐任务消费口径（AC-33）、§3.4.1 同键冲突确定性合并（**防御路径**——
/// 生产路径上同主键已在保存期拒绝，见 §3.4.1 / `TaskValidator.IsSameToggleKey`）、
/// §3.4.1 族归一（"配右 Ctrl、按左 Ctrl"必须命中）、AC-02（禁用任务无反应）。
/// </para>
/// </summary>
public class ToggleSnapshotTests
{
    // ── 构建语义：启用 / 禁用 / 边界 ────────────────────────────────

    [Fact]
    public void 禁用任务不进快照()
    {
        // AC-02：禁用的任务点按开关键无任何反应。它若进了快照，钩子仍会把该键
        // 当开关键处理（甚至按配置吞掉用户的按键）——禁用语义的反面。
        var disabled = new TaskDefinition
        {
            Name = "禁用任务",
            Enabled = false,
            ToggleKey = KeyBinding.FromKey(Key.F6),
            ConsumeToggleInput = true,
        };

        var snapshot = ToggleSnapshot.Build(new[] { disabled });

        Assert.Equal(0, snapshot.Count);
        Assert.False(snapshot.Contains(PhysicalKey.FromKeyboard(Key.F6)));
        Assert.False(snapshot.TryGetConsume(PhysicalKey.FromKeyboard(Key.F6), out _));
    }

    [Fact]
    public void 禁用任务不污染同键的启用任务()
    {
        // 同一开关键的启用与禁用任务并存：合并结果只能来自启用任务。
        // 若禁用任务参与了合并，这里会得到 consume = true（被禁用任务的配置带偏）。
        var enabled = new TaskDefinition
        {
            Name = "启用任务",
            Enabled = true,
            ToggleKey = KeyBinding.FromKey(Key.F6),
            ConsumeToggleInput = false,
        };
        var disabled = new TaskDefinition
        {
            Name = "禁用任务",
            Enabled = false,
            ToggleKey = KeyBinding.FromKey(Key.F6),
            ConsumeToggleInput = true,
        };

        var snapshot = ToggleSnapshot.Build(new[] { enabled, disabled });

        Assert.Equal(1, snapshot.Count);
        Assert.True(snapshot.TryGetConsume(PhysicalKey.FromKeyboard(Key.F6), out var consume));
        Assert.False(consume);
    }

    [Fact]
    public void 空输入不抛且为空快照()
    {
        var snapshot = ToggleSnapshot.Build(Array.Empty<TaskDefinition>());

        Assert.Equal(0, snapshot.Count);
        Assert.False(snapshot.TryGetConsume(PhysicalKey.FromKeyboard(Key.F6), out _));
    }

    [Fact]
    public void null输入不抛且为空快照()
    {
        // 配置尚未加载时调用方传 null 属正常状态，不该让调用方四处判空。
        var snapshot = ToggleSnapshot.Build(null);

        Assert.Equal(0, snapshot.Count);
        Assert.False(snapshot.TryGetConsume(PhysicalKey.FromKeyboard(Key.F6), out _));
    }

    [Fact]
    public void 列表含null元素时跳过而不是抛()
    {
        // 畸形配置（JSON 数组含 null）不该让整张快照构建失败——
        // 那会让**全部**开关键同时失灵，代价远大于跳过一条坏记录。
        var good = new TaskDefinition
        {
            Enabled = true,
            ToggleKey = KeyBinding.FromKey(Key.F7),
            ConsumeToggleInput = true,
        };

        var snapshot = ToggleSnapshot.Build(new TaskDefinition?[] { null, good }!);

        Assert.Equal(1, snapshot.Count);
        Assert.True(snapshot.TryGetConsume(PhysicalKey.FromKeyboard(Key.F7), out var consume));
        Assert.True(consume);
    }

    [Fact]
    public void 未命中时consume恒为false()
    {
        var snapshot = ToggleSnapshot.Build(new[]
        {
            new TaskDefinition { Enabled = true, ToggleKey = KeyBinding.FromKey(Key.F6), ConsumeToggleInput = true },
        });

        Assert.False(snapshot.TryGetConsume(PhysicalKey.FromKeyboard(Key.A), out var consume));
        Assert.False(consume);
    }

    // ── 族归一：修饰键左右归一（§3.4.1，与按住表同源）────────────────

    [Fact]
    public void 右Ctrl配置可由左Ctrl命中()
    {
        // 快照构建时归一：配置的开关是右 Ctrl，快照里存的是族代表键（左 Ctrl）。
        var snapshot = ToggleSnapshot.Build(new[]
        {
            new TaskDefinition { Enabled = true, ToggleKey = KeyBinding.FromKey(Key.RightCtrl), ConsumeToggleInput = true },
        });

        Assert.True(snapshot.TryGetConsume(PhysicalKey.FromKeyboard(Key.LeftCtrl), out var consume));
        Assert.True(consume);
    }

    [Fact]
    public void 左Ctrl配置可由右Ctrl命中()
    {
        // 查询侧同样归一：钩子捕获到的是物理右 Ctrl，配置里存的是左 Ctrl，必须命中。
        var snapshot = ToggleSnapshot.Build(new[]
        {
            new TaskDefinition { Enabled = true, ToggleKey = KeyBinding.FromKey(Key.LeftCtrl), ConsumeToggleInput = false },
        });

        Assert.True(snapshot.TryGetConsume(PhysicalKey.FromKeyboard(Key.RightCtrl), out var consume));
        Assert.False(consume);
    }

    [Theory]
    [InlineData(Key.RightAlt, Key.LeftAlt)]
    [InlineData(Key.LeftAlt, Key.RightAlt)]
    [InlineData(Key.RightShift, Key.LeftShift)]
    [InlineData(Key.LeftShift, Key.RightShift)]
    [InlineData(Key.RWin, Key.LWin)]
    [InlineData(Key.LWin, Key.RWin)]
    public void 四族修饰键左右互通(Key configured, Key queried)
    {
        // 四个修饰键族（Ctrl / Alt / Shift / Win）归一口径一致；
        // 漏掉任何一族都会表现为"某类开关键配了却不生效"。
        var snapshot = ToggleSnapshot.Build(new[]
        {
            new TaskDefinition { Enabled = true, ToggleKey = KeyBinding.FromKey(configured), ConsumeToggleInput = true },
        });

        Assert.True(snapshot.TryGetConsume(PhysicalKey.FromKeyboard(queried), out var consume));
        Assert.True(consume);
    }

    [Fact]
    public void 非修饰键不参与归一并保持精确匹配()
    {
        // 归一不能把非修饰键也折叠掉：F6 与 F7 是不同实体键，
        // 若归一过度，"配 F6"会错误命中 F7 的按键。
        var snapshot = ToggleSnapshot.Build(new[]
        {
            new TaskDefinition { Enabled = true, ToggleKey = KeyBinding.FromKey(Key.F6), ConsumeToggleInput = true },
        });

        Assert.True(snapshot.TryGetConsume(PhysicalKey.FromKeyboard(Key.F6), out _));
        Assert.False(snapshot.TryGetConsume(PhysicalKey.FromKeyboard(Key.F7), out _));

        // 归一后仍与自身相等（幂等），故左 Ctrl 配置的左 Ctrl 查询不受影响。
        Assert.True(snapshot.Contains(PhysicalKey.FromKeyboard(Key.F6)));
    }

    [Fact]
    public void 修饰键与非修饰键不互相命中()
    {
        var snapshot = ToggleSnapshot.Build(new[]
        {
            new TaskDefinition { Enabled = true, ToggleKey = KeyBinding.FromKey(Key.LeftShift), ConsumeToggleInput = true },
        });

        Assert.True(snapshot.Contains(PhysicalKey.FromKeyboard(Key.RightShift)));
        Assert.False(snapshot.Contains(PhysicalKey.FromKeyboard(Key.LeftCtrl)));
        Assert.False(snapshot.Contains(PhysicalKey.FromKeyboard(Key.A)));
    }

    // ── 鼠标键：原样、可命中（§3.4 鼠标开关键同理）──────────────────

    [Fact]
    public void 鼠标键开关键原样入表且可命中()
    {
        // FR-TOGGLE-01 允许鼠标键作开关键；鼠标键没有左右族概念，不参与归一。
        var snapshot = ToggleSnapshot.Build(new[]
        {
            new TaskDefinition
            {
                Enabled = true,
                ToggleKey = KeyBinding.FromMouse(MouseKey.Right),
                ConsumeToggleInput = true,
            },
        });

        Assert.Equal(1, snapshot.Count);
        Assert.True(snapshot.TryGetConsume(PhysicalKey.FromMouse(MouseKey.Right), out var consume));
        Assert.True(consume);
    }

    [Fact]
    public void 鼠标键之间不互相命中且不与键盘键混淆()
    {
        var snapshot = ToggleSnapshot.Build(new[]
        {
            new TaskDefinition
            {
                Enabled = true,
                ToggleKey = KeyBinding.FromMouse(MouseKey.X1),
                ConsumeToggleInput = false,
            },
        });

        Assert.True(snapshot.Contains(PhysicalKey.FromMouse(MouseKey.X1)));
        Assert.False(snapshot.Contains(PhysicalKey.FromMouse(MouseKey.X2)));
        Assert.False(snapshot.Contains(PhysicalKey.FromMouse(MouseKey.Left)));
        // 鼠标键与键盘键是同一个联合类型的两支，不得混同：
        // 一个 MouseKey.X1 查询绝不能命中键盘侧的任何条目。
        Assert.False(snapshot.Contains(PhysicalKey.FromKeyboard(Key.F6)));
    }

    // ── 同键冲突：确定性合并（防御路径）──────────────────────────

    [Fact]
    public void 同键冲突任一为true即true_真在前()
    {
        var snapshot = ToggleSnapshot.Build(new[]
        {
            new TaskDefinition { Enabled = true, ToggleKey = KeyBinding.FromKey(Key.F6), ConsumeToggleInput = true },
            new TaskDefinition { Enabled = true, ToggleKey = KeyBinding.FromKey(Key.F6), ConsumeToggleInput = false },
        });

        Assert.Equal(1, snapshot.Count);
        Assert.True(snapshot.TryGetConsume(PhysicalKey.FromKeyboard(Key.F6), out var consume));
        Assert.True(consume);
    }

    [Fact]
    public void 同键冲突任一为true即true_假在前()
    {
        // 合并结果必须与遍历顺序无关——否则同一份配置在两次构建间会给出不同口径，
        // 直接违反 §3.4「同一配置每次走同一分支」。（防御路径，见 §3.4.1）
        var snapshot = ToggleSnapshot.Build(new[]
        {
            new TaskDefinition { Enabled = true, ToggleKey = KeyBinding.FromKey(Key.F6), ConsumeToggleInput = false },
            new TaskDefinition { Enabled = true, ToggleKey = KeyBinding.FromKey(Key.F6), ConsumeToggleInput = true },
        });

        Assert.Equal(1, snapshot.Count);
        Assert.True(snapshot.TryGetConsume(PhysicalKey.FromKeyboard(Key.F6), out var consume));
        Assert.True(consume);
    }

    [Fact]
    public void 同键全false时合并为false()
    {
        var snapshot = ToggleSnapshot.Build(new[]
        {
            new TaskDefinition { Enabled = true, ToggleKey = KeyBinding.FromKey(Key.F6), ConsumeToggleInput = false },
            new TaskDefinition { Enabled = true, ToggleKey = KeyBinding.FromKey(Key.F6), ConsumeToggleInput = false },
            new TaskDefinition { Enabled = true, ToggleKey = KeyBinding.FromKey(Key.F6), ConsumeToggleInput = false },
        });

        Assert.Equal(1, snapshot.Count);
        Assert.True(snapshot.TryGetConsume(PhysicalKey.FromKeyboard(Key.F6), out var consume));
        Assert.False(consume);
    }

    [Fact]
    public void 族归一后的同键也能合并()
    {
        // 一个任务配左 Ctrl、另一个配右 Ctrl：归一到同一个实体条目，
        // 故"任一为 true 即 true"同样适用于跨左右侧的冲突。
        var snapshot = ToggleSnapshot.Build(new[]
        {
            new TaskDefinition { Enabled = true, ToggleKey = KeyBinding.FromKey(Key.LeftCtrl), ConsumeToggleInput = false },
            new TaskDefinition { Enabled = true, ToggleKey = KeyBinding.FromKey(Key.RightCtrl), ConsumeToggleInput = true },
        });

        Assert.Equal(1, snapshot.Count);
        Assert.True(snapshot.TryGetConsume(PhysicalKey.FromKeyboard(Key.LeftCtrl), out var consume));
        Assert.True(consume);
        Assert.True(snapshot.TryGetConsume(PhysicalKey.FromKeyboard(Key.RightCtrl), out consume));
        Assert.True(consume);
    }

    [Fact]
    public void 三任务混合冲突合并为true()
    {
        var snapshot = ToggleSnapshot.Build(new[]
        {
            new TaskDefinition { Enabled = true, ToggleKey = KeyBinding.FromKey(Key.F6), ConsumeToggleInput = false },
            new TaskDefinition { Enabled = false, ToggleKey = KeyBinding.FromKey(Key.F6), ConsumeToggleInput = true },
            new TaskDefinition { Enabled = true, ToggleKey = KeyBinding.FromKey(Key.F6), ConsumeToggleInput = false },
        });

        Assert.Equal(1, snapshot.Count);
        Assert.True(snapshot.TryGetConsume(PhysicalKey.FromKeyboard(Key.F6), out var consume));
        Assert.False(consume);   // 唯一的 true 来自禁用任务，不参与合并
    }

    // ── 多任务共存：快照覆盖每一个启用任务的开关键 ──────────────────

    [Fact]
    public void 多任务各自开关键全部入表()
    {
        var snapshot = ToggleSnapshot.Build(new[]
        {
            new TaskDefinition { Enabled = true, ToggleKey = KeyBinding.FromKey(Key.F6), ConsumeToggleInput = true },
            new TaskDefinition { Enabled = true, ToggleKey = KeyBinding.FromKey(Key.F7), ConsumeToggleInput = false },
            new TaskDefinition { Enabled = true, ToggleKey = KeyBinding.FromMouse(MouseKey.Middle), ConsumeToggleInput = true },
        });

        Assert.Equal(3, snapshot.Count);
        Assert.True(snapshot.TryGetConsume(PhysicalKey.FromKeyboard(Key.F6), out var f6));
        Assert.True(f6);
        Assert.True(snapshot.TryGetConsume(PhysicalKey.FromKeyboard(Key.F7), out var f7));
        Assert.False(f7);
        Assert.True(snapshot.TryGetConsume(PhysicalKey.FromMouse(MouseKey.Middle), out var middle));
        Assert.True(middle);
    }

    [Fact]
    public void 组合形态开关键需主键与修饰族全部按下才命中()
    {
        // 组合形态（Ctrl+Alt+F8）：设计文档 §3.4.1 口径——
        // 命中 = 主键命中 **且** 条目的修饰键族集合全部处于按下态。
        var snapshot = ToggleSnapshot.Build(new[]
        {
            new TaskDefinition
            {
                Enabled = true,
                ToggleKey = KeyBinding.FromCombo(new[] { Key.LeftCtrl, Key.LeftAlt }, Key.F8),
                ConsumeToggleInput = true,
            },
        });

        var f8 = PhysicalKey.FromKeyboard(Key.F8);
        Assert.Equal(1, snapshot.Count);

        // 单按触发键：不命中（修饰键未按下）。
        Assert.False(snapshot.TryGetConsume(f8, new HashSet<string>(), out _));

        // 调用方直接传 null（无按下修饰键的另一表达）：同样不命中。
        // null 是公开契约的一部分（见 TryGetConsume 的 <paramref name="pressedFamilies"/> 说明）。
        Assert.False(snapshot.TryGetConsume(f8, null, out _));

        // 只按部分修饰键：不命中。
        Assert.False(snapshot.TryGetConsume(f8, new HashSet<string> { "Ctrl" }, out _));

        // 修饰族全部按下：命中，且消费口径为吞掉。
        Assert.True(snapshot.TryGetConsume(f8, new HashSet<string> { "Ctrl", "Alt" }, out var both));
        Assert.True(both);

        // 按下的是超集（多按了 Shift）：仍应命中（子集判定，而非集合相等）。
        Assert.True(snapshot.TryGetConsume(f8, new HashSet<string> { "Ctrl", "Alt", "Shift" }, out var superset));
        Assert.True(superset);
    }

    [Fact]
    public void 组合形态修饰键左右同族均算命中()
    {
        // 与 TaskValidator.IsSameBinding 同族口径：修饰键按**族名**判等，左右同族视为同一个。
        // 配置里写左 Ctrl，钩子侧按下右 Ctrl，必须同样命中——否则「配右 Ctrl、按左 Ctrl」会失灵。
        var snapshot = ToggleSnapshot.Build(new[]
        {
            new TaskDefinition
            {
                Enabled = true,
                ToggleKey = KeyBinding.FromCombo(new[] { Key.RightCtrl }, Key.F8),
                ConsumeToggleInput = false,
            },
        });

        // 快照内族名统一为 "Ctrl"（由 KeyCatalog.ModifierFamily 归一），钩子侧按压左 Ctrl 亦然。
        Assert.True(snapshot.TryGetConsume(
            PhysicalKey.FromKeyboard(Key.F8), new HashSet<string> { "Ctrl" }, out var consume));
        Assert.False(consume);
    }

    [Fact]
    public void 单键形态不受修饰键按下影响()
    {
        // §3.4.1：单键与鼠标键形态**不做任何额外判定**——用户按裸 F6 时命中，
        // 按 Ctrl+F6 时同样命中（主键即全部条件）。这是本口径的固有含义，
        // 与之相应的 UI 提示见设计文档 §5.2「裸键误触提示」。
        var snapshot = ToggleSnapshot.Build(new[]
        {
            new TaskDefinition { Enabled = true, ToggleKey = KeyBinding.FromKey(Key.F6), ConsumeToggleInput = true },
        });

        var f6 = PhysicalKey.FromKeyboard(Key.F6);
        Assert.True(snapshot.TryGetConsume(f6, new HashSet<string>(), out var bare));
        Assert.True(bare);
        Assert.True(snapshot.TryGetConsume(f6, new HashSet<string> { "Ctrl" }, out var withModifier));
        Assert.True(withModifier);
    }

    [Fact]
    public void 同主键不同修饰集合并为一且两者均能命中()
    {
        // 设计文档 §3.4.1「同键冲突确定性合并」的**防御路径**：字典键是**主键**，修饰集进值。
        // 生产路径上同主键在保存期已被拒绝（TaskValidator.IsSameToggleKey），
        // 故本用例覆盖的是「手改坏配置文件直接绕过 UI」时的自愈：
        // 两个任务 Ctrl+F8 与 Alt+F8 共用主键 F8 → 合并为一条，修饰集取**并集**。
        // 合并后两者都不失效：Ctrl+Alt+F8 同时满足两者。
        var snapshot = ToggleSnapshot.Build(new[]
        {
            new TaskDefinition
            {
                Enabled = true,
                ToggleKey = KeyBinding.FromCombo(new[] { Key.LeftCtrl }, Key.F8),
                ConsumeToggleInput = false,
            },
            new TaskDefinition
            {
                Enabled = true,
                ToggleKey = KeyBinding.FromCombo(new[] { Key.LeftAlt }, Key.F8),
                ConsumeToggleInput = true,
            },
        });

        var f8 = PhysicalKey.FromKeyboard(Key.F8);
        Assert.Equal(1, snapshot.Count);

        // 合并后修饰集为 {Ctrl, Alt}，两者都按下才命中；consume 取或（true）。
        Assert.False(snapshot.TryGetConsume(f8, new HashSet<string> { "Ctrl" }, out _));
        Assert.False(snapshot.TryGetConsume(f8, new HashSet<string> { "Alt" }, out _));
        Assert.True(snapshot.TryGetConsume(f8, new HashSet<string> { "Ctrl", "Alt" }, out var consume));
        Assert.True(consume);
    }

    // ── 不可变性 / 空快照 / 手工条目构造 ──────────────────────────

    [Fact]
    public void 空快照是空且查询恒不命中()
    {
        Assert.Equal(0, ToggleSnapshot.Empty.Count);
        Assert.False(ToggleSnapshot.Empty.TryGetConsume(PhysicalKey.FromKeyboard(Key.F6), out var consume));
        Assert.False(consume);
        Assert.False(ToggleSnapshot.Empty.Contains(PhysicalKey.FromMouse(MouseKey.Left)));
    }

    [Fact]
    public void 已构建快照不受源任务列表后续变更影响()
    {
        // 不可变语义的直接判据：快照发布后，源任务对象再被改动（用户改配置）
        // 不得让已发布的快照改变——否则钩子读到的口径会在无替换的情况下漂移。
        var task = new TaskDefinition
        {
            Enabled = true,
            ToggleKey = KeyBinding.FromKey(Key.F6),
            ConsumeToggleInput = false,
        };
        var snapshot = ToggleSnapshot.Build(new[] { task });

        task.Enabled = false;
        task.ConsumeToggleInput = true;
        task.ToggleKey = KeyBinding.FromKey(Key.F9);

        Assert.Equal(1, snapshot.Count);
        Assert.True(snapshot.TryGetConsume(PhysicalKey.FromKeyboard(Key.F6), out var consume));
        Assert.False(consume);
        Assert.False(snapshot.Contains(PhysicalKey.FromKeyboard(Key.F9)));
    }

    [Fact]
    public void 手工条目构造同样归一并合并()
    {
        var snapshot = ToggleSnapshot.FromEntries(new[]
        {
            new KeyValuePair<PhysicalKey, bool>(PhysicalKey.FromKeyboard(Key.RightCtrl), false),
            new KeyValuePair<PhysicalKey, bool>(PhysicalKey.FromKeyboard(Key.LeftCtrl), true),
            new KeyValuePair<PhysicalKey, bool>(PhysicalKey.FromMouse(MouseKey.X2), true),
        });

        Assert.Equal(2, snapshot.Count);
        Assert.True(snapshot.TryGetConsume(PhysicalKey.FromKeyboard(Key.RightCtrl), out var ctrl));   // 归一后合并为 true
        Assert.True(ctrl);
        Assert.True(snapshot.TryGetConsume(PhysicalKey.FromMouse(MouseKey.X2), out var x2));
        Assert.True(x2);
    }

    [Fact]
    public void null条目序列按空处理()
    {
        Assert.Equal(0, ToggleSnapshot.FromEntries(null).Count);
    }

    [Fact]
    public void 计数与条目数一致()
    {
        // Count 是诊断与测试的观测面，必须与 Entries 的实际条目数一致
        // （两处若各算各的，UI 或日志会显示一个对不上的数字）。
        var snapshot = ToggleSnapshot.Build(new[]
        {
            new TaskDefinition { Enabled = true, ToggleKey = KeyBinding.FromKey(Key.LeftCtrl), ConsumeToggleInput = true },
            new TaskDefinition { Enabled = true, ToggleKey = KeyBinding.FromKey(Key.RightCtrl), ConsumeToggleInput = false },
            new TaskDefinition { Enabled = true, ToggleKey = KeyBinding.FromKey(Key.F6), ConsumeToggleInput = true },
            new TaskDefinition { Enabled = false, ToggleKey = KeyBinding.FromKey(Key.F7), ConsumeToggleInput = true },
        });

        Assert.Equal(2, snapshot.Count);
        Assert.Equal(snapshot.Count, snapshot.Entries.Count);
    }

    [Fact]
    public void 按下与松开同口径_快照只按按键判定()
    {
        // §3.4：同一开关键的按下与松开必须同等对待（都吞或都放行）。
        // 设计上这条由"快照只按按键判定、不带按下/松开维度"天然保证——
        // 本用例把该结构性保证固定下来，防止将来有人往快照里加入方向维度。
        var snapshot = ToggleSnapshot.Build(new[]
        {
            new TaskDefinition { Enabled = true, ToggleKey = KeyBinding.FromKey(Key.F6), ConsumeToggleInput = true },
        });

        // 同一按键连续查询（对应按下与松开两次回调）必须给出同一口径。
        for (var i = 0; i < 2; i++)
        {
            Assert.True(snapshot.TryGetConsume(PhysicalKey.FromKeyboard(Key.F6), out var consume));
            Assert.True(consume);
        }
    }

    // ── 任务 Id 查表（§5.4 方案 A）──────────────────────────────

    [Fact]
    public void 取任务Id_单键命中返回该任务Id()
    {
        var a = new TaskDefinition { Enabled = true, ToggleKey = KeyBinding.FromKey(Key.F6), ConsumeToggleInput = true };
        var b = new TaskDefinition { Enabled = true, ToggleKey = KeyBinding.FromKey(Key.F7), ConsumeToggleInput = false };

        var snapshot = ToggleSnapshot.Build(new[] { a, b });

        Assert.True(snapshot.TryGetTaskId(PhysicalKey.FromKeyboard(Key.F6), null, out var idA));
        Assert.Equal(a.Id, idA);

        Assert.True(snapshot.TryGetTaskId(PhysicalKey.FromKeyboard(Key.F7), null, out var idB));
        Assert.Equal(b.Id, idB);
    }

    [Fact]
    public void 取任务Id_未入表或未启用返回失败且Id为空()
    {
        var disabled = new TaskDefinition { Enabled = false, ToggleKey = KeyBinding.FromKey(Key.F7), ConsumeToggleInput = true };
        var snapshot = ToggleSnapshot.Build(new[]
        {
            new TaskDefinition { Enabled = true, ToggleKey = KeyBinding.FromKey(Key.F6), ConsumeToggleInput = true },
            disabled,
        });

        // 未入表的键：失败，且 out 参数必须是 Guid.Empty（而非上次的残留值）。
        Assert.False(snapshot.TryGetTaskId(PhysicalKey.FromKeyboard(Key.F9), null, out var missing));
        Assert.Equal(Guid.Empty, missing);

        // 未启用任务的开关键不入表（与 TryGetConsume 同口径）。
        Assert.False(snapshot.TryGetTaskId(PhysicalKey.FromKeyboard(Key.F7), null, out var off));
        Assert.Equal(Guid.Empty, off);

        // 空快照同理。
        Assert.False(ToggleSnapshot.Empty.TryGetTaskId(PhysicalKey.FromKeyboard(Key.F6), null, out var empty));
        Assert.Equal(Guid.Empty, empty);
    }

    [Fact]
    public void 取任务Id_组合形态与消费口径同源命中()
    {
        // 本用例是 §5.4 方案 A 的**核心约束**：任务 Id 查表必须与 TryGetConsume
        // 给出完全一致的命中判定。二者不同源，就会出现"钩子吞了键、Engine 找不到任务"
        // 或"Engine 启动了任务、钩子却没吞"的静默失配。
        var combo = new TaskDefinition
        {
            Enabled = true,
            ToggleKey = KeyBinding.FromCombo(new[] { Key.LeftCtrl, Key.LeftAlt }, Key.F8),
            ConsumeToggleInput = true,
        };
        var snapshot = ToggleSnapshot.Build(new[] { combo });
        var f8 = PhysicalKey.FromKeyboard(Key.F8);

        var cases = new (string Name, IReadOnlyCollection<string>? Pressed, bool ShouldHit)[]
        {
            ("空集", new HashSet<string>(), false),
            ("null", null, false),
            ("仅 Ctrl", new HashSet<string> { "Ctrl" }, false),
            ("仅 Alt", new HashSet<string> { "Alt" }, false),
            ("Ctrl+Alt", new HashSet<string> { "Ctrl", "Alt" }, true),
            ("超集", new HashSet<string> { "Ctrl", "Alt", "Shift" }, true),
        };

        foreach (var (name, pressed, shouldHit) in cases)
        {
            var consumed = snapshot.TryGetConsume(f8, pressed, out _);
            var found = snapshot.TryGetTaskId(f8, pressed, out var id);

            Assert.Equal(consumed, found);
            Assert.Equal(shouldHit, consumed);
            Assert.Equal(consumed ? combo.Id : Guid.Empty, id);
        }
    }

    [Fact]
    public void 取任务Id_鼠标键形态()
    {
        var task = new TaskDefinition
        {
            Enabled = true,
            ToggleKey = KeyBinding.FromMouse(MouseKey.Middle),
            ConsumeToggleInput = false,
        };
        var snapshot = ToggleSnapshot.Build(new[] { task });

        Assert.True(snapshot.TryGetTaskId(PhysicalKey.FromMouse(MouseKey.Middle), null, out var hit));
        Assert.Equal(task.Id, hit);

        Assert.False(snapshot.TryGetTaskId(PhysicalKey.FromMouse(MouseKey.Right), null, out _));
    }

    [Fact]
    public void 取任务Id_左右修饰键归一后仍命中同一任务()
    {
        // 族归一由快照内部完成，调用方传原始物理键——本用例固定住这一点：
        // 配置里写 LeftCtrl、用户实际按 RightCtrl，仍应命中同一个任务。
        var task = new TaskDefinition
        {
            Enabled = true,
            ToggleKey = KeyBinding.FromCombo(new[] { Key.LeftCtrl }, Key.F8),
            ConsumeToggleInput = true,
        };
        var snapshot = ToggleSnapshot.Build(new[] { task });

        Assert.True(snapshot.TryGetTaskId(PhysicalKey.FromKeyboard(Key.F8), new HashSet<string> { "Ctrl" }, out var id));
        Assert.Equal(task.Id, id);
    }

    [Fact]
    public void 取任务Id_手工条目无任务归属()
    {
        // FromEntries 是测试/上层直给的手工条目，没有 TaskDefinition 可言，
        // 以 Guid.Empty 表达"有开关、无任务归属"——不能让调用方误以为命中了某个任务。
        var snapshot = ToggleSnapshot.FromEntries(new[]
        {
            new KeyValuePair<PhysicalKey, bool>(PhysicalKey.FromKeyboard(Key.F6), true),
        });

        Assert.True(snapshot.TryGetTaskId(PhysicalKey.FromKeyboard(Key.F6), null, out var id));
        Assert.Equal(Guid.Empty, id);
    }
}
