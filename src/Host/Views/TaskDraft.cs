using Core.Keys;
using Core.Model;
using Core.Validation;

namespace Host.Views;

/// <summary>
/// 任务编辑草稿——编辑页持有的**可变副本**，保存时才提交给门面。
///
/// <para>
/// <b>为何要副本</b>：编辑页是"改完再保存"的语义。若直接编辑列表里的同一对象，
/// 用户改到一半取消、或改出一份非法配置，列表已经被污染——而列表是立即生效的
/// （设计文档 §6.2）。副本把"编辑中的状态"与"已生效的状态"彻底隔开。
/// </para>
/// <para>
/// <b>副本必须取自 <c>Snapshot()</c> 而不是 <c>Registry.Find()</c></b>：
/// <c>Find</c> 返回注册表的**内部引用**，拿它当草稿等于直接编辑生效对象，
/// 副本的理由当场作废（这条是硬约束，不得改写）。
/// </para>
/// </summary>
public sealed class TaskDraft
{
    /// <summary>新建任务的开关默认键（与 <c>TaskDefinition</c> 的默认值一致）。</summary>
    private static readonly System.Windows.Input.Key DefaultToggleKey = System.Windows.Input.Key.F6;

    /// <summary>任务 Id。新建时为 <see cref="Guid.Empty"/>，保存时由门面分配。</summary>
    public Guid Id { get; set; }

    /// <summary>是否为新建（决定保存走 <c>AddTask</c> 还是 <c>UpdateTask</c>）。</summary>
    public bool IsNew { get; set; }

    /// <summary>任务名（"名称"是界面上唯一的自由文本框，FR-UI-04）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>任务是否启用（AC-19：仅启用项注册开关键）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 开关按键绑定；<c>null</c> 表示尚未捕获（保存时由校验器报"开关按键未设置"）。
    /// </summary>
    public KeyBinding? ToggleKey { get; set; }

    /// <summary>是否吞掉开关按键本身（不把它透传给前台程序）。</summary>
    public bool ConsumeToggleInput { get; set; } = true;

    /// <summary>命令行（每条命令一行，顺序即界面顺序）。</summary>
    public List<CommandRow> Commands { get; } = new();

    /// <summary>
    /// 由既有任务建草稿（编辑路径）。
    ///
    /// <para>
    /// <paramref name="source"/> 必须是 <c>Tasks.Snapshot()</c> 里取出的**副本**
    /// （见类型注释）。本方法只读取它，不做任何回写。
    /// </para>
    /// </summary>
    /// <param name="source">来源任务（快照副本）。</param>
    public static TaskDraft FromDefinition(TaskDefinition source)
    {
        var draft = new TaskDraft
        {
            Id = source.Id,
            IsNew = false,
            Name = source.Name,

            // 界面上的"启用"与领域模型是两个概念（领域模型没有 Enabled 字段时以恒真兜底），
            // 这里保持与快照一致的读法，避免新建/编辑两条路径语义分叉。
            Enabled = source.Enabled,
            ToggleKey = source.ToggleKey,
            ConsumeToggleInput = source.ConsumeToggleInput,
        };

        foreach (var command in source.Commands)
        {
            draft.Commands.Add(CommandRow.FromDefinition(command));
        }

        // 空任务在界面上表现为"一行待填"，而不是一片空白——
        // 否则用户会以为界面坏了（FR-UI-04 的可操作性要求）。
        if (draft.Commands.Count == 0)
        {
            draft.Commands.Add(CommandRow.FromDefinition(null));
        }

        return draft;
    }

    /// <summary>
    /// 建一条新任务的草稿（新建路径）。
    /// </summary>
    public static TaskDraft CreateNew()
    {
        var draft = new TaskDraft
        {
            Id = Guid.Empty,
            IsNew = true,
            Name = string.Empty,
            Enabled = true,
            ToggleKey = null,
            ConsumeToggleInput = true,
        };

        draft.Commands.Add(CommandRow.FromDefinition(null));
        return draft;
    }

    /// <summary>
    /// 把草稿折算成待保存的领域对象。
    ///
    /// <para>
    /// <b>不校验</b>：结果一律交给 <see cref="TaskValidator.Validate"/> 统一判定（§5.3）。
    /// 本方法只做形状转换，因此可以对任意中间态调用而不抛异常。
    /// </para>
    /// </summary>
    public TaskDefinition ToDefinition()
    {
        var task = new TaskDefinition
        {
            Id = Id,
            Name = Name ?? string.Empty,
            Enabled = Enabled,

            // 未捕获时给一个非 null 的绑定（默认 F6）：
            // 校验器判的是"有没有开关按键"，给 null 也能报出同一条问题项，
            // 但给非 null 可避免下游（快照/钩子表）出现空引用路径。
            ToggleKey = ToggleKey ?? KeyBinding.FromKey(DefaultToggleKey),
            ConsumeToggleInput = ConsumeToggleInput,
        };

        foreach (var row in Commands)
        {
            task.Commands.Add(row.BuildDefinition());
        }

        return task;
    }
}
