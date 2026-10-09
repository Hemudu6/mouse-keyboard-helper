using System.Text.Json.Serialization;
using System.Windows.Input;

namespace Core.Model;

/// <summary>
/// 键盘单键命令（需求 FR-CMD-K1）。
/// 如"键盘 A · 按住"：按下 A 后保持不松，直到任务停止时补发松开（AC-10）。
/// </summary>
public sealed class KeyboardKeyCommand : CommandDefinition
{
    /// <summary>被操作的键盘虚拟键码。</summary>
    public Key VirtualKey { get; set; } = Key.A;

    /// <inheritdoc />
    [JsonIgnore]
    public override string DisplayName => $"键盘 {Core.Keys.KeyCatalog.Describe(VirtualKey)} · {DescribeMode(TapMode)}";

    /// <summary>执行形态的中文短名，供子类复用。</summary>
    internal static string DescribeMode(TapMode tapMode) => tapMode switch
    {
        TapMode.Tap => "点按",
        TapMode.Hold => "按住",
        _ => "—",
    };

    /// <summary>该命令对应的实体键（用于 FR-SEQ-10 的"同一实体键"判等）。</summary>
    /// <remarks>派生值，不落盘：由 <see cref="VirtualKey"/> 算出，写进文件是噪声。</remarks>
    [JsonIgnore]
    public Core.Keys.PhysicalKey PhysicalKey => Core.Keys.PhysicalKey.FromKeyboard(VirtualKey);
}
