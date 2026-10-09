namespace Core.Validation;

/// <summary>
/// 一条校验不通过的结论（设计文档 §5.3）。
///
/// 刻意**不**用"列表下标"定位问题：<see cref="TaskDefinition.Commands"/> 是集合语义
/// （FR-SEQ-01：命令之间无顺序），用户看到的界面也没有稳定的行号。
/// 定位改用 <see cref="Command"/> 引用本身——界面拿引用去选中对应卡片，
/// 既不依赖顺序，也不会因为列表被重排而指错。
/// </summary>
/// <param name="Rule">命中的校验条目（设计文档 §5.3 表格中的哪一行）。</param>
/// <param name="Message">面向用户的中文提示（需求 FR-UI-05：界面全中文）。</param>
/// <param name="Command">
/// 问题所属的命令；任务级问题（重名、开关按键冲突等）为 <c>null</c>。
/// </param>
public sealed record ValidationIssue(ValidationRule Rule, string Message, Model.CommandDefinition? Command = null)
{
    /// <summary>该问题是否落在某条具体命令上。</summary>
    public bool IsCommandScoped => Command is not null;
}

/// <summary>
/// 设计文档 §5.3 校验清单的**逐条标识**。
///
/// 与需求编号（FR-xx）分开：需求编号回答"为什么要有这条规则"，
/// 本枚举回答"命中的是哪条规则"。两者不是一对一——例如 FR-TAP-05
/// 同时约束频率区间与滚轮格数，但它们是两条可分别命中的规则。
///
/// 界面可以凭本枚举决定"把焦点跳到哪个控件"，单测可以凭它逐条断言，
/// 不必去比对提示文案（文案会改，枚举不会）。
/// </summary>
public enum ValidationRule
{
    /// <summary>任务名非空且在列表内唯一（FR-TASK-06）。</summary>
    TaskNameRequiredAndUnique,

    /// <summary>命令集合非空（FR-SEQ-08）。</summary>
    CommandsNotEmpty,

    /// <summary>点按频率在 1~1000 次/秒内（FR-TAP-05）。</summary>
    FrequencyInRange,

    /// <summary>开关按键必填（FR-TOGGLE-01）。</summary>
    ToggleKeyRequired,

    /// <summary>开关按键未被其他任务占用（FR-TOGGLE-05）。</summary>
    ToggleKeyNotTaken,

    /// <summary>开关按键不与本任务命令集合内的实体键重叠（FR-TOGGLE-07）。</summary>
    ToggleKeyNotOverlappingOwnCommands,

    /// <summary>同一实体键不同时存在"按住"与"点按"两条命令（FR-SEQ-10）。</summary>
    NoConflictingTapModesOnSameKey,

    /// <summary>紧急停止快捷键不与任何任务开关按键冲突（FR-RUN-03）。</summary>
    EmergencyStopKeyNotConflicting,

    /// <summary>鼠标移动 / 滚轮类命令的执行形态必须为"不适用"（FR-TAP-01）。</summary>
    TapModeNotApplicableForCommand,

    /// <summary>键盘 / 鼠标按键类命令的执行形态必须是"点按"或"按住"（FR-TAP-01）。</summary>
    TapModeRequiredForCommand,

    /// <summary>滚轮格数为正整数且不超过上限（FR-CMD-M3）。</summary>
    ScrollNotchesInRange,

    /// <summary>组合键必须至少有一个修饰键，且主键本身不是修饰键（FR-CMD-K2）。</summary>
    ComboShapeValid,
}
