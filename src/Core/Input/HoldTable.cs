using Core.Keys;

namespace Core.Input;

/// <summary>
/// 全局唯一的"当前按住表"（设计文档 §3.1.1，已裁定）。
///
/// <para>
/// 本表是 FR-SEQ-07 / FR-RUN-05"补发松开"的**唯一权威来源**——
/// 记录的是**实际已按下、尚未松开**的事件，不靠遍历命令反推。
/// 全局唯一（不按任务分表）的理由见设计文档 §3.1.1：物理键盘只有一份状态。
/// </para>
/// <para>
/// **职责边界**：本类只做**账本**（引用计数 + 属主集合），
/// 不调用任何注入出口、不写日志。真正的按下/松开由 <see cref="InputSimulator"/> 在
/// 账本变更前后驱动。这样账本逻辑可被单测穷举，无需任何系统调用。
/// </para>
/// <para>
/// **归一化**：修饰键按"左右归一"入表（左/右 Ctrl 视为同一个实体键，
/// 用 <see cref="KeyCatalog.RepresentativeOfFamily"/>）。理由是钩子捕获到的
/// 左右修饰键与配置里存的可能不同侧，不归一就会出现"按下左 Ctrl、
/// 释放右 Ctrl 配不上"的漏减引用。鼠标键无左右归一需求。
/// </para>
/// <para>
/// **线程安全**：调用方（<see cref="InputSimulator"/>）以锁串行化，
/// 本类自身不加锁——它只被 <see cref="InputSimulator"/> 在持锁状态下使用。
/// </para>
/// </summary>
internal sealed class HoldTable
{
    private readonly Dictionary<PhysicalKey, HeldKeyEntry> _entries = new();

    /// <summary>当前表中的条目数（供测试与诊断）。</summary>
    internal int Count => _entries.Count;

    /// <summary>把物理键归一到表内表示（修饰键左右归一；鼠标键原样）。</summary>
    internal static PhysicalKey Normalize(PhysicalKey key)
    {
        if (key.KeyboardVirtualKey is { } vk && KeyCatalog.IsModifier(vk))
        {
            return PhysicalKey.FromKeyboard(KeyCatalog.RepresentativeOfFamily(vk));
        }

        return key;
    }

    /// <summary>
    /// 登记一次"按下"。
    /// </summary>
    /// <returns>
    /// <c>true</c> 表示这是该键的**首次**按下（调用方应向系统注入按下事件）；
    /// <c>false</c> 有两种情形，**调用方都不得注入按下**：
    /// <list type="bullet">
    /// <item>同一属主重复按住同一键——幂等，计数未变；</item>
    /// <item>该键已被别的属主按住——只递增引用计数，实际按下已存在，不得重复注入。</item>
    /// </list>
    /// 两种情形的共同点是“实际按下已存在”，故调用方**无需区分**它们。
    /// </returns>
    internal bool Acquire(PhysicalKey key, Guid owner)
    {
        var normalized = Normalize(key);

        if (_entries.TryGetValue(normalized, out var entry))
        {
            // 同一属主重复"按住"同一键：幂等，不递增计数——否则该属主停止时
            // 只释放一次，计数永远归不了零，键会卡在按下状态。
            if (entry.Owners.Contains(owner))
            {
                return false;
            }

            entry.Owners.Add(owner);
            entry.Count++;
            return false;
        }

        var created = new HeldKeyEntry(normalized);
        created.Owners.Add(owner);
        created.Count = 1;
        _entries.Add(normalized, created);
        return true;
    }

    /// <summary>
    /// 登记一次"松开"。
    /// </summary>
    /// <returns>
    /// <c>true</c> 表示引用计数归零、条目已移除（调用方向系统注入松开事件）；
    /// <c>false</c> 表示该键仍被其他属主持有（调用方**不得**注入松开，
    /// 否则会连带松掉别的任务按住的键），或该键本就不在表中（空操作）。
    /// </returns>
    internal bool Release(PhysicalKey key, Guid owner)
    {
        var normalized = Normalize(key);

        if (!_entries.TryGetValue(normalized, out var entry))
        {
            return false;
        }

        if (!entry.Owners.Remove(owner))
        {
            // 该属主并未持有此键（重复释放 / 停止时该命令尚未按下）——空操作。
            return false;
        }

        entry.Count--;
        if (entry.Count > 0)
        {
            return false;
        }

        _entries.Remove(normalized);
        return true;
    }

    /// <summary>
    /// 释放某属主持有的**全部**引用，返回引用归零、需要真正注入松开的键。
    ///
    /// <para>
    /// 返回顺序遵守 FR-TAP-06 / 设计文档 §4.4：**同一组合键内主键先松、修饰键后松**。
    /// 由于修饰键与主键同属一个"持有单元"（同一次 <c>HoldCombo</c> 以同一个属主登记），
    /// 这里按"主键优先"排序即可：非修饰键排在修饰键之前。
    /// 不同键之间无依赖，顺序不做其他保证。
    /// </para>
    /// <para>
    /// **为何主键先松**：若修饰键先松开，主键会以"无修饰"的形态被系统采到，
    /// 产生一次非预期的单键输入（例如 Ctrl+C 变成单独一个 C）。
    /// </para>
    /// </summary>
    internal IReadOnlyList<PhysicalKey> ReleaseAllOf(Guid owner)
    {
        List<PhysicalKey>? toRelease = null;

        foreach (var (key, entry) in _entries)
        {
            if (!entry.Owners.Remove(owner))
            {
                continue;
            }

            entry.Count--;
            if (entry.Count > 0)
            {
                continue;
            }

            (toRelease ??= new List<PhysicalKey>()).Add(key);
        }

        if (toRelease is null)
        {
            return Array.Empty<PhysicalKey>();
        }

        foreach (var key in toRelease)
        {
            _entries.Remove(key);
        }

        // 主键（非修饰键）先松、修饰键后松（FR-TAP-06）。同排名内不保证次序。
        toRelease.Sort(static (a, b) => ReleaseRank(a).CompareTo(ReleaseRank(b)));
        return toRelease;
    }

    /// <summary>当前表中持有的全部属主（供 <c>ReleaseAllAll</c> 遍历；返回快照）。</summary>
    internal IReadOnlyCollection<Guid> AllOwners()
    {
        var owners = new HashSet<Guid>();
        foreach (var entry in _entries.Values)
        {
            foreach (var owner in entry.Owners)
            {
                owners.Add(owner);
            }
        }

        return owners;
    }

    /// <summary>松开次序的排名：0 = 主键（先松），1 = 修饰键（后松）。</summary>
    private static int ReleaseRank(PhysicalKey key)
        => key.KeyboardVirtualKey is { } vk && KeyCatalog.IsModifier(vk) ? 1 : 0;

    /// <summary>该键当前是否处于按住状态（供测试与诊断）。</summary>
    internal bool IsHeld(PhysicalKey key) => _entries.ContainsKey(Normalize(key));

    /// <summary>该键当前的引用计数；不在表中返回 0（供测试与诊断）。</summary>
    internal int RefCount(PhysicalKey key)
        => _entries.TryGetValue(Normalize(key), out var entry) ? entry.Count : 0;
}
