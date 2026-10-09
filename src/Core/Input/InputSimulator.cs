using System.Windows.Input;
using Core.Keys;

namespace Core.Input;

/// <summary>
/// 输入模拟器（设计文档 §3.1）——向系统注入键鼠操作的**唯一**上层入口。
///
/// <para>
/// 对外暴露语义化方法（点按 / 按住 / 松开 / 移动 / 滚轮 / 组合键），
/// 对下层通过 <see cref="IInputSink"/> 注入单个事件——
/// 真正的 <c>SendInput</c> 调用只存在于宿主侧的适配器实现里。
/// </para>
/// <para>
/// 内部维护**全局唯一的按住表**（<see cref="HoldTable"/>，设计文档 §3.1.1）：
/// 引用计数保证"两任务按住同一键"时不会提前松开；
/// 属主集合保证"任务停止只释放它自己持有的引用"。
/// </para>
/// <para>
/// <b>属主（owner）</b>：以任务的 <see cref="Guid"/> 作为属主标识
/// （<c>TaskDefinition.Id</c>）。停止某任务即 <see cref="ReleaseAll"/> 该 Id。
/// <see cref="Guid.Empty"/> 保留为"非任务属主"——用于不属于任何任务的临时按住；
/// 它会同样被 <see cref="ReleaseAllAll"/> 释放。
/// </para>
/// <para>
/// <b>日志</b>：本类只产出结构化日志事件（<see cref="InjectionFailed"/> 与构造传入的
/// <c>log</c> 回调），不落盘、不弹窗——日志落地由上层（Engine）决定。
/// 同一动作的注入失败**按时间窗聚合**（设计文档 §3.1.2），
/// 聚合口径见 <see cref="InjectionFailureLimiter"/>。
/// **上报在锁外**：回调是外部代码（日志落地可能写磁盘），
/// 在持有 <c>_gate</c> 时调用它会让注入热路径被磁盘 IO 拖住，也会给回调方制造死锁机会。
/// </para>
/// <para>
/// <b>线程安全</b>：按住表的读写、以及"查表 + 注入"这个复合动作都在同一把锁内完成。
/// 这是必需的——若两线程同时"按下同一键"，不加锁会双双判定为首次按下而重复注入，
/// 或双双判定为已存在而漏掉首次注入。锁只保护内存账本与调用适配器（微秒级），
/// 不引入可感知的阻塞。
/// </para>
/// <para>
/// <b>注入键码口径</b>：<see cref="_table"/> 中的键是**归一后**的（修饰键左右归一，
/// 见 <see cref="HoldTable.Normalize"/>），因此**按住链路**（Hold / Release / 补发）
/// 注入时一律使用归一后的代表键——按下侧与松开侧必须用**同一个**键码，
/// 否则会出现“按右 Ctrl、松左 Ctrl”的错配，右 Ctrl 永久卡在按下状态。
/// 该口径**只服务于对齐表中同一个实体条目**；<b>点按链路（<c>Tap*</c>）不进表，不参与归一</b>，
/// 按配置**原样注入**（设计文档 §3.1）。
/// </para>
/// </summary>
public sealed class InputSimulator
{
    private readonly IInputSink _sink;
    private readonly Action<string>? _log;
    private readonly InjectionFailureLimiter _failureLimiter;
    private readonly HoldTable _table = new();
    private readonly object _gate = new();

    /// <summary>
    /// 构造输入模拟器。
    /// </summary>
    /// <param name="sink">底层注入出口（生产环境为 <c>SendInput</c> 适配器）。</param>
    /// <param name="log">
    /// 日志接收器（可空）。收到的是**已经过时间窗聚合**的失败描述，
    /// 不含时间戳——时间戳由日志落地方补。
    /// </param>
    /// <param name="now">
    /// 时钟（可空），返回单调递增的毫秒数。默认 <see cref="Environment.TickCount64"/>；
    /// 单测注入假时钟以验证时间窗聚合，无需真实等待。
    /// </param>
    /// <param name="failureWindowMs">
    /// 同一动作注入失败的日志聚合窗口（毫秒）。默认
    /// <see cref="InjectionFailureLimiter.DefaultWindowMs"/>（一分钟一条）。
    /// </param>
    public InputSimulator(
        IInputSink sink,
        Action<string>? log = null,
        Func<long>? now = null,
        int failureWindowMs = InjectionFailureLimiter.DefaultWindowMs)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _log = log;
        _failureLimiter = new InjectionFailureLimiter(now, failureWindowMs);
    }

    /// <summary>注入失败时上报的日志事件（已聚合）。</summary>
    public event Action<string>? InjectionFailed;

    /// <summary>当前按住表中的条目数（供诊断与测试）。</summary>
    public int HeldCount
    {
        get
        {
            lock (_gate)
            {
                return _table.Count;
            }
        }
    }

    /// <summary>该键当前是否处于按住状态（供诊断与测试）。</summary>
    public bool IsHeld(PhysicalKey key)
    {
        lock (_gate)
        {
            return _table.IsHeld(key);
        }
    }

    /// <summary>点按一个键盘键（按下 + 立即松开）。FR-TAP-02。</summary>
    public void TapKey(Key virtualKey)
    {
        string? failure;
        lock (_gate)
        {
            // 点按不持有引用计数：按下与松开在同一临界区内成对完成，
            // 不与"按住"语义争夺引用。
            //
            // 系统层面只有一个虚拟键位，无法“只松自己这一份”——若同一键另有任务按住，
            // 本次点按的松开会把那一次按住一并解掉。该跨任务交互按设计文档 §3.1
            // “已知限制”取舍为完全旁路引用计数，不参与归一。
            failure = EmitTap(PhysicalKey.FromKeyboard(virtualKey));
        }

        NotifyFailure(failure);
    }

    /// <summary>
    /// 按住一个键盘键（仅按下，保持按住状态）。FR-TAP-03。
    /// </summary>
    /// <param name="virtualKey">键盘虚拟键。</param>
    /// <param name="owner">持有该按住的属主（通常为任务 Id）。</param>
    public void HoldKey(Key virtualKey, Guid owner)
    {
        string? failure;
        lock (_gate)
        {
            // 归一：表中的条目已归一（左右修饰键同一实体），注入必须与表口径一致，
            // 否则会出现“按右 Ctrl、松左 Ctrl”的错配（设计文档 §3.1）。
            var key = HoldTable.Normalize(PhysicalKey.FromKeyboard(virtualKey));
            failure = _table.Acquire(key, owner)
                ? EmitKeyDown(key)
                : null;
        }

        NotifyFailure(failure);
    }

    /// <summary>
    /// 松开一个键盘键。通常不由调用方直接使用——
    /// 任务停止时统一走 <see cref="ReleaseAll"/>（设计文档 §3.1.3 的单一入口）。
    /// </summary>
    public void ReleaseKey(Key virtualKey, Guid owner)
    {
        string? failure;
        lock (_gate)
        {
            var key = HoldTable.Normalize(PhysicalKey.FromKeyboard(virtualKey));
            failure = _table.Release(key, owner)
                ? EmitKeyUp(key)
                : null;
        }

        NotifyFailure(failure);
    }

    /// <summary>点按一个鼠标按键（FR-CMD-M2 + FR-TAP-02）。</summary>
    public void TapMouseButton(MouseKey button)
    {
        string? failure;
        lock (_gate)
        {
            failure = EmitTap(PhysicalKey.FromMouse(button));
        }

        NotifyFailure(failure);
    }

    /// <summary>按住一个鼠标按键（FR-TAP-03）。</summary>
    public void HoldMouseButton(MouseKey button, Guid owner)
    {
        string? failure;
        lock (_gate)
        {
            var key = PhysicalKey.FromMouse(button);
            failure = _table.Acquire(key, owner)
                ? EmitMouseDown(key)
                : null;
        }

        NotifyFailure(failure);
    }

    /// <summary>松开一个鼠标按键。</summary>
    public void ReleaseMouseButton(MouseKey button, Guid owner)
    {
        string? failure;
        lock (_gate)
        {
            var key = PhysicalKey.FromMouse(button);
            failure = _table.Release(key, owner)
                ? EmitMouseUp(key)
                : null;
        }

        NotifyFailure(failure);
    }

    /// <summary>把鼠标移动到屏幕绝对坐标（FR-CMD-M1）。瞬时操作，不涉及按住表。</summary>
    public void MoveMouseAbsolute(int x, int y)
    {
        string? failure;
        lock (_gate)
        {
            // 一次移动 = 一个注入事件（适配器内可能展开为多个 SendInput 结构，
            // 但对外语义是一件事件），故为 1 才算成功。
            failure = CheckResult(_sink.MoveAbsolute(x, y), "鼠标移动");
        }

        NotifyFailure(failure);
    }

    /// <summary>
    /// 注入滚轮（FR-CMD-M3）。瞬时操作，不涉及按住表。
    /// </summary>
    /// <param name="direction">滚动方向：上 / 下（需求只定义上下）。</param>
    /// <param name="notches">格数（正数，方向由 <paramref name="direction"/> 决定）。</param>
    public void ScrollWheel(ScrollDirection direction, int notches)
    {
        string? failure;
        lock (_gate)
        {
            var signed = Sign(direction) * Magnitude(notches);
            failure = CheckResult(
                _sink.Scroll(vertical: true, notches: signed),
                direction == ScrollDirection.Up ? "滚轮上滚" : "滚轮下滚");
        }

        NotifyFailure(failure);
    }

    /// <summary>
    /// 点按一个组合键：修饰键↓ → 主键↓↑ → 修饰键↑（FR-TAP-06、AC-14b）。
    /// 整个序列在同一临界区内完成，不参与按住表。
    /// </summary>
    public void TapCombo(IEnumerable<Key> modifiers, Key virtualKey)
    {
        var mods = MaterializeModifiers(modifiers);
        List<string> failures = [];
        lock (_gate)
        {
            foreach (var modifier in mods)
            {
                AddIfFailed(failures, EmitKeyDown(PhysicalKey.FromKeyboard(modifier)));
            }

            AddIfFailed(failures, EmitTap(PhysicalKey.FromKeyboard(virtualKey)));

            // 修饰键逆序松开（与按下顺序对称；修饰键之间无依赖）。
            for (var i = mods.Count - 1; i >= 0; i--)
            {
                AddIfFailed(failures, EmitKeyUp(PhysicalKey.FromKeyboard(mods[i])));
            }
        }

        NotifyFailures(failures);
    }

    /// <summary>
    /// 按住一个组合键：修饰键↓ → 主键↓（保持）。
    ///
    /// <para>
    /// **原子性**（设计文档 §3.1.1）：修饰键与主键同属**一个**持有单元——
    /// 它们以同一个属主登记，停止时一起减引用、按"主键先松、修饰键后松"补发。
    /// </para>
    /// </summary>
    public void HoldCombo(IEnumerable<Key> modifiers, Key virtualKey, Guid owner)
    {
        var mods = MaterializeModifiers(modifiers);
        List<string> failures = [];
        lock (_gate)
        {
            foreach (var modifier in mods)
            {
                var key = HoldTable.Normalize(PhysicalKey.FromKeyboard(modifier));
                if (_table.Acquire(key, owner))
                {
                    AddIfFailed(failures, EmitKeyDown(key));
                }
            }

            var mainKey = HoldTable.Normalize(PhysicalKey.FromKeyboard(virtualKey));
            if (_table.Acquire(mainKey, owner))
            {
                AddIfFailed(failures, EmitKeyDown(mainKey));
            }
        }

        NotifyFailures(failures);
    }

    /// <summary>
    /// 按属主补发松开（设计文档 §3.1.3，唯一入口）。
    ///
    /// 三个触发源（任务停止 / 紧急停止 / 退出）共用这一条路径。
    /// 只释放该属主自己的引用；计数归零的键才真正注入松开。
    /// 同一持有单元内按"主键先松、修饰键后松"（FR-TAP-06）补发。
    /// </summary>
    public void ReleaseAll(Guid owner)
    {
        List<string> failures = [];
        lock (_gate)
        {
            foreach (var key in _table.ReleaseAllOf(owner))
            {
                AddIfFailed(failures, EmitRelease(key));
            }
        }

        NotifyFailures(failures);
    }

    /// <summary>
    /// 释放**全部**属主的引用（紧急停止 / 退出 / 停止全部）。
    ///
    /// 内部仍走同一条减引用逻辑（设计文档 §3.1.3），不是另一套实现：
    /// 枚举当前全部属主，逐个调用 <see cref="HoldTable.ReleaseAllOf"/>。
    /// </summary>
    public void ReleaseAllAll()
    {
        List<string> failures = [];
        lock (_gate)
        {
            foreach (var owner in _table.AllOwners())
            {
                foreach (var key in _table.ReleaseAllOf(owner))
                {
                    AddIfFailed(failures, EmitRelease(key));
                }
            }
        }

        NotifyFailures(failures);
    }

    // ---- 内部实现 ----

    /// <summary>点按：按下 + 立即松开（调用方须持锁）。返回需上报的失败描述（可空）。</summary>
    private string? EmitTap(PhysicalKey key)
    {
        if (key.KeyboardVirtualKey is not null)
        {
            return EmitKeyDown(key) ?? EmitKeyUp(key);
        }

        if (key.Mouse is not null)
        {
            return EmitMouseDown(key) ?? EmitMouseUp(key);
        }

        return null;
    }

    /// <summary>注入一次键盘按下（调用方须持锁；键码按传入值原样注入，归一由调用方负责）。</summary>
    private string? EmitKeyDown(PhysicalKey key)
    {
        var vk = key.KeyboardVirtualKey;
        return vk is null
            ? null
            : CheckResult(_sink.KeyDown(vk.Value), $"键盘按下 {vk.Value}");
    }

    /// <summary>注入一次键盘松开（调用方须持锁；键码按传入值原样注入，归一由调用方负责）。</summary>
    private string? EmitKeyUp(PhysicalKey key)
    {
        var vk = key.KeyboardVirtualKey;
        return vk is null
            ? null
            : CheckResult(_sink.KeyUp(vk.Value), $"键盘松开 {vk.Value}");
    }

    /// <summary>注入一次鼠标按下（调用方须持锁）。</summary>
    private string? EmitMouseDown(PhysicalKey key)
        => key.Mouse is { } mouse
            ? CheckResult(_sink.MouseDown(mouse), $"鼠标按下 {mouse}")
            : null;

    /// <summary>注入一次鼠标松开（调用方须持锁）。</summary>
    private string? EmitMouseUp(PhysicalKey key)
        => key.Mouse is { } mouse
            ? CheckResult(_sink.MouseUp(mouse), $"鼠标松开 {mouse}")
            : null;

    /// <summary>
    /// 向系统注入一次松开（调用方须持锁）。
    ///
    /// <para>
    /// 表中键**已归一**（修饰键左右归一，见 <see cref="HoldTable.Normalize"/>）——
    /// 注入用归一后的键码，保证与按下侧一致，避免"按右 Ctrl 松左 Ctrl"的错配。
    /// </para>
    /// </summary>
    private string? EmitRelease(PhysicalKey key)
        => key.IsKeyboard ? EmitKeyUp(key) : EmitMouseUp(key);

    /// <summary>
    /// 校验一次注入的结果（调用方须持锁）。
    /// 返回需上报的失败描述，或 <c>null</c>（成功 / 已聚合掉）。
    /// </summary>
    private string? CheckResult(int injected, string action)
    {
        if (injected == 1)
        {
            return null;
        }

        if (!_failureLimiter.ShouldReport(action))
        {
            return null;
        }

        // 失败类型取自适配器——只记"失败了"而不知"为何失败"，日志对排障没有价值
        // （设计文档 §3.1.2 要求记录失败类型与目标事件）。
        var error = _sink.LastInjectionError;
        var reason = error == 0 ? "原因未知" : $"错误码 {error}";
        return $"输入注入失败：{action}（{reason}）——未中断任务，将继续尝试";
    }

    private static void AddIfFailed(List<string> failures, string? failure)
    {
        if (failure is not null)
        {
            failures.Add(failure);
        }
    }

    /// <summary>
    /// 在**锁外**上报失败（聚合判定已在 <see cref="CheckResult"/> 内完成）。
    ///
    /// <para>
    /// 必须锁外：回调是外部代码（<c>_log</c> 可能写磁盘、<see cref="InjectionFailed"/>
    /// 是任意订阅者的代码），持锁调用会让注入热路径被外部代码的耗时拖住，
    /// 也会给外部代码制造回调重入本类的死锁机会。
    /// </para>
    /// </summary>
    private void NotifyFailures(List<string> failures)
    {
        foreach (var failure in failures)
        {
            NotifyFailure(failure);
        }
    }

    /// <summary>在**锁外**上报一条失败（理由同 <see cref="NotifyFailures"/>）。</summary>
    private void NotifyFailure(string? failure)
    {
        if (failure is null)
        {
            return;
        }

        _log?.Invoke(failure);
        InjectionFailed?.Invoke(failure);
    }

    private static int Sign(ScrollDirection direction)
        => direction == ScrollDirection.Up ? 1 : -1;

    /// <summary>
    /// 取格数绝对值。用 <c>long</c> 中转以规避 <c>Math.Abs(int.MinValue)</c> 溢出
    /// （取负仍是自身，<see cref="OverflowException"/>）。
    /// </summary>
    private static int Magnitude(int notches)
    {
        var magnitude = Math.Abs((long)notches);
        return (int)Math.Min(magnitude, int.MaxValue / 2);
    }

    private static List<Key> MaterializeModifiers(IEnumerable<Key>? modifiers)
        => modifiers is null ? new List<Key>() : new List<Key>(modifiers);
}
