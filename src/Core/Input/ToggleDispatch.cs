namespace Core.Input;

/// <summary>
/// 一次开关/紧急停止事件该做什么（设计文档 §3.5.2、§3.5.3）。
///
/// <para>
/// <b>为什么要有这个类型</b>：门面收到 <c>ToggleRequested</c> 后要做的事，
/// 只由两个事实决定——<b>是不是紧急停止</b>（由归属标识是否为 <c>null</c> 表达）
/// 与<b>是按下还是松开</b>。这两者构成的四个组合里只有两个是真的动作，
/// 另两个必须被丢弃；把这份判定写成一处纯函数，它才可被单测钉住。
/// </para>
/// <para>
/// <b>为什么必须落在 Core</b>：这段判定原先内联在 <c>HostFacade</c>（Host 工程）里，
/// 而测试工程只引用 Core（<c>Host.Tests.csproj</c> 的既定约定）。
/// 于是它曾以「归属解析在松开侧返回 <c>null</c>」的形态倒置而不被任何测试发现——
/// 编译通过、事件照常入队、门面照常返回，唯一表现是"开关键按了没反应"。
/// 判定下沉到 Core 后，这个组合表才有回归网。
/// </para>
/// </summary>
public enum ToggleDispatch
{
    /// <summary>丢弃这条事件（不是本工具的动作）。</summary>
    Ignore,

    /// <summary>紧急停止：停止全部运行中的任务。</summary>
    EmergencyStop,

    /// <summary>任务开关：切换该任务的启停。</summary>
    ToggleTask,
}

/// <summary>
/// 时间口径的**唯一判定处**（设计文档 §3.5.3）。
///
/// <para>
/// <b>两条口径相反，且不可倒转</b>：
/// 任务开关**只认松开**（点按语义在松开瞬间成立；按下的那次忽略，否则一次点按触发两轮启停）；
/// 紧急停止**只认按下**（保险丝必须在按下瞬间动作，松开才响既慢半拍、
/// 又与"按住不放"这种最自然的应激反应相悖）。
/// </para>
/// <para>
/// <b>为什么归属标识为 <c>null</c> 就一定是紧急停止</b>：见 <see cref="ToggleSnapshot"/>——
/// 两张表同源构建、共用一个事件，靠载荷里的可空任务 Id 区分。
/// 上层**必须**靠"是不是紧急停止"分流，**不得**改用"按任务 Id 查不到任务"之类的旁路判据：
/// 任务刚被删除、或快照与列表存在合法时间差时，那会把一次普通开关误判成紧急停止而停掉全部任务。
/// </para>
/// <para>
/// <b>与归属解析的关系</b>：钩子侧的归属解析（算"这个键属于哪个任务"）
/// <b>不得</b>按 <c>isKeyDown</c> 过滤——它对按下与松开必须给出<b>同一个</b>任务 Id，
/// 时间口径只由本函数承担。若归属解析在松开侧返回 <c>null</c>，那条本该点火的事件
/// 会被当成"紧急停止的松开"而在下方第一分支被丢弃，开关按下与松开两条全部落空。
/// </para>
/// </summary>
public static class ToggleDispatchPolicy
{
    /// <summary>
    /// 判定这条开关/紧急停止事件该做什么（§3.5.3 的组合表）。
    /// </summary>
    /// <param name="isKeyDown">是否按下事件（<c>false</c> = 松开）。</param>
    /// <param name="taskId">
    /// 归属标识：<c>null</c> = 紧急停止；非 <c>null</c> = 该开关所属任务 Id（§3.5.2）。
    /// </param>
    /// <param name="taskExists">
    /// 归属任务是否仍在注册表中。快照与任务列表之间存在<b>合法</b>的时间差
    /// （改配置后快照重建与列表变更并非同一瞬间），查不到任务时静默丢弃，不作为异常。
    /// </param>
    /// <param name="taskEnabled">
    /// 命中任务当前是否启用。仅在归属为非 <c>null</c> 且为松开事件时被参考——
    /// 停用的任务不响应开关键。
    /// </param>
    public static ToggleDispatch Decide(bool isKeyDown, Guid? taskId, bool taskExists, bool taskEnabled)
    {
        // 紧急停止：只认按下（保险丝在按下瞬间动作），松开不重复触发。
        if (taskId is null)
        {
            return isKeyDown ? ToggleDispatch.EmergencyStop : ToggleDispatch.Ignore;
        }

        // 任务开关：只认松开（点火语义）——按下的那一次丢弃，否则一次点按触发两轮启停。
        if (isKeyDown)
        {
            return ToggleDispatch.Ignore;
        }

        // 快照与任务列表的合法时间差、以及任务被停用：都静默丢弃。
        return taskExists && taskEnabled ? ToggleDispatch.ToggleTask : ToggleDispatch.Ignore;
    }
}
