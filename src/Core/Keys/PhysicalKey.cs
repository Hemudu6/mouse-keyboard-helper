namespace Core.Keys;

/// <summary>
/// "物理实体键"——附着于一个真实按键上的标识，可以是键盘虚拟键码，也可以是鼠标键。
///
/// 存在意义有两处（对应设计文档 §5.2 / §5.3）：
///   1. <see cref="KeyBinding"/>（开关按键）与命令实体键（键盘键命令 / 鼠标按键命令）
///      要能放到**同一个集合**里比对——需求 FR-TOGGLE-05（开关按键不得被其他任务占用）
///      与 FR-TOGGLE-07（开关按键不得与本任务命令实体键重叠）。
///      两者若用两套不可比的类型表示，这两条校验就得写两遍特例。
///   2. 需求 FR-SEQ-10「同一实体键不同时存在按住与点按两条命令」同样需要"同一实体键"
///      有一个统一的判等口径。
///
/// 用 <c>readonly record struct</c>：判等按值、可作字典键、无堆分配。
/// </summary>
/// <param name="KeyboardVirtualKey">
/// 键盘虚拟键码（<c>System.Windows.Input.Key</c>）。鼠标实体键时为 <c>null</c>。
/// </param>
/// <param name="Mouse">
/// 鼠标键。键盘实体键时为 <c>null</c>。
/// </param>
public readonly record struct PhysicalKey(
    System.Windows.Input.Key? KeyboardVirtualKey,
    MouseKey? Mouse)
{
    /// <summary>这是键盘实体键。</summary>
    /// <remarks>
    /// 派生值，不落盘：它是从 <see cref="KeyboardVirtualKey"/> 算出来的，
    /// 写进配置文件只会造成"两处真相"，且读回时该属性不可写，纯属噪声字段。
    /// </remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsKeyboard => KeyboardVirtualKey is not null;

    /// <summary>这是鼠标实体键。</summary>
    /// <remarks>派生值，不落盘——理由同 <see cref="IsKeyboard"/>。</remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsMouse => Mouse is not null;

    /// <summary>由键盘虚拟键码构造。</summary>
    public static PhysicalKey FromKeyboard(System.Windows.Input.Key virtualKey)
        => new(virtualKey, null);

    /// <summary>由鼠标键构造。</summary>
    public static PhysicalKey FromMouse(MouseKey mouseKey)
        => new(null, mouseKey);
}
