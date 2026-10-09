using System.Text.Json.Serialization;
using System.Windows.Input;

namespace Core.Model;

/// <summary>
/// 键盘组合键命令（需求 FR-CMD-K2）。
/// 如 Ctrl+C：按下时修饰键先按（Ctrl↓），松开时主键先松、修饰键后松
/// ——事件序列 Ctrl↓、C↓、C↑、Ctrl↑（AC-11 / AC-14b）。
///
/// 组合键**两种形态都支持**（FR-TAP-06）：
/// 点按形态下整个组合（含修饰键）按命令频率反复执行；
/// 按住形态下修饰键与主键保持按下，任务停止时按“主键先松、修饰键后松”的顺序补发
/// （否则修饰键先松会导致主键被当作单键发出）。
/// 只有“不适用”（<see cref="TapMode.None"/>）被拒绝——组合键无事可做。
/// </summary>
public sealed class KeyboardComboCommand : CommandDefinition
{
    /// <summary>
    /// 修饰键集合（Ctrl / Alt / Shift / Win）。至少一个。
    /// 存为 <see cref="List{T}"/> 以便序列化；判等与比对走 <see cref="Modifiers"/>。
    /// </summary>
    public List<Key> ModifierKeys { get; set; } = new();

    /// <summary>组合的主键（非修饰键）。</summary>
    public Key VirtualKey { get; set; } = Key.C;

    /// <summary>修饰键集合的只读视图。</summary>
    [JsonIgnore]
    public IReadOnlySet<Key> Modifiers => new HashSet<Key>(ModifierKeys ?? new List<Key>());

    /// <inheritdoc />
    [JsonIgnore]
    public override string DisplayName
        => $"{Core.Keys.KeyCatalog.Describe(ToBinding())} · {KeyboardKeyCommand.DescribeMode(TapMode)}";

    /// <summary>转换为开关按键绑定形态，复用 <c>KeyCatalog.Describe</c> 的组合键排版规则。</summary>
    public Core.Keys.KeyBinding ToBinding()
        => new(Core.Keys.PhysicalKey.FromKeyboard(VirtualKey), Modifiers);

    /// <summary>
    /// 组合键占用的实体键集合：修饰键 + 主键。
    ///
    /// 用于 FR-SEQ-10 的"同一实体键不得同时配置按住与点按"与 FR-TOGGLE-07 的
    /// "开关按键不得与本任务命令实体键重叠"——Ctrl+C 这一条命令同时占用了
    /// Ctrl 与 C 两个实体键，把开关按键设为 Ctrl 或 C 都应被拒绝。
    /// </summary>
    [JsonIgnore]
    public IEnumerable<Core.Keys.PhysicalKey> PhysicalKeys
    {
        get
        {
            // ModifierKeys 理论上非空（默认值 + 形状校验），但它是个可被反序列化
            // 赋成 null 的可写属性——裸遍历会让校验器抛 NRE（AC-21 要求不崩）。
            foreach (var modifier in ModifierKeys ?? new List<Key>())
            {
                yield return Core.Keys.PhysicalKey.FromKeyboard(modifier);
            }

            yield return Core.Keys.PhysicalKey.FromKeyboard(VirtualKey);
        }
    }
}
