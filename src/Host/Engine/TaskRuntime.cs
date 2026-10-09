using System.Diagnostics;
using Core.Diagnostics;
using Core.Input;
using Core.Model;
using Core.Scheduling;

namespace Host.Engine;

/// <summary>
/// 一个任务的运行期状态与执行引擎（设计文档 §4.1 / §4.4）。
///
/// <para>
/// **职责划分**：<see cref="TaskRegistry"/> 管"有哪些任务"（持久化真相），
/// 本类管"某个任务正在怎么跑"（运行期派生状态，**不持久化**——
/// 需求 §5.1 的 <c>RuntimeState</c> 明确标为运行期派生）。
/// 两者互不引用对方的状态：Registry 不关心谁在跑，Runtime 不关心列表里还有什么。
/// </para>
/// <para>
/// **执行模型**（§4.1）：每条命令一个**独立节拍器**，互不共享计时器、
/// 互不阻塞。点按形态 = 反复动作；按住形态 = 按一次后挂着等停止；
/// 瞬时操作（移动 / 滚轮）与点按同构，只是没有 TapMode。
/// </para>
/// <para>
/// **一个任务一个所有者**：全部按住引用挂在任务 Id 上（<c>InputSimulator</c> 的
/// <c>owner</c> 参数）。停止时一次 <see cref="IInputSimulatorView.ReleaseAll"/> 即可
/// 精确收回本任务按下的全部键，不会误伤别的任务——这是 FR-SEQ-06/07 的直接实现。
/// </para>
/// </summary>
/// <remarks>
/// **为什么本类收 <c>InputSimulator</c> 具体类而不是一个接口**：
/// 设计文档 §13.1 已定下接口边界——测试接缝是 Core 的 <c>IInputSink</c>
/// （假 sink + 假时钟驱动 <c>InputSimulator</c> 的引用计数与补发顺序），
/// 而 Host 明确**不列入测试工程引用**。“再抽一个注入接口”不会带来任何可测性收益
/// （本类在 Host 里，测试工程看不见它），只会往已冻结的 Core 契约里加东西。
/// 故此处直接依赖具体类，不为形式上的解耦而扩接口。
/// </remarks>
public sealed class TaskRuntime
{
    private readonly TaskDefinition _task;
    private readonly InputSimulator _input;
    private readonly ILogSink? _log;

    /// <summary>
    /// 任务的停止标志（§4.4 第 1 步）。<c>volatile</c>：由触发线程写、
    /// 由各命令循环线程读，必须保证可见性——否则循环可能永远读不到已置位的标志，
    /// 任务停不下来（这正是 §4.4 强调"立即停止"的失败形态）。
    /// </summary>
    private volatile bool _stopRequested;

    /// <summary>命令循环的线程列表，用于停止时唤醒与收尾。</summary>
    private readonly List<CommandLoop> _loops = new();

    /// <summary>
    /// 保护 <see cref="_loops"/> 的增删与状态迁移。
    /// 不用 <c>lock(this)</c>：外部代码能拿到本对象引用，锁公共对象会引入外部干扰。
    /// </summary>
    private readonly object _gate = new();

    /// <summary>任务是否处于运行态（§4.4 第 4 步的"已停止"判据）。</summary>
    private volatile bool _running;

    /// <summary>
    /// `Start()` 尚未执行时是否已收到停止请求。
    /// <para>
    /// 竞态背景：<c>Scheduler</c> 采取"锁内占位、锁外起线程"——占位与 <see cref="Start"/>
    /// 之间有一个窗口。并发的另一次点按可在该窗口内把本运行期移出运行表并调 <see cref="Stop"/>。
    /// 若此刻的 <see cref="Stop"/> 因 <c>!_running</c> 静默返回，随后 <see cref="Start"/> 仍会
    /// 照常拉起循环线程——而本运行期已不在运行表里，<c>StopAll</c>/<c>Shutdown</c> 永远够不到它，
    /// 循环将永久注入（实测：8 次并发点按复现 2 次，键 A 注入恒为 2×/3×）。
    /// </para>
    /// <para>故此处必须留下"已要求停止"的印记，由 <see cref="Start"/> 消费。</para>
    /// </summary>
    private bool _stopBeforeStart;

    /// <summary>任务停止完成时触发（供 Engine 层转告 UI）。参数为任务 Id。</summary>
    public event Action<Guid>? Stopped;

    /// <summary>构造一个任务运行期。</summary>
    /// <param name="task">任务定义。本类**持有其引用**——调用方须保证运行期间不被改写。</param>
    /// <param name="input">输入模拟器（成对语义层，负责按住表与补发顺序）。</param>
    /// <param name="log">日志接收器（可空）。命令循环异常是"某个键突然不动了"的唯一线索，
    /// 必须落盘（§7.3「错误事件」）；空则只写 <see cref="Debug"/>。</param>
    public TaskRuntime(TaskDefinition task, InputSimulator input, ILogSink? log = null)
    {
        _task = task ?? throw new ArgumentNullException(nameof(task));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _log = log;
    }

    /// <summary>本运行期对应的任务 Id。</summary>
    public Guid TaskId => _task.Id;

    /// <summary>是否正在运行。</summary>
    public bool IsRunning => _running;

    /// <summary>
    /// 启动任务（设计文档 §4.1）。
    ///
    /// 每次调用为**每条命令**起一个循环。命令数不设上限（Q2 定案），
    /// 故线程数随命令数增长——这是 §4.3 明确接受的语义代价，
    /// 物理承载（专用线程 vs 池化）是可调的实现细节。
    /// </summary>
    /// <remarks>
    /// 重复启动是**非法调用**：Engine 层（<c>Scheduler</c>）负责"已在跑就不再起"，
    /// 本类不做静默去重——静默去重会掩盖调度层的状态机 bug
    /// （两个循环跑同一任务 = 频率翻倍，且按住引用重复计数）。
    /// <para>
    /// 但本方法**不拉起任何循环即返回**是合法路径：竞态下 <see cref="Stop"/> 可能先于本方法执行
    /// （见 <see cref="_stopBeforeStart"/>）。此时本方法消费该停止意图、放弃启动，不抛异常——
    /// 调用方（<c>Scheduler</c>）已按"已停止"处理，这是竞态收敛而非非法调用。
    /// </para>
    /// </remarks>
    /// <returns>是否真的拉起了命令循环。</returns>
    public bool Start()
    {
        lock (_gate)
        {
            if (_running)
            {
                throw new InvalidOperationException(
                    $"任务 {_task.Id} 已在运行，不得重复启动。");
            }

            // 竞态收敛：启动前已被要求停止 ⇒ 不拉起任何循环。
            // 消费掉这枚印记——本次启动机会已经报废，不得留下影响下次启动。
            // 同时清掉 _stopRequested：停止意图已转记为 _stopBeforeStart 并在此消费，
            // 两个标志同源于一次停止请求，必须一起清算——否则残留的 true 会泄漏到下一次启动。
            if (_stopBeforeStart)
            {
                _stopBeforeStart = false;
                _stopRequested = false;
                return false;
            }

            _stopRequested = false;
            _running = true;

            foreach (var command in _task.Commands)
            {
                 var loop = new CommandLoop(command, _task.Id, _input, () => _stopRequested, _log);
                _loops.Add(loop);
                loop.Start();
            }

            return true;
        }
    }

    /// <summary>
    /// 停止任务（设计文档 §4.4 的四步流程，一步不少）。
    ///
    /// <para>
    /// 顺序**不可调换**：先置标志（①）→ 唤醒等待中的循环（②）→
    /// 补发松开（③）→ 通知（④）。
    /// 若先补发松开再置标志，一个此刻正在执行动作的循环会在"补发"之后又按下键，
    /// 那个键将永远留在按住表里直到下次停止——这正是 §4.4「时序细节」点明的竞态。
    /// </para>
    /// <para>
    /// **不等待循环线程结束即补发松开**：等待会引入不确定延迟，
    /// 而立即停止是需求 FR-RUN-05 的硬要求。代价是补发瞬间某个循环可能刚做完
    /// "执行前检查"——但它在执行后还会再检查一次标志，且停止后它发出的动作
    /// 已不可能再进入按住表（补发已完成，引用计数归零的键不会再被"部分松开"）。
    /// </para>
    /// </summary>
    public void Stop()
    {
        CommandLoop[] loops;
        lock (_gate)
        {
            if (!_running)
            {
                // 竞态兜底：本运行期可能刚被 <c>Scheduler</c> 占位、但 <see cref="Start"/> 尚未执行
                // （占位与启动之间有锁外窗口）。此刻静默返回，随后的 Start() 就会把循环照常拉起，
                // 而本运行期已不在调度器的运行表里——那些循环将永久注入、无人能停。
                // 故此处留下停止印记，交由 Start() 消费；若 Start() 永不执行，这枚印记也无害。
                _stopBeforeStart = true;
                return;
            }

            // ① 置停止标志。此后任何循环的下一次"执行前检查"都会直接退出。
            _stopRequested = true;

            // ② 唤醒所有等待中的循环。若不唤醒，最坏情况要等满一个周期
            //    （1000 次/秒的命令是 1ms，可忽略；但 1 次/秒的命令要等 1 秒），
            //    而"立即停止"不允许这种等待。
            loops = _loops.ToArray();
        }

        foreach (var loop in loops)
        {
            loop.Wake();
        }

        // ③ 补发松开（FR-TAP-06 / FR-SEQ-06）。同一持有单元内"主键先松、修饰键后松"
        //    的顺序由 InputSimulator 的按住表保证，本层不重复实现。
        _input.ReleaseAll(_task.Id);

        lock (_gate)
        {
            _running = false;
            _loops.Clear();
        }

        // ④ 通知 UI。
        Stopped?.Invoke(_task.Id);
    }

    /// <summary>要求停止但不等待（供退出流程使用：紧急停止要立刻回到调用方）。</summary>
    /// <remarks>
    /// 仅置标志，**不覆盖“启动前已被要求停止”的印记**：本运行期可能尚在占位窗口内、循环未拉起，
    /// 此时仅置标志会被 <see cref="Start"/> 的开头重置淹没，停止意图静默丢失。
    /// </remarks>
    public void RequestStop()
    {
        lock (_gate)
        {
            if (!_running)
            {
                _stopBeforeStart = true;
                return;
            }

            _stopRequested = true;
        }
    }

    /// <summary>
    /// 单条命令的重复节拍器（设计文档 §4.1 "每条命令一个独立的重复节拍器"）。
    ///
    /// 每个循环独占一个后台线程，线程内不与其他循环共享任何可变状态——
    /// 唯一共享的是注入出口（其线程安全由 <c>InputSimulator</c> 内部锁保证）
    /// 与停止标志（volatile 读）。这就是 NFR-03"互不影响"的落地方式。
    /// </summary>
    private sealed class CommandLoop
    {
        private readonly CommandDefinition _command;
        private readonly Guid _owner;
        private readonly InputSimulator _input;
        private readonly Func<bool> _stopRequested;
        private readonly ILogSink? _log;
        private readonly ManualResetEventSlim _wake = new(false);
        private Thread? _thread;

        public CommandLoop(
            CommandDefinition command,
            Guid owner,
            InputSimulator input,
            Func<bool> stopRequested,
            ILogSink? log)
        {
            _command = command;
            _owner = owner;
            _input = input;
            _stopRequested = stopRequested;
            _log = log;
        }

        public void Start()
        {
            // IsBackground = true：宿主被强杀（任务管理器 / 注销）时不让循环线程
            // 吊住进程。进程正常退出走 §8 的补发松开流程，不依赖线程自行收尾。
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = $"cmd-{_command.GetType().Name}-{_command.Frequency}",
            };

            // 高频命令给更高优先级（§4.2 第 3 条：专用线程的"可控性"之一）。
            // 不设 Highest：那会与系统关键线程抢 CPU，收益微小而代价明显。
            if (TapSchedule.NeedsDedicatedThread(_command.Frequency))
            {
                _thread.Priority = ThreadPriority.AboveNormal;
            }

            _thread.Start();
        }

        /// <summary>唤醒等待中的循环（§4.4 第 2 步）。</summary>
        public void Wake() => _wake.Set();

        private void Run()
        {
            try
            {
                // 按住形态只是"执行一次后挂起等停止"，与点按共用同一套时间轴代码
                // 反而会引入无谓的周期计算。此处显式分流，语义更直白。
                if (_command.TapMode == TapMode.Hold)
                {
                    RunHold();
                }
                else
                {
                    RunMetronome();
                }
            }
            catch (Exception ex)
            {
                // 循环线程内不得让异常逃逸：逃逸会静默杀死该命令（用户只看到
                // "某个键不动了"却无从排查），且 .NET 中未处理的后台线程异常会终结进程。
                // 这里吞掉并记录——任务其余命令不受影响（NFR-03）。
                // 落盘（§7.3「错误事件」）是这条线索的唯一出口：Debug 输出在发布构建里不存在。
                _log?.Error($"命令循环异常（任务 {_owner}）", ex);
                Debug.WriteLine($"[TaskRuntime] 命令循环异常（任务 {_owner}）：{ex}");
            }
        }

        /// <summary>按住形态：按一次，然后挂着等停止。</summary>
        private void RunHold()
        {
            if (_stopRequested())
            {
                return;
            }

            ExecuteHold();

            // 等待停止信号。这里不需要时间轴——按住形态没有"下一次"。
            // 用长超时轮询而非无限等待：即使 Wake() 因任何原因漏掉，
            // 循环也能在一个周期内自行发现标志并退出（对"停不下来"的兜底）。
            while (!_stopRequested())
            {
                _wake.Wait(TimeSpan.FromMilliseconds(50));
            }
        }

        /// <summary>
        /// 点按 / 瞬时形态：按**绝对时间轴**重复（§4.2 第 2 条）。
        ///
        /// 关键：第 n 次的目标时刻 = <c>t0 + n × (1e6 / 频率)</c> 微秒，
        /// 每次由 <see cref="TapSchedule.NextDueMicroseconds"/> 从 t0 重新算出，
        /// **不累加睡眠误差**。动作本身的耗时因此不会漂移进总时长——
        /// 这是 AC-23（±20ms 连续 30 次）能达标的前提。
        /// </summary>
        private void RunMetronome()
        {
            var ticksPerMicrosecond = (double)Stopwatch.Frequency / 1_000_000.0;

            // 时间轴基准统一取**微秒**：NextDueMicroseconds 的 t0 与
            // WaitUntil 里 (now - t0) 的 t0 必须是同一个量纲，否则 due 是绝对时刻、
            // now 是相对耗时，remaining 恒为 t0 量级（巨正），循环永远等不到期。
            var t0Microseconds = (long)(Stopwatch.GetTimestamp() / ticksPerMicrosecond);
            var frequency = TapSchedule.ClampFrequency(_command.Frequency);

            long index = 0;

            while (true)
            {
                // §4.4 时序细节：执行动作**前**检查停止标志。
                if (_stopRequested())
                {
                    return;
                }

                ExecuteCommand();

                if (_stopRequested())
                {
                    return;
                }

                index++;
                var dueMicroseconds = TapSchedule.NextDueMicroseconds(t0Microseconds, index, frequency);
                WaitUntil(t0Microseconds, dueMicroseconds, ticksPerMicrosecond);
            }
        }

        /// <summary>
        /// 高精度等待至绝对时刻（§4.2 第 3 条 / §9.3 V-2 的实测结论）。
        ///
        /// 策略与实测一致：**远处 <c>Sleep(1)</c> 让出 CPU，最后 2ms 自旋**。
        /// 全程自旋能把最坏尖峰压下一档，但常驻占满一核不划算（§9.3 V-2 明确不采用）；
        /// 纯 <c>Sleep(1)</c> 又受系统时钟粒度拉长（不开 <c>timeBeginPeriod</c> 时达 5.6~6.2ms）。
        /// <param name="t0Microseconds">时间轴基准（微秒），与 <c>NextDueMicroseconds</c> 同源。</param>
        /// <param name="dueMicroseconds">目标绝对时刻（微秒），同基准。</param>
        /// <param name="ticksPerMicrosecond">Stopwatch tick 到微秒的换算系数。</param>
        private void WaitUntil(long t0Microseconds, long dueMicroseconds, double ticksPerMicrosecond)
        {
            while (true)
            {
                if (_stopRequested())
                {
                    return;
                }

                var nowMicroseconds = (long)(Stopwatch.GetTimestamp() / ticksPerMicrosecond);
                var remaining = dueMicroseconds - nowMicroseconds;

                if (remaining <= 0)
                {
                    // 已到期（或因系统卡顿迟发）。直接返回、不做补偿性“追赶”：
                    // 时间轴是绝对的，下一次自然对表——表现为“偶尔漏一拍”而非“越来越慢”。
                    return;
                }

                if (remaining > 2000)
                {
                    // 远处：让出 CPU。用 <c>Sleep(1)</c> 而非 <c>Thread.Sleep</c> 长睡，
                    // 保证停止请求最多 1ms 内被发现（可唤醒性优先于省电）。
                    Thread.Sleep(1);
                }
                else
                {
                    // 近处（≤2ms）：自旋等精度。此处**不**自旋整个间隔，
                    // 只自旋最后 2ms，兼顾精度与 CPU 占用。
                    Thread.SpinWait(50);
                }
            }
        }

        /// <summary>执行一次命令动作（点按 / 瞬时）。</summary>
        private void ExecuteCommand()
        {
            switch (_command)
            {
                case KeyboardKeyCommand key:
                    // 仅按下型（Hold）不走这里；到此处必是点按。
                    _input.TapKey(key.VirtualKey);
                    break;

                case KeyboardComboCommand combo:
                    _input.TapCombo(combo.ModifierKeys, combo.VirtualKey);
                    break;

                case MouseButtonCommand button:
                    _input.TapMouseButton(button.Button);
                    break;

                case MouseMoveCommand move:
                    _input.MoveMouseAbsolute(move.X, move.Y);
                    break;

                case MouseScrollCommand scroll:
                    _input.ScrollWheel(scroll.Direction, scroll.Notches);
                    break;

                default:
                    // 新增命令类型时编译器不会提醒此 switch——故用显式抛错兜底，
                    // 而不是静默忽略。静默忽略的表现是"用户加了命令但没反应"，
                    // 比一个响亮的异常难排查得多。
                    throw new NotSupportedException(
                        $"未处理的命令类型：{_command.GetType().FullName}");
            }
        }

        /// <summary>执行一次按住动作（按住形态专用）。</summary>
        private void ExecuteHold()
        {
            switch (_command)
            {
                case KeyboardKeyCommand key:
                    _input.HoldKey(key.VirtualKey, _owner);
                    break;

                case KeyboardComboCommand combo:
                    _input.HoldCombo(combo.ModifierKeys, combo.VirtualKey, _owner);
                    break;

                case MouseButtonCommand button:
                    _input.HoldMouseButton(button.Button, _owner);
                    break;

                case MouseMoveCommand:
                case MouseScrollCommand:
                    // 鼠标类命令无按住形态：TaskValidator 已在校验期拒绝（FR-TAP-01）。
                    // 此处兜底抛错——校验被绕过时（如外部改坏配置文件）要立刻可见，
                    // 而不是"按住任务跑起来但什么都不发生"。
                    throw new NotSupportedException(
                        $"鼠标类命令不支持按住形态：{_command.GetType().Name}");

                default:
                    throw new NotSupportedException(
                        $"未处理的命令类型：{_command.GetType().FullName}");
            }
        }
    }
}
