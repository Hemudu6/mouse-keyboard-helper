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
/// <item><see cref="HookService.ToggleRequested"/> → 查表 → <see cref="Scheduler.Toggle"/>：事件订阅。
///       事件在**投递线程**抛出，订阅回调必须立刻返回（§3.2 回调最小化）。</item>
/// <item>任务列表变更 → 重建快照 → <see cref="HookService.UpdateToggleTable"/>：单向推送。
///       <b>漏推的后果是"新任务的开关键不生效"</b>——故本类的每个增删改入口都在末尾调
///       <see cref="RefreshToggleTable"/>，没有例外。</item>
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
    /// 取快照的串行化门。与 <see cref="_wiringGate"/> 分开是因为它还要挡
    /// <see cref="ToggleRequestedHandler"/>：钩子投递线程上取快照时，
    /// 不能在 UI 改列表的中途看到半成品快照。
    /// </summary>
    private readonly object _snapshotGate = new();

    /// <summary>最近一次重建后交给钩子的快照（开关命中时用它查任务 Id）。</summary>
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
        var settings = Settings.Value ?? new AppSettings();

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

    /// <summary>任务注册表（界面增删改任务的入口）。</summary>
    public TaskRegistry Tasks { get; }

    /// <summary>全局设置的载入结果（含损坏提示，AC-21 / FR-CFG-02）。</summary>
    public ConfigLoadResult<AppSettings> Settings { get; }

    /// <summary>全局设置（已补默认值；界面改后用 <see cref="SaveSettings"/> 落盘）。</summary>
    public AppSettings CurrentSettings => Settings.Value ?? new AppSettings();

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

    /// <summary>新增任务并推送新快照（FR-TASK-02）。校验不过则不写入、不推送。</summary>
    public ValidationResult AddTask(TaskDefinition task)
    {
        ThrowIfDisposed();
        return ApplyChange(() => Tasks.Add(task, CurrentSettings.EmergencyStopKey));
    }

    /// <summary>更新任务并推送新快照（FR-TASK-04）。</summary>
    public ValidationResult UpdateTask(TaskDefinition task)
    {
        ThrowIfDisposed();
        return ApplyChange(() => Tasks.Update(task, CurrentSettings.EmergencyStopKey));
    }

    /// <summary>
    /// 删除任务（FR-TASK-05）。
    ///
    /// <para>
    /// <b>先停再删</b>：任务正在跑时把它从列表移除，它的循环仍在注入——必须
    /// 先把调度器里那个任务停掉。删除是"立刻生效"的语义（FR-TASK-05），
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

    /// <summary>复制任务并推送新快照（FR-TASK-06）。名字重名等校验问题由 Registry 返回。</summary>
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
    /// <b>保存后立即刷新紧急停止键？</b>——本工程的紧急停止落在钩子侧的
    /// <see cref="KeyBinding"/> 上，而快照只承载开关表；紧急停止键的独立钩子路径
    /// 由批次五接线，此处只负责落盘，不声称已生效。
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
        }

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
    /// <b>只认松开</b>：点按语义在松开的瞬间成立（§3.4 点火语义），按下的那一次
    /// 事件直接忽略——否则一次点按会触发两轮启停，任务看起来"点了没反应"。
    /// </para>
    /// </summary>
    private void ToggleRequestedHandler(PhysicalKey key, bool isKeyDown, string[] modifierFamilies)
    {
        if (_disposed || isKeyDown)
        {
            return;
        }

        // 查任务 Id 必须与钩子同源（§6.5.2 ①）：用同一份快照的归一表查，
        // 而不是拿原始键去 TaskRegistry 里比对——快照内部做族归一、TaskDefinition
        // 存的是原始键，另起一张表就会在"配右 Ctrl 的组合开关"上查不中。
        // 钩子已经把命中时的按 ToggleRequested 事件转发过来（第三参数）。
        TaskDefinition? task;
        lock (_snapshotGate)
        {
            if (!_snapshot.TryGetTaskId(key, modifierFamilies, out var taskId))
            {
                // 查不到任务即静默丢弃（§6.5.2 ②）：键在表内但任务已删除/已禁用
                // 是合法且常见的中间态（删任务时事件可能正在队列里排队）。
                return;
            }

            task = Tasks.Find(taskId);
        }

        if (task is null || !task.Enabled)
        {
            // 快照与列表之间的合法时间差，同样静默丢弃。
            return;
        }

        Scheduler.Toggle(task);
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
    /// <b>先更新本地快照、再推给钩子</b>：本类的 <see cref="_snapshot"/> 是
    /// 事件回调查任务 Id 的依据，必须与钩子手上那份是同一个对象，
    /// 否则会出现"钩子按新表吞了键、门面按旧表查不到任务"的静默失配。
    /// </para>
    /// </summary>
    private void RefreshToggleTable()
    {
        var snapshot = ToggleSnapshot.Build(Tasks.Snapshot());

        lock (_snapshotGate)
        {
            _snapshot = snapshot;
        }

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
