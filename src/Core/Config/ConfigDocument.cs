using System.Text.Json.Serialization;
using Core.Model;

namespace Core.Config;

/// <summary>
/// <c>tasks.json</c> 的顶层文档（设计文档 §7.1）。
///
/// 顶层不是裸数组而是"版本号 + 数组"的对象：
/// 版本号必须能独立于内容被读到，否则 FR-CFG-03（升级后识别旧配置）
/// 无从判断——裸数组里塞一个版本号元素既别扭又会被当成一条任务读进来。
/// </summary>
public sealed class TaskConfigDocument
{
    /// <summary>配置结构版本（见 <see cref="ConfigSchema"/>）。</summary>
    public int SchemaVersion { get; set; } = ConfigSchema.CurrentVersion;

    /// <summary>全部任务。</summary>
    public List<TaskDefinition> Tasks { get; set; } = new();
}

/// <summary>
/// <c>settings.json</c> 的顶层文档。
///
/// 与 <see cref="TaskConfigDocument"/> 同构（版本号 + 载荷），
/// 好处是两份文件的版本识别逻辑完全一致，不必各写一套。
/// 这里不走泛型容器是为了让 JSON 形态显式可读——
/// 配置文件是给人排查用的（设计文档 §7.1 选 JSON 的理由），
/// 多一层泛型包装会让文件里多出一个没有语义的层级。
/// </summary>
public sealed class SettingsConfigDocument
{
    /// <summary>配置结构版本（见 <see cref="ConfigSchema"/>）。</summary>
    public int SchemaVersion { get; set; } = ConfigSchema.CurrentVersion;

    /// <summary>全局设置。</summary>
    [JsonPropertyName("settings")]
    public AppSettings Settings { get; set; } = new();
}
