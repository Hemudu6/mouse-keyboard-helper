using System.Collections.ObjectModel;
using System.Windows.Input;
using Core.Keys;
using Core.Model;
// KeyBinding 在本文件内一律指 Core 的绑定契约：System.Windows.Input（UseWPF 带进来）
// 也有同名类型，不消歧会在每个 KeyBinding 处报 CS0104。用 alias 而非全限定名，
// 是为了让签名、XML 文档 <see cref>、局部变量声明共用同一个短名字（少一处写法就不一致）。
using KeyBinding = Core.Keys.KeyBinding;

namespace Core.Input;

/// <summary>
/// 开关按键与紧急停止键的**预计算快照**（设计文档 §3.4.1、§3.5.1）——"物理键（族归一后）→ 条目"的不可变映射，含**两张表**：开关键表与紧急停止表。
///
/// <para>
/// <b>为什么需要它</b>：钩子回调的返回值是同步且强制的（设计文档 §3.2「回调返回值即时性约束」）——
/// "照常发送 / 吞掉"必须在回调内当场判定，不能经由队列往返（队列只能事后通知，
/// 那时事件早已放行，改不掉）。而任务列表可变且不设上限，回调内遍历任务列表
/// 直接违反"最小判定"。因此把决策**预先算好**成一张小表：回调内只剩
/// 「按键标识 → 查表 → 读一个布尔位」三件事。
/// </para>
/// <para>
/// <b>为什么不可变</b>：快照发布后不再修改，替换是单一引用的原子写
/// （调用方用 <c>Volatile.Write</c> / <c>Interlocked.Exchange</c> 整体替换引用），
/// 钩子侧用 <c>Volatile.Read</c> 读取 → 回调**永不取锁**，
/// 也就不存在"回调等锁 → 逼近 <c>LowLevelHooksTimeout</c> → 钩子被系统静默摘除"的路径。
/// </para>
/// <para>
/// <b>为什么落在 Core</b>：本类无 P/Invoke、无 IO、无平台调用，是纯逻辑；
/// 而测试工程只引用 Core（不引用 Host），故需要单测的逻辑必须落在这里。
/// </para>
/// <para>
/// <b>职责边界</b>：本类**只持有与查询**，不认识任务模型之外的东西——
/// 不读配置文件、不依赖调度器、不引用 Input 层以外的任何实现（§2.1 分层）。
/// </para>
/// <para>
/// <b>两张表同源构建</b>：紧急停止表与开关键表由同一个 <see cref="Build"/> 一次产出，
/// 共用同一套族归一与修饰键口径——分头构建会让"配右 Ctrl、按左 Ctrl"在一张表命中、
/// 另一张表落空。两张表在回调里各查一次，命中都投同一个 <c>ToggleRequested</c>，
/// 仅归属标识不同（任务 Id / <c>null</c>=紧急停止）。
/// </para>
/// </summary>
public sealed class ToggleSnapshot
{
    /// <summary>
    /// 空快照（无任何开关按键、无紧急停止绑定）。与 <c>Build(null)</c> 等价，
    /// 供调用方在"尚未加载配置"时使用，省掉一次判空。
    /// </summary>
    public static ToggleSnapshot Empty { get; } = new(new Dictionary<PhysicalKey, ToggleEntry>(), null);

    /// <summary>
    /// 键 → 开关条目的映射。键已**族归一**（修饰键左右归一）。
    ///
    /// <para>
    /// 只构造一次、此后只读：本类型不提供任何修改入口（无 <c>Add</c> / 无索引器 setter），
    /// 保证快照在钩子侧被读到的那一瞬间与它在构造完成时是同一致的。
    /// </para>
    /// </summary>
    private readonly Dictionary<PhysicalKey, ToggleEntry> _entriesByKey;

    /// <summary>
    /// 紧急停止快捷键条目（设计文档 §3.5.2）。<c>null</c> = 没有可用的紧急停止绑定。
    ///
    /// <para>
    /// 它是<b>单条目</b>而非一张表：紧急停止快捷键全应用只有一个（<c>AppSettings.EmergencyStopKey</c>），
    /// 不复存在"多个绑定"的情形，用表反而要处理一个永远不会出现的取舍。
    /// 但它与开关键表 <b>共用 <see cref="Normalize"/> 与修饰键族口径</b>：
    /// 同一物理键在两个入口下必须命中同一个条目，否则“配右 Ctrl、按左 Ctrl”会在一边生效、另一边落空。
    /// </para>
    /// <para>
    /// 与 <see cref="ToggleEntry"/> 不同，本条目不携带任务 Id——紧急停止的归属是"全部运行中任务"，
    /// 没有哪个任务 Id 能表达它。事件侧用 <c>null</c> 表达该归属（§3.5.2）。
    /// </para>
    /// </summary>
    private readonly EmergencyStopEntry? _emergencyStop;

    /// <summary>
    /// 由"任务列表"构建快照——**配置拥有者**在钩子之外调用（设计文档 §3.4.1「构建」）。
    ///
    /// <para>
    /// 只取 <see cref="TaskDefinition.Enabled"/> 为 <c>true</c> 的任务：
    /// 禁用任务的开关按键按 AC-02 应"无任何反应"，把它放进快照会让钩子
    /// 仍把它当开关键消费（甚至吞掉用户的按键）——那是禁用语义的反面。
    /// </para>
    /// <para>
    /// 组合形态（<see cref="KeyBinding.Modifiers"/> 非空）的修饰键**归一为族名集合**
    /// （Ctrl / Alt / Shift / Win），与 <see cref="TaskValidator.IsSameBinding"/> 的判等口径同族：
    /// 主键严格比对，修饰键按族名集合判等——"配右 Ctrl、按左 Ctrl"因此仍算同一组合。
    /// </para>
    /// <para>
    /// <paramref name="tasks"/> 为 <c>null</c> 时按空处理（不抛）：
    /// 配置尚未加载、或调用方拿到的是空表，都属于正常状态，不该让调用方四处判空。
    /// </para>
    /// </summary>
    /// <param name="tasks">当前任务列表；<c>null</c> 等价于空。</param>
    /// <param name="emergencyStopKey">
    /// 紧急停止快捷键绑定（<see cref="AppSettings.EmergencyStopKey"/> 形态）；
    /// <c>null</c> 等价于"未配置"——不建紧急停止条目，其余行为不变。
    /// </param>
    /// <remarks>
    /// <b>签名带紧急停止键而非只读任务列表</b>：设计文档 §3.5.4 要求开关键表在构建期
    /// **按主键排除**与紧急停止键同主键的绑定，且要求两张表**同源构建**（共用同一套族归一口径）。
    /// 因此紧急停止绑定必须从**同一个构造入口**进来，分两次构建做不到排除，
    /// 也会导致钩子侧需要维护两个引用、两次原子写——那就破坏了"整体替换"。
    /// </remarks>
    public static ToggleSnapshot Build(IEnumerable<TaskDefinition>? tasks, KeyBinding? emergencyStopKey = null)
    {
        var emergency = EmergencyStopEntry.Create(emergencyStopKey);
        var map = new Dictionary<PhysicalKey, ToggleEntry>();

        if (tasks is not null)
        {
            foreach (var task in tasks)
            {
                // 畸形配置里可能出现 null 元素（JSON 数组含 null）——跳过而不是抛：
                // 一个坏任务不该让整张快照构建失败，从而让**全部**开关按键失灵。
                // ToggleKey 为 null 同理：反序列化喂进来的 `"toggleKey": null` 会绕过
                // 属性的默认值（与 KeyCatalog 对空绑定的兜底是同一类防御），
                // 同一个道理——没配开关键的任务就是不参与快照。
                if (task is null || !task.Enabled || task.ToggleKey is null)
                {
                    continue;
                }

                var key = Normalize(task.ToggleKey.Trigger);

                // 与紧急停止键同主键的绑定**不进开关键表**（设计文档 §3.5.4）。
                // 不排除的话，同一个物理键会在同一次点按里命中两张表，投递出两条事件
                // （一条停止全部、一条切换某任务），用户看到的是"按一下做了两件事"。
                // 这是**纵深防御**：保存期已按完整绑定拒绝冲突（FR-RUN-03），
                // 但校验器拦不住手改配置文件。排除后非法配置退化为"开关键那一侧不生效"，
                // 而**紧急停止仍可用**——兑底手段在数据损坏时依然兑底。
                //
                // 判等口径比保存期**更宽**（此处只比主键，保存期比完整绑定）：
                // 因为两张表的查表键就是主键，主键相同即必然同时命中，
                // 而修饰集不同时哪一张表命中取决于用户实际按了几个修饰键——那是不可接受的"看心情"。
                if (emergency is { } e && e.MatchesKey(key))
                {
                    continue;
                }

                var families = ModifierFamilies(task.ToggleKey.Modifiers);
                var entry = new ToggleEntry(task.Id, families, task.ConsumeToggleInput);

                // 同键合并（设计文档 §3.4.1）：生产路径不可达的**防线**——
                // 保存期校验已按主键拒绝同主键绑定（FR-TOGGLE-05），
                // 这里只接住手改坏的配置文件。没有它，后一个任务会静默覆盖前一个，
                // "哪个胜出"取决于任务在列表里的顺序，表现为"开关时灵时不灵"。
                // consume 取或（更保守的一侧，与遍历顺序无关）；
                // 修饰键族集合取并集，两个任务的原修饰组合都仍可命中。
                if (map.TryGetValue(key, out var existing))
                {
                    map[key] = existing.Merge(entry);
                }
                else
                {
                    map[key] = entry;
                }
            }
        }

        return new ToggleSnapshot(map, emergency);
    }

    /// <summary>
    /// 由"已归一的键 → 是否吞掉"条目直接构造（便利形态：所有条目都是**单键**，无修饰键）。
    /// 供测试与调用方传入手工条目（<see cref="Build"/> 是生产路径）；
    /// 组合形态请用 <see cref="Build"/> 从任务列表构建。
    /// </summary>
    /// <param name="entries">条目序列；<c>null</c> 等价于空。</param>
    public static ToggleSnapshot FromEntries(IEnumerable<KeyValuePair<PhysicalKey, bool>>? entries)
    {
        if (entries is null)
        {
            return Empty;
        }

        var map = new Dictionary<PhysicalKey, ToggleEntry>();
        foreach (var (key, consume) in entries)
        {
            var normalized = Normalize(key);
            var entry = new ToggleEntry(Guid.Empty, ToggleEntry.NoModifiers, consume);
            if (map.TryGetValue(normalized, out var existing))
            {
                map[normalized] = existing.Merge(entry);
            }
            else
            {
                map[normalized] = entry;
            }
        }

        return new ToggleSnapshot(map, null);
    }

    /// <summary>
    /// 由"已归一的映射 + 紧急停止条目"构造（内部用；不复制入参字典的语义见注释）。
    /// 手工条目形态（<see cref="FromEntries"/>）永远传 <c>null</c>——手工条目是测试/便利入口，
    /// 不承担紧急停止语义。
    /// </summary>
    private ToggleSnapshot(Dictionary<PhysicalKey, ToggleEntry> map, EmergencyStopEntry? emergencyStop)
    {
        // 拷贝一份：入参字典不是本类型的私有物（Empty 用的是静态共享字典、
        // Build/FromEntries 用的是局部变量），若直接持有同一实例，
        // 持有者后续对它的任何写入都会穿透到已发布的快照上，
        // 与"发布后不再修改"的不可变契约直接冲突。
        _entriesByKey = new Dictionary<PhysicalKey, ToggleEntry>(map);
        _emergencyStop = emergencyStop;
    }

    /// <summary>快照中的开关键条目数（供诊断与测试）。</summary>
    public int Count => _entriesByKey.Count;

    /// <summary>紧急停止条目（未配置时为 <c>null</c>；供诊断与测试）。</summary>
    public EmergencyStopEntry? EmergencyStop => _emergencyStop;

    /// <summary>
    /// 该物理键此刻是否命中**紧急停止**，且条目的修饰键族集合已全部按下。
    ///
    /// <para>
    /// <b>口径与开关键查询完全一致</b>（<see cref="TryGetTaskId"/>）：主键先族归一再查，
    /// 条目为组合形态时要求修饰键族集合 ⊆ <paramref name="pressedFamilies"/>。
    /// 单一来源保证了"配合得上的两个入口"——两处各写一遍判定，
    /// 迟早一份改了另一份没改，表现为紧急停止偶尔无响应。
    /// </para>
    /// </summary>
    /// <param name="key">待判定的物理键（原始，未归一）。</param>
    /// <param name="pressedFamilies">当前按下的修饰键族名集合；<c>null</c> 或空视为一个都没按。</param>
    /// <returns>该键此刻是否命中紧急停止。</returns>
    public bool IsEmergencyStop(PhysicalKey key, IReadOnlyCollection<string>? pressedFamilies)
        => _emergencyStop is { } entry
            && entry.MatchesKey(Normalize(key))
            && entry.AllFamiliesPressed(pressedFamilies);

    /// <summary>
    /// 紧急停止快捷键条目（设计文档 §3.5.2）——**主键 + 修饰键族集合**，与
    /// <see cref="ToggleEntry"/> 同口径但不带任务 Id。
    ///
    /// <para>
    /// <b>与 <see cref="ToggleEntry"/> 的分工</b>：两者都用同一个 <see cref="Normalize"/> 归一，
    /// 故同一物理键在两个入口下命中同一实体；差别在归属承载——
    /// 开关键条目带任务 Id，紧急停止不带（它的归属是"全部运行中任务"，没有哪个 Id 能表达）。
    /// 事件侧用 <c>null Guid?</c> 承担该差别（§3.5.2）。
    /// </para>
    /// <para>
    /// 独立成类型而非复用 <see cref="ToggleEntry"/>，是因为后者字段含 TaskId：
    /// 复用只能填 <see cref="Guid.Empty"/>，而 <c>Guid.Empty</c> 是**合法的任务 Id 值域**
    /// （<see cref="FromEntries"/> 也用它）——两个语义撞同一个值时，
    /// 日后任何一次"看到 Empty 就当紧急停止/就当手工条目"的推断都会错。
    /// </para>
    /// </summary>
    public readonly record struct EmergencyStopEntry(
        PhysicalKey Key,
        IReadOnlySet<string> ModifierFamilies)
    {
        /// <summary>
        /// 由配置绑定构造条目（<c>null</c> = 未配置 ⇒ 返回 <c>null</c>）。
        ///
        /// <para>
        /// <b>已族归一的主键</b>：构造时即归一，查询时只需对入参归一——两边各归一一次，
        /// 与开关键表 <see cref="Build"/> 的做法一致。修饰键族集合同理在构造期算定
        /// （不晚到查询时——查询在钩子热路径，那里只该做查表与比较）。
        /// </para>
        /// </summary>
        internal static EmergencyStopEntry? Create(KeyBinding? binding)
        {
            if (binding is null)
            {
                return null;
            }

            // 显式写全限定名而不是裸 Normalize/ModifierFamilies：本类型是嵌套类型，
            // 而这两个静态方法在**外层类** ToggleSnapshot 上——嵌套类型不能直接继承外层静态成员，
            // 裸写会被编译器当成对主构造函数参数的引用（CS9105/CS0149）。
            return new EmergencyStopEntry(
                ToggleSnapshot.Normalize(binding.Trigger),
                ToggleSnapshot.ModifierFamilies(binding.Modifiers));
        }

        /// <summary>是否为组合形态（需要校验修饰键是否全部按下）。</summary>
        public bool IsCombo => ModifierFamilies.Count > 0;

        /// <summary>
        /// 该（已归一的）主键是否就是本条目主键。
        ///
        /// <para>
        /// <b>只比主键，不比修饰集</b>——这是刻意的：构建期排除同键开关键时用的就是这个口径。
        /// 若改成比修饰集，则"同样主键、修饰集不同"的开关键会被放过，
        /// 于是同一个物理键同时命中两张表，一次点按投出两条事件。
        /// </para>
        /// </summary>
        internal bool MatchesKey(PhysicalKey normalizedKey) => Key == normalizedKey;

        /// <summary>
        /// 本条目要求的修饰键族是否**全部**在 <paramref name="pressedFamilies"/> 中（口径同 <see cref="ToggleEntry.AllFamiliesPressed"/>）。
        /// </summary>
        internal bool AllFamiliesPressed(IReadOnlyCollection<string>? pressedFamilies)
        {
            if (pressedFamilies is null || pressedFamilies.Count == 0)
            {
                return ModifierFamilies.Count == 0;
            }

            foreach (var family in ModifierFamilies)
            {
                if (!pressedFamilies.Contains(family))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// 该物理键是否为某个**启用任务**的开关键；是则给出其消费口径。
    ///
    /// <para>
    /// <b>单键口径</b>：不做任何修饰键判定（组合形态请用
    /// <see cref="TryGetConsume(PhysicalKey, IReadOnlyCollection{string}?, out bool)"/>）。
    /// 本重载保留给鼠标键路径与不需要组合语义的调用方。
    /// </para>
    /// <para>
    /// <paramref name="key"/> **先族归一**再查表——钩子捕获到的是物理上的左/右 Ctrl，
    /// 而配置里存的可能是另一侧；不归一会让"配右 Ctrl、按左 Ctrl"查不中。
    /// </para>
    /// </summary>
    /// <param name="key">待判定的物理键（原始，未归一）。</param>
    /// <param name="consume">命中时：<c>true</c> = 吞掉，<c>false</c> = 照常发送；未命中时恒为 <c>false</c>。</param>
    /// <returns>该键是否为某启用任务的开关键。</returns>
    public bool TryGetConsume(PhysicalKey key, out bool consume)
    {
        if (_entriesByKey.TryGetValue(Normalize(key), out var entry))
        {
            consume = entry.Consume;
            return true;
        }

        consume = false;
        return false;
    }

    /// <summary>
    /// 组合口径查询：该键是否为开关键，且其修饰键族集合**全部**处于按下态。
    ///
    /// <para>
    /// <b>判定</b>（设计文档 §3.4.1）：命中 = 主键命中 <b>且</b> 条目的修饰键族集合
    /// ⊆ <paramref name="pressedFamilies"/>。单键与鼠标键条目（<see cref="ToggleEntry.IsCombo"/>
    /// 为 <c>false</c>）**不做任何额外判定**——即修饰键按下与否都不影响它们是否命中。
    /// </para>
    /// </summary>
    /// <param name="key">待判定的物理键（原始，未归一）。</param>
    /// <param name="pressedFamilies">当前按下的修饰键族名集合；<c>null</c> 或空视为一个都没按。</param>
    /// <param name="consume">命中时：<c>true</c> = 吞掉，<c>false</c> = 照常发送；未命中时恒为 <c>false</c>。</param>
    /// <returns>该键此刻是否应判定为某启用任务的开关键。</returns>
    public bool TryGetConsume(PhysicalKey key, IReadOnlyCollection<string>? pressedFamilies, out bool consume)
    {
        if (!_entriesByKey.TryGetValue(Normalize(key), out var entry))
        {
            consume = false;
            return false;
        }

        if (entry.IsCombo && !entry.AllFamiliesPressed(pressedFamilies))
        {
            consume = false;
            return false;
        }

        consume = entry.Consume;
        return true;
    }

    /// <summary>
    /// 该物理键此刻命中的**开关所属任务 Id**（设计文档 §5.4 方案 A）。
    ///
    /// <para>
    /// <b>存在的理由</b>：<see cref="TryGetConsume"/族只给出“吞 / 放行”的布尔位，
    /// 给不出“这是哪个任务”。Engine 收到开关事件后要启停任务，就必须知道命中谁——
    /// 因此任务 Id 在构建快照时就并入条目，此处按同一个归一口径取出。
    /// </para>
    /// <para>
    /// <b>口径与 <see cref="TryGetConsume(PhysicalKey, IReadOnlyCollection{string}?, out bool)"/> 完全一致</b>：
    /// 主键命中且（组合形态时）条目的修饰键族集合 ⊆ <paramref name="pressedFamilies"/>。
    /// 单键与鼠标条目不做修饰键判定。调用方传**原始物理键**即可，族归一由本类完成，
    /// 调用方不得重复一遍归一规则（重复维护两份规则会产生“钩子命中了、Engine 找不到任务”的静默失配）。
    /// </para>
    /// </summary>
    /// <param name="key">待判定的物理键（原始，未归一）。</param>
    /// <param name="pressedFamilies">当前按下的修饰键族名集合；<c>null</c> 或空视为一个都没按。</param>
    /// <param name="taskId">命中时：该开关所属任务 Id；未命中时恒为 <see cref="Guid.Empty"/>。</param>
    /// <returns>该键此刻是否命中某启用任务的开关键。</returns>
    public bool TryGetTaskId(PhysicalKey key, IReadOnlyCollection<string>? pressedFamilies, out Guid taskId)
    {
        if (!_entriesByKey.TryGetValue(Normalize(key), out var entry)
            || (entry.IsCombo && !entry.AllFamiliesPressed(pressedFamilies)))
        {
            taskId = Guid.Empty;
            return false;
        }

        taskId = entry.TaskId;
        return true;
    }

    /// <summary>该物理键是否为某启用任务的开关键（不需要消费口径时用）。</summary>
    public bool Contains(PhysicalKey key) => _entriesByKey.ContainsKey(Normalize(key));

    /// <summary>
    /// 族归一：修饰键（Ctrl / Alt / Shift / Win）左右归一到代表键，鼠标键与非修饰键原样。
    ///
    /// <para>
    /// 与按住表 <see cref="HoldTable.Normalize"/> **同源**（都用 <see cref="KeyCatalog.RepresentativeOfFamily"/>）：
    /// 两处若各归一一口径，"按住表认为同一个键、快照认为两个键"的错配会直接表现为
    /// 开关键时灵时不灵。鼠标键没有左右族概念，原样保留。
    /// </para>
    /// </summary>
    private static PhysicalKey Normalize(PhysicalKey key)
        => key.KeyboardVirtualKey is { } virtualKey
            ? PhysicalKey.FromKeyboard(KeyCatalog.RepresentativeOfFamily(virtualKey))
            : key;

    /// <summary>
    /// 把 <see cref="KeyBinding.Modifiers"/> 归一为**族名集合**（Ctrl / Alt / Shift / Win）。
    ///
    /// <para>
    /// 与 <see cref="TaskValidator.IsSameBinding"/> 的判等口径同源：修饰键按族名判等，
    /// 左右同族视为同一个。非修饰键（畸形配置）不入集——它不构成组合的前置条件。
    /// </para>
    /// </summary>
    private static IReadOnlySet<string> ModifierFamilies(IReadOnlySet<Key>? modifiers)
    {
        if (modifiers is null || modifiers.Count == 0)
        {
            return ToggleEntry.NoModifiers;
        }

        var families = new HashSet<string>();
        foreach (var key in modifiers)
        {
            if (KeyCatalog.ModifierFamily(key) is { } family)
            {
                families.Add(family);
            }
        }

        return families.Count > 0 ? families : ToggleEntry.NoModifiers;
    }

    /// <summary>枚举全部条目（供测试与诊断；顺序不保证）。</summary>
    /// <remarks>
    /// 返回只读包装而非底层字典本身：直接暴露字段会让调用方强转回
    /// <see cref="Dictionary{TKey,TValue}"/> 后改写已发布（乃至静态共享）的快照，
    /// 破坏不可变契约。
    /// </remarks>
    public IReadOnlyCollection<KeyValuePair<PhysicalKey, ToggleEntry>> Entries
        => _readOnlyView ??= new ReadOnlyCollection<KeyValuePair<PhysicalKey, ToggleEntry>>(
            new List<KeyValuePair<PhysicalKey, ToggleEntry>>(_entriesByKey));

    /// <summary>只读视图缓存（构造后内容不再变，故可缓存）。</summary>
    private ReadOnlyCollection<KeyValuePair<PhysicalKey, ToggleEntry>>? _readOnlyView;

    /// <summary>
    /// 一个开关按键条目：**所属任务 Id + 修饰键族集合 + 消费口径**（设计文档 §5.4 方案 A）。
    ///
    /// <para>
    /// <see cref="TaskId"/> 为发出该开关的任务 Id，供 Engine 命中后定位任务；
    /// 手工条目（<see cref="FromEntries"/>）无任务归属，取 <see cref="Guid.Empty"/>。
    /// </para>
    /// <para>
    /// <see cref="ModifierFamilies"/> 为族名字符串集合（Ctrl / Alt / Shift / Win），
    /// 空集表示单键或鼠标键形态。<see cref="IsCombo"/> 即"集合非空"的派生判断——
    /// 组合形态才需要校验修饰键是否按下。
    /// </para>
    /// </summary>
    public readonly record struct ToggleEntry(
        Guid TaskId,
        IReadOnlySet<string> ModifierFamilies,
        bool Consume)
    {
        /// <summary>共用的空修饰键集（单键 / 鼠标键形态共用同一个实例，避免每次分配）。</summary>
        /// <remarks>
        /// 以 <see cref="IReadOnlySet{T}"/> 暴露且**永不被写入**；构造时即包装为只读视图，
        /// 防止调用方强转回 <see cref="HashSet{T}"/> 后改掉这个进程级共享实例
        /// （与 <c>Entries</c> 返回 <see cref="ReadOnlyCollection{T}"/> 的防护同一理由）。
        /// </remarks>
        internal static readonly IReadOnlySet<string> NoModifiers =
            new HashSet<string>();

        /// <summary>是否为组合形态（需要校验修饰键是否全部按下）。</summary>
        public bool IsCombo => ModifierFamilies.Count > 0;

        /// <summary>
        /// 本条目要求的修饰键族是否**全部**在 <paramref name="pressedFamilies"/> 中。
        ///
        /// <para>
        /// 用逐族 <c>Contains</c> 而非 <c>IsSubsetOf</c>：调用方（钩子回调）持有的是
        /// <c>Dictionary&lt;string,int&gt;.Keys</c> 视图，只保证 <see cref="IReadOnlyCollection{T}"/>，
        /// 且本方法只在回调热路径被调用——逐族线性扫描（最多 4 族）比构造集合更省。
        /// </para>
        /// </summary>
        internal bool AllFamiliesPressed(IReadOnlyCollection<string>? pressedFamilies)
        {
            if (pressedFamilies is null || pressedFamilies.Count == 0)
            {
                return ModifierFamilies.Count == 0;
            }

            foreach (var family in ModifierFamilies)
            {
                if (!pressedFamilies.Contains(family))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 同主键两个条目的确定性合并：修饰集取并集，consume 取或（任一要求吞掉即吞掉）。
        ///
        /// <para>
        /// <b>生产路径不可达（防线）</b>：保存期校验已按<b>主键</b>拒绝同主键绑定
        /// （<c>TaskValidator.IsSameToggleKey</c>，FR-TOGGLE-05），故合法配置里同一主键至多一个条目。
        /// 本方法存在的唯一理由是<b>手改坏的配置文件</b>——校验器拦不住绕过 UI 的写入。
        /// 没有它，后一个任务会静默覆盖前一个，且“哪个胜出”取决于任务在列表里的顺序，
        /// 用户看到的是“开关时灵时不灵”而无从归因。合并保证两个任务都仍有可命中的组合。
        /// </para>
        /// <para>
        /// <b>为什么不取替换</b>：取并集而非替换——合并后的条目对两个任务的原修饰组合
        /// 都仍能命中（<c>Ctrl+Alt+F8</c> 同时满足 <c>Ctrl+F8</c> 与 <c>Alt+F8</c>），
        /// 没有任何一方被静默吞掉。
        /// </para>
        /// </summary>
        internal ToggleEntry Merge(ToggleEntry other)
        {
            // 防御网合并只保住"仍有组合可命中"；它不定义"同键时哪个任务胜出"——
            // 那是生产路径不可达的情形（§5.4 冲突口径：保存期已按主键拒绝）。
            // 若真出现，两个任务 Id 无法共存于一个条目，这里**确定性**取较小的 Id
            // （二者必有一个点按下不中开关，好过"结果随任务在列表里的顺序而变"）。
            var taskId = CompareTaskIds(TaskId, other.TaskId) <= 0 ? TaskId : other.TaskId;

            if (ModifierFamilies.Count == 0)
            {
                return other with { TaskId = taskId, Consume = Consume || other.Consume };
            }

            if (other.ModifierFamilies.Count == 0)
            {
                return this with { TaskId = taskId, Consume = Consume || other.Consume };
            }

            var union = new HashSet<string>(ModifierFamilies);
            union.UnionWith(other.ModifierFamilies);
            return new ToggleEntry(taskId, union, Consume || other.Consume);
        }

        /// <summary>
        /// 两个任务 Id 的确定性比较（仅用于防御网合并的"谁胜出"）。
        ///
        /// <para>
        /// 不用 <c>Guid.CompareTo</c>：其内部按字段分段比较，跨 .NET 版本不承诺
        /// 稳定的全序口径。这里取 <c>ToByteArray</c> 逐字节比较，是**与实现无关的**
        /// 确定性序——防御网合并只要求"结果不随遍历顺序变"，不要求任何业务含义。
        /// </para>
        /// </summary>
        private static int CompareTaskIds(Guid a, Guid b)
        {
            var x = a.ToByteArray();
            var y = b.ToByteArray();
            return x.AsSpan().SequenceCompareTo(y);
        }
    }
}
