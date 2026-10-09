using System.Windows.Input;
using Core.Input;
using Core.Keys;

namespace Host.Tests;

/// <summary>
/// <see cref="InputSimulator"/> 的单元测试（设计文档 §3.1.1 / §3.1.2 / §3.1.3）。
///
/// <para>
/// 验证手段是**假注入出口 + 假时钟**：把 <see cref="IInputSink"/> 记录成一个事件序列，
/// 于是"引用计数是否走对""补发顺序是否主键先松""失败是否只记一次日志"这些
/// 全部是可断言的纯逻辑事实，无需真的操作系统输入、也无需真实等待。
/// </para>
/// <para>
/// 验收口径对应：AC-10 / AC-14b / AC-17 / AC-32（补发松开与组合键顺序）、
/// FR-SEQ-07、FR-TAP-06、FR-RUN-05、NFR-07 / AC-25（全停不残留按下）。
/// </para>
/// </summary>
public sealed class InputSimulatorTests
{
    /// <summary>记录注入事件的假出口。返回值为可配置的"本次成功注入的事件数"。</summary>
    private sealed class FakeSink : IInputSink
    {
        public readonly List<string> Events = new();

        /// <summary>下一次注入返回的事件数（模拟 SendInput 失败：0 = 未注入）。</summary>
        public int NextResult = 1;

        /// <summary>模拟适配器缓存的最近一次 Win32 错误码。</summary>
        public int LastInjectionError { get; set; }

        public int KeyDown(Key virtualKey)
        {
            Events.Add($"K↓{virtualKey}");
            return NextResult;
        }

        public int KeyUp(Key virtualKey)
        {
            Events.Add($"K↑{virtualKey}");
            return NextResult;
        }

        public int MouseDown(MouseKey button)
        {
            Events.Add($"M↓{button}");
            return NextResult;
        }

        public int MouseUp(MouseKey button)
        {
            Events.Add($"M↑{button}");
            return NextResult;
        }

        public int MoveAbsolute(int x, int y)
        {
            Events.Add($"MOVE({x},{y})");
            return NextResult;
        }

        public int Scroll(bool vertical, int notches)
        {
            Events.Add($"SCROLL(v={vertical},{notches})");
            return NextResult;
        }
    }

    private static (InputSimulator Sim, FakeSink Sink) Create(Func<long>? now = null)
    {
        var sink = new FakeSink();
        return (new InputSimulator(sink, now: now), sink);
    }

    // ---- §3.1.1 引用计数 ----

    [Fact]
    public void HoldKey_首次按住_发出一次按下()
    {
        var (sim, sink) = Create();
        var task = Guid.NewGuid();

        sim.HoldKey(Key.A, task);

        Assert.Equal(new[] { "K↓A" }, sink.Events);
        Assert.Equal(1, sim.HeldCount);
    }

    [Fact]
    public void HoldKey_两个任务按住同一键_只发一次按下_计数为二()
    {
        var (sim, sink) = Create();
        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();

        sim.HoldKey(Key.A, t1);
        sim.HoldKey(Key.A, t2);

        // 物理键盘只有一份状态：第二次按住不得重发按下。
        Assert.Equal(new[] { "K↓A" }, sink.Events);
        Assert.Equal(1, sim.HeldCount);
    }

    [Fact]
    public void ReleaseAll_一个任务停止_另一个任务仍按住时_不得松开()
    {
        var (sim, sink) = Create();
        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();

        sim.HoldKey(Key.A, t1);
        sim.HoldKey(Key.A, t2);
        sim.ReleaseAll(t1);

        // t2 还按着 → 不得补发松开（否则违反 FR-SEQ-07 的意图：键被提前松掉）。
        Assert.Equal(new[] { "K↓A" }, sink.Events);
        Assert.True(sim.IsHeld(PhysicalKey.FromKeyboard(Key.A)));
    }

    [Fact]
    public void ReleaseAll_最后一个属主停止_才真正松开并移除条目()
    {
        var (sim, sink) = Create();
        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();

        sim.HoldKey(Key.A, t1);
        sim.HoldKey(Key.A, t2);
        sim.ReleaseAll(t1);
        sim.ReleaseAll(t2);

        Assert.Equal(new[] { "K↓A", "K↑A" }, sink.Events);
        Assert.Equal(0, sim.HeldCount);
    }

    [Fact]
    public void HoldKey_同一属主重复按住同一键_幂等_不重复计数()
    {
        var (sim, sink) = Create();
        var task = Guid.NewGuid();

        sim.HoldKey(Key.A, task);
        sim.HoldKey(Key.A, task);
        sim.ReleaseAll(task);

        // 若重复按住被计成 2，则这一次 ReleaseAll 只减 1，键会永远卡住——这是本测试要挡住的事故。
        Assert.Equal(new[] { "K↓A", "K↑A" }, sink.Events);
        Assert.Equal(0, sim.HeldCount);
    }

    [Fact]
    public void ReleaseAll_未持有任何键的任务_空操作()
    {
        var (sim, sink) = Create();

        sim.ReleaseAll(Guid.NewGuid());

        Assert.Empty(sink.Events);
    }

    [Fact]
    public void 修饰键_左右归一_按住左Ctrl后释放右Ctrl_能松掉()
    {
        var (sim, sink) = Create();
        var task = Guid.NewGuid();

        sim.HoldKey(Key.LeftCtrl, task);
        sim.ReleaseKey(Key.RightCtrl, task);

        // 不归一的话，右 Ctrl 找不到条目 → 引用永不归零 → 键卡在按下（AC-10 类事故）。
        // 松开注入的也必须是**归一后**的键：否则就是“按左 Ctrl、松右 Ctrl”——
        // 左 Ctrl 永久卡在按下状态（物理键盘上左右 Ctrl 是两个虚拟键码）。
        Assert.Equal(new[] { "K↓LeftCtrl", "K↑LeftCtrl" }, sink.Events);
        Assert.Equal(0, sim.HeldCount);
    }

    // ---- FR-TAP-06 / §4.4 补发顺序 ----

    [Fact]
    public void ReleaseAll_组合键按住_主键先松_修饰键后松()
    {
        var (sim, sink) = Create();
        var task = Guid.NewGuid();

        sim.HoldCombo(new[] { Key.LeftCtrl }, Key.C, task);
        sink.Events.Clear();
        sim.ReleaseAll(task);

        // FR-TAP-06 / AC-14b：修饰键先松会让 C 以单键形态被系统采到。
        Assert.Equal(new[] { "K↑C", "K↑LeftCtrl" }, sink.Events);
    }

    [Fact]
    public void ReleaseAll_多修饰键组合_主键先于全部修饰键松开()
    {
        var (sim, sink) = Create();
        var task = Guid.NewGuid();

        sim.HoldCombo(new[] { Key.LeftCtrl, Key.LeftShift, Key.LeftAlt }, Key.C, task);
        sink.Events.Clear();
        sim.ReleaseAll(task);

        Assert.Equal(4, sink.Events.Count);
        Assert.Equal("K↑C", sink.Events[0]);
        Assert.Contains("K↑LeftCtrl", sink.Events.Skip(1));
        Assert.Contains("K↑LeftShift", sink.Events.Skip(1));
        Assert.Contains("K↑LeftAlt", sink.Events.Skip(1));
    }

    [Fact]
    public void HoldCombo_按下顺序_修饰键先于主键()
    {
        var (sim, sink) = Create();
        var task = Guid.NewGuid();

        sim.HoldCombo(new[] { Key.LeftCtrl, Key.LeftShift }, Key.C, task);

        // FR-TAP-06：修饰键↓ → 主键↓。
        Assert.Equal(new[] { "K↓LeftCtrl", "K↓LeftShift", "K↓C" }, sink.Events);
    }

    [Fact]
    public void TapCombo_事件序列为_修饰键下_主键下上_修饰键上()
    {
        var (sim, sink) = Create();

        sim.TapCombo(new[] { Key.LeftCtrl }, Key.C);

        // AC-14b 的事件序列口径。
        Assert.Equal(new[] { "K↓LeftCtrl", "K↓C", "K↑C", "K↑LeftCtrl" }, sink.Events);
        Assert.Equal(0, sim.HeldCount);
    }

    // ---- 点按 / 瞬时操作 ----

    [Fact]
    public void TapKey_按下后立即松开_且不残留按住状态()
    {
        var (sim, sink) = Create();

        sim.TapKey(Key.A);

        Assert.Equal(new[] { "K↓A", "K↑A" }, sink.Events);
        Assert.Equal(0, sim.HeldCount);
    }

    [Fact]
    public void TapMouseButton_按下后立即松开()
    {
        var (sim, sink) = Create();

        sim.TapMouseButton(MouseKey.Left);

        Assert.Equal(new[] { "M↓Left", "M↑Left" }, sink.Events);
    }

    [Fact]
    public void MoveMouseAbsolute_单次注入_绝对坐标原样传递()
    {
        var (sim, sink) = Create();

        sim.MoveMouseAbsolute(500, 300);

        Assert.Equal(new[] { "MOVE(500,300)" }, sink.Events);
    }

    [Fact]
    public void ScrollWheel_上滚为正_下滚为负()
    {
        var (sim, sink) = Create();

        sim.ScrollWheel(ScrollDirection.Up, 3);
        sim.ScrollWheel(ScrollDirection.Down, 3);

        Assert.Equal(new[] { "SCROLL(v=True,3)", "SCROLL(v=True,-3)" }, sink.Events);
    }

    // ---- §3.1.3 全停 ----

    [Fact]
    public void ReleaseAllAll_释放全部属主_无残留()
    {
        var (sim, sink) = Create();
        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();

        sim.HoldKey(Key.A, t1);
        sim.HoldKey(Key.B, t2);
        sim.HoldMouseButton(MouseKey.Right, t2);

        sim.ReleaseAllAll();

        // NFR-07 / AC-25：全停时不残留任何按下状态（AC-17 的退出场景同此）。
        Assert.Equal(0, sim.HeldCount);

        // 不绑定字典枚举顺序：只断言“每个按下的键都补发了对应的松开”。
        var downs = sink.Events.Where(e => e.StartsWith('K') && e[1] == '↓').ToArray();
        var ups = sink.Events.Where(e => e.StartsWith('K') && e[1] == '↑').ToArray();
        Assert.Equal(
            downs.Select(e => "K↑" + e[2..]).OrderBy(e => e, StringComparer.Ordinal),
            ups.OrderBy(e => e, StringComparer.Ordinal));
    }

    [Fact]
    public void ReleaseAllAll_全部释放_鼠标键也补发松开()
    {
        var (sim, sink) = Create();
        var t2 = Guid.NewGuid();

        sim.HoldMouseButton(MouseKey.Right, t2);
        sim.HoldKey(Key.A, t2);
        sink.Events.Clear();

        sim.ReleaseAllAll();

        Assert.Equal(0, sim.HeldCount);
        Assert.Contains("M↑Right", sink.Events);
        Assert.Contains("K↑A", sink.Events);
    }

    [Fact]
    public void ReleaseAllAll_共享键_只松一次()
    {
        var (sim, sink) = Create();
        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();

        sim.HoldKey(Key.A, t1);
        sim.HoldKey(Key.A, t2);
        sink.Events.Clear();

        sim.ReleaseAllAll();

        // 引用计数归零一次 → 只发一次松开（重复松开是多余的注入动作）。
        Assert.Equal(new[] { "K↑A" }, sink.Events);
        Assert.Equal(0, sim.HeldCount);
    }

    [Fact]
    public void ReleaseAllAll_空表_空操作()
    {
        var (sim, sink) = Create();

        sim.ReleaseAllAll();

        Assert.Empty(sink.Events);
    }

    // ---- §3.1.2 失败不中断 + 日志时间窗聚合 ----

    [Fact]
    public void 注入失败_不抛异常_不中断后续注入()
    {
        var (sim, sink) = Create();
        sink.NextResult = 0; // 模拟 UIPI 拒绝

        var ex = Record.Exception(() => sim.MoveMouseAbsolute(10, 10));
        sim.MoveMouseAbsolute(20, 20);

        Assert.Null(ex);
        Assert.Equal(new[] { "MOVE(10,10)", "MOVE(20,20)" }, sink.Events);
    }

    [Fact]
    public void 注入失败_同一动作在时间窗内_只记一条日志()
    {
        long clock = 0;
        var sink = new FakeSink { NextResult = 0 };
        var logs = new List<string>();
        var sim = new InputSimulator(sink, logs.Add, () => clock, failureWindowMs: 60_000);

        // 连发 100 次失败：若逐条记日志，1000 次/秒的命令会瞬间冲垮日志（设计文档 §3.1.2）。
        for (var i = 0; i < 100; i++)
        {
            sim.MoveMouseAbsolute(i, i);
        }

        Assert.Single(logs);
    }

    [Fact]
    public void 注入失败_窗口过去后_可以再记一条()
    {
        long clock = 0;
        var sink = new FakeSink { NextResult = 0 };
        var logs = new List<string>();
        var sim = new InputSimulator(sink, logs.Add, () => clock, failureWindowMs: 1_000);

        sim.MoveMouseAbsolute(1, 1);      // 窗口起点：记
        clock = 500;
        sim.MoveMouseAbsolute(2, 2);      // 窗口内：不记
        clock = 1_000;
        sim.MoveMouseAbsolute(3, 3);      // 窗口已过：记

        Assert.Equal(2, logs.Count);
    }

    [Fact]
    public void 注入失败_不同动作各自独立计时()
    {
        long clock = 0;
        var sink = new FakeSink { NextResult = 0 };
        var logs = new List<string>();
        var sim = new InputSimulator(sink, logs.Add, () => clock, failureWindowMs: 60_000);

        sim.MoveMouseAbsolute(1, 1);
        sim.ScrollWheel(ScrollDirection.Down, 1);

        // 不同动作是两个不同问题，不该互相压掉对方的日志。
        Assert.Equal(2, logs.Count);
    }

    [Fact]
    public void 注入成功_不记日志()
    {
        var sink = new FakeSink(); // NextResult 默认 1 = 成功
        var logs = new List<string>();
        var sim = new InputSimulator(sink, logs.Add);

        sim.MoveMouseAbsolute(1, 1);
        sim.ScrollWheel(ScrollDirection.Up, 2);

        Assert.Empty(logs);
    }

    [Fact]
    public void 注入失败_日报事件与日志回调同时触发()
    {
        var sink = new FakeSink { NextResult = 0 };
        var viaCallback = new List<string>();
        var viaEvent = new List<string>();
        var sim = new InputSimulator(sink, viaCallback.Add);
        sim.InjectionFailed += viaEvent.Add;

        sim.MoveMouseAbsolute(1, 1);

        Assert.Single(viaCallback);
        Assert.Equal(viaCallback, viaEvent);
    }

    [Fact]
    public void 注入失败_日志带失败类型_错误码来自适配器()
    {
        var sink = new FakeSink { NextResult = 0, LastInjectionError = 5 };
        var logs = new List<string>();
        var sim = new InputSimulator(sink, logs.Add);

        sim.MoveMouseAbsolute(1, 1);

        // 设计文档 §3.1.2：失败日志要能支撑排障，光写“失败了”没有价值。
        Assert.Single(logs);
        Assert.Contains("错误码 5", logs[0]);
    }

    [Fact]
    public void 注入失败_无错误码时_记为原因未知()
    {
        var sink = new FakeSink { NextResult = 0, LastInjectionError = 0 };
        var logs = new List<string>();
        var sim = new InputSimulator(sink, logs.Add);

        sim.MoveMouseAbsolute(1, 1);

        Assert.Single(logs);
        Assert.Contains("原因未知", logs[0]);
    }

    [Theory]
    [InlineData("hold")]
    [InlineData("release")]
    [InlineData("tap")]
    [InlineData("holdMouse")]
    [InlineData("releaseMouse")]
    [InlineData("tapMouse")]
    [InlineData("holdCombo")]
    [InlineData("tapCombo")]
    [InlineData("releaseAll")]
    [InlineData("releaseAllAll")]
    public void 注入失败_每一条注入路径都上报(string path)
    {
        // 🔴 回归：只给“鼠标移动/滚轮”两条路径查返回值是不够的——
        // 键盘与鼠标按键的注入才是卡键事故的主战场，它们失败必须同样可见。
        var sink = new FakeSink { NextResult = 0 };
        var logs = new List<string>();
        var sim = new InputSimulator(sink, logs.Add);
        var task = Guid.NewGuid();

        switch (path)
        {
            case "hold": sim.HoldKey(Key.A, task); break;
            case "release": sim.HoldKey(Key.A, task); sim.ReleaseKey(Key.A, task); break;
            case "tap": sim.TapKey(Key.A); break;
            case "holdMouse": sim.HoldMouseButton(MouseKey.Left, task); break;
            case "releaseMouse": sim.HoldMouseButton(MouseKey.Left, task); sim.ReleaseMouseButton(MouseKey.Left, task); break;
            case "tapMouse": sim.TapMouseButton(MouseKey.Left); break;
            case "holdCombo": sim.HoldCombo(new[] { Key.LeftCtrl }, Key.C, task); break;
            case "tapCombo": sim.TapCombo(new[] { Key.LeftCtrl }, Key.C); break;
            case "releaseAll": sim.HoldKey(Key.A, task); sim.ReleaseAll(task); break;
            case "releaseAllAll": sim.HoldKey(Key.A, task); sim.ReleaseAllAll(); break;
            default: throw new InvalidOperationException(path);
        }

        Assert.NotEmpty(logs);
    }

    [Fact]
    public void 按住修饰键_按下侧也注入归一代表键()
    {
        var (sim, sink) = Create();

        sim.HoldKey(Key.RightCtrl, Guid.NewGuid());

        // 按下侧若注入原始键（RightCtrl），停止时注入的是归一键（LeftCtrl）——
        // 右 Ctrl 永久卡在按下状态。两侧必须同口径。
        Assert.Equal(new[] { "K↓LeftCtrl" }, sink.Events);
    }

    [Fact]
    public void 按住组合键_修饰键按下侧也注入归一代表键()
    {
        var (sim, sink) = Create();

        sim.HoldCombo(new[] { Key.RightShift }, Key.C, Guid.NewGuid());

        Assert.Equal(new[] { "K↓LeftShift", "K↓C" }, sink.Events);
    }

    [Fact]
    public void 点按修饰键_按配置原样注入不参与归一()
    {
        // 设计文档 §3.1：归一只为对齐按住表中的同一个实体条目；
        // 点按链路（Tap*）不进表，故按配置**原样注入**——右 Ctrl 就点右 Ctrl。
        var (sim, sink) = Create();

        sim.TapKey(Key.RightCtrl);

        Assert.Equal(new[] { "K↓RightCtrl", "K↑RightCtrl" }, sink.Events);
    }

    [Fact]
    public void 点按组合键_修饰键按配置原样注入不参与归一()
    {
        var (sim, sink) = Create();

        sim.TapCombo(new[] { Key.RightShift }, Key.C);

        Assert.Equal(new[] { "K↓RightShift", "K↓C", "K↑C", "K↑RightShift" }, sink.Events);
    }


    [Fact]
    public void 日志回调_在锁外调用_回调内重入模拟器不死锁()
    {
        // 🟡 回归：若在持有 _gate 时就回调，回调再进本类（重入）会立即死锁。
        // 用一个带超时的重入回调把这件事变成可断言的失败。
        var sink = new FakeSink { NextResult = 0 };
        InputSimulator? sim = null;
        var reentered = false;
        sim = new InputSimulator(sink, _ =>
        {
            var done = Task.Run(() =>
            {
                sim!.HeldCount.GetHashCode();
                reentered = true;
            });
            Assert.True(done.Wait(TimeSpan.FromSeconds(5)), "日志回调在锁内执行——重入本类会死锁");
        });

        sim.MoveMouseAbsolute(1, 1);

        Assert.True(reentered);
    }

    [Fact]
    public void 滚轮_格数为int最小值_不抛异常()
    {
        var (sim, sink) = Create();

        // Math.Abs(int.MinValue) 会抛 OverflowException，必须在取绝对值时避开。
        var ex = Record.Exception(() => sim.ScrollWheel(ScrollDirection.Down, int.MinValue));

        Assert.Null(ex);
        Assert.Single(sink.Events);
        Assert.StartsWith("SCROLL(v=True,-", sink.Events[0]);
    }
}
