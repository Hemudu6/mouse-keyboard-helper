using Host.Engine;

namespace Host.Views;

/// <summary>
/// 任务编辑页的导航参数：门面 + 要编辑哪个任务。
///
/// <para>
/// 单独一个类型而不是 <c>object[]</c>：编辑页要判空（<c>TaskId is null</c> = 新建），
/// 元组或数组的参数在这个位置很容易读错顺序或漏判。
/// </para>
/// </summary>
/// <param name="Facade">进程内唯一的组合根。</param>
/// <param name="TaskId">要编辑的任务 Id；<c>null</c> = 新建任务。</param>
public sealed record TaskEditArgs(HostFacade Facade, Guid? TaskId);
