using System.Text.Json.Serialization;
using Core.Keys;
using WpfKey = System.Windows.Input.Key;

namespace Core.Config;

/// <summary>
/// 全局设置（设计文档 §7.1 的 <c>settings.json</c>）。
///
/// 与任务配置（<c>tasks.json</c>）**分文件存放**的原因见设计文档 §7.1：
/// 一处损坏不应拖垮全部——设置丢了顶多是"快捷键回默认值"，
/// 任务丢了则是用户全部劳动成果，两者损坏代价相差极大，不该共命运。
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// 紧急停止快捷键（需求 FR-RUN-01/02）。
    ///
    /// **必须有默认值且不为空**（FR-RUN-02 / AC-36）：新装无配置文件时首次启动，
    /// 该键即应为 <c>Ctrl+Alt+F12</c>，无需用户手动设置。
    /// 故这里给的是实打实的默认值而不是 <c>null</c>——"空值 + 界面兜底"
    /// 会让 AC-36 取决于界面实现，而它本该是配置层就成立的事实。
    ///
    /// 用 <c>Ctrl+Alt+F12</c> 作默认的理由：含两个修饰键，与绝大多数软件的
    /// 快捷键（Ctrl+单键）不重叠；F12 在多数程序里无默认行为（F5/F11 等
    /// 常被开发工具占用，Ctrl+Alt+Del 是系统保留键更不可用）。
    /// </summary>
    public KeyBinding EmergencyStopKey { get; set; }
        = KeyBinding.FromCombo(new[] { WpfKey.LeftCtrl, WpfKey.LeftAlt }, WpfKey.F12);

    /// <summary>
    /// 配置结构版本号（需求 FR-CFG-03：升级后读旧配置）。
    ///
    /// 与 <see cref="ConfigSchema.CurrentVersion"/> 比对：
    /// 低于当前值 → 走迁移/补齐路径；高于当前值 → 明确提示不兼容
    /// （旧程序读新文件时字段语义可能已变，静默按默认值补齐比报错更危险）。
    /// </summary>
    public int SchemaVersion { get; set; } = ConfigSchema.CurrentVersion;
}

/// <summary>
/// 配置结构版本号的定义（单一真相点，供 <c>tasks.json</c> 与 <c>settings.json</c> 共用）。
/// </summary>
public static class ConfigSchema
{
    /// <summary>
    /// 当前配置结构版本。
    ///
    /// 变更口径：**只要字段语义变化（而不仅是增删字段）就必须 +1**——
    /// 纯新增字段靠"默认值补齐"即可兼容，不必改版本号；
    /// 而"同一个字段换了个意思"（如频率单位从 Hz 改成 ms）无法靠补齐识别，
    /// 必须靠版本号拦截。
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// 本程序能读的最低版本。低于此值的配置无法迁移（结构差异过大），
    /// 走"不兼容"处理路径：提示用户 + 保留原文件 + 以默认配置启动。
    /// </summary>
    public const int MinimumSupportedVersion = 1;

    /// <summary>
    /// 缺失版本号时假定的版本（早期文件可能没写这个字段）。
    /// 取 <see cref="CurrentVersion"/>：缺版本号说明文件是本程序写的旧格式，
    /// 其结构就是当时的当前结构，按当前版本读最合理。
    /// </summary>
    public const int AssumedVersionWhenMissing = CurrentVersion;
}

/// <summary>
/// 配置回退/兼容的处理结论（诊断用；不参与序列化）。
/// </summary>
public enum ConfigRecovery
{
    /// <summary>正常读取，无需任何处理。</summary>
    None = 0,

    /// <summary>文件不存在（首次启动），使用默认配置。<b>不是错误</b>。</summary>
    FileMissing = 1,

    /// <summary>文件损坏（解析失败 / 结构不符），已保留为 <c>.bak</c> 并回退到默认值（FR-CFG-02）。</summary>
    Corrupted = 2,

    /// <summary>版本不兼容（过高或过低无法迁移），已保留原文件并回退到默认值（FR-CFG-03）。</summary>
    Incompatible = 3,
}

/// <summary>
/// 配置读取结果：数据 + 是否需要向用户提示。
///
/// 读取**永不抛异常**（FR-CFG-02 要求不崩溃），故"失败"必须作为一种**返回值**存在，
/// 由界面决定怎么提示。<see cref="Message"/> 非空即表示发生了需要告知用户的事情。
/// </summary>
/// <typeparam name="T">配置数据类型。</typeparam>
/// <param name="Value">读到的配置；回退时为默认值（非 null）。</param>
/// <param name="Recovery">本次读取的处理结论。</param>
/// <param name="Message">面向用户的提示；无需提示时为 <c>null</c>。</param>
/// <param name="PreservedPath">损坏/不兼容时保留的原文件路径；无保留时为 <c>null</c>。</param>
public sealed record ConfigLoadResult<T>(
    T Value,
    ConfigRecovery Recovery,
    string? Message = null,
    string? PreservedPath = null)
{
    /// <summary>是否发生了需要用户知晓的异常情况（界面据此弹提示）。</summary>
    public bool HasNotice => !string.IsNullOrWhiteSpace(Message);
}

/// <summary>
/// 配置写入结果。
/// </summary>
/// <param name="Succeeded">是否写盘成功。</param>
/// <param name="Message">失败原因（面向用户）；成功时为 <c>null</c>。</param>
/// <param name="Value">
/// 实际落盘的那一份数据（成功时才有意义）。
///
/// <para>
/// <b>为什么要有它</b>：入参可能是 <c>null</c>，而存储层会把它换成一份默认设置再落盘——
/// 此时"调用方手上的东西"与"磁盘上的东西"是两份不同的对象。调用方（门面）的内存态
/// 必须以**落盘的那一份**为准，否则内存与磁盘当场分家：界面改完设置后读到的仍是旧值，
/// 紧急停止键表现为"改了要重启才生效"（设计文档 §7.1）。
/// </para>
/// <para>
/// 失败时为 <c>null</c>——没落盘就没有"落盘的那一份"。
/// </para>
/// </param>
public sealed record ConfigSaveResult(bool Succeeded, string? Message = null, AppSettings? Value = null)
{
    /// <summary>
    /// 写入成功。<paramref name="value"/> 是**实际落盘的那一份**设置；
    /// 不落设置的写入路径（如仅写任务）传 <c>null</c>——没落盘就没有那一份。
    /// </summary>
    public static ConfigSaveResult Ok(AppSettings? value = null) => new(true, null, value);

    /// <summary>构造一个失败结果。</summary>
    public static ConfigSaveResult Fail(string message) => new(false, message);
}
