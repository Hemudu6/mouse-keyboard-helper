using Core.Model;

namespace Host.Tests;

/// <summary>
/// 任务 Id 的分配不变式（<see cref="TaskDefinition.EnsureId"/>）。
///
/// <para>
/// <b>为什么要锁这条</b>：界面新建路径（<c>TaskDraft.CreateNew</c>）显式把草稿的
/// Id 写成 <see cref="Guid.Empty"/> 表示"这是新任务"，入库前必须补齐。若漏补，
/// 后果不是显示难看，而是**功能失效**——开关快照用 <see cref="Guid.Empty"/>
/// 作为"未命中任何任务"的哨兵值，带空 Id 的任务其开关键会被直接丢弃；
/// 且多个新建任务共用一个 Id，按 Id 定位（编辑/删除/停止）全部退化为"第一个"。
/// </para>
/// <para>
/// 该不变式的实现放在 Core（非宿主）正是为了让它可被本测试覆盖：
/// 宿主工程带管理员清单，不进测试进程（见 <c>Host.Tests.csproj</c> 的引用说明）。
/// </para>
/// </summary>
public class TaskDefinitionIdTests
{
    [Fact]
    public void 新建任务默认带非空Id()
    {
        var task = new TaskDefinition();

        Assert.NotEqual(Guid.Empty, task.Id);
    }

    [Fact]
    public void EnsureId_空Id时分配一个并报告已分配()
    {
        var task = new TaskDefinition { Id = Guid.Empty };

        var assigned = task.EnsureId();

        Assert.True(assigned);
        Assert.NotEqual(Guid.Empty, task.Id);
    }

    [Fact]
    public void EnsureId_已有Id时保持原样并报告未改动()
    {
        var original = Guid.NewGuid();
        var task = new TaskDefinition { Id = original };

        var assigned = task.EnsureId();

        Assert.False(assigned);
        Assert.Equal(original, task.Id);
    }

    [Fact]
    public void EnsureId_幂等_重复调用不换Id()
    {
        var task = new TaskDefinition { Id = Guid.Empty };

        task.EnsureId();
        var first = task.Id;

        var second = task.EnsureId();

        Assert.False(second);
        Assert.Equal(first, task.Id);
    }

    [Fact]
    public void EnsureId_两个空Id任务分配后互不相同()
    {
        var first = new TaskDefinition { Id = Guid.Empty };
        var second = new TaskDefinition { Id = Guid.Empty };

        first.EnsureId();
        second.EnsureId();

        // 这是"多个新建任务共用一个 Id"缺陷的直接回归断言。
        Assert.NotEqual(first.Id, second.Id);
    }
}
