using Core.Keys;

namespace Core.Input;

/// <summary>
/// 按住表中的一个条目（设计文档 §3.1.1）。
///
/// <code>按住条目 = { 物理键/鼠标按钮, 引用计数 N, 属主集合（哪些任务持有） }</code>
///
/// <para>
/// **为什么需要引用计数**：物理键盘只有一份状态。两个任务都按住 A 时，
/// 其中一个停止**不得**松开 A——另一个的 A 还按着。若用无计数的集合，
/// 先停的任务会把键提前松掉，任务仍在运行而键被松掉，违反 FR-SEQ-07。
/// </para>
/// <para>
/// **属主集合**用于"按任务解除"：任务停止时只需释放它自己持有的引用，
/// 无需知道其他任务按了什么。
/// </para>
/// <para>
/// 本类型是**可变**的（计数增减是热路径，避免每次分配新对象），
/// 但只在 <see cref="HoldTable"/> 内部使用，不外泄。
/// </para>
/// </remarks>
internal sealed class HeldKeyEntry
{
    /// <summary>该条目对应的物理实体键。</summary>
    internal PhysicalKey Key { get; }

    /// <summary>
    /// 引用计数（设计文档 §3.1.1 的 N）。永不为 0——归零即从表中移除。
    /// </summary>
    internal int Count { get; set; }

    /// <summary>
    /// 属主集合：当前持有该键引用的任务。同一属主**不重复计数**——
    /// 一个任务对同一键重复"按住"是有害的（任务内部不应有重复按住命令，
    /// 且一旦重复，它停止时也只该释放一次）。
    /// </summary>
    internal HashSet<Guid> Owners { get; } = new();

    internal HeldKeyEntry(PhysicalKey key)
    {
        Key = key;
    }
}
