using System.Text.Json.Serialization;

namespace Core.Model;

/// <summary>
/// 任务中的一条命令（需求 FR-SEQ-01~10 / FR-CMD-*）。
///
/// **多态基类**，用 System.Text.Json 的 <c>$type</c> 判别器落地（见 <c>JsonConfigStore</c>）：
/// 用户可能删除、添加、修改任意一条命令，配置文件里每一条的类型必须能自行还原。
/// 若把五种子命令揉成一个"什么字段都可能为空"的平坦类，读取时就要靠字段组合反推类型，
/// 那正是"投毒文件不抛异常"（AC-21）最怕的模糊地带。
///
/// 每条命令**各自持有自己的频率**：需求 FR-TAP-04 明确"同一任务内的两条点按命令
/// 各自按自己的频率重复"。频率在并行执行下是每条命令的独立节拍，
/// 不存在"任务级频率"这种东西。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(KeyboardKeyCommand), "keyboard-key")]
[JsonDerivedType(typeof(KeyboardComboCommand), "keyboard-combo")]
[JsonDerivedType(typeof(MouseMoveCommand), "mouse-move")]
[JsonDerivedType(typeof(MouseButtonCommand), "mouse-button")]
[JsonDerivedType(typeof(MouseScrollCommand), "mouse-scroll")]
public abstract class CommandDefinition
{
    /// <summary>
    /// 执行形态。默认"点按"——新建命令时的默认值。
    ///
    /// 不设 <c>init</c> 之外的禁写逻辑：鼠标类命令把被赋成 <see cref="TapMode.Tap"/>
    /// 时，由 <see cref="TaskValidator"/> 拒绝保存，而不是在此处静默改写——
    /// 静默改写会让用户看到自己选的形态"自己变了"，比拒绝保存更难排查。
    /// </summary>
    public TapMode TapMode { get; set; } = TapMode.Tap;

    /// <summary>
    /// 该命令重复执行的频率，单位"次/秒"（需求 FR-TAP-02 / FR-TAP-05）。
    ///
    /// 合法区间由 <see cref="TaskValidator"/> 判定：> 0 且 &lt;= 1000。
    /// 不用 double：AC-23b 要求 > 50 次/秒区间的实际平均频率 ≥ 配置值的 90%，
    /// 而 1000 次的整数上限足以覆盖全部需求，整数避免了"10.000000001 次/秒"这类
    /// 无意义精度与随之而来的界面显示问题。
    /// </summary>
    public int Frequency { get; set; } = 10;

    /// <summary>
    /// 该命令在界面上显示的中文短名（需求 FR-UI-05）。
    /// 由各子类实现；不参与序列化（每次由类型与字段算出，存进配置文件只会造成两处真相）。
    /// </summary>
    [JsonIgnore]
    public abstract string DisplayName { get; }
}
