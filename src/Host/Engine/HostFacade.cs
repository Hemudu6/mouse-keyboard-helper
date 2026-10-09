using Core.Config;
using Core.Diagnostics;
using Core.Input;
using Core.Keys;
using Core.Model;
using Core.Validation;
using Host.Input;

namespace Host.Engine;

/// <summary>
/// 组合根：进程内**唯一的装配点**（设计文档 §2.2 指定的落点，§6.5 给出接线全图）。
///
/// <para>
/// <b>为什么要有这么个类</b>：本工程的三个输入侧组件（<see cref="HookService"/> /
/// <see cref="InputSimulator"/> / <see cref="Scheduler"/>）各自只做自己那一层的事，
/// 谁都不认识谁。把它们缝起来的那几根线——尤其是"开关事件 → 查表 → 启停任务"——
/// 是产品行为的真正载体，但散落在 UI 里就会变成"每个页面各接一遍、漏一处就是一个功能不工作"。
/// 于是集中到这里：UI 只跟本类打交道，接线只此一处（漏推快照这类 bug 因此不可能在某个页面上单独出现）。
/// </para>
///
/// <para>
/// <b>分层</b>：本类在 Engine 层，引用 Input 层（<c>Ui → Engine → Input</c> 单向）。
/// 它**不反向依赖 UI**——对界面的暴露只有 <see cref="RunStateChanged"/> 一个事件
/// （Engine → UI 的唯一推送通道，§6.5.3），UI 不得反过来轮询运行态。
/// </para>
///
/// <para><b>接线全图</b>（逐条对应 §6.5.1 的表格）：</para>
/// <list type="number">
/// <item><see cref="JsonConfigStore"/> → <see cref="TaskRegistry"/>：构造注入，
///       <see cref="TaskRegistry.Load"/> 载入并回写一次归一结果。</item>
/// <item><see cref="SendInputSink"/> → <see cref="InputSimulator"/> → <see cref="Scheduler"/>：
///       构造注入。全进程**只有一个** <see cref="InputSimulator"/>——按住表是全局唯一一张
///       （§3.1.1），所以必须先建模拟器再建调度器。</item>
/// <item><see cref="HookService"/>：独立持有，<see cref="Start"/> 装钩子、<see cref="Dispose"/> 摘钩子。</item>
/// <item><see cref="HookService.ToggleRequested"/> → 判归属 → <see cref="Scheduler.Toggle"/> / 
///       逐任务 <see cref="Scheduler.Stop"/>：事件订阅。载荷里携带任务 Id（<c>null</c> = 紧急停止，
///       FR-RUN-01；非 <c>null</c> = 任务开关，§3.5.2）。
///       事件在**投递线程**抛出，订阅回调必须立刻返回（§3.2 回调最小化）。</item>
/// <item>任务列表变更 / 全局设置变更 → 重建快照 → <see cref="HookService.UpdateToggleTable"/>：单向推送。
///       <b>漏推的后果是“新任务的开关键不生效”或“紧急停止键改了不生效”</b>——故本类的
///       每个增删改入口都在末尾调 <see cref="RefreshToggleTable"/>，没有例外；
///       设置保存路径（会改紧急停止键）同理（§3.5.4）。</item>
/// <item><see cref="ILogSink"/>（实作 <see cref="AppLogger"/>）：全局唯一，落盘到
///       配置目录下的 <c>logs\</c>（§7.3）。全部错误源（注入失败 / 命令循环异常 /
///       落盘失败 / 设置保存失败 / 钩子启停失败）与业务事件（任务启停）均由本类
///       注入、订阅或就地捕获，各层自己不创建日志器——否则一个进程会开出好几个
///       文件句柄各写各的。</item>
/// </list>
/// </summary>
public sealed class HostFacade : IDisposable
{
    /// <summary>本类是否已处置（处置后一切入口静默返回）。</summary>
    private bool _disposed;

    /// <summary>
    /// 装配串行化门。挡住"两个线程同时改任务列表"——改列表要经过
    /// 「改 Registry → 重建快照 → 推给钩子」三步，这三步必须原子地成组完成，
    /// 否则中间态会被钩子看到（快照与列表不一致的一瞬间，正好有按键进来就是误命中）。
    /// </summary>
    private readonly object _wiringGate = new();

    /// <summary>
    /// 快照发布与事件处理之间的交接锁。见 <see cref="RefreshToggleTable"/> 与
    /// <see cref="ToggleRequestedHandler"/>：前者在锁内发布 <see cref="_snapshot"/>，
    /// 后者在锁内读任务注册表——两者同锁，事件处理就不会插在“新表已发布”
    /// 与“新表已推给钩子”中间。
    /// </summary>
    /// <remarks>
    /// <b>它做不到什么（写下来免得后人误会）</b>：推送 <see cref="HookService.UpdateToggleTable"/>
    /// 不在锁内，且 <see cref="TaskRegistry"/> 自身是线程安全的。
    /// 所以真实存在的竞态窗口只有“快照刚建好、钩子或任务注册表已被改”这一种，
    /// 它的后果是**一条**开关事件被静默丢弃（<see cref="Tasks"/> 里查不到那个 Id），
    /// 既不会启错任务、也不会漏启任务：下一次点按就对了。
    /// 本锁把“发布”与“查任务”排了个固定的先后，仅此而已——
    /// 它**不**保证“钩子手上的表”与“门面手上的注册表”永远一致，那也不是它能做到的。
    /// </remarks>
    private readonly object _snapshotGate = new();

    /// <summary>
    /// 最近一次重建并推给钩子的快照。**只被 <see cref="RefreshToggleTable"/> 写、
    /// 不被任何回调读**——门面手上那份表就是它，而回调查任务走
    /// <see cref="TaskRegistry.Find"/>，故它不参与查表。
    /// 它的作用是让 <see cref="_snapshotGate"/> 下的“发布→推送”成为一次不可分的状态转变，
    /// 而不是供查表用。
    /// </summary>
    private ToggleSnapshot _snapshot = ToggleSnapshot.Empty;

    /// <summary>
    /// 构造门面并完成全部接线。构造即"可用状态"：钩子尚未安装（由 UI 在
    /// 主窗口就绪后调 <see cref="Start"/>），但任务已载入、快照已就绪。
    /// </summary>
    public HostFacade()
    {
        // ⓪ 日志器：**最先建**。它本身不依赖任何别的组件，而后面每一步都可能出错，
        //    晚建一步就少记一步。落盘失败也不抛（AppLogger 内部吞掉）——
        //    "日志器装不起来"不该让进程起不来。
        Logger = new AppLogger();

        // ① 配置存储 + 任务注册表。Load() 载入列表并立刻回写归一结果（§5.2），
        //    读配置永不抛异常（FR-CFG-02），失败信息留在 LoadNotice 里给界面用。
        Store = new JsonConfigStore();
        Tasks = new TaskRegistry(Store, Logger);
        Tasks.Load();

        Settings = Store.LoadSettings();
        _currentSettings = Settings.Value ?? new AppSettings();

        // ② 输入链路：Simulator 必须先于 Scheduler（按住表全局唯一）。
        //    注入失败经模拟器的 60s 时间窗聚合后进日志（§7.3）——同一类失败
        //    在此处已降频，落盘不会再被刷爆。
        Input = new InputSimulator(new SendInputSink(), log: FailureLog);
        Scheduler = new Scheduler(Input, Logger);

        // ③ 钩子：独立持有，先于 Start 把快照推过去，免得装钩子后有一瞬空表。
        Hooks = new HookService();
        RefreshToggleTable();

        // ④ 开关命中链路：钩子事件 → 查任务 Id → 启停。
        Hooks.ToggleRequested += ToggleRequestedHandler;

        // ⑤ Engine → UI 唯一推送通道，原样转发（§6.5.3 的重读纪律由 UI 侧承担）。
        //    **任务启停的业务轨迹在此落盘**（§7.3「任务启动/停止」）：选这个订阅点
        //    而不是 Scheduler 内部，是因为它是全部启停路径的**汇**（Toggle / Stop /
        //    StopAll / Shutdown 都经它发通知），在调度器里逐路径写会四处重复，
        //    且将来新增路径必然漏一处。本回调在调度线程上，不在钩子回调路径（§3.2）。
        Scheduler.RunStateChanged += OnRunStateChanged;
    }

    /// <summary>全局日志器（配置目录 <c>logs\</c> 下的按日文件；§7.3）。</summary>
    public AppLogger Logger { get; }

    /// <summary>
    /// 投给 <see cref="ILogSink"/> 的委托（<see cref="InputSimulator"/> 的 <c>log</c> 参数）。
    ///
    /// <para>
    /// 模拟器的注入失败已是**聚合后的**文本（不含时间戳——时间戳由日志器补），
    /// 故这里直通 <see cref="ILogSink.Error"/> 而不是 <c>Info</c>：
    /// 注入失败是 §7.3 明列的"错误事件"。
    /// </para>
    /// </summary>
    private void FailureLog(string failure) => Logger.Error(failure);

    /// <summary>配置存储（界面的"打开配置目录"等功能可能要用）。</summary>
    public JsonConfigStore Store { get; }

    /// <summary>
    /// 当前生效的设置（<see cref="CurrentSettings"/> 的实体）。
    ///
    /// <para>
    /// 它会被钩子投递线程上的紧急停止路径读到，所以用 <see cref="Volatile"/> 读写——
    /// 引用赋值本身是原子的，要的只是“看到的是换之前还是换之后那一个”这件事别被
    /// 寄存器缓存住。为这一个字段上锁不值得，而错读的代价只是“本次按下用旧键”，
    /// 下一次按下就对上了。
    /// </para>
    /// </summary>
    private AppSettings _currentSettings;

    /// <summary>任务注册表（界面增删改任务的入口）。</summary>
    public TaskRegistry Tasks { get; }

    /// <summary>全局设置的载入结果（含损坏提示，AC-21 / FR-CFG-02）。</summary>
    /// <remarks>
    /// 这是**载入时那一次**的结果——保存会换掉内存里的设置，但载入结果不该被改写
    /// （它的 <see cref="ConfigLoadResult{T}.Recovery"/> / <c>Message</c> 描述的是启动那一刻
    /// 发生过什么，事后改写会让“那份损坏提示”变得不可追溯）。读当前值请用
    /// <see cref="CurrentSettings"/>。
    /// </remarks>
    public ConfigLoadResult<AppSettings> Settings { get; }

    /// <summary>
    /// 全局设置（已补默认值；界面改后用 <see cref="SaveSettings"/> 落盘）。
    ///
    /// <para>
    /// 它是**可变的当前值**：保存成功后由 <see cref="SaveSettings"/> 换成实际落盘的那一份。
    /// 界面改设置后立刻读它（包括重新推快照）拿到的才是新值——
    /// 若它永远等于启动时载入的那份，紧急停止键就会“改了要重启才生效”（§3.5.4）。
    /// </para>
    /// </summary>
    public AppSettings CurrentSettings => Volatile.Read(ref _currentSettings);

    /// <summary>输入模拟器（界面一般不需要，托盘退出等路径由本类代劳）。</summary>
    public InputSimulator Input { get; }

    /// <summary>调度器（界面用它读运行态、或直接停单个任务）。</summary>
    public Scheduler Scheduler { get; }

    /// <summary>钩子服务（界面一般不需要直接持有它，装配由本类负责）。</summary>
    internal HookService Hooks { get; }

    /// <summary>
    /// Engine → UI 的唯一推送通道（原样转发 <see cref="Scheduler.RunStateChanged"/>）。
    ///
    /// <para>
    /// <b>它不代表"循环已在跑"</b>，只表达调度器对"表内有无此任务"的观测结论；
    /// 订阅方收到后应**重读** <see cref="Scheduler.IsRunning"/> / <see cref="Scheduler.RunningTaskIds"/>
    /// 再更新显示，且不得据事件先后推断因果（§6.5.3）。事件在调度线程抛出，
    /// UI 侧须自行封送到界面线程。
    /// </para>
    /// </summary>
    public event Action<Guid, bool>? RunStateChanged;

    /// <summary>载入配置时的问题提示（<c>null</c> = 无异常）。界面启动时若非空应提示用户。</summary>
    public string? LoadNotice => Tasks.LoadNotice;

    /// <summary>
    /// 安装钩子。须在**界面主窗口就绪之后**调用——本方法只装钩子，
    /// 但事件一旦开始投递就会驱动任务启停，早于窗口就绪会让错误无处提示。
    /// </summary>
    public void Start()
    {
        ThrowIfDisposed();

        // 装钩子前再推一次表：构造到现在可能已过了任意长时间（UI 初始化耗时），
        // 期间若有别的入口改过列表，这里保证钩子拿到的是最新那份。
        RefreshToggleTable();

        // 装钩子是本进程最可能失败的一步（会话/完整性级别不匹配时 Win32Exception）。
        // 失败事实先落盘再原样上抛：App 侧的崩溃路径不变，但日志里得留下痕迹——
        // 否则「装不上钩子」这个用户最常见的故障在日志里完全不可见（§7.3.1 把
        // 「钩子异常/失效」列为错误事件）。此处是调用方线程，不是钩子回调路径（§3.2）。
        try
        {
            Hooks.Start();
        }
        catch (Exception ex)
        {
            Logger.Error("钩子安装失败，任务启停开关将不可用。", ex);
            throw;
        }
    }

    // ── 任务列表的变更入口（每个都在末尾推快照）──────────────────────

    /// <summary>
    /// 新增任务并推送新快照（FR-TASK-01）。校验不过则不写入、不推送。
    ///
    /// <para>
    /// <b>Id 在此分配</b>：界面新建路径（<c>TaskDraft.CreateNew</c>）产出的草稿带
    /// <see cref="Guid.Empty"/>，需要由门面发一个真 Id —— 这不是"顺手补一行"，
    /// 而是 <c>Guid.Empty</c> 在本系统里另有一处**语义占用**：开关快照用
    /// <c>Guid.Empty</c> 表示"这次点按没有命中任何任务"（<c>ToggleSnapshot</c> 的哨兵值）。
    /// 若不分配，新任务的 Id 恰等于哨兵值，钩子侧会把它的开关键当成"未命中"丢弃，
    /// 表现为**新建的任务开关键完全失效**；同时多个新建任务共用一个 Id，
    /// 编排器/注册表按 Id 定位会全部退化为"第一个"。
    /// </para>
    /// <para>
    /// 分配必须在**校验之前**：校验器与快照构建都要读真实 Id。
    /// </para>
    /// </summary>
    public ValidationResult AddTask(TaskDefinition task)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(task);

        return ApplyChange(() =>
        {
            // 只在调用方没给 Id 时补——按 Id 显式构造的任务（导入/测试）保持原样。
            task.EnsureId();

            return Tasks.Add(task, CurrentSettings.EmergencyStopKey);
        });
    }

    /// <summary>更新任务并推送新快照（FR-TASK-02）。</summary>
    public ValidationResult UpdateTask(TaskDefinition task)
    {
        ThrowIfDisposed();
        return ApplyChange(() => Tasks.Update(task, CurrentSettings.EmergencyStopKey));
    }

    /// <summary>
    /// 删除任务（FR-TASK-03）。
    ///
    /// <para>
    /// <b>先停再删</b>：任务正在跑时把它从列表移除，它的循环仍在注入——必须
    /// 先把调度器里那个任务停掉。删除是"立刻生效"的语义（FR-TASK-03），
    /// 所以这里用同步 <see cref="Scheduler.Stop"/> 而非等循环自己退出。
    /// </para>
    /// <para>
    /// <see cref="TaskRegistry.Remove"/> 不做校验（删一条已存在的记录不会让别的记录变非法），
    /// 返回的是被删掉的那个任务（<c>null</c> = 本来就没有）——故本方法以"列表内容是否
    /// 真的变了"决定是否推快照，而不是以校验结果。
    /// </para>
    /// </summary>
    /// <returns>被删除的任务；<c>null</c> 表示该 Id 不在列表里。</returns>
    public TaskDefinition? RemoveTask(Guid taskId)
    {
        ThrowIfDisposed();

        lock (_wiringGate)
        {
            // 停任务要在改列表**之前**：Stop 需要按 Id 找运行实例，
            // 而那一步不依赖列表（Scheduler 自持运行表），先停能让"列表里没有了
            // 但还在注入"这个窗口彻底消失。
            Scheduler.Stop(taskId);

            var removed = Tasks.Remove(taskId);
            if (removed is null)
            {
                // 本来就不在列表里：什么都没变，不推快照。
                return null;
            }

            RefreshToggleTable();
            return removed;
        }
    }

    /// <summary>复制任务并推送新快照（FR-TASK-04）。名字重名等校验问题由 Registry 返回。</summary>
    public (TaskDefinition? Copy, ValidationResult Result) DuplicateTask(Guid taskId)
    {
        ThrowIfDisposed();

        lock (_wiringGate)
        {
            var (copy, result) = Tasks.Duplicate(taskId, CurrentSettings.EmergencyStopKey);
            if (!result.IsValid)
            {
                return (null, result);
            }

            RefreshToggleTable();
            return (copy, result);
        }
    }

    /// <summary>
    /// 整体替换任务列表并推送新快照（导入/批量场景）。
    ///
    /// <para>
    /// 替换会让**已删除任务的循环**留在调度器里，故先
    /// <see cref="Scheduler.StopAll"/>——这是"列表内容整体换了"最保守的正确语义。
    /// </para>
    /// </summary>
    public ValidationResult ReplaceAllTasks(IEnumerable<TaskDefinition> tasks)
    {
        ThrowIfDisposed();

        return ApplyChange(() =>
        {
            Scheduler.StopAll();
            return Tasks.ReplaceAll(tasks, CurrentSettings.EmergencyStopKey);
        });
    }

    // ── 设置与退出 ─────────────────────────────────────────────────

    /// <summary>
    /// 保存全局设置（FR-CFG-06）。
    ///
    /// <para>
    /// <b>保存成功后立即重推快照</b>（FR-RUN-01，§3.5.4）：紧急停止键就在
    /// <see cref="CurrentSettings"/> 里，改完不重推则钩子手上仍是旧快照，
    /// 表现为"紧急停止键改了不生效，重启后才对"——而改键往往正是因为它与
    /// 别的软件撞了，用户当场就要验证，这种"重启才对"的延迟最伤信任。
    /// </para>
    /// <para>
    /// <b>只在保存成功时重推</b>：失败时新设置没落盘，推一份基于未落盘设置的快照
    /// 会造成"本次会话生效、下次启动又不是这样"的静默不一致。
    /// </para>
    /// </summary>
    /// <returns>落盘结果（失败时界面须提示，AC-37）。</returns>
    public ConfigSaveResult SaveSettings(AppSettings settings)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(settings);

        var result = Store.SaveSettings(settings);
        if (!result.Succeeded)
        {
            // AC-37 要求保存失败必须被知晓：界面弹窗（调用方）+ 日志留痕（此处）。
            // 两条都要——弹窗一关就什么都不剩，而用户回头报"设置改了没保持"时
            // 日志是唯一能对上号的证据。
            Logger.Error($"全局设置保存失败：{result.Message}");
            return result;
        }

        // 先换内存里的设置、再重建快照——顺序不能反：RefreshToggleTable 会去
        // 读 CurrentSettings.EmergencyStopKey，设置没换就推的还是一份旧键的快照。
        // 写回的是**实际落盘的那一份**（result.Value）而非入参：入参可能是 null
        // 被存储层换成默认设置，内存继续拿着入参会与磁盘当场分家。
        // 这是 CurrentSettings 陈旧问题的修复点（§3.5.4 / §6.5.1）——
        // 原来此处只落盘不更新内存，界面改完设置后 CurrentSettings 仍是旧值，
        // 于是紧急停止键"改了要重启才生效"。
        _currentSettings = result.Value ?? settings;
        RefreshToggleTable();
        return result;
    }

    /// <summary>
    /// 退出前的统一收尾——**退出路径的唯一入口**（§6.5.5）。
    ///
    /// <para>
    /// 顺序不能反：先摘钩子，再停任务。反过来的话，摘钩子之前进来的一次开关事件
    /// 可能在"已停完"之后又把任务拉起来，留下一个没人管的循环在后台注入。
    /// 停任务走 <see cref="Scheduler.Shutdown"/>，它内含 <c>StopAll</c> +
    /// <c>ReleaseAllAll</c> 两层保障（§3.1.3 兜底：即便某个任务的松开补发失败，
    /// 全局那次清表也会把按住的键全部放掉）。
    /// </para>
    /// <para>
    /// 本方法**不**负责进程退出动作本身（托盘退出还是关窗退出由 UI 决定），
    /// 只保证"任务已全停、按键已全松、钩子已摘"。
    /// </para>
    /// </summary>
    public void Shutdown()
    {
        if (_disposed)
        {
            return;
        }

        // 先摘钩子：摘完就不会再有新事件进来。摘钩子失败**只记不抛**——退出路径
        // 不该因为拆卸失败而中断，后面还有「停任务 + 松开全部按键」这件更要紧的事。
        try
        {
            Hooks.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Error("钩子卸载失败。", ex);
        }

        // 再停任务并释放全部按住的键（两层保障）。
        Scheduler.Shutdown();
    }

    // ── 内部：接线实现 ─────────────────────────────────────────────

    /// <summary>
    /// 任务运行状态变化的订阅点（在**调度线程**上执行，不在钩子回调路径——§3.2）。
    ///
    /// <para>
    /// 两件事，顺序不可反：**先落盘再转发**。日志器内部吞掉全部异常（<see cref="AppLogger"/>），
    /// 故它不会阻断转发；但若反过来先转发、UI 处理器抛异常，这次启停就永远没有记录——
    /// 而"UI 抛异常"恰恰是最需要知道任务当时到底跑没跑的时候。
    /// </para>
    /// </summary>
    private void OnRunStateChanged(Guid taskId, bool running)
    {
        // 名字查不到也无妨：Id 足以定位，任务刚删掉时名字本来就不存在了。
        var name = Tasks.Find(taskId)?.Name;
        Logger.Info(running
            ? $"任务启动：{name ?? "(已删除)"}（{taskId}）"
            : $"任务停止：{name ?? "(已删除)"}（{taskId}）");

        RunStateChanged?.Invoke(taskId, running);
    }

    /// <summary>
    /// 开关事件的订阅回调（在**投递线程**上执行）。
    ///
    /// <para>
    /// <b>立刻返回</b>是硬要求：本回调运行在钩子投递线程上，阻塞它会让开关事件
    /// 在队列里堆积，表现为"点了开关没反应"或延迟响应（§6.5.1）。因此这里只做
    /// 「查任务 Id → 交给调度器启停」两步：<see cref="Scheduler.Toggle"/> 自身
    /// 是"锁内状态迁移 + 锁外起线程"，不会把任务循环的实质工作压在本线程上。
    /// </para>
    /// <para>
    /// <b>两类事件的归属判定分岔点在这里</b>（§3.5.2）：载荷里任务 Id 为 <c>null</c>
    /// 即紧急停止，非 <c>null</c> 即任务开关。归属判定必须在**最前面**做，
    /// 且早于下面的 <paramref name="isKeyDown"/> 早退——因为这两类命中的时间口径相反：
    /// 任务开关只认**松开**（点按语义，§3.4），紧急停止只认**按下**（保险丝语义，§3.5.3）。
    /// 二者不可共用同一道早退门。
    /// </para>
    /// <para>
    /// <b>只认松开</b>：点按语义在松开的瞬间成立（§3.4 点火语义），按下的那一次
    /// 事件直接忽略——否则一次点按会触发两轮启停，任务看起来"点了没反应"。
    /// （该规矩只适用于任务开关；紧急停止的按下事件已在上面被接走。）
    /// </para>
    /// </summary>
    private void ToggleRequestedHandler(PhysicalKey key, bool isKeyDown, string[] modifierFamilies, Guid? taskId)
    {
        if (_disposed)
        {
            return;
        }

        // 查任务 Id 必须与钩子同源（§6.5.2 ①）：用同一份快照的归一表查，
        // 而不是拿原始键去 TaskRegistry 里比对——快照内部做族归一、TaskDefinition
        // 存的是原始键，另起一张表就会在"配右 Ctrl 的组合开关"上查不中。
        // 钩子已经把命中时的任务 Id 算好并随事件带来（第四参数）。
        TaskDefinition? task = null;
        if (taskId is not null)
        {
            lock (_snapshotGate)
            {
                // 用钩子算定的任务 Id 直接查（§3.5.2）：归属在回调侧一次算定、全程带下来，
                // 此处不再重算——否则与钩子的判定口径一旦漂移（如族集合快照时机不同），
                // 就会出现"钩子按 A 任务吞了键、门面查出 B 任务"的静默串台。
                task = Tasks.Find(taskId.Value);
            }
        }

        // 时间口径与归属分流**唯一判定处**（§3.5.3）：组合表在 Core 里，
        // 与钩子侧归属解析共用一个口径，且有单测钉住。
        // 这段判定原先内联在此处，曾以「归属解析在松开侧返回 null」的形态倒置：
        // 紧急停止的松开被当动作、任务开关的按下与松开全被丢弃，
        // 而编译期与单测都不显形——下沉 Core 就是为了不再重演。
        switch (ToggleDispatchPolicy.Decide(isKeyDown, taskId, task is not null, task?.Enabled ?? false))
        {
            case ToggleDispatch.EmergencyStop:
                StopAllRunningTasks();
                return;

            case ToggleDispatch.ToggleTask:
                Scheduler.Toggle(task!);
                return;

            default:
                // 快照与列表之间的合法时间差、停用任务、以及非动作的那两个组合：静默丢弃。
                return;
        }
    }

    /// <summary>
    /// 紧急停止：把**全部运行中**的任务逐个停掉（FR-RUN-01，§3.5.7）。
    ///
    /// <para>
    /// <b>逐任务 <see cref="Scheduler.Stop"/>，不用 <see cref="Scheduler.StopAll"/></b>：
    /// 两者都停任务，但 <c>StopAll</c> 带着"整体清场"的语义（退出路径在用），
    /// 而紧急停止是**用户的一次操作**，不是收尾——它的语义边界应与"再点一次开关"
    /// 同类，即"停任务、不改启用状态、不碰别的"。用窄接口把语义边界守在调用点，
    /// 将来 <c>StopAll</c> 若因退出路径需要加收尾动作，也不会莫名其妙地被紧急停止继承。
    /// </para>
    /// <para>
    /// <b>不改启用状态</b>：停止是运行态，启用是配置态。紧急停止后用户再点一次开关
    /// 就应该能重新跑起来——若把它做成了"停用"，紧急停止就成了隐形的配置修改，
    /// 用户下次启动会发现"任务怎么都不跑了"，且无从归因。
    /// </para>
    /// <para>
    /// <b>先拍列表快照再逐个停</b>：<see cref="TaskRegistry.Snapshot"/> 是当下时刻的
    /// 列表副本，遍历它而不是边遍历边问注册表，避免停止过程中列表被改动引发的
    /// 枚举异常。已经停了的任务再 <c>Stop</c> 一次是安全的（调度器内部幂等）。
    /// </para>
    /// </summary>
    private void StopAllRunningTasks()
    {
        foreach (var task in Tasks.Snapshot())
        {
            Scheduler.Stop(task.Id);
        }
    }

    /// <summary>
    /// 重建快照并整体推给钩子（§6.5.1 第 5 条：单向推送）。
    ///
    /// <para>
    /// 用 <c>Build</c> 从**当前任务列表**重建，而不是增量改表——开关表的正确性
    /// 依赖"全部启用任务"这一全局视图（重名键冲突、族归一都在里面），
    /// 增量维护迟早会与列表漂移。整表重建的代价是每次改动一次 O(n) 遍历，
    /// 而任务数是个位数级别，这个代价可以忽略。
    /// </para>
    /// <para>
    /// <b>同时传入紧急停止键</b>（FR-RUN-01，§3.5.1）：紧急停止表与开关表由同一份
    /// 快照承载、同行重建——两张表若各自维护，“紧急停止键与开关同键”这类交叉约束
    /// 就没有一个能同时看到两者的地方去守。构建期会把与紧急停止键同主键的开关绑定排除掉。
    /// </para>
    /// <para>
    /// <b>构建与发布在锁内、推送在锁外</b>：锁里做的事只有“把新表挂到
    /// <see cref="_snapshot"/>”这一下；向钩子推送排在其后。
    /// 这样排是为了让“门面已认新表”必须发生在“钩子已可判新表”之前——
    /// 反过来的话，钩子会按新表判出一次命中、而门面那边那个任务还不存在。
    /// </para>
    /// </summary>
    private void RefreshToggleTable()
    {
        var snapshot = ToggleSnapshot.Build(Tasks.Snapshot(), CurrentSettings.EmergencyStopKey);

        lock (_snapshotGate)
        {
            _snapshot = snapshot;
        }

        // 推送在锁外，理由见上：把“发布”与“推送”之间的窗口留给事件处理，
        // 而不是把钩子挡在锁外等。
        Hooks.UpdateToggleTable(snapshot);
    }

    /// <summary>
    /// 变更类操作的统一外壳：串行化 → 执行 → 成功则推快照 → 返回校验结果。
    ///
    /// <para>
    /// <see cref="_wiringGate"/> 挡的是"两次改列表交错"：三步成组（改 Registry →
    /// 重建快照 → 推钩子）中间被插入另一次改动，推给钩子的顺序就可能与列表实际状态
    /// 相反，表现为"改完任务开关不生效"。校验失败时不推快照——列表没变，
    /// 推一次同样的表纯属多余，且会让"失败"与"成功"两条路径在日志上难以分辨。
    /// </para>
    /// </summary>
    private ValidationResult ApplyChange(Func<ValidationResult> change)
    {
        ValidationResult result;
        lock (_wiringGate)
        {
            result = change();
            if (!result.IsValid)
            {
                return result;
            }

            RefreshToggleTable();
        }

        return result;
    }

    /// <summary>处置后拒绝新的操作（钩子已摘、调度器已停，再改列表没有意义）。</summary>
    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // 先做退出收尾（摘钩子 + 停任务 + 释放按键），再标记处置。
        Shutdown();
        _disposed = true;
    }
}
