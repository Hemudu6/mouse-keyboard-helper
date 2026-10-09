using System.Text.Json.Serialization;
using Core.Keys;

namespace Core.Model;

/// <summary>
/// 鼠标按键命令（需求 FR-CMD-M2）。
/// 如"鼠标左键 · 点按"（AC-07）、"鼠标 X1 · 按住"。
///
/// 注意与 <see cref="MouseMoveCommand"/> / <see cref="MouseScrollCommand"/> 的区别：
/// 本命令**有**执行形态——按下/松开对鼠标按键是有意义的物理状态。
/// </summary>
public sealed class MouseButtonCommand : CommandDefinition
{
    /// <summary>被操作的鼠标键。</summary>
    public MouseKey Button { get; set; } = MouseKey.Left;

    /// <inheritdoc />
    [JsonIgnore]
    public override string DisplayName
        => $"{KeyCatalog.Describe(Button)} · {KeyboardKeyCommand.DescribeMode(TapMode)}";

    /// <summary>该命令对应的实体键（用于 FR-SEQ-10 与 FR-TOGGLE-07 的判等）。</summary>
    [JsonIgnore]
    public PhysicalKey PhysicalKey => PhysicalKey.FromMouse(Button);
}
