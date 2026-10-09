using System.Text.Json.Serialization;

namespace Core.Model;

/// <summary>
/// 鼠标移动命令（需求 FR-CMD-M1）。
/// 反复把指针移动到绝对坐标 (X, Y)，频率由本命令的 <c>Frequency</c> 决定（AC-12 / AC-14c）。
///
/// 坐标为屏幕绝对像素。跨多显示器时用虚拟桌面坐标系
/// （输入层以 <c>MOUSEEVENTF_VIRTUALDESK</c> 归一化，见设计文档 §6.3）。
/// 故此处允许负值：主显示器左侧的副显示器坐标为负。
/// </summary>
public sealed class MouseMoveCommand : CommandDefinition
{
    /// <summary>目标 X 坐标（虚拟桌面坐标系，可为负）。</summary>
    public int X { get; set; }

    /// <summary>目标 Y 坐标（虚拟桌面坐标系，可为负）。</summary>
    public int Y { get; set; }

    /// <inheritdoc />
    [JsonIgnore]
    public override string DisplayName => $"鼠标移动到 ({X}, {Y}) · {Frequency} 次/秒";
}
