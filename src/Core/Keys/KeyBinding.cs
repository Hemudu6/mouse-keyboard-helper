using System.Text.Json.Serialization;
using System.Windows.Input;

namespace Core.Keys;

/// <summary>
/// 开关按键的绑定形态（设计文档 §5.2）。
///
/// 与 <see cref="PhysicalKey"/> 的区别不是结构上的，而是**语义上的**：
/// <see cref="PhysicalKey"/> 回答"这是哪个实体键"（用于命令实体、用于冲突比对）；
/// <see cref="KeyBinding"/> 回答"用户的这个开关按键是如何绑定的"——单键、还是键位组合。
///
/// 需求 FR-TOGGLE-01 只要求"键盘键或鼠标键"可作开关键；引入组合形态是因为
/// 单键作开关在实用中几乎不可用（任意一个 F 键都会被其他程序占用）。
/// 组合与鼠标键互斥：鼠标键不参与组合。
/// </summary>
public sealed record KeyBinding
{
    /// <summary>
    /// 触发键。键盘单键、键盘组合的主键、或鼠标键。
    /// </summary>
    public PhysicalKey Trigger { get; init; }

    /// <summary>
    /// 修饰键集合（Ctrl / Alt / Shift / Win）。鼠标键作开关时必须为空；
    /// 键盘单键作开关时也为空。空集合表示"无修饰条件"。
    ///
    /// **不直接落盘**：类型是 <see cref="IReadOnlySet{T}"/>，而 System.Text.Json
    /// **无法为接口类型建实例**——读取时直接报 "could not be instantiated"，
    /// 于是每条保存过的任务都读不回来（AC-29 会整条不成立）。
    /// 落盘形状由 <see cref="ModifiersForJson"/> 固定成 JSON 数组，读出时再还原成集合。
    /// </summary>
    private IReadOnlySet<Key> _modifiers = new HashSet<Key>();

    /// <inheritdoc cref="_modifiers"/>
    [JsonIgnore]
    public IReadOnlySet<Key> Modifiers
    {
        get => _modifiers;
        init => _modifiers = value is null ? new HashSet<Key>() : value;
    }

    /// <summary>无参构造：供反序列化与对象初始化器使用。</summary>
    public KeyBinding()
    {
    }

    /// <summary>由触发键与修饰键集合构造。</summary>
    public KeyBinding(PhysicalKey trigger, IReadOnlySet<Key> modifiers)
    {
        Trigger = trigger;
        // 归一 null：畸形文件里 "modifiers": null 是合法 JSON，
        // 但它绝不该让本对象处于"修饰键为 null"的不可用状态（AC-21 要求投毒文件不崩溃）。
        Modifiers = modifiers;
    }

    /// <summary>由单个键盘键构造（无修饰）。</summary>
    public static KeyBinding FromKey(Key virtualKey)
        => new(PhysicalKey.FromKeyboard(virtualKey), new HashSet<Key>());

    /// <summary>由鼠标键构造（无修饰；鼠标键不参与组合）。</summary>
    public static KeyBinding FromMouse(MouseKey mouseKey)
        => new(PhysicalKey.FromMouse(mouseKey), new HashSet<Key>());

    /// <summary>由"修饰键 + 主键"构造键盘组合。</summary>
    public static KeyBinding FromCombo(IEnumerable<Key> modifiers, Key virtualKey)
        => new(PhysicalKey.FromKeyboard(virtualKey), new HashSet<Key>(modifiers));

    /// <summary>是否为键盘组合形态（有修饰键）。</summary>
    [JsonIgnore]
    public bool IsCombo => Modifiers.Count > 0;

    /// <summary>
    /// 触发键的键盘虚拟键码；触发键是鼠标键时返回 <c>null</c>。
    ///
    /// <para>
    /// 供系统级热键注册等只认键盘键的调用方取用（鼠标键无法被 <c>RegisterHotKey</c> 注册）。
    /// 派生值，不落盘——真相在 <see cref="Trigger"/> 里，写出去只会造成两处真相。
    /// </para>
    /// </summary>
    [JsonIgnore]
    public Key? MainKey => Trigger.KeyboardVirtualKey;

    /// <summary>
    /// <see cref="Modifiers"/> 的落盘桥接（设计文档 §7.1）。
    ///
    /// 把落盘形状固定成 JSON 数组，读出时再还原成集合——
    /// 这样配置文件里就是一个数组，而不是 STJ 对接口类型无从下手后抛出的异常。
    ///
    /// <para>
    /// 注意：落盘形态是 <see cref="Key"/> 的**数值**而非枚举名
    /// （实测 <c>"modifiers": [118]</c> 即 <c>Key.LeftCtrl</c>）——
    /// STJ 未挂 <c>JsonStringEnumConverter</c>，写成 <c>["LeftCtrl"]</c> 会直接抛。
    /// </para>
    ///
    /// <para>
    /// <c>init</c> 只在**输入非 null**时覆盖：<c>null</c>（含字段缺失）归一为空集，
    /// 非空输入原样生效——故文件里真实存在的修饰集不会被落盘端写出的空数组反向掏空。
    /// </para>
    /// </summary>
    [JsonPropertyName("modifiers")]
    public List<Key> ModifiersForJson
    {
        get => Modifiers.ToList();
        init => Modifiers = value is null ? new HashSet<Key>() : new HashSet<Key>(value);
    }
}
