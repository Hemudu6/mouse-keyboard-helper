using Core.Model;

namespace Core.Config;

/// <summary>
/// 配置存储契约（设计文档 §7；需求 FR-CFG-01~03）。
///
/// 抽成接口是为了让 Engine / UI 层能脱离真实文件系统被测试与替换：
/// 界面要在"保存失败"时弹提示（AC-37 要求日志里也有对应条目），
/// 若直接依赖静态文件读写，"保存失败"这条路径就只能靠手工制造磁盘故障来验。
/// </summary>
public interface IConfigStore
{
    /// <summary>配置根目录（<c>%APPDATA%&lt;软件名&gt;</c>，FR-CFG-01）。</summary>
    string ConfigDirectory { get; }

    /// <summary>
    /// 读取任务配置。<b>永不抛异常</b>（FR-CFG-02）：失败时回退到空配置，
    /// 并通过 <see cref="ConfigLoadResult{T}.Message"/> 告知调用方需要提示用户。
    /// </summary>
    ConfigLoadResult<IReadOnlyList<TaskDefinition>> LoadTasks();

    /// <summary>
    /// 读取全局设置。文件不存在时返回**默认设置**（含默认紧急停止快捷键，AC-36）。
    /// </summary>
    ConfigLoadResult<AppSettings> LoadSettings();

    /// <summary>
    /// 保存任务配置（原子写：临时文件 + 替换，设计文档 §7.1）。
    /// </summary>
    ConfigSaveResult SaveTasks(IEnumerable<TaskDefinition> tasks);

    /// <summary>保存全局设置（原子写）。</summary>
    ConfigSaveResult SaveSettings(AppSettings settings);
}
