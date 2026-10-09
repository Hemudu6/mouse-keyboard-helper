using System.Text.Json.Serialization;
using Core.Keys;

namespace Core.Model;

/// <summary>
/// 一个任务（需求 FR-TASK-01~09）：一条开关按键 + 一组并行执行的命令。
///
/// 命令之间**无顺序、无依赖、无轮次**（需求 FR-SEQ-01 明确"并行执行 + 独立频率"
/// 已取代早期的"循环执行"设想）。故 <see cref="Commands"/> 是集合语义，
/// 列表顺序不代表执行顺序，界面上也不提供排序控件（AC-07a）。
/// </summary>
public sealed class TaskDefinition
{
    /// <summary>
    /// 任务标识。仅用于程序内定位（运行期状态表、配置增删改），不面向用户显示。
    /// 用 <see cref="Guid"/> 而非名字：名字可改，改名字不应打断正在运行的任务的定位。
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>任务名（用户可见）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 是否启用（需求 FR-TASK-07）。
    /// 禁用的任务点按其开关键无任何反应（AC-02）。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>开关按键绑定（需求 FR-TOGGLE-01）。</summary>
    public KeyBinding ToggleKey { get; set; } = KeyBinding.FromKey(System.Windows.Input.Key.F6);

    /// <summary>
    /// 点按开关按键时，是否**吞掉**该按键的原始输入、不继续送达前台程序
    /// （需求 §6.1 / AC-33）。
    ///
    /// 逐任务配置、默认 <c>false</c>，即**照常发送**给前台程序（设计 §3.4 默认口径）。
    /// 设为 <c>true</c> 时，判定为该任务开关键后返回非零值且不调用 <c>CallNextHookEx</c>，
    /// 原始输入被吞掉。
    ///
    /// 按下与松开**同口径**（要么都吞、要么都放行）——
    /// 只吞按下不吞松开会让前台程序收到一个无配对的松开事件，行为不可预期。
    /// </summary>
    public bool ConsumeToggleInput { get; set; }

    /// <summary>任务的命令集合（需求 FR-SEQ-01）。</summary>
    public List<CommandDefinition> Commands { get; set; } = new();

    /// <summary>
    /// 任务名（界面显示用）。名字为空时回退到 <see cref="Id"/> —— 
    /// 宁可显示一串不好看的标识，也不显示空白行（空行会让用户找不到是哪个任务）。
    /// </summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id.ToString() : Name;
}
