using System.Diagnostics;
using System.Runtime.InteropServices;
using Core.Diagnostics;
using Core.Input;
using Core.Model;

namespace Host.Engine;

/// <summary>
/// 任务运行的**唯一入口**（设计文档 §4.1 / §4.4）。
///
/// <para>
/// <b>职责</b>：把"哪个任务在跑"这一件事收拢到一处——
/// 开关按键的点按如何切换（FR-TASK-08）、任务间互不影响（FR-TASK-09）、
/// 紧急停止与"停止全部"（FR-RUN-01/04）、退出时补发松开（FR-RUN-05）、
/// 以及系统计时器分辨率的开合（§4.2 第 4 条）。
/// </para>
/// <para>
/// <b>为什么状态集中在这里</b>：需求 FR-TASK-08「同一任务不允许两个运行实例」
/// 是**全局性质**，不是单个任务的性质。若让每个 <see cref="TaskRuntime"/> 自己判定
/// 是否已在跑，判定结果会随"谁先看到"而变；集中在单一持有者手里，
/// 去重才有唯一真相。同理 NFR-08「随时可终止」要求任何路径都能找到全部运行中的任务。
/// </para>
/// </summary>
/// <remarks>
/// <b>本类不做校验、不碰持久化</b>：任务合法性归 <c>TaskValidator</c>（经 Registry），
/// 磁盘归 <c>IConfigStore</c>。本类只回答"现在谁在跑"并据此驱动输入。
/// </remarks>
public sealed class Scheduler : IDisposable
{
    private readonly InputSimulator _input;
    private readonly ILogSink? _log;

    /// <summary>
    /// 运行中的任务表：任务 Id → 运行期。
    /// 用 <see cref="Dictionary{TKey,TValue}"/> 而非列表：启停判定与停止查找都是按 Id 的，
    /// 列表会让每次查找退化成线性扫描，而任务数不设上限（FR-TASK-09）。
    /// </summary>
    private readonly Dictionary<Guid, TaskRuntime> _running = new();

    /// <summary>保护 <see cref="_running"/> 与计时器分辨率计数的全部读写。</summary>
    private readonly object _gate = new();

    /// <summary>
    /// 系统计时器分辨率的**引用计数**（§4.2 第 4 条）。
    ///
    /// <para>
    /// 为什么是计数而不是布尔：<c>timeBeginPeriod</c> 是**进程级全局状态**，
    /// 两个任务先后启动时若各调一次、停止时各调一次 <c>timeEndPeriod</c>，
    /// 先停的那个会把仍在运行的任务所需的 1ms 分辨率一起还原掉——
    /// 表现为"后停的任务精度突然变差"，且极难复现排查。
    /// 计数保证只有**全部任务停止**才真正还原。
    /// </para>
    /// <para>
    /// 只用布尔标志的话，正确的还原时机就无法表达；用裸调用则必然出错。
    /// </para>
    /// </summary>
    private int _timerResolutionRefCount;

    /// <summary>
    /// 任务运行状态变化时触发（参数：任务 Id，是否正在运行）。供 UI 更新。
    ///
    /// <para>
    /// <b>仅代表本调度器对“表内有无此任务”的观测结论</b>，不是“循环已在跑”的保证。
    /// 且因停止需要先做锁外实质工作，<c>false</c> 的通知时机随路径而异：
    /// <see cref="Toggle"/> / <see cref="Stop"/> 是在该任务停完后逐条发，
    /// <see cref="StopAll"/> 是先把所有任务停完、再逐条补发。
    /// 订阅方（UI）应只把事件当作“该重读一下运行态”的信号，不要据此推断事件之间的先后因果。
    /// </para>
    /// </summary>
    public event Action<Guid, bool>? RunStateChanged;

    /// <summary>构造调度器。</summary>
    /// <param name="input">输入模拟器（全局唯一，按住表在其内部）。</param>
    /// <param name="log">日志接收器（可空）。本层只记错误事件（§7.3）：
    /// 停任务失败、计时器分辨率开启失败、命令循环异常。任务启停的**正常**轨迹
    /// 不在这里记——那属于业务事件，由组合根在订阅点上记，避免一处事件两处落盘。</param>
    public Scheduler(InputSimulator input, ILogSink? log = null)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _log = log;
    }

    /// <summary>当前运行中的任务数量（供诊断与测试）。</summary>
    public int RunningCount
    {
        get
        {
            lock (_gate)
            {
                return _running.Count;
            }
        }
    }

    /// <summary>某任务此刻是否在运行。</summary>
    public bool IsRunning(Guid taskId)
    {
        lock (_gate)
        {
            return _running.ContainsKey(taskId);
        }
    }

    /// <summary>当前全部运行中任务的 Id 快照。</summary>
    public IReadOnlyList<Guid> RunningTaskIds
    {
        get
        {
            lock (_gate)
            {
                return _running.Keys.ToList();
            }
        }
    }

    /// <summary>
    /// 点按开关按键的效果：**在运行与停止之间切换**（FR-SEQ-06、FR-TASK-08）。
    ///
    /// <para>
    /// 重复点按是**幂等的**：已经在跑再点按 = 停止，不会叠加第二个实例（AC-03）。
    /// 用户快速连点（AC-03 的测法）会并发进入本方法，两次点按若都判出"未运行"
    /// 就会双双启动——因此启动路径采用**占位 + 锁内二次检查**两步：
    /// 先在锁内把"这个 Id 有人了"写进运行表，再在锁外起线程。
    /// 判定与写入本可同处一把锁，但停止的实质工作（补发松开）必须离开本锁
    /// （见下），于是启停两条路径都收敛为"锁内只做状态迁移、锁外做实质工作"，
    /// 去重则完全由锁内那一次 <c>ContainsKey</c> 承担。
    /// </para>
    /// </summary>
    /// <param name="task">被点按开关的任务。调用方须保证 <see cref="TaskDefinition.Enabled"/> 已检查。</param>
    /// <returns>切换后是否处于运行态。</returns>
    public bool Toggle(TaskDefinition task)
    {
        ArgumentNullException.ThrowIfNull(task);

        lock (_gate)
        {
            TaskRuntime? stopping = null;
            if (_running.TryGetValue(task.Id, out var runtime))
            {
                // 锁内只做状态迁移。
                _running.Remove(task.Id);
                ReleaseTimerResolutionIfIdleLocked();
                stopping = runtime;
            }

            if (stopping is not null)
            {
                // 在跑 → 停。停止的实质工作（补发松开等）在 TaskRuntime 内完成，
                // 但必须在锁外调用：它的补发路径会取 InputSimulator 的锁，
                // 在持本锁时调用会造成锁序耦合（本锁 → 注入锁），给将来的反向取锁埋死锁。
                StopRuntime(stopping);
                RunStateChanged?.Invoke(task.Id, false);
                return false;
            }
        }

        // 启动路径同理：先把"占用名额"写进表，再在锁外起线程。
        // 必须先占位再起线程——否则两次并发点按可能都通不过上面的 ContainsKey 检查。
        TaskRuntime created;
        lock (_gate)
        {
            // 锁内二次检查：锁外判定与锁内写入之间有窗口，此处以锁内结果为准。
            if (_running.ContainsKey(task.Id))
            {
                // 此刻表里的运行期已由**另一次并发点按**占位（其启停仍在进行中），
                // 本任务确将在跑，故报 true。该返回值为**调度层自用**（<c>Scheduler.Start</c>
                // 据此决定是否对外发运行状态通知），不代表"循环已拉起"；
                // 占位者的循环最终若因竞态收敛而未拉起，由它的那条路径负责撤占位并纠偏。
                return true;
            }

            created = new TaskRuntime(task, _input, _log);
            _running[task.Id] = created;
            AcquireTimerResolutionLocked();
        }

        try
        {
            // 竞态收敛：上一轮并发点按可能在占位与启动之间要求过停止，
            // 此时 Start() 放弃拉起循环并返回 false。占位必须同步撤掉，
            // 否则本任务会永久“卡在运行态”却无任何循环在跑（表现为再也点不动）。
            if (!created.Start())
            {
                RemovePlaceholderIfOwned(task.Id, created);
                return false;
            }
        }
        catch
        {
            // 启动失败必须把占位撤掉，否则该任务永久"卡在运行态"却没有任何循环在跑，
            // 用户再点开关只会把它"停掉"——表现是任务再也无法启动。
            RemovePlaceholderIfOwned(task.Id, created);
            throw;
        }

        // 启动成功后占位可能已易主：本运行期在锁外起线程期间，并发点按可能已把
        // 运行表换成另一个运行期。此时必须自行停下，否则本运行期不在表内、无人能停。
        //
        // "判定仍归我所有"与"发布运行成功"必须落在同一把锁内完成：若分两次取锁，
        // 两者之间可能插进一次 StopAll——它清表并停掉本运行期，而本方法随后仍然
        // 对外宣告"已启动"，UI 会显示一个并不在跑的任务。
        // 故判定与宣告合并为 ClaimRunning 一次取锁。
        if (!ClaimRunning(task.Id, created))
        {
            StopRuntime(created);
            return false;
        }

        RunStateChanged?.Invoke(task.Id, true);
        return true;
    }

    /// <summary>
    /// 原子地“确认所有权并宣布在跑”：仅当运行表仍指向 <paramref name="expected"/> 时返回 <c>true</c>。
    ///
    /// <para>
    /// 身份判定必须在同一把锁内完成。“宣布”是紧随其后的一次调用——不能把判定与宣布拆成两次取锁，
    /// 否则中间可能插进一次 <c>StopAll</c>：它清表并停掉本运行期，而调用方随后仍会对外宣告“已启动”。
    /// 本方法只保证“此刻仍归我所有”，宣布本身仍由调用方在锁外发出（事件回调不得持锁）。
    /// </para>
    /// </summary>
    private bool ClaimRunning(Guid taskId, TaskRuntime expected)
    {
        lock (_gate)
        {
            return _running.TryGetValue(taskId, out var current) && ReferenceEquals(current, expected);
        }
    }

    /// <summary>
    /// 仅当运行表仍指向 <paramref name="expected"/> 时移除该占位。
    ///
    /// <para>
    /// <b>为何不能直接按 Id 删：</b>占位与启线程之间有锁外窗口，期间并发点按可能已经
    /// 把同一任务换成**另一个**运行期。若此处不加判定地 <c>Remove(taskId)</c>，
    /// 删掉的是新占位——新运行期随即被拉起却不在运行表里，从此无人能停（实证：
    /// 叠加泄漏，StopAll 后仍持续注入）。
    /// </para>
    /// </summary>
    private void RemovePlaceholderIfOwned(Guid taskId, TaskRuntime expected)
    {
        lock (_gate)
        {
            if (_running.TryGetValue(taskId, out var current) && ReferenceEquals(current, expected))
            {
                _running.Remove(taskId);
                ReleaseTimerResolutionIfIdleLocked();
            }
        }
    }

    /// <summary>
    /// 启动指定任务（若未在跑）。
    /// 与 <see cref="Toggle"/> 的区别：这是**幂等启动**，用于"启动全部"之类的批量语义，
    /// 不会把已运行的任务停掉。
    /// </summary>
    /// <returns>本次调用是否真的启动了它。</returns>
    public bool Start(TaskDefinition task)
    {
        ArgumentNullException.ThrowIfNull(task);

        TaskRuntime created;
        lock (_gate)
        {
            if (_running.ContainsKey(task.Id))
            {
                return false;
            }

            created = new TaskRuntime(task, _input, _log);
            _running[task.Id] = created;
            AcquireTimerResolutionLocked();
        }

        try
        {
            // 同 Toggle：Start() 可能因竞态收敛而放弃拉起循环，占位须同步撤掉。
            if (!created.Start())
            {
                RemovePlaceholderIfOwned(task.Id, created);
                return false;
            }
        }
        catch
        {
            RemovePlaceholderIfOwned(task.Id, created);
            throw;
        }

        // 同 Toggle：启动期间占位可能已易主，本运行期须自行停下（否则不在表内、无人能停）。
        if (!ClaimRunning(task.Id, created))
        {
            StopRuntime(created);
            return false;
        }

        RunStateChanged?.Invoke(task.Id, true);
        return true;
    }

    /// <summary>停止指定任务（若在跑）。用于"停止全部"与紧急停止的逐任务路径。</summary>
    /// <returns>本次调用是否真的停掉了它。</returns>
    public bool Stop(Guid taskId)
    {
        TaskRuntime? runtime;
        lock (_gate)
        {
            if (!_running.TryGetValue(taskId, out runtime))
            {
                return false;
            }

            _running.Remove(taskId);
            ReleaseTimerResolutionIfIdleLocked();
        }

        StopRuntime(runtime);
        RunStateChanged?.Invoke(taskId, false);
        return true;
    }

    /// <summary>
    /// 停止**全部**运行中的任务（FR-RUN-01 紧急停止 / FR-RUN-04 "停止全部"按钮 / NFR-08）。
    ///
    /// <para>
    /// 这是紧急停止与"停止全部"的共同实现——需求 AC-15/AC-16/AC-32 要求两条路径
    /// 都能一次性停下所有任务，若各写一套，两者的行为差异会随维护逐渐分叉。
    /// </para>
    /// <para>
    /// <b>先一次性清空状态表再逐个停</b>：清空后新到的启动请求不会被"正在停"的状态干扰，
    /// 且停止过程中即使某个任务抛出异常，其余任务仍会被停到（逐个 try）。
    /// 紧急停止场景下"停不掉"比"部分任务多停了"严重得多。
    /// </para>
    /// </summary>
    /// <returns>本次停掉的任务数。</returns>
    public int StopAll()
    {
        List<TaskRuntime> runtimes;
        lock (_gate)
        {
            if (_running.Count == 0)
            {
                return 0;
            }

            runtimes = _running.Values.ToList();
            _running.Clear();
            ReleaseTimerResolutionIfIdleLocked();
        }

        var stopped = 0;
        foreach (var runtime in runtimes)
        {
            try
            {
                StopRuntime(runtime);
                RunStateChanged?.Invoke(runtime.TaskId, false);
                stopped++;
            }
            catch (Exception ex)
            {
                // 单个任务停止失败不得中断整体紧急停止（NFR-08"随时可终止"）。
                _log?.Error($"停止任务 {runtime.TaskId} 失败", ex);
                Debug.WriteLine($"[Scheduler] 停止任务 {runtime.TaskId} 失败：{ex}");
            }
        }

        return stopped;
    }

    /// <summary>
    /// 退出流程（FR-RUN-05、AC-17）：停止全部任务，并**兜底**释放所有残留按住。
    ///
    /// <para>
    /// 与 <see cref="StopAll"/> 的区别：本方法额外调用 <c>ReleaseAllAll</c>，
    /// 覆盖"不属于任何任务的按住"（<c>Guid.Empty</c> 属主）与任何异常路径的残留。
    /// 需求 AC-17 的判据是"进程结束前后无残留的按下未松开按键"——
    /// 这是**结果性**要求，不能只依赖"每个任务都正确释放了自己"这一过程性保证。
    /// </para>
    /// </summary>
    public void Shutdown()
    {
        StopAll();

        // 兜底：补发全部属主的松开。即使上面每个任务都已 ReleaseAll 过，
        // 这里对空表调用也是安全的（按住表已空 → 无副作用）。
        _input.ReleaseAllAll();
    }

    /// <summary>停止单个运行期并做异常隔离。</summary>
    private void StopRuntime(TaskRuntime runtime)
    {
        try
        {
            runtime.Stop();
        }
        catch (Exception ex)
        {
            _log?.Error($"运行期停止异常（任务 {runtime.TaskId}）", ex);
            Debug.WriteLine($"[Scheduler] 运行期停止异常（任务 {runtime.TaskId}）：{ex}");
        }
    }

    // ── 系统计时器分辨率（§4.2 第 1、4 条）────────────────────────────────

    /// <summary>
    /// 按引用计数打开 1ms 分辨率。**只在首次（计数 0 → 1）时真正调用**。
    ///
    /// <para>
    /// <c>timeBeginPeriod</c> 影响系统能耗（§4.2 第 4 条），故仅在**有任务运行时**生效。
    /// 计数归零时由 <see cref="ReleaseTimerResolutionIfIdleLocked"/> 还原。
    /// </para>
    /// </summary>
    private void AcquireTimerResolutionLocked()
    {
        if (_timerResolutionRefCount == 0)
        {
            // 失败不抛：分辨率未生效只影响高频任务的精度（AC-23 的达成率），
            // 而"打不开分辨率"不应导致任务完全无法运行——宁可精度降级也要能干活。
            // 返回值 0 表示成功，非 0 为错误码。
            var result = timeBeginPeriod(1);
            if (result != 0)
            {
                _log?.Error($"timeBeginPeriod(1) 失败，错误码 {result}；高频命令精度可能下降。");
                Debug.WriteLine(
                    $"[Scheduler] timeBeginPeriod(1) 失败，错误码 {result}；高频命令精度可能下降。");
            }
        }

        _timerResolutionRefCount++;
    }

    /// <summary>
    /// 按引用计数还原分辨率。**只在计数归零时真正调用**。
    /// 计数不会低于 0——归零后不再递减，避免成对的 <c>timeEndPeriod</c> 多于
    /// <c>timeBeginPeriod</c>（那会把分辨率还到比开始时更低的状态）。
    /// </summary>
    private void ReleaseTimerResolutionIfIdleLocked()
    {
        if (_timerResolutionRefCount == 0)
        {
            return;
        }

        _timerResolutionRefCount--;

        if (_timerResolutionRefCount == 0)
        {
            timeEndPeriod(1);
        }
    }

    /// <summary>释放调度器：停止全部任务并还原计时器分辨率。</summary>
    public void Dispose()
    {
        Shutdown();

        // 还原到初始状态。Shutdown 已使计数归零，此处仅为防御
        // （例如某次启动异常导致计数未按预期回落）。
        lock (_gate)
        {
            if (_timerResolutionRefCount != 0)
            {
                timeEndPeriod(1);
                _timerResolutionRefCount = 0;
            }
        }
    }

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint timeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint timeEndPeriod(uint uMilliseconds);
}
