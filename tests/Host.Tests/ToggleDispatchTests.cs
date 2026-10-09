using Core.Input;
using Xunit;

namespace Host.Tests;

/// <summary>
/// 开关/紧急停止事件的组合表测试（设计文档 §3.5.3）。
///
/// <para>
/// <b>本组测试的来历</b>：这段判定原先内联在 <c>HostFacade</c>（Host 工程）里，
/// 曾以「归属解析在松开侧返回 <c>null</c>」的形态倒置——紧急停止的松开被当动作、
/// 任务开关的按下与松开全被丢弃，开关键彻底失灵；而编译期通过、事件照常入队、
/// 门面照常返回，唯一表现是"按了没反应"。测试工程只引用 Core，够不着 Host，
/// 这类倒置因此无回归网。判定下沉 Core 后，下面每条用例都是当时漏网的那种。
/// </para>
/// </summary>
public class ToggleDispatchTests
{
    private static readonly Guid TaskA = Guid.Parse("11111111-1111-1111-1111-111111111111");

    // ── 任务开关：只认松开（点火语义） ──────────────────────────────

    [Fact]
    public void 任务开关_松开_命中启用任务_切换任务()
    {
        Assert.Equal(
            ToggleDispatch.ToggleTask,
            ToggleDispatchPolicy.Decide(isKeyDown: false, taskId: TaskA, taskExists: true, taskEnabled: true));
    }

    [Fact]
    public void 任务开关_按下_丢弃()
    {
        // 按下若不丢，一次点按会触发两轮启停（按下一次 + 松开一次），任务看起来"点了没反应"。
        Assert.Equal(
            ToggleDispatch.Ignore,
            ToggleDispatchPolicy.Decide(isKeyDown: true, taskId: TaskA, taskExists: true, taskEnabled: true));
    }

    [Fact]
    public void 任务开关_任务已停用_丢弃()
    {
        Assert.Equal(
            ToggleDispatch.Ignore,
            ToggleDispatchPolicy.Decide(isKeyDown: false, taskId: TaskA, taskExists: true, taskEnabled: false));
    }

    [Fact]
    public void 任务开关_任务已不存在_丢弃而非误判紧急停止()
    {
        // 快照与任务列表之间的**合法**时间差（删任务时事件可能正在队列里排队）。
        // 此处若被判成紧急停止，一次普通开关就会停掉全部任务——这正是
        // 设计文档 §3.5.2 禁止"按 Id 查不到任务即当紧急停止"旁路判据的原因。
        Assert.Equal(
            ToggleDispatch.Ignore,
            ToggleDispatchPolicy.Decide(isKeyDown: false, taskId: TaskA, taskExists: false, taskEnabled: false));
    }

    // ── 紧急停止：只认按下（保险丝语义） ────────────────────────────

    [Fact]
    public void 紧急停止_按下_停全部任务()
    {
        Assert.Equal(
            ToggleDispatch.EmergencyStop,
            ToggleDispatchPolicy.Decide(isKeyDown: true, taskId: null, taskExists: false, taskEnabled: false));
    }

    [Fact]
    public void 紧急停止_松开_丢弃()
    {
        // 只认按下：松开若也动作，一次按下会停两遍（幂等故无害，但语义不清）。
        Assert.Equal(
            ToggleDispatch.Ignore,
            ToggleDispatchPolicy.Decide(isKeyDown: false, taskId: null, taskExists: false, taskEnabled: false));
    }

    [Fact]
    public void 紧急停止_不受任务存在性影响()
    {
        // 紧急停止不查任务表，任何参数组合下按下都必须动作（保险丝不得被任何前置条件拦住）。
        Assert.Equal(
            ToggleDispatch.EmergencyStop,
            ToggleDispatchPolicy.Decide(isKeyDown: true, taskId: null, taskExists: true, taskEnabled: true));
    }

    // ── 回归锚：两类命中时间口径相反 ────────────────────────────────

    [Fact]
    public void 时间口径相反_任务认松开_紧急停止认按下()
    {
        // 这是本条 bug 的核心不变式：两类命中在同一事件位上给出**不同**动作。
        // 若有人把两者统一成"都认按下"或"都认松开"，此断言立刻失败。
        var taskOnUp = ToggleDispatchPolicy.Decide(false, TaskA, true, true);
        var emergencyOnDown = ToggleDispatchPolicy.Decide(true, null, false, false);

        Assert.Equal(ToggleDispatch.ToggleTask, taskOnUp);
        Assert.Equal(ToggleDispatch.EmergencyStop, emergencyOnDown);
        Assert.NotEqual(taskOnUp, emergencyOnDown);
    }
}
