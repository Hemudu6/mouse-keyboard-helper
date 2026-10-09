using System.Diagnostics;
using System.Windows.Input;
using Core.Input;
using Core.Keys;
using Core.Model;
using Core.Scheduling;
using Host.Engine;
using MouseKey = Core.Keys.MouseKey;

// ════════════════════════════════════════════════════════════════════════
// 批次二「实机验收」临时驱动器（验收后删除，不进版本库）
//
// 目的：用**真实的** Scheduler + TaskRuntime + InputSimulator 跑一遍，
//       只把最底层的 SendInput 换成记录型 sink——于是"注入了什么、什么时刻、
//       有没有漏发松开"全部可观测，而调度/竞态/计时器分辨率的代码路径
//       与将来接 UI 时**完全一致**。
//
// 这不是单测（单测不进 Host，见设计文档 §13.1），是一次性人工验收。
// ════════════════════════════════════════════════════════════════════════

var sink = new RecordingSink();
var input = new InputSimulator(sink);
var scheduler = new Scheduler(input);

var failures = new List<string>();
void Check(string label, bool ok, string detail = "")
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}{(detail.Length > 0 ? "  - " + detail : "")}");
    if (!ok)
    {
        failures.Add(label);
    }
}

TaskDefinition MakeTask(string name, Key toggleKey, int frequency, Key injectedKey,
    TapMode mode = TapMode.Tap)
{
    var task = new TaskDefinition
    {
        Name = name,
        Enabled = true,
        ToggleKey = Core.Keys.KeyBinding.FromKey(toggleKey),
    };

    task.Commands.Add(new KeyboardKeyCommand
    {
        VirtualKey = injectedKey,
        Frequency = frequency,
        TapMode = mode,
    });

    return task;
}

var taskFast = MakeTask("快任务", Key.F6, 50, Key.A);
var taskSlow = MakeTask("慢任务", Key.F7, 12, Key.B);
var taskHold = MakeTask("按住任务", Key.F8, 10, Key.A, TapMode.Hold);

Console.WriteLine("═══ 批次二 实机验收 ═══\n");

// ── ① AC-03：连点开关不叠加 ────────────────────────────────────────────
Console.WriteLine("① 连点开关（AC-03：反复快速点按不叠加）");
{
    sink.Clear();
    for (var i = 0; i < 20; i++)
    {
        scheduler.Toggle(i % 2 == 0 ? taskFast : taskSlow);
    }

    scheduler.Toggle(taskFast);

    Check("连点后运行数正确（无叠加）", scheduler.RunningCount == 1,
        $"RunningCount={scheduler.RunningCount}, 期望 1");

    var before = scheduler.IsRunning(taskFast.Id);
    scheduler.Toggle(taskFast);
    scheduler.Toggle(taskFast);
    var after = scheduler.IsRunning(taskFast.Id);
    Check("同一任务 Toggle 两次回到原状态", before == after, $"{before} -> {after}");

    scheduler.StopAll();
}

// ── ② FR-TASK-08：并发连点同一开关 ────────────────────────────────────
Console.WriteLine("\n② 并发连点同一开关 8 线程 x 200 次（去重竞态）");
{
    scheduler.StopAll();
    var errors = new List<Exception>();
    var barrier = new Barrier(8);

    var threads = Enumerable.Range(0, 8).Select(_ => new Thread(() =>
    {
        barrier.SignalAndWait();
        for (var i = 0; i < 200; i++)
        {
            try
            {
                scheduler.Toggle(taskFast);
            }
            catch (Exception ex)
            {
                lock (errors)
                {
                    errors.Add(ex);
                }
            }
        }
    })).ToArray();

    foreach (var t in threads)
    {
        t.Start();
    }

    foreach (var t in threads)
    {
        t.Join();
    }

    Check("并发点按无异常抛出", errors.Count == 0,
        errors.Count > 0 ? errors[0].GetType().Name + ": " + errors[0].Message : "");
    Check("并发点按后运行数 <= 1（无重复实例）", scheduler.RunningCount <= 1,
        $"RunningCount={scheduler.RunningCount}");

    scheduler.StopAll();
    Thread.Sleep(80);
    Check("停止后运行数归零", scheduler.RunningCount == 0,
        $"RunningCount={scheduler.RunningCount}");
}

// ── ③-prep 最小复现：Start 后立即观察（不碰停止标志——只要还有线程活着，
//    强行清标志会让已停止的循环复活并持续注入，污染后续用例的观测） ──
{
    Console.WriteLine("\n③-prep 最小复现：Start 后立即观察");
    sink.Clear();
    var started = scheduler.Start(taskFast);
    Console.WriteLine($"      Start 返回 {started}，立即 RunningCount={scheduler.RunningCount}");
    Thread.Sleep(300);
    Console.WriteLine($"      300ms 后键A 计数 = {sink.CountKey(Key.A)}（50Hz 应约 15）");
    Console.WriteLine($"      异常记录：{(sink.LastException is null ? "无" : sink.LastException.ToString())}");
    Console.WriteLine($"      注入事件总数 = {sink.Total}");
    Console.WriteLine($"      IsRunning(taskFast) = {scheduler.IsRunning(taskFast.Id)}");

    scheduler.StopAll();
    // 给已置停止标志的循环留出退出时间，避免它们混入下一个用例的采样。
    Thread.Sleep(150);
}

// ── ③ AC-03a：两任务并发，频率不失真 ──────────────────────────────────
{
    Console.WriteLine("\n③ 两任务并发，各自频率不失真（AC-03a / NFR-03）");
    sink.Clear();
    scheduler.Start(taskFast);
    scheduler.Start(taskSlow);
    Check("两任务同时在跑", scheduler.RunningCount == 2,
        $"RunningCount={scheduler.RunningCount}");

    Thread.Sleep(1000);

    var fastCount = sink.CountKey(Key.A);
    var slowCount = sink.CountKey(Key.B);

    Console.WriteLine($"      快任务(50Hz,键A) 1 秒发 {fastCount} 次；慢任务(12Hz,键B) 发 {slowCount} 次");
    Check("快任务频率合理（35~65）", fastCount is >= 35 and <= 65, $"实测 {fastCount}");
    Check("慢任务频率合理（8~18）", slowCount is >= 8 and <= 18, $"实测 {slowCount}");

    scheduler.StopAll();
}

// ── ④ AC-23：间隔稳定性 ───────────────────────────────────────────────
Console.WriteLine("\n④ 单任务间隔稳定性（AC-23：连续 30 次偏差 <= ±20ms）");
{
    sink.Clear();
    scheduler.Start(taskSlow);
    Thread.Sleep(2200);
    scheduler.StopAll();

    // ④ 的采样对象是 taskSlow（用键 B）——取键 B 的间隔，取错键会采到 0 个。
    var gaps = sink.KeyGapsMs(Key.B);
    Console.WriteLine($"      采样到 {gaps.Count} 个间隔");

    if (gaps.Count >= 10)
    {
        var target = 1000.0 / 12;
        var maxDev = gaps.Max(g => Math.Abs(g - target));
        var avg = gaps.Average();
        Console.WriteLine($"      目标 {target:F2}ms，实测均值 {avg:F2}ms，最大偏差 {maxDev:F2}ms");
        Check("间隔最大偏差 <= 20ms", maxDev <= 20.0, $"最大偏差 {maxDev:F2}ms");
    }
    else
    {
        Check("采样数足够", false, $"仅 {gaps.Count} 个");
    }
}

// ── ⑤ 按住形态 + 补发松开 ─────────────────────────────────────────────
Console.WriteLine("\n⑤ 按住形态：按下后停止，是否补发松开");
{
    sink.Clear();
    scheduler.Start(taskHold);
    Thread.Sleep(300);

    var downsBeforeStop = sink.CountKeyDown(Key.A);
    scheduler.StopAll();
    Thread.Sleep(150);

    Console.WriteLine($"      停止前按下 A {downsBeforeStop} 次，松开 {sink.CountKeyUp(Key.A)} 次");
    Check("按住形态确实发出了按下", downsBeforeStop > 0, $"按下 {downsBeforeStop} 次");
    Check("停止后按住表为空（无残留按键）", input.HeldCount == 0, $"按住表剩余 {input.HeldCount}");

    var netDown = sink.CountKeyDown(Key.A) - sink.CountKeyUp(Key.A);
    Check("按下/松开配平（净差 0）", netDown == 0, $"净差 {netDown}");
}

// ── ⑥ AC-15 / AC-16：StopAll ─────────────────────────────────────────
Console.WriteLine("\n⑥ 停止全部：立即停净（AC-15/AC-16）");
{
    sink.Clear();
    scheduler.Start(taskFast);
    scheduler.Start(taskSlow);
    scheduler.Start(taskHold);
    Thread.Sleep(250);

    var sw = Stopwatch.StartNew();
    var stopped = scheduler.StopAll();
    sw.Stop();

    Thread.Sleep(120);
    var afterStop = sink.CountKeyA();

    Check("StopAll 返回 3", stopped == 3, $"返回 {stopped}");
    Check("停止后运行数归零", scheduler.RunningCount == 0, $"RunningCount={scheduler.RunningCount}");
    Check("停止后不再有新注入", sink.CountKeyA() == afterStop, $"{afterStop} -> {sink.CountKeyA()}");
    Check("按住表为空", input.HeldCount == 0, $"剩余 {input.HeldCount}");
    Console.WriteLine($"      StopAll 耗时 {sw.Elapsed.TotalMilliseconds:F2}ms");
}

// ── ⑦ 计时器分辨率引用计数 ────────────────────────────────────────────
Console.WriteLine("\n⑦ 计时器分辨率引用计数（§4.2 第 4 条）");
{
    var baseline = TimeResolution.Query();

    scheduler.Start(taskFast);
    var withOne = TimeResolution.Query();

    scheduler.Start(taskSlow);
    var withTwo = TimeResolution.Query();

    scheduler.Stop(taskFast.Id);
    var afterOne = TimeResolution.Query();

    scheduler.Stop(taskSlow.Id);
    Thread.Sleep(80);
    var afterAll = TimeResolution.Query();

    Console.WriteLine($"      初始 {baseline}ms -> 1任务 {withOne}ms -> 2任务 {withTwo}ms -> 停1个 {afterOne}ms -> 全停 {afterAll}ms");

    Check("任务运行时分辨率提升到 1ms", withOne <= 1, $"实测 {withOne}ms");
    Check("停掉 1 个（仍有任务在跑）时保持 1ms", afterOne <= 1, $"实测 {afterOne}ms");
    Check("全停后回落到初始值", afterAll == baseline, $"{baseline} vs {afterAll}");
}

// ── ⑧ 退出兜底 ────────────────────────────────────────────────────────
Console.WriteLine("\n⑧ Shutdown 兜底释放（FR-RUN-05 / AC-17）");
{
    sink.Clear();
    scheduler.Start(taskHold);
    Thread.Sleep(250);

    scheduler.Shutdown();

    var netDown = sink.CountKeyDown(Key.A) - sink.CountKeyUp(Key.A);
    Check("Shutdown 后按下/松开配平", netDown == 0, $"净差 {netDown}");
    Check("Shutdown 后按住表为空", input.HeldCount == 0, $"剩余 {input.HeldCount}");
    Check("Shutdown 后无任务在跑", scheduler.RunningCount == 0, $"RunningCount={scheduler.RunningCount}");
}

Console.WriteLine("\n═══ 结果 ═══");
if (failures.Count == 0)
{
    Console.WriteLine("全部通过");
    return 0;
}

Console.WriteLine($"{failures.Count} 项未通过：");
foreach (var f in failures)
{
    Console.WriteLine($"  - {f}");
}

return 1;

// ════════════════════════════════════════════════════════════════════════

/// <summary>记录型 sink：记录每次注入，不碰真实输入。</summary>
internal sealed class RecordingSink : IInputSink
{
    private readonly object _gate = new();
    private readonly List<Event> _events = new();

    public int LastInjectionError => 0;

    /// <summary>循环线程内被吞掉的异常——验收期用它暴露“静默死掉”的命令循环。</summary>
    public Exception? LastException { get; private set; }

    public void NoteException(Exception ex) => LastException = ex;

    /// <summary>记录每次注入后按住表的状态（排障用）。</summary>
    public string HeldOwners { get; set; } = "";


    private void Record(string what, Key key)
    {
        lock (_gate)
        {
            _events.Add(new Event(what, key, Stopwatch.GetTimestamp()));
        }
    }

    public int KeyDown(Key virtualKey)
    {
        Record("down", virtualKey);
        return 1;
    }

    public int KeyUp(Key virtualKey)
    {
        Record("up", virtualKey);
        return 1;
    }

    public int MouseDown(MouseKey button)
    {
        Record("down", Key.None);
        return 1;
    }

    public int MouseUp(MouseKey button)
    {
        Record("up", Key.None);
        return 1;
    }

    public int MoveAbsolute(int x, int y) => 1;

    public int Scroll(bool vertical, int notches) => 1;

    public void Clear()
    {
        lock (_gate)
        {
            _events.Clear();
        }
    }

    public int Total
    {
        get
        {
            lock (_gate)
            {
                return _events.Count;
            }
        }
    }

    public int CountKeyA()
    {
        lock (_gate)
        {
            return _events.Count(e => e.What == "down" && e.Key == Key.A);
        }
    }

    public int CountKey(Key key)
    {
        lock (_gate)
        {
            return _events.Count(e => e.What == "down" && e.Key == key);
        }
    }

    public int CountKeyDown(Key key)
    {
        lock (_gate)
        {
            return _events.Count(e => e.What == "down" && e.Key == key);
        }
    }

    public int CountKeyUp(Key key)
    {
        lock (_gate)
        {
            return _events.Count(e => e.What == "up" && e.Key == key);
        }
    }

    public List<double> KeyGapsMs(Key key)
    {
        List<long> stamps;
        lock (_gate)
        {
            stamps = _events
                .Where(e => e.What == "down" && e.Key == key)
                .Select(e => e.Stamp)
                .ToList();
        }

        var gaps = new List<double>();
        for (var i = 1; i < stamps.Count; i++)
        {
            gaps.Add((stamps[i] - stamps[i - 1]) * 1000.0 / Stopwatch.Frequency);
        }

        return gaps;
    }

    private readonly record struct Event(string What, Key Key, long Stamp);
}

/// <summary>查询系统计时器分辨率（ntdll）。</summary>
internal static class TimeResolution
{
    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    private static extern int NtQueryTimerResolution(
        out uint minimum, out uint maximum, out uint current);

    public static uint Query()
    {
        // 返回值单位 100ns；/10000 得毫秒。
        NtQueryTimerResolution(out _, out _, out var current);
        return current / 10000;
    }
}
