using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Core.Input;
using Core.Keys;

namespace Host.Input;

/// <summary>
/// 全局低级钩子服务（设计文档 §3.2 / §3.3 / §3.4）。
///
/// <para>
/// <b>职责</b>：装钩子（键盘 + 鼠标各一）→ 回调内最小判定 → 把命中写成
/// <see cref="ToggleRequested"/> 事件投递给上层。**到此为止**——
/// 本类不实现"开关 → 启停任务"的业务接线（那是下一层的事，见 §3.4 业务接线说明）。
/// </para>
///
/// <para>
/// <b>本类刻意不认识 <c>TaskDefinition</c></b>：它只吃一张预计算的
/// <see cref="ToggleSnapshot"/>（见 <see cref="UpdateToggleTable"/>）。任务模型的解析、
/// 校验、启用/禁用语义都在 Core，由上层做成快照后交给这里——这样钩子侧就永远不会
/// 因为任务模型膨胀而变重，也让"回调内不做重活"这条硬约束**在类型层面**成立。
/// </para>
///
/// <para><b>三条硬约束的落点</b>（逐条对应设计文档 §3.2 的表格）：</para>
/// <list type="number">
/// <item>钩子过程必须短于 <c>LowLevelHooksTimeout</c>，超时后系统**静默摘除**钩子且应用无从感知
///       → 回调内只做「读注入标志 → 转键 → 查表 → 入队」，不取锁、不分配、不做 IO、不调 UI；
///       并且为了**不取锁**，快照用 <see cref="Volatile.Read{T}(ref T)"/> 整体读引用（见 §3.4.1）。</item>
/// <item>安装钩子的线程必须有消息循环 → <see cref="EnsurePumpThread"/> 起专用线程跑
///       <c>GetMessage</c> 泵，钩子在**该线程上**安装。</item>
/// <item>回调不能被 GC 移动（否则 <c>ExecutionEngineException</c>）→ 回调实现为 <c>static</c> 方法，
///       并保存在 <c>static readonly</c> 字段里（既是静态方法、又有根引用，双保险）。</item>
/// </list>
///
/// <para>
/// <b>线程模型</b>：本类有四类线程活动——① 消息泵线程（装钩子、被系统回调）；
/// ② 投递线程（把队列里的事件抛成事件，保证订阅方不在回调线程上收到通知）；
/// ③ 重装计时器回调（每 5 分钟重装钩子）；④ 外部调用线程（<see cref="Start"/> /
/// <see cref="Stop"/> / <see cref="UpdateToggleTable"/>）。共享状态只有三个字段，
/// 各自都有明确的同步手段（见字段注释）。
/// </para>
/// </summary>
internal sealed class HookService : IDisposable
{
    // ── 当前实例（供静态回调路由）──────────────────────────────────

    /// <summary>
    /// 静态回调的实例入口。回调必须是 <c>static</c>（约束③），
    /// 而判定逻辑是实例状态 → 需要一个静态字段把回调路由回实例。
    ///
    /// <para>
    /// 本服务按「整个进程只有一份」使用（UIDispatcher 之外的全局输入源只有一处），
    /// 故用单引用保存；若并发 <see cref="Start"/> 会以"最后写入者胜"的方式互相覆盖，
    /// 由 <see cref="_lifecycleGate"/> 串行化避免。
    /// </para>
    /// </summary>
    private static HookService? _current;

    /// <summary>
    /// 键盘钩子过程。保存为静态字段：静态方法本身不可被 GC 移动，
    /// 这个字段是"再保险"（设计文档 §3.2 引用的微软要求）。
    /// </summary>
    private static readonly LowLevelKeyboardProc KeyboardProc = StaticKeyboardProc;

    /// <summary>鼠标钩子过程。理由同 <see cref="KeyboardProc"/>。</summary>
    private static readonly LowLevelMouseProc MouseProc = StaticMouseProc;

    // ── 快照与消费判定 ────────────────────────────────────────────

    /// <summary>
    /// 当前快照。**整体替换、原子读**：写方 <see cref="UpdateToggleTable"/> 用
    /// <see cref="Volatile.Write{T}(ref T, T)"/>，读方（钩子回调）用
    /// <see cref="Volatile.Read{T}(ref T)"/> —— 回调因此**永不取锁**。
    /// </summary>
    private ToggleSnapshot _snapshot = ToggleSnapshot.Empty;

    /// <summary>
    /// 开关事件队列：回调侧 <c>Enqueue</c>（无锁、无等待），投递线程 <c>TryDequeue</c>。
    /// 队列是**单向**的：回调只写，"吞掉 / 照常"的决策**不**经此往返
    /// （返回值是同步强制的，等队列回来事件早已放行——见 §3.2「回调返回值即时性约束」）。
    /// </summary>
    private readonly ConcurrentQueue<ToggleEvent> _pending = new();

    /// <summary>投递线程的唤醒信号（有事件入队时置位，避免空转轮询）。</summary>
    private readonly AutoResetEvent _deliverySignal = new(false);

    /// <summary>
    /// 当前按下的修饰键族集合（"Ctrl" / "Alt" / "Shift" / "Win"），用于组合开关键的命中判定
    /// （设计文档 §3.4.1「组合命中」）。
    ///
    /// <para>
    /// <b>只被钩子回调线程读写</b>：低级钩子回调在消息泵线程上串行执行，修饰键的按下/松开
    /// 与查询都在同一线程，天然无竞态——故**不加锁**（符合 §3.4.1「回调永不取锁」）。
    /// <see cref="UpdateToggleTable"/> 不触碰本集合。
    /// </para>
    /// <para>
    /// 用<b>计数</b>而非布尔：修饰键的键重复（auto-repeat）会连发 down 消息，
    /// 只在计数归零时才移除——否则一次重复的 up 就会让"仍处于按下态"的修饰键提前失效。
    /// </para>
    /// <para>
    /// <b>只在键盘路径维护</b>：鼠标键没有修饰键族概念（组合形态仅键盘，见 §3.4.1），
    /// 鼠标回调不进入本集合。
    /// </para>
    /// </summary>
    private readonly Dictionary<string, int> _pressedModifierFamilies = new();

    // ── 按键捕获（录制）─────────────────────────────────────────

    /// <summary>
    /// 是否处于「按键捕获」录制态（设计文档 §5.2 / §6.5.6）。
    ///
    /// <para>
    /// <b>为什么需要它</b>：录制期间用户的按键<b>不是</b>开关键点按，不能拿去查表；
    /// 而且录制态与快照替换来自两个线程（UI 线程置位、钩子回调线程读取），
    /// 所以必须是一个原子读写、且<b>无需取锁</b>的标志——回调不能取锁（§3.2 约束①）。
    /// </para>
    /// <para>
    /// <b>只影响开关路径，不影响对账</b>：修饰键的按下态照常维护（见调用点），
    /// 否则录制出一个组合键之后，<c>_pressedModifierFamilies</c> 会与实际键盘状态脱节。
    /// </para>
    /// </summary>
    private volatile bool _recording;

    /// <summary>录制到的那一次按键；<c>null</c> = 尚无。</summary>
    private CaptureEvent? _capturedEvent;

    /// <summary>录制产出的信号（无捕获时不置位、不空转）。</summary>
    private readonly AutoResetEvent _captureSignal = new(false);

    /// <summary>
    /// 录制到的那一次按键（回调线程写、等待线程读；靠 <see cref="_captureSignal"/> 建立先行关系）。
    /// </summary>
    private readonly record struct CaptureEvent(PhysicalKey Key, string[] ModifierFamilies)
    {
        /// <summary>
        /// 主键：本次录到的物理键里挑一个“非修饰键”；全为修饰键时才退回自身。
        ///
        /// <para>
        /// 场景：用户把 Ctrl+Shift 录成一个开关组合——修饰族是 {Ctrl, Shift}，而“录到的那一下”
        /// 是后松开的 Shift。主键应取 Ctrl（先按下的那个、也是描述里的头一个），
        /// 与 <c>KeyCatalog</c> 的固定序（Ctrl→Alt→Shift→Win）口径一致。
        /// </para>
        /// </summary>
        public PhysicalKey PrimaryKey()
        {
            // 鼠标键没有修饰键族概念（组合形态仅键盘），直接就是它自己。
            if (Key.IsMouse)
            {
                return Key;
            }

            // 修饰族名与 KeyCatalog 用的是同一批字符串（Ctrl / Alt / Shift / Win）。
            // 取族的代表键（左侧那个）——族名不是虚拟键码，不能直接喂给
            // RepresentativeOfFamily（它只认键，遇到未知族名会原样返回，得到错误的主键）。
            foreach (var family in ModifierFamilies)
            {
                var keys = KeyCatalog.KeysOfFamily(family);
                if (keys.Count > 0)
                {
                    return PhysicalKey.FromKeyboard(KeyCatalog.RepresentativeOfFamily(keys[0]));
                }
            }

            return Key;
        }
    }

    /// <summary>
    /// 录制产出的一次按键：物理键 + 主键 + 已归一的修饰键族集合。
    ///
    /// <para>
    /// <b>为何把“主键”与“族集合”都给出</b>：主键是编辑页「主键」下拉的预选值
    /// （FR-TOGGLE-05 的占用判定按主键，不区分修饰键）；族集合交给上层用 <c>KeyCatalog</c>
    /// 重算落盘形状（固定序描述、修饰键集合），编辑页不必自己猜。
    /// </para>
    /// <para>
    /// <b>为何不在这里直接给 <c>KeyBinding</c></b>：本类（Input 层）不认识任务模型与落盘契约，
    /// 它的产出只到今天这一层为止（Ui → Engine → Input）。
    /// </para>
    /// </summary>
    public readonly record struct CapturedKeyEvent(PhysicalKey Key, PhysicalKey PrimaryKey, string[] ModifierFamilies);

    // ── 生命周期 ────────────────────────────────────────────────

    /// <summary>生命周期串行化门（Start / Stop / Dispose 互斥；不参与回调路径）。</summary>
    private readonly object _lifecycleGate = new();

    /// <summary>是否已 <see cref="Start"/> 且尚未 <see cref="Stop"/> / <see cref="Dispose"/>。</summary>
    private bool _started;

    /// <summary>线程/句柄是否已创建（<see cref="Start"/> 后再 <see cref="Stop"/> 不至于重复创建）。</summary>
    private bool _resourcesCreated;

    /// <summary>处置标记：置位后一切入口直接返回。</summary>
    private bool _disposed;

    // ── 原生资源 ────────────────────────────────────────────────

    /// <summary>消息泵线程。钩子在该线程上安装，故 <c>Unhook</c> 也必须回到该线程（见 <see cref="Stop"/>）。</summary>
    private Thread? _pumpThread;

    /// <summary>队列投递线程。</summary>
    private Thread? _deliveryThread;

    /// <summary>投递线程退出信号。</summary>
    private readonly ManualResetEventSlim _deliveryStopping = new(false);

    /// <summary>消息泵线程的 id（<c>PostThreadMessage</c> 需要；0 表示尚无）。</summary>
    private volatile uint _pumpThreadId;

    /// <summary>消息泵线程完成初始化的信号（<see cref="Start"/> 等它，避免竞态）。</summary>
    private readonly ManualResetEventSlim _pumpReady = new(false);

    /// <summary>
    /// 泵线程初始化失败时的异常（<c>null</c> 表示初始化成功）。
    /// <para>
    /// 存在的理由：装钩子必须在泵线程上做，而失败必须让 <see cref="Start"/> 的**调用方**知道。
    /// 若在泵线程上直接重抛，异常会成为未处理异常（.NET 会终止整个进程），
    /// 而 <see cref="Start"/> 已因信号置位而正常返回——既丢了"装钩失败"这个事实，
    /// 又让进程直接死掉。故失败只**记录**于此，由得到泵线程信号的 <see cref="Start"/> 重抛。
    /// </para>
    /// </summary>
    private Exception? _pumpInitFailure;

    /// <summary>键盘钩子句柄。</summary>
    private IntPtr _keyboardHook = IntPtr.Zero;

    /// <summary>鼠标钩子句柄。</summary>
    private IntPtr _mouseHook = IntPtr.Zero;

    /// <summary>本模块句柄（低级钩子不注入他进程，传本模块句柄即可，见 <see cref="GetModuleHandle"/> 调用点注释）。</summary>
    private IntPtr _moduleHandle = IntPtr.Zero;

    /// <summary>周期重装计时器（回退口径；见 <see cref="PeriodicReinstallInterval"/>）。</summary>
    private Timer? _reinstallTimer;

    /// <summary>
    /// 周期重装间隔：设计文档 §3.2 回退口径给出的"如每 5 分钟"。
    /// </summary>
    /// <remarks>
    /// <b>为什么现在是这个策略</b>：钩子被系统静默摘除后应用无从感知，规范做法是
    /// §3.2 所述的**心跳自检**（定期验证钩子是否仍生效），其可行性挂在待验证项 V-4 上。
    /// V-4 未验证前，本实现先落回退口径——无间重装，用"宁可多装一次"换"不会长期无钩子"。
    /// V-4 结论出来后此处应改为自检策略。**该策略与 V-4 状态绑定，改动前先看 §9 待验证项。**
    /// </remarks>
    private static readonly TimeSpan PeriodicReinstallInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 开关事件（回调 → 投递线程的载荷）。用只读结构体而非元组，避免装箱与顺序误读。
    /// </summary>
    private readonly record struct ToggleEvent(PhysicalKey Key, bool IsKeyDown, string[] ModifierFamilies);

    /// <summary>
    /// 某开关键被点按（按下或松开都会各抛一次）。
    ///
    /// <para>
    /// <b>不在回调线程上抛出</b>：订阅方会做启停任务之类的实事，若直接在钩子回调里调用，
    /// 就把调用方的工作量算进了 <c>LowLevelHooksTimeout</c>（约束①）。故本事件由
    /// 投递线程抛出。
    /// </para>
    /// <para>
    /// 第二参数：<c>true</c> = 按下，<c>false</c> = 松开。上层据此决定"点按"的语义
    /// （点按通常在松开的瞬间成立——见设计文档 §3.4「点火语义」）。
    /// </para>
    /// <para>
    /// 第三参数：事件在回调侧当场命中的**修饰键族名集合**（Ctrl / Alt / Shift / Win，已归一），
    /// 单键与鼠标键路径为空数组。事件在**投递线程**抛出，而修饰键状态只在回调线程维护，
    /// 若订阅方到投递线程再读钩子的修饰键状态，那个状态可能已被后续按键改掉——
    /// 因此命中时的族集合必须在<b>回调线程当场取快照</b>并随事件带走（见 §3.4.1 组合命中）。
    /// 订阅方（Engine）据此重算命中任务 Id；钩子侧**不**做任务归属判定，
    /// 保持"低层输入层不认识任务模型"的分层（Ui → Engine → Input）。
    /// </para>
    /// </summary>
    public event Action<PhysicalKey, bool, string[]>? ToggleRequested;

    /// <summary>
    /// 构造。不做任何系统调用、不建线程——资源在 <see cref="Start"/> 时创建。
    /// </summary>
    public HookService()
    {
    }

    // ── 对外接口 ────────────────────────────────────────────────

    /// <summary>
    /// 替换开关按键快照（设计文档 §3.4.1「发布」）。任务列表变更后由配置拥有者调用。
    ///
    /// <para>
    /// <b>参数是一张已构建好的快照，不是任务列表</b>：任务列表 → 快照的翻译
    /// （启用/禁用过滤、族归一、同键合并）全在 Core 的 <see cref="ToggleSnapshot.Build"/> 里完成，
    /// 本类只负责"换上去"。这样钩子侧完全不认识任务模型——低层输入层不依赖业务模型，
    /// 依赖方向（Ui → Engine → Input）得以保持。
    /// </para>
    /// <para>
    /// 传 <c>null</c> 等价于空快照（全放行）。**整体替换**而非增量修改：
    /// 回调侧读到的是"旧快照"或"新快照"二者之一，绝不会读到半张表。
    /// </para>
    /// </summary>
    /// <param name="snapshot">新建的快照；<c>null</c> 视为空快照。</param>
    public void UpdateToggleTable(ToggleSnapshot? snapshot)
    {
        if (_disposed)
        {
            return;
        }

        // 一次引用原子写：回调侧要么看到旧的、要么看到新的，不存在中间态。
        // 这是在"回调不能取锁"前提下的唯一发布手段。
        Volatile.Write(ref _snapshot, snapshot ?? ToggleSnapshot.Empty);
    }

    /// <summary>
    /// 投递一张手工条目表（便利重载，内部转成快照）。
    /// </summary>
    /// <param name="entries">"已归一的物理键 → 是否吞掉"条目；<c>null</c> 视为空。</param>
    public void UpdateToggleTable(IEnumerable<KeyValuePair<PhysicalKey, bool>>? entries)
        => UpdateToggleTable(ToggleSnapshot.FromEntries(entries));

    // ── 按键捕获（对外接口）─────────────────────────────────────

    /// <summary>
    /// 开始按键捕获（设计文档 §5.2 的「录制按键」）。
    ///
    /// <para>
    /// 进入后，钩子只上报<b>下一次</b>按键并随即自动退出录制态（「捕获一次」语义），
    /// 本次事件<b>被消费</b>（不再传给系统）——因为它是用户的配置动作，不是要用的按键。
    /// 键盘与鼠标共用本通道，开关键、命令键、紧急停止快捷键三处的捕获全走它。
    /// </para>
    /// <para>
    /// 重复调用是安全的：仍处于录制态时直接返回 <c>false</c>（不排队、不叠加）。
    /// </para>
    /// </summary>
    /// <returns>
    /// <c>true</c> = 已进入录制态；<c>false</c> = 未生效。未生效的两种情形：
    /// ① 已在录制态（重复调用）；② 钩子未在运行（<see cref="Start"/> 之前或 <see cref="Stop"/> 之后）
    /// ——此时没有任何东西会喂给本通道，静默等待会把 UI 永远挂住。
    /// </returns>
    public bool BeginCapture()
    {
        lock (_lifecycleGate)
        {
            // 与 Start/Stop 同锁：否则可能出现「Stop 摘钩子之后才置位录制态」，
            // 之后 Start 重装钩子时录制态莫名已经是开的。
            if (_disposed || !_started || _recording)
            {
                return false;
            }

            _recording = true;
            return true;
        }
    }

    /// <summary>
    /// 等待捕获结果。**不要用 <c>0</c> 或负值当超时**——捕获靠人手按下，快不了。
    /// </summary>
    /// <param name="timeoutMilliseconds">等待上限（毫秒，须 &gt; 0；超限按默认值截断）。</param>
    /// <returns>捕获结果；超时或捕获期间钩子被停掉时返回 <c>null</c>（录制态随之一并复位）。</returns>
    public CapturedKeyEvent? WaitForCapture(int timeoutMilliseconds)
    {
        var timeout = NormalizeCaptureTimeout(timeoutMilliseconds);

        // 分片等待而非一次等到超时：录制态可能被 Stop() 取消，而取消信号来自另一线程。
        // 分片让「取消」也能在一个分片内被察觉，不必等满整个超时。
        var deadline = Environment.TickCount64 + timeout;
        while (true)
        {
            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0)
            {
                CancelCapture();
                return null;
            }

            var slice = (int)Math.Min(remaining, CaptureWaitSliceMilliseconds);
            if (_captureSignal.WaitOne(slice))
            {
                return TakeCaptureResult();
            }

            if (!_recording || _disposed)
            {
                // 录制已被取消（Stop / Dispose）——这里**不必**再清一次标志，取消方已经清了。
                return null;
            }
        }
    }

    /// <summary>
    /// 退出录制态。
    ///
    /// <para>
    /// 用户「取消录制」与 UI 关闭都调它；<see cref="Stop"/> / <see cref="Dispose"/> 内部也调
    /// （否则钩子停了、录制态还开着，界面就会干等一个永远不来的按键）。
    /// </para>
    /// <para>幂等：不在录制态时是空操作。**保留已捕获的结果**——取消针对的是「还没捕获到」。
    /// </para>
    /// </summary>
    public void CancelCapture()
    {
        // 置位信号：把可能正阻塞在 WaitForCapture 里的等待者立刻放出来（由它自己判空返回）。
        if (_recording)
        {
            _recording = false;
            _captureSignal.Set();
        }
    }

    /// <summary>
    /// 取出捕获结果（仅等待方调用）。
    /// <para><b>谁清录制态</b>：正是本方法。钩子侧只置信号、不碰标志；取消方只清标志、不碰结果。
    /// 等待方是唯一同时看得到两者的人，由它一次对齐最干净：拿到结果就定态退出，
    /// 不把“录制态已清但结果还没取走”这个中间态留给下一次调用去踩。</para>
    /// </summary>
    private CapturedKeyEvent? TakeCaptureResult()
    {
        var captured = _capturedEvent;
        _capturedEvent = null;
        _recording = false;

        // 信号在无结果时被置位（例如取消与捕获撞在一起）：返回空，不把脏状态带出去。
        return captured is { } value
            ? new CapturedKeyEvent(value.Key, value.PrimaryKey(), value.ModifierFamilies)
            : null;
    }


    /// <summary>
    /// 启动：建模块句柄与线程、装钩子、起投递线程与重装计时器。
    /// **幂等**——未 <see cref="Stop"/> 前重复调用直接返回。
    /// </summary>
    /// <exception cref="Win32Exception">钩子安装失败（通常是权限或会话问题）时抛出，调用方据此提示用户。</exception>
    public void Start()
    {
        lock (_lifecycleGate)
        {
            if (_disposed || _started)
            {
                return;
            }

            _moduleHandle = ResolveModuleHandle();

            // 先起投递线程：钩子一旦装上就可能立刻有事件入队，
            // 投递线程若还没起，事件会在队列里多躺一会儿（不丢，只是延迟）。
            EnsureDeliveryThread();
            EnsurePumpThread();
            StartReinstallTimerIfNeeded();

            _started = true;
        }
    }

    /// <summary>
    /// 停止：摘钩子、停线程、停计时器。**幂等**，可在未 <see cref="Start"/> 时安全调用。
    /// </summary>
    public void Stop()
    {
        lock (_lifecycleGate)
        {
            if (!_started && !_resourcesCreated)
            {
                return;
            }

            _started = false;
            CancelCapture();

            // 钩子必须回到安装它的线程上摘除（钩子是按线程登记的）。
            // 顺序上先摘钩子再停泵：泵一停，PostThreadMessage 就没人收了。
            UnhookOnPumpThread();
            StopPumpThread();
            StopDeliveryThread();
            StopReinstallTimer();

            _resourcesCreated = false;
        }
    }

    /// <summary>
    /// 处置：<see cref="Stop"/> + 释放同步原语。**幂等**。
    /// </summary>
    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CancelCapture();
            UnhookOnPumpThread();
            StopPumpThread();
            StopDeliveryThread();
            StopReinstallTimer();

            _resourcesCreated = false;

            // 只摘掉指向自己的引用：多实例场景下不能把别人的路由入口清掉（单实例是本类的既定用法）。
            if (ReferenceEquals(_current, this))
            {
                _current = null;
            }

            _pumpReady.Dispose();
            _deliverySignal.Dispose();
            _captureSignal.Dispose();
            _deliveryStopping.Dispose();
        }
    }

    /// <summary>
    /// 超时参数的合法化：非正数（含 <c>0</c>）按默认值处理，超出上限按上限截断。
    ///
    /// <para>
    /// <c>WaitOne(0)</c> 是“立刻探一下”而不是“等一会儿”——UI 调用方若因笔误传 0，
    /// 会得到一个永远超时的录制按钮，且不报错。与其让调用方踩这个跣，不如在此扶正。
    /// </para>
    /// </summary>
    private static int NormalizeCaptureTimeout(int timeoutMilliseconds)
    {
        if (timeoutMilliseconds <= 0)
        {
            return DefaultCaptureTimeoutMilliseconds;
        }

        return Math.Min(timeoutMilliseconds, MaxCaptureTimeoutMilliseconds);
    }

    /// <summary>
    /// 建消息泵线程（若尚未建）。线程函数在**自己身上**装钩子——这是"钩子与消息泵同线程"的实现要点。
    /// </summary>
    private void EnsurePumpThread()
    {
        if (_pumpThread is not null)
        {
            return;
        }

        _pumpReady.Reset();
        _pumpInitFailure = null;

        _pumpThread = new Thread(PumpThreadMain)
        {
            // 后台线程：宿主退出时不该被本线程吊住。
            IsBackground = true,
            Name = "WkmaHookPump",
        };
        _pumpThread.Start();

        // 等钩子装好再返回：否则 Start() 返回后立刻 Stop() 会与线程初始化赛跑。
        _pumpReady.Wait();

        // 装钩失败要由**本方法**（即 Start 的调用方）感知：泵线程只记录异常，不重抛。
        // 此刻钩子未装上，只能把已起的资源整体回滚，再对外抛出——调用方据此提示用户。
        var initFailure = _pumpInitFailure;
        if (initFailure is not null)
        {
            StopPumpThread();
            throw new Win32Exception(
                initFailure is Win32Exception win32 ? win32.NativeErrorCode : Marshal.GetLastWin32Error(),
                "启动消息泵时安装钩子失败。");
        }

        _resourcesCreated = true;
    }

    /// <summary>
    /// 消息泵线程主体：登记线程 id → 装钩子 → 跑消息循环 → 摘钩子 → 复位线程 id。
    /// </summary>
    /// <remarks>
    /// 本方法是线程入口：**任何异常都不得逃出**——未处理异常会终止进程。
    /// 初始化失败一律记录到 <see cref="_pumpInitFailure"/> 后置位信号，交由
    /// <see cref="EnsurePumpThread"/>（及其调用方 <see cref="Start"/>）处理。
    /// </remarks>
    private void PumpThreadMain()
    {
        _pumpThreadId = GetCurrentThreadId();

        // 让钩子回调能路由回本实例。放在装钩子之前写，保证"钩子被调用"时它已就位。
        _current = this;

        try
        {
            InstallHooksOnCurrentThread();

            // 线程消息队列要先产生一条消息，GetMessage 才会阻塞等待而不是立刻返回 0
            // （0 被当成 WM_QUIT）。这也是开始跑消息循环前的固定手法。
            PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);
        }
        catch (Exception ex)
        {
            _current = null;

            // 记录而不重抛：本方法若把异常抛出到这里，就是"未处理异常 → 进程终止"，
            // 而调用方拿到的却是 Start() 正常返回。记录 + 置位信号才是正确传导路径。
            _pumpInitFailure = ex;
            _pumpReady.Set();
            _pumpThreadId = 0;
            return;
        }

        _pumpReady.Set();

        // 消息循环：钩子回调正是由这个循环派发的，所以它必须一直在跑。
        // 循环退出条件由 StopPumpThread 投递的 WM_QUIT 触发（GetMessage 返回 0）。
        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            // 本进程自定义消息不会走 DispatchMessage（没有窗口），
            // 必须在循环里就地处理——这也是它们唯一的去处。
            switch (message.message)
            {
                case PostFixMessageUnhook:
                    // Stop / Dispose 请求摘钩子。摘完就走：随后 StopPumpThread 会投 WM_QUIT，
                    // 而本线程的收尾路径（循环退出后的 UnhookHooksOnCurrentThread）是幂等的。
                    UnhookHooksOnCurrentThread();
                    break;

                case PostFixMessageReinstall:
                    // 周期性重装（§3.2 回退口径）。只换钩子句柄，不动消息泵。
                    ReinstallHooksOnPumpThread();
                    break;

                default:
                    // 窗口消息（本类没有窗口，但 TranslateMessage/DispatchMessage 是消息泵的
                    // 标准配方，保留它使本线程与常规 UI 线程行为一致）。
                    TranslateMessage(ref message);
                    DispatchMessage(ref message);
                    break;
            }
        }

        // 循环退出后（可能还有挂起的重装计时器回调在途）先摘钩子，再复位。
        UnhookHooksOnCurrentThread();
        _pumpThreadId = 0;
    }

    /// <summary>
    /// 在当前线程上安装键盘与鼠标两个低级钩子。失败时清理已装的那个并抛出。
    /// </summary>
    private void InstallHooksOnCurrentThread()
    {
        if (_moduleHandle == IntPtr.Zero)
        {
            // 取不到模块句柄通常意味着进程已处于异常状态；此时传零句柄让系统用调用方
            // 模块兜底（见 ResolveModuleHandle 注释），这里只是不阻断启动。
            _moduleHandle = ResolveModuleHandle();
        }

        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, KeyboardProc, _moduleHandle, 0);
        if (_keyboardHook == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "安装键盘低级钩子失败。");
        }

        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, MouseProc, _moduleHandle, 0);
        if (_mouseHook == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
            throw new Win32Exception(error, "安装鼠标低级钩子失败。");
        }
    }

    /// <summary>摘除两个钩子（允许在任意线程调用——只改句柄字段，卸载动作见 <see cref="UnhookHooksOnCurrentThread"/>）。</summary>
    private void UnhookOnPumpThread()
    {
        var thread = _pumpThread;
        if (thread is null || _keyboardHook == IntPtr.Zero && _mouseHook == IntPtr.Zero)
        {
            return;
        }

        if (GetCurrentThreadId() == _pumpThreadId)
        {
            UnhookHooksOnCurrentThread();
            return;
        }

        // 钩子是**按线程**登记的，跨线程 UnhookWindowsHookEx 不能保证成功，
        // 故请求泵线程自己摘（投递一个自定义消息，泵线程在循环里处理）。
        if (_pumpThreadId != 0)
        {
            PostThreadMessage(_pumpThreadId, PostFixMessageUnhook, IntPtr.Zero, IntPtr.Zero);

            // 等泵线程摘完再继续（Stop 之后调用方可能立刻重装/重建，不能与摘钩子赛跑）。
            // 等待有上限：泵线程若已死或卡住，不能把 Stop 也一起拖住。
            var deadline = Environment.TickCount64 + UnhookWaitMilliseconds;
            while (Environment.TickCount64 < deadline && (_keyboardHook != IntPtr.Zero || _mouseHook != IntPtr.Zero))
            {
                Thread.Sleep(5);
            }
        }
    }

    /// <summary>
    /// 真正调用 <c>UnhookWindowsHookEx</c>——**必须在装钩子的那个线程上执行**。
    /// </summary>
    private void UnhookHooksOnCurrentThread()
    {
        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
    }

    /// <summary>停消息泵线程（投 WM_QUIT → 等线程退出）。</summary>
    private void StopPumpThread()
    {
        var thread = _pumpThread;
        if (thread is null)
        {
            return;
        }

        var threadId = _pumpThreadId;
        if (threadId != 0)
        {
            PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        // 等泵线程退出再清引用：否则重装计时器可能读到已死线程的 id（见 ReinstallHooks 注释）。
        thread.Join(ThreadJoinTimeoutMilliseconds);
        _pumpThread = null;
        _pumpThreadId = 0;
    }

    // ── 钩子回调（静态入口 → 实例路由）───────────────────────────

    /// <summary>
    /// 键盘钩子回调的静态入口。**本方法体内只做一件事**：把调用路由到实例方法。
    /// 静态 + 静态字段持有 = 满足"回调不可被 GC 移动"（约束③）。
    /// </summary>
    private static IntPtr StaticKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        var self = _current;
        if (self is null)
        {
            // 实例已消失（Dispose 后仍被系统调用的窗口期）→ 一律放行。
            // 放行是唯一安全选择：吞掉用户的按键而不做任何事，比不工作更糟。
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        return self.OnKeyboard(nCode, wParam, lParam);
    }

    /// <summary>鼠标钩子回调的静态入口。理由同 <see cref="StaticKeyboardProc"/>。</summary>
    private static IntPtr StaticMouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        var self = _current;
        if (self is null)
        {
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        return self.OnMouse(nCode, wParam, lParam);
    }

    /// <summary>
    /// 键盘回调的实例逻辑——**三件事，仅此而已**：读注入标志 → 转物理键 → 查快照判返回值。
    ///
    /// <para>
    /// <b>返回值口径（设计文档 §3.4）</b>：吞掉 = 返回**非零**且**不调** <c>CallNextHookEx</c>；
    /// 照常 = 调 <c>CallNextHookEx</c> 并**返回它的返回值**。按下与松开**同口径**——
    /// 只在按下吞、松开放行会让 Windows 看到"永久的按下态"，是明确的 bug。
    /// </para>
    /// <para>
    /// <b>本方法内禁止</b>（约束①，超时即被系统静默摘钩且不可感知）：加锁、分配、IO、
    /// 调用 <c>SendInput</c>、触碰 UI、遍历任务列表。新增代码若突破这三件事，请先回到 §3.2。
    /// </para>
    /// </summary>
    private IntPtr OnKeyboard(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // 系统要求 nCode < 0 时原样转发，不做任何处理。
        if (nCode < 0)
        {
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        var message = wParam.ToInt32();

        // 只关心按下/松开（含 Alt 变体）；其余消息（如系统热键字段）直接放行。
        var isKeyDown = message is WM_KEYDOWN or WM_SYSKEYDOWN;
        if (!isKeyDown && message is not (WM_KEYUP or WM_SYSKEYUP))
        {
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

        // §3.3 自注入过滤：我们自己 SendInput 发出的键同样会走到这里，
        // 不过滤会形成"任务点按 A → 钩子看到 A → A 恰是开关键 → 误触发"的恶性循环。
        // 这一条必须排在查表**之前**——否则模拟输入会被当成用户按键消费掉。
        if ((info.flags & LLKHF_INJECTED) != 0)
        {
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        var key = KeyInterop.KeyFromVirtualKey(unchecked((int)info.vkCode));

        // §3.4.1 组合命中的前置状态：先按本次事件维护"当前按下的修饰键族集合"，
        // 再查表——顺序不能反：用户按下 Ctrl（先）再按 F8（后）时，判定 F8 必须
        // 已经看到 Ctrl 在集合里。
        // 修饰键事件本身也会继续走下面的查表（它可能正是一个任务的单键开关键），
        // 维护与查表互不替代。
        // 对账必须紧跟维护之后、查表之前（§3.4.1「无重同步路径」）：
        // 漏收 key-up 会让族计数永不归零，组合开关键从此永久失灵；
        // 这里用 GetAsyncKeyState 核对已记账的族，把残留清掉再看本次事件是否命中。
        TrackModifier(key, isKeyDown);
        ReconcileModifierFamilies();

        var physicalKey = PhysicalKey.FromKeyboard(key);

        // 录制态判定**必须晚于修饰键维护**：录一个组合键时，用户是「先按 Ctrl」的，
        // Ctrl 那一下自己就是一个按键事件——若录制判定排在维护之前，录到的会是裸 Ctrl 而不是
        // Ctrl+主键；而且跳过维护会让 _pressedModifierFamilies 与实际键盘状态脱节。
        // 但必须**早于查表**：录制期间用户按的是「要录的键」，不是「开关键」。
        if (_recording)
        {
            return Capture(physicalKey, isKeyDown, nCode, wParam, lParam);
        }

        return RouteToggle(physicalKey, isKeyDown, nCode, wParam, lParam, isKeyboard: true);
    }

    /// <summary>
    /// 维护"当前按下的修饰键族集合"（仅键盘路径调用，见 <see cref="_pressedModifierFamilies"/>）。
    ///
    /// <para>
    /// 非修饰键直接返回——它们不参与组合前置条件。族名由
    /// <see cref="KeyCatalog.ModifierFamily"/> 给出（已含左右归一），故左右 Ctrl 统一计入 "Ctrl"。
    /// </para>
    /// <para>
    /// <b>无分配</b>：命中已存在的族名时只改计数（字典是预分配的，键是静态字符串）；
    /// 首次出现某族名时才会插入一次——每族至多一次，不在热路径上反复分配。
    /// </para>
    /// </summary>
    private void TrackModifier(Key key, bool isKeyDown)
    {
        if (KeyCatalog.ModifierFamily(key) is not { } family)
        {
            return;
        }

        if (isKeyDown)
        {
            _pressedModifierFamilies[family] = _pressedModifierFamilies.TryGetValue(family, out var count)
                ? count + 1
                : 1;
        }
        else if (_pressedModifierFamilies.TryGetValue(family, out var count))
        {
            if (count <= 1)
            {
                _pressedModifierFamilies.Remove(family);
            }
            else
            {
                _pressedModifierFamilies[family] = count - 1;
            }
        }
    }

    /// <summary>
    /// 修饰键族状态的**对账**（设计文档 §3.4.1「无重同步路径」）——维护之后、查表之前调用。
    ///
    /// <para>
    /// <b>为什么需要它</b>：<see cref="_pressedModifierFamilies"/> 只在钩子回调里增删，
    /// 而 key-up 未必总能收到（UAC 提权确认 / 安全桌面切换 / 进程被挂起时钩子收不到事件）。
    /// 一旦漏收，该族计数永不归零 ⇒ 组合开关键永久失灵，且用户完全无从知晓。
    /// 本方法用 <see cref="GetAsyncKeyState"/> 核对**已记账**的每一族是否**真的**还按着，
    /// 不实的就地移除——把"永久的错误状态"降级为"最多错到下一次键盘事件"。
    /// </para>
    /// <para>
    /// <b>只核已记账的族</b>（不是全量四族）：集合为空时零开销，
    /// 代价只与"当前按着的修饰键数"成正比，与族总数无关。
    /// </para>
    /// <para>
    /// <b>无分配</b>：常态路径（无残留）不分配任何对象——
    /// <see cref="List{T}"/> 仅在**确实发现残留**时才创建；命中残留时只 <c>Remove</c>。
    /// </para>
    /// <para>
    /// <b>已知限制</b>：<see cref="GetAsyncKeyState"/> 在 UAC 安全桌面上本身也可能返回不准，
    /// 故本对账修的是"回到普通桌面后残留"这一类（最常见）；安全桌面**持续期间**的组合键
    /// 失灵仍无解（见设计文档 §3.4.1）。
    /// </para>
    /// </summary>
    private void ReconcileModifierFamilies()
    {
        // 集合为空是常态（绝大多数时刻没按修饰键）——先短路，省掉字典枚举开销。
        if (_pressedModifierFamilies.Count == 0)
        {
            return;
        }

        // 不能在枚举字典的同时删除元素（会抛 InvalidOperationException）——
        // 故先收集待删族名，再统一移除。
        List<string>? stale = null;
        foreach (var family in _pressedModifierFamilies.Keys)
        {
            if (IsFamilyPhysicallyDown(family))
            {
                continue;
            }

            (stale ??= new List<string>()).Add(family);
        }

        if (stale is null)
        {
            return;
        }

        foreach (var family in stale)
        {
            _pressedModifierFamilies.Remove(family);
        }
    }

    /// <summary>
    /// 把 <see cref="_pressedModifierFamilies"/> **当场复制**成数组，供开关事件携带（方案 (a)）。
    ///
    /// <para>
    /// <b>为什么是复制</b>：事件在<b>投递线程</b>抛出，而族状态只在回调线程维护、随后续按键变化。
    /// 若把 <c>Dictionary.Keys</c> 视图直接塞进事件，订阅方读到的是"投递那一刻"的活状态——
    /// 可能是用户在按键之后又补按了 Ctrl，与本次命中的判定依据已不是同一份数据。
    /// 回调线程当场复制，事件携带的即是**命中依据本身**。
    /// </para>
    /// <para>
    /// <b>热路径代价</b>：只在命中开关的那一次触摸上分配（常态下绝大多数键盘事件根本不进本方法），
    /// 且长度至多 4（四族）。这是"回调内不做重活"约束下的可接受成本——
    /// 组合判定的正确性优先于一次小分配（§3.4.1）。
    /// </para>
    /// </summary>
    private string[] SnapshotModifierFamilies()
    {
        // 空集合是常态（没按修饰键）：直接复用零长度数组，命中普通单键时零分配。
        if (_pressedModifierFamilies.Count == 0)
        {
            return Array.Empty<string>();
        }

        var families = new string[_pressedModifierFamilies.Count];
        _pressedModifierFamilies.Keys.CopyTo(families, 0);
        return families;
    }


    /// <summary>
    /// 该族是否有任一物理键此刻真的按着（左或右，任一即算）。
    ///
    /// <para>
    /// <see cref="GetAsyncKeyState"/> 只看**高位**（<c>0x8000</c>）——
    /// 低位是"自上次调用后曾被按过"的历史位，与"此刻是否按下"无关，不能拿来做判定。
    /// </para>
    /// </summary>
    private static bool IsFamilyPhysicallyDown(string family)
    {
        foreach (var key in KeyCatalog.KeysOfFamily(family))
        {
            if ((GetAsyncKeyState(KeyInterop.VirtualKeyFromKey(key)) & 0x8000) != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 鼠标回调的实例逻辑。与 <see cref="OnKeyboard"/> 同构：注入过滤 → 转物理键 → 查快照。
    /// </summary>
    /// <remarks>
    /// 只处理按键类消息（左/右/中/侧键的按下与松开）；移动、滚轮、以及 XBUTTON 的
    /// 非按下/松开状态一律放行——它们既不是开关按键，也不该被消费判定碰到。
    /// </remarks>
    private IntPtr OnMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        var message = wParam.ToInt32();

        var isButtonDown = message is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN;
        if (!isButtonDown && message is not (WM_LBUTTONUP or WM_RBUTTONUP or WM_MBUTTONUP or WM_XBUTTONUP))
        {
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

        // §3.3：自注入过滤，理由同键盘路径（我们模拟的鼠标点击也会被自己捕获）。
        if ((info.flags & LLMHF_INJECTED) != 0)
        {
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        if (!TryMapMouseButton(message, info.mouseData, out var mouseKey))
        {
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        var physicalKey = PhysicalKey.FromMouse(mouseKey);

        // 录制态判定与键盘路径同口径：早于查表（鼠标路径没有修饰键维护，故这里直接判）。
        if (_recording)
        {
            return Capture(physicalKey, isButtonDown, nCode, wParam, lParam);
        }

        return RouteToggle(physicalKey, isButtonDown, nCode, wParam, lParam, isKeyboard: false);
    }

    /// <summary>
    /// 按键捕获（录制）的钩子侧落点（设计文档 §5.2「捕获期间钩子消费掉按键」）。
    ///
    /// <para>
    /// <b>本方法能进钩子回调，故适用回调的全部禁令</b>（§3.2 约束①）：不取锁、不分配、
    /// 不做 IO、不碰 UI、不查任务表。它做的三件事都在这条线内：
    /// ① 声明一次捕获事件（值类型，无装箱）；② 原子换出 + 就地定态（消费语义）；
    /// ③ 置一个 AutoResetEvent 叫醒等待方——置信号不阻塞、不分配。
    /// </para>
    /// <para>
    /// <b>为什么捕获事件声明要留在回调线程</b>：键是「按下的那一下」才成立，
    /// 松开的捕获会把主键收到松开事件上；而且交给别的线程就得多一份待处理的键，又回到分配上。
    /// 回调线程是按键事件的天然序列化点，在这里一次定态最自然。
    /// </para>
    /// <para>
    /// <b>为什么只置信号、由等待方自己清标志</b>：本方法与 <see cref="CancelCapture"/>
    /// 会在两个线程上并发（用户在 UI 上点取消、或 <see cref="Stop"/>）。
    /// 若两边都写 <c>_recording</c>，取消方清掉后本方法又置回去，会留下一个「钩子不再喂数据、
    /// 录制态却开着」的黑洞，UI 永远等不到结果。故只有取消方负责清，捕获方只置信号，
    /// 等待方醒来后无条件清——三处各自单写、无竞态。
    /// </para>
    /// </summary>
    /// <returns>一律「吞掉」：录制期的按键是用户的配置动作，不应落到别的窗口去。</returns>
    private IntPtr Capture(PhysicalKey key, bool isKeyDown, int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (!isKeyDown)
        {
            // 只认按下：若把松开当捕获结果，用户按住不放会产生两个事件，先到的那个反而会被丢掉
            // （AutoResetEvent 只记得一次信号）。放行松开不吞：录制的是一次按下，松开无关位。
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        // 当场取修饰键族集合（与开关事件同口径）：鼠标路径无修饰键族概念，给空数组。
        var families = key.IsKeyboard ? SnapshotModifierFamilies() : Array.Empty<string>();
        _capturedEvent = new CaptureEvent(key, families);
        _captureSignal.Set();

        return new IntPtr(1);
    }

    /// <summary>
    /// 共用的判定与分发：查快照 → 命中则入队 → 按消费口径决定"吞掉 / 照常"。
    ///
    /// <para>
    /// 入队与返回值**互不影响**：入队是"通知上层"，返回值是"是否放行本次事件"。
    /// 无论吞或不吞，命中的开关键都要通知上层——否则"配了吞掉"的任务永远收不到开关信号。
    /// </para>
    /// <para>
    /// <paramref name="isKeyboard"/> 决定查表口径：键盘走组合判定（修饰键族集合必须全部按下），
    /// 鼠标走单键判定（鼠标键没有修饰键族概念，见 §3.4.1）。
    /// </para>
    /// </summary>
    private IntPtr RouteToggle(PhysicalKey key, bool isKeyDown, int nCode, IntPtr wParam, IntPtr lParam, bool isKeyboard)
    {
        // 无锁读：快照是整体替换的，这里要么拿到旧表要么拿到新表。
        var snapshot = Volatile.Read(ref _snapshot);

        // 键盘路径用组合口径（修饰键族集合 ⊆ 当前按下集）；鼠标路径用单键口径。
        // 注意：_pressedModifierFamilies 只在回调线程读写，此处无锁无分配。
        var hit = isKeyboard
            ? snapshot.TryGetConsume(key, _pressedModifierFamilies.Keys, out var consume)
            : snapshot.TryGetConsume(key, out consume);

        if (!hit)
        {
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        // 命中时的修饰键族集合**当场快照**：本线程是修饰键状态的唯一维护者，
        // 投递线程上再读会拿到被后续按键改过的值。鼠标路径无修饰键族概念，给空数组。
        var families = isKeyboard
            ? SnapshotModifierFamilies()
            : Array.Empty<string>();

        _pending.Enqueue(new ToggleEvent(key, isKeyDown, families));
        _deliverySignal.Set();

        // §3.4：吞掉 = 非零返回且不调 CallNextHookEx（事件到此为止，后面的钩子与
        // 目标窗口都收不到）；照常 = 调 CallNextHookEx 并返回它的返回值（保持钩子链约定）。
        return consume ? new IntPtr(1) : CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    /// <summary>把鼠标消息映射为鼠标键（不涉及任何"自创映射表"——就是 Win32 消息本身的语义）。</summary>
    private static bool TryMapMouseButton(int message, uint mouseData, out MouseKey key)
    {
        switch (message)
        {
            case WM_LBUTTONDOWN:
            case WM_LBUTTONUP:
                key = MouseKey.Left;
                return true;
            case WM_RBUTTONDOWN:
            case WM_RBUTTONUP:
                key = MouseKey.Right;
                return true;
            case WM_MBUTTONDOWN:
            case WM_MBUTTONUP:
                key = MouseKey.Middle;
                return true;
            case WM_XBUTTONDOWN:
            case WM_XBUTTONUP:
                // X1/X2 共用一条消息，具体是哪个由 mouseData 的高位字给出。
                var xButton = (mouseData >> 16) & 0xFFFF;
                if (xButton == XBUTTON1)
                {
                    key = MouseKey.X1;
                    return true;
                }

                if (xButton == XBUTTON2)
                {
                    key = MouseKey.X2;
                    return true;
                }

                key = default;
                return false;
            default:
                key = default;
                return false;
        }
    }

    // ── 事件投递线程 ────────────────────────────────────────────

    /// <summary>建投递线程（若尚未建）。</summary>
    private void EnsureDeliveryThread()
    {
        if (_deliveryThread is not null)
        {
            return;
        }

        _deliveryStopping.Reset();

        _deliveryThread = new Thread(DeliveryThreadMain)
        {
            IsBackground = true,
            Name = "WkmaHookDelivery",
            // 投递线程上跑的是订阅方的业务代码（启停任务等），给个常规优先级即可；
            // 它不在钩子回调路径上，故不涉及 LowLevelHooksTimeout。
            Priority = ThreadPriority.Normal,
        };
        _deliveryThread.Start();
        _resourcesCreated = true;
    }

    /// <summary>
    /// 投递线程主体：把队列里的事件抛成 <see cref="ToggleRequested"/>。
    /// </summary>
    /// <remarks>
    /// <b>为什么必须另起线程</b>：事件的处理者会做实事（启停任务 → 可能 <c>SendInput</c>）。
    /// 若在钩子回调线程上直接抛，就把订阅方的工作量算进了 <c>LowLevelHooksTimeout</c>，
    /// 一旦超时钩子被系统静默摘除——这正是 §3.2 要避免的。
    /// </remarks>
    private void DeliveryThreadMain()
    {
        while (!_deliveryStopping.IsSet)
        {
            if (!_pending.TryDequeue(out var item))
            {
                // 无事件可投：等信号（带超时是为了即使信号丢失也能定期复查退出标志）。
                _deliverySignal.WaitOne(DeliveryIdleWaitMilliseconds);
                continue;
            }

            DeliverSafely(item);
        }

        // 退出前把队列里剩余的事件投完：Stop 之后丢掉已入队的开关事件，
        // 会让用户"明明按了却没反应"且无任何解释。至多加一次批量投递，不会无限循环。
        while (_pending.TryDequeue(out var item))
        {
            DeliverSafely(item);
        }
    }

    /// <summary>
    /// 抛出单个事件，并把订阅方异常**隔离**在本线程内。
    /// </summary>
    /// <remarks>
    /// <b>为什么吞异常而不是让它冒泡</b>：投递线程是唯一投递渠道，它死掉意味着
    /// "钩子看得见按键、上层永远收不到"——比抛出一个异常难排查得多。这里选择
    /// 记录到调试输出并继续投递；真正的错误由订阅方自己处理（它的 try/catch 比这里更懂上下文）。
    /// </remarks>
    private void DeliverSafely(ToggleEvent item)
    {
        var handler = ToggleRequested;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(item.Key, item.IsKeyDown, item.ModifierFamilies);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HookService] 开关事件订阅方抛出异常：{ex}");
        }
    }

    /// <summary>停投递线程：置退出信号 → 唤醒 → 等线程把余量投完退出。</summary>
    private void StopDeliveryThread()
    {
        var thread = _deliveryThread;
        if (thread is null)
        {
            return;
        }

        _deliveryStopping.Set();
        _deliverySignal.Set();
        thread.Join(ThreadJoinTimeoutMilliseconds);
        _deliveryThread = null;
    }

    // ── 周期性重装（回退口径）────────────────────────────────────

    /// <summary>
    /// 起周期重装计时器（若尚未起）。仅当钩子已在运行时有意义。
    /// </summary>
    private void StartReinstallTimerIfNeeded()
    {
        if (_reinstallTimer is not null)
        {
            return;
        }

        _reinstallTimer = new Timer(
            _ => ReinstallHooks(),
            state: null,
            dueTime: PeriodicReinstallInterval,
            period: PeriodicReinstallInterval);
    }

    /// <summary>停周期重装计时器。</summary>
    private void StopReinstallTimer()
    {
        var timer = _reinstallTimer;
        if (timer is null)
        {
            return;
        }

        _reinstallTimer = null;

        // 先置空字段挡住计时器回调入队，再释放底层定时器。
        // 释放之后仍可能有一次已在途的回调，它走 _disposed / _started 分支直接返回。
        _ = timer.DisposeAsync();
    }

    /// <summary>
    /// 计时器回调可能在任意线程池线程上执行，而"摘钩子 / 装钩子"都必须在泵线程上做，
    /// 所以这里只做**请求投递**：让泵线程自己完成 摘 → 装。
    ///
    /// <para>
    /// <b>句柄的时效性由泵线程把关</b>：本方法与 <see cref="StopPumpThread"/> 会并发——
    /// 计时器可能恰好在"泵线程已退出、<c>_pumpThreadId</c> 尚未清"的窗口里读到线程 id。
    /// 此时投出的 <c>PostFixMessageReinstall</c> 会落到一个**已死线程的队列**上（无害，没人收），
    /// 但若该线程 id 已被系统回收给新线程，就投错了对象。故本方法**不自行判死**，
    /// 而是把"句柄是否有效"的判断放到接收侧（<see cref="ReinstallHooksOnPumpThread"/> 只该由
    /// 活着的泵线程调用），并保持与 <see cref="StopPumpThread"/> 的<b>同锁顺序</b>：
    /// 两者都先取 <see cref="_lifecycleGate"/>。
    /// </para>
    /// </remarks>
    private void ReinstallHooks()
    {
        if (_disposed || !_started)
        {
            return;
        }

        lock (_lifecycleGate)
        {
            // 与 StopPumpThread 同锁：拿到锁就说明"泵线程的建立/拆除"没在进行，
            // _pumpThreadId 此刻是稳定的，不会读到已死线程的 id。
            if (_disposed || !_started)
            {
                return;
            }

            var threadId = _pumpThreadId;
            if (threadId == 0)
            {
                return;
            }

            PostThreadMessage(threadId, PostFixMessageReinstall, IntPtr.Zero, IntPtr.Zero);
        }
    }

    /// <summary>在泵线程上执行"摘掉再装上"。失败时不抛——下个周期还会再试。</summary>
    private void ReinstallHooksOnPumpThread()
    {
        UnhookHooksOnCurrentThread();

        try
        {
            InstallHooksOnCurrentThread();
        }
        catch (Win32Exception ex)
        {
            // 重装失败：记一笔，等下一个周期。这里**不能**让异常冲掉消息泵线程——
            // 泵线程一死，整套输入监听就彻底失效了，比"本次没装上"严重得多。
            System.Diagnostics.Debug.WriteLine($"[HookService] 周期性重装钩子失败，将在下个周期重试：{ex.Message}");
        }
    }

    // ── 原生互操作 ──────────────────────────────────────────────

    /// <summary>
    /// 取本模块句柄。低级钩子不注入他进程（文档明确），因此传本模块句柄即可；
    /// 用 <c>GetModuleHandle(null)</c> 取**主模块**句柄，正是当前可执行模块。
    /// </summary>
    /// <remarks>
    /// 这个取法是**被验证过的**（2026-10-08 现场探针）：.NET 8 下由
    /// <c>实例方法组 → Action</c> 派生的委托，其 <c>Target</c> 就是实例本身；
    /// 而低级钩子的 <c>hMod</c> 参数在"回调位于调用方进程内"时不需要是回调所在的
    /// 具体程序集句柄——传主模块句柄是通行且成立的用法。
    /// </remarks>
    private static IntPtr ResolveModuleHandle() => GetModuleHandle(null);

    // ── P/Invoke 声明 ───────────────────────────────────────────

    // 常量与结构体都私有落在本文件内：与既有 SendInputSink 的内部 Interop 同款风格
    // （P/Invoke 自包含，不外泄平台细节）。命名沿用 Win32 原文，便于与文档对照。

    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;

    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_MBUTTONUP = 0x0208;
    private const int WM_XBUTTONDOWN = 0x020B;
    private const int WM_XBUTTONUP = 0x020C;

    private const uint XBUTTON1 = 0x0001;
    private const uint XBUTTON2 = 0x0002;

    /// <summary>键盘事件由 <c>SendInput</c>/<c>keybd_event</c> 注入（§3.3 据此过滤）。</summary>
    private const uint LLKHF_INJECTED = 0x10;

    /// <summary>鼠标事件由 <c>SendInput</c>/<c>mouse_event</c> 注入（§3.3 据此过滤）。</summary>
    private const uint LLMHF_INJECTED = 0x01;

    private const uint PM_NOREMOVE = 0x0000;

    /// <summary>退出消息循环的消息（<c>GetMessage</c> 返回 0 即循环终止）。</summary>
    private const uint WM_QUIT = 0x0012;

    private const int DefaultCaptureTimeoutMilliseconds = 30_000;

    /// <summary>等待录制结果的上限封顶（毫秒）：更长也没有意义，只会把 UI 卡死。</summary>
    private const int MaxCaptureTimeoutMilliseconds = 300_000;

    /// <summary>
    /// <see cref="WaitForCapture"/> 的等待分片（毫秒）：每等一段就去复查一次录制态，
    /// 让“取消”不必等到整段超时才生效。
    /// </summary>
    private const int CaptureWaitSliceMilliseconds = 100;

    /// <summary>线程同步等待的上限（毫秒）。泵线程若已卡死，不能把 Stop 也一起拖住。</summary>
    private const int ThreadJoinTimeoutMilliseconds = 2000;

    /// <summary>摘钩子请求消息（见消息泵循环里的处理）。</summary>
    private const uint PostFixMessageUnhook = 0x8000 + 1;

    /// <summary>重装钩子请求消息（见消息泵循环里的处理）。</summary>
    private const uint PostFixMessageReinstall = 0x8000 + 2;

    /// <summary>等待泵线程摘钩子的上限（毫秒）。</summary>
    private const int UnhookWaitMilliseconds = 500;

    /// <summary>投递线程空闲时的信号等待上限（毫秒）；同时充当退出标志的复查周期。</summary>
    private const int DeliveryIdleWaitMilliseconds = 250;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>键盘低级钩子的回调参数（Win32 <c>KBDLLHOOKSTRUCT</c>）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        /// <summary>虚拟键码。</summary>
        public uint vkCode;

        /// <summary>硬件扫描码。</summary>
        public uint scanCode;

        /// <summary>标志位（<see cref="LLKHF_INJECTED"/> 等）。</summary>
        public uint flags;

        /// <summary>消息时间戳。</summary>
        public uint time;

        /// <summary>附加信息。</summary>
        public IntPtr dwExtraInfo;
    }

    /// <summary>鼠标低级钩子的回调参数（Win32 <c>MSLLHOOKSTRUCT</c>）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        /// <summary>屏幕坐标。</summary>
        public int ptX;

        /// <summary>屏幕坐标。</summary>
        public int ptY;

        /// <summary>滚轮 / XBUTTON 数据（XBUTTON 编号在**高 16 位**）。</summary>
        public uint mouseData;

        /// <summary>标志位（<see cref="LLMHF_INJECTED"/> 等）。</summary>
        public uint flags;

        /// <summary>消息时间戳。</summary>
        public uint time;

        /// <summary>附加信息。</summary>
        public IntPtr dwExtraInfo;
    }

    /// <summary>消息结构（<c>PeekMessage</c> / <c>GetMessage</c> 用）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
        public uint lPrivate;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 查询指定虚拟键的物理按下状态（设计文档 §3.4.1「无重同步路径」的对账手段）。
    ///
    /// <para>
    /// 返回值是 <c>short</c>：<b>高位（0x8000）置位 = 当前按下</b>；低位是
    /// "自上次调用后曾被按过"的历史位，本处**只看高位**（历史位与我们的对账无关）。
    /// </para>
    /// <para>
    /// 已知限制：在 UAC 安全桌面（提升确认界面）上，被提升的进程对本函数返回的状态
    /// 未必反映真实键盘——故本函数只能修复"回到普通桌面后残留"这一类，安全桌面
    /// **持续期间**的组合键失灵仍无解（见 §3.4.1）。
    /// </para>
    /// </summary>
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
