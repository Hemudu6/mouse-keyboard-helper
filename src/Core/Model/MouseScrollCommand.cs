using System.Text.Json.Serialization;
using Core.Keys;

namespace Core.Model;

/// <summary>
/// 鼠标滚轮命令（需求 FR-CMD-M3）。
/// 如"滚轮向下 · 3 格"：每次执行滚动 <see cref="Notches"/> 格（AC-12a）。
///
/// 无执行形态——滚动量是瞬时动作，不存在"按住"。
/// </summary>
public sealed class MouseScrollCommand : CommandDefinition
{
    /// <summary>滚动方向（固定集合，界面用选择控件）。</summary>
    public ScrollDirection Direction { get; set; } = ScrollDirection.Down;

    /// <summary>
    /// 每次滚动格数（Windows 上 1 格 = <c>WHEEL_DELTA</c> = 120）。
    /// 上限由 <see cref="TaskValidator"/> 判定；下界为 1——滚 0 格是无意义命令。
    /// </summary>
    public int Notches { get; set; } = 1;

    /// <inheritdoc />
    [JsonIgnore]
    public override string DisplayName
        => $"鼠标滚轮{(Direction == ScrollDirection.Up ? "向上" : "向下")} · {Notches} 格 · {Frequency} 次/秒";
}
