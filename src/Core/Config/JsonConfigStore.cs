using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Core.Keys;
using Core.Model;
using WpfKey = System.Windows.Input.Key;

namespace Core.Config;

/// <summary>
/// 基于 JSON 文件的配置存储（设计文档 §7，需求 FR-CFG-01~03）。
///
/// 三条硬约束决定了本类的形态：
///
/// 1. **不崩溃**（FR-CFG-02 / AC-21）。用户手改配置文件、磁盘半损坏、
///    别的版本写坏结构——任何一种都只能表现为"提示 + 回退空配置"，
///    绝不允许异常穿透到启动流程。故读写两条路径上的每一处 IO / 解析
///    都被捕获并转化为返回值。
///
/// 2. **原子写**（设计文档 §7.1）。直接 <c>File.WriteAllText</c> 在写入中途
///    断电/崩溃会留下半截 JSON，下次启动就是"配置损坏"——用户全部任务丢失。
///    故先写同目录临时文件，<c>Flush</c> 落盘后再替换目标文件。
///    替换走 <see cref="File.Replace(string,string,string)"/> 或
///    <see cref="File.Move(string,string,bool)"/>，两者在 NTFS 上都是
///    原子性的重命名语义，"目标文件要么是旧的完整内容、要么是新的完整内容"。
///
/// 3. **版本识别**（FR-CFG-03 / AC-21a）。读到版本号后判断能否处理：
///    纯新增字段靠"反序列化默认值补齐"自然兼容，不改版本号；
///    语义变化才升版本，低于最低支持版本或高于当前版本都判为不兼容并明确提示
///    ——**静默按默认值读**会把用户数据悄悄丢掉一部分，比报错更糟。
///
/// 本类不做校验（<see cref="Validation.TaskValidator"/> 的职责）：校验失败的配置
/// 是"能读但非法"，与"读不出来"是两回事；前者由界面在编辑/启动任务时拦截。
/// 这里只保证"结构上能还原成对象"。
/// </summary>
public sealed class JsonConfigStore : IConfigStore
{
    /// <summary>任务配置文件名（设计文档 §7.1）。</summary>
    public const string TasksFileName = "tasks.json";

    /// <summary>全局设置文件名（设计文档 §7.1）。</summary>
    public const string SettingsFileName = "settings.json";

    /// <summary>损坏文件保留后缀（FR-CFG-02 要求"保留损坏文件为 .bak 供排查"）。</summary>
    public const string BackupSuffix = ".bak";

    /// <summary>写盘用的临时文件后缀（与目标同目录，保证替换是同卷原子操作）。</summary>
    private const string TempSuffix = ".tmp";

    /// <summary>
    /// 保留的备份份数上限。每次损坏都新写一个 <c>.bak</c> 会让目录无限膨胀
    /// （NFR-04 要求磁盘占用稳定）；保留最近若干份足以排查，
    /// 更早的损坏现场已无参考价值。
    /// </summary>
    private const int MaxBackups = 3;

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    /// <inheritdoc />
    public string ConfigDirectory { get; }

    /// <summary>
    /// 以指定配置目录构造。默认目录为 <c>%APPDATA%&lt;软件名&gt;</c>（FR-CFG-01）。
    /// 允许显式传入是为了单测能在临时目录里跑，不污染真实配置。
    /// </summary>
    public JsonConfigStore(string? configDirectory = null)
    {
        ConfigDirectory = configDirectory ?? DefaultConfigDirectory();
    }

    /// <summary>软件名——同时是配置目录名与单实例互斥体名的一部分。</summary>
    public const string AppName = "鼠标键盘辅助";

    /// <summary>默认配置目录：<c>%APPDATA%\&lt;软件名&gt;\</c>（FR-CFG-01）。</summary>
    public static string DefaultConfigDirectory()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppName);

    /// <summary>任务配置文件完整路径。</summary>
    public string TasksFilePath => Path.Combine(ConfigDirectory, TasksFileName);

    /// <summary>全局设置文件完整路径。</summary>
    public string SettingsFilePath => Path.Combine(ConfigDirectory, SettingsFileName);

    /// <summary>
    /// 序列化配置。
    ///
    /// <c>WriteIndented</c>：配置文件是给人排查用的（设计文档 §7.1 选 JSON 的理由之一），
    /// 单行 JSON 会让排查变成酷刑。
    /// <c>UnsafeRelaxedJsonEscaping</c>：默认编码器会把中文转成 <c>\uXXXX</c>，
    /// 任务名与提示文案全是中文，转义后文件不可读，与选 JSON 的初衷相悖。
    /// 这是**本地文件**，不涉及 HTML 注入面，放宽转义是安全的。
    /// </summary>
    private static JsonSerializerOptions CreateSerializerOptions() => new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    // ── 读取 ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public ConfigLoadResult<IReadOnlyList<TaskDefinition>> LoadTasks()
    {
        var read = ReadFile(TasksFilePath);
        switch (read.Recovery)
        {
            case ConfigRecovery.FileMissing:
                // 首次启动：不是错误，空任务列表开局。
                return new ConfigLoadResult<IReadOnlyList<TaskDefinition>>(
                    Array.Empty<TaskDefinition>(), ConfigRecovery.FileMissing);

            case ConfigRecovery.Corrupted:
            case ConfigRecovery.Incompatible:
                return new ConfigLoadResult<IReadOnlyList<TaskDefinition>>(
                    Array.Empty<TaskDefinition>(), read.Recovery, read.Message, read.PreservedPath);
        }

        try
        {
            var document = JsonSerializer.Deserialize<TaskConfigDocument>(read.Text!, SerializerOptions);
            if (document is null)
            {
                // "null" 是合法 JSON，但作为配置文件没有任何意义 —— 按损坏处理，
                // 否则会静默变成空配置而用户看不到任何提示（FR-CFG-02 要求提示）。
                return Corrupt<IReadOnlyList<TaskDefinition>>(
                    TasksFilePath, "任务配置文件内容为空。");
            }

            if (CheckVersion(document.SchemaVersion, TasksFilePath, out var incompatibility) is { } fallback)
            {
                return new ConfigLoadResult<IReadOnlyList<TaskDefinition>>(
                    Array.Empty<TaskDefinition>(), fallback.Recovery, fallback.Message, fallback.PreservedPath);
            }

            // 列表里可能混入 null（外部编辑把数组元素写成 null）——
            // 剔掉而不是让它在后续遍历里到处炸。
            var tasks = (document.Tasks ?? new List<TaskDefinition>())
                .Where(t => t is not null)
                .ToList();

            // 逐条任务的命令列表同样可能含 null。
            foreach (var task in tasks)
            {
                task.Commands = (task.Commands ?? new List<CommandDefinition>())
                    .Where(c => c is not null)
                    .ToList();
            }

            return new ConfigLoadResult<IReadOnlyList<TaskDefinition>>(tasks, ConfigRecovery.None);
        }
        catch (JsonException ex)
        {
            return Corrupt<IReadOnlyList<TaskDefinition>>(TasksFilePath, DescribeJsonError(ex));
        }
        catch (NotSupportedException ex)
        {
            // 未知的 $type 判别器（别的版本新增了本版本不认识的命令类型）也会走到这里。
            return Corrupt<IReadOnlyList<TaskDefinition>>(TasksFilePath, DescribeJsonError(ex));
        }
    }

    /// <inheritdoc />
    public ConfigLoadResult<AppSettings> LoadSettings()
    {
        var read = ReadFile(SettingsFilePath);
        switch (read.Recovery)
        {
            case ConfigRecovery.FileMissing:
                // 文件不存在时返回**默认设置** —— 其中含默认紧急停止快捷键，
                // 这正是 AC-36（新装首启即为 Ctrl+Alt+F12）的落地位置。
                return new ConfigLoadResult<AppSettings>(new AppSettings(), ConfigRecovery.FileMissing);

            case ConfigRecovery.Corrupted:
            case ConfigRecovery.Incompatible:
                return new ConfigLoadResult<AppSettings>(
                    new AppSettings(), read.Recovery, read.Message, read.PreservedPath);
        }

        try
        {
            var document = JsonSerializer.Deserialize<SettingsConfigDocument>(read.Text!, SerializerOptions);
            var settings = document?.Settings;

            // 设置文件里 "settings": null 与整体缺失都可能出现；
            // 此时退回默认值（含默认快捷键），而不是给用户一个空设置对象。
            settings ??= new AppSettings();

            if (CheckVersion(document?.SchemaVersion, SettingsFilePath, out _) is { } fallback)
            {
                return new ConfigLoadResult<AppSettings>(
                    new AppSettings(), fallback.Recovery, fallback.Message, fallback.PreservedPath);
            }

            NormalizeSettings(settings);
            return new ConfigLoadResult<AppSettings>(settings, ConfigRecovery.None);
        }
        catch (JsonException ex)
        {
            return Corrupt<AppSettings>(SettingsFilePath, DescribeJsonError(ex));
        }
        catch (NotSupportedException ex)
        {
            return Corrupt<AppSettings>(SettingsFilePath, DescribeJsonError(ex));
        }
    }

    /// <summary>
    /// 补齐设置中的**结构性空值**。
    ///
    /// 反序列化会把 JSON 里显式的 <c>"emergencyStopKey": null</c> 还原成 <c>null</c>，
    /// 而 FR-RUN-02 明确"快捷键必须有默认值（不可为空）"。
    /// 在此补齐是最后一道防线：即使文件被外部改坏成 null，程序仍拿到一个可用的默认快捷键，
    /// 界面也无须为"快捷键为空"这一不可能状态写分支。
    /// </summary>
    private static void NormalizeSettings(AppSettings settings)
    {
        settings.EmergencyStopKey ??=
            KeyBinding.FromCombo(new[] { WpfKey.LeftCtrl, WpfKey.LeftAlt }, WpfKey.F12);

        if (settings.SchemaVersion < ConfigSchema.MinimumSupportedVersion
            || settings.SchemaVersion > ConfigSchema.CurrentVersion)
        {
            settings.SchemaVersion = ConfigSchema.CurrentVersion;
        }
    }

    // ── 写入 ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public ConfigSaveResult SaveTasks(IEnumerable<TaskDefinition> tasks)
    {
        var document = new TaskConfigDocument
        {
            SchemaVersion = ConfigSchema.CurrentVersion,
            Tasks = (tasks ?? Enumerable.Empty<TaskDefinition>())
                .Where(t => t is not null)
                .ToList(),
        };

        return WriteFile(TasksFilePath, document);
    }

    /// <inheritdoc />
    public ConfigSaveResult SaveSettings(AppSettings settings)
    {
        var document = new SettingsConfigDocument
        {
            SchemaVersion = ConfigSchema.CurrentVersion,
            Settings = settings ?? new AppSettings(),
        };

        return WriteFile(SettingsFilePath, document);
    }

    /// <summary>
    /// 原子写：序列化 → 建目录 → 写临时文件 → 落盘 → 替换目标。
    ///
    /// 临时文件与目标**同目录**：跨卷的重命名不是原子操作（甚至直接失败），
    /// 放 <c>%TEMP%</c> 就毁掉了"原子替换"这一前提。
    ///
    /// 失败时清理临时文件：留着会在配置目录里堆垃圾，
    /// 且它的名字带有误导性（看似配置的一部分）。
    /// </summary>
    private ConfigSaveResult WriteFile<T>(string path, T document)
    {
        string? tempPath = null;
        try
        {
            Directory.CreateDirectory(ConfigDirectory);

            var json = JsonSerializer.Serialize(document, SerializerOptions);

            tempPath = path + TempSuffix;

            // FileShare.None：写入期间不让别人读走半截内容。
            using (var stream = new FileStream(
                       tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();

                // Flush(true)：把缓冲区一路刷到磁盘。少了这一步，
                // 系统崩溃时"已写完的临时文件"仍可能只是页缓存里的幻影，
                // 原子替换的前提（临时文件内容完整）就不成立。
                stream.Flush(true);
            }

            ReplaceTarget(tempPath, path);

            // 替换成功后临时文件已不存在（被移动/替换掉了），无需清理。
            tempPath = null;
            return ConfigSaveResult.Ok;
        }
        catch (Exception ex)
        {
            return ConfigSaveResult.Fail(DescribeIoError(ex));
        }
        finally
        {
            if (tempPath is not null)
            {
                TryDeleteFile(tempPath);
            }
        }
    }

    /// <summary>
    /// 用临时文件替换目标文件。
    ///
    /// 优先 <see cref="File.Replace(string,string,string)"/>：它在 NTFS 上是
    /// 单次元数据事务，语义最干净。但它要求目标文件**已存在**，
    /// 故首次保存走 <see cref="File.Move(string,string,bool)"/> 的覆盖式移动
    /// （同样是重命名语义）。两条路径都是原子的"目标要么旧、要么新"。
    /// </summary>
    private static void ReplaceTarget(string tempPath, string targetPath)
    {
        if (File.Exists(targetPath))
        {
            File.Replace(tempPath, targetPath, destinationBackupFileName: null);
            return;
        }

        File.Move(tempPath, targetPath, overwrite: true);
    }

    // ── 文件读取与损坏处理 ────────────────────────────────────────────

    /// <summary>文件读取结果（内部用；损坏/缺失已转化为结论而非异常）。</summary>
    private sealed record FileReadOutcome(
        string? Text,
        ConfigRecovery Recovery,
        string? Message = null,
        string? PreservedPath = null);

    /// <summary>
    /// 读文件并做编码/IO 层面的健壮化处理。
    ///
    /// 关键取舍：**IO 异常不当成"损坏"**。文件被别的进程独占（用户开着编辑器保存中）
    /// 与文件内容烂掉是两种处境——前者原文件是好的，把它改名成 <c>.bak</c>
    /// 等于把用户完好的配置挪走，反而制造了问题。
    /// 故 IO 失败只回退空配置并提示，**不动原文件**。
    /// </summary>
    private FileReadOutcome ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new FileReadOutcome(null, ConfigRecovery.FileMissing);
            }

            var text = File.ReadAllText(path);

            // 空文件（0 字节）按损坏处理并保留：它多半是"写到一半被拔电"的产物，
            // 静默当成空配置会让用户以为任务凭空消失。
            if (string.IsNullOrWhiteSpace(text))
            {
                var preserved = PreserveCorrupted(path);
                return new FileReadOutcome(
                    null,
                    ConfigRecovery.Corrupted,
                    $"配置文件 {Path.GetFileName(path)} 内容为空，已按默认配置启动。"
                    + (preserved is null ? string.Empty : $"原文件已保留为 {Path.GetFileName(preserved)}。"),
                    preserved);
            }

            return new FileReadOutcome(text, ConfigRecovery.None);
        }
        catch (Exception ex)
        {
            // 读不动（权限 / 被占用 / 设备错误）——**保留原文件**，仅回退。
            return new FileReadOutcome(
                null,
                ConfigRecovery.Corrupted,
                $"配置文件 {Path.GetFileName(path)} 无法读取（{ex.Message}），已按默认配置启动；原文件未改动。");
        }
    }

    /// <summary>
    /// 版本检查。<c>null</c> 表示可继续处理；
    /// 非 null 表示不兼容，返回值即"已保留原文件"的回退结论。
    /// </summary>
    private FileReadOutcome? CheckVersion(
        int? schemaVersion,
        string path,
        out FileReadOutcome? fallback)
    {
        var version = schemaVersion ?? ConfigSchema.AssumedVersionWhenMissing;

        if (version >= ConfigSchema.MinimumSupportedVersion && version <= ConfigSchema.CurrentVersion)
        {
            fallback = null;
            return null;
        }

        var preserved = PreserveCorrupted(path);
        var reason = version > ConfigSchema.CurrentVersion
            ? $"配置文件 {Path.GetFileName(path)} 来自更新版本的软件（版本 {version}，本程序支持至 {ConfigSchema.CurrentVersion}）"
            : $"配置文件 {Path.GetFileName(path)} 版本过旧（版本 {version}，本程序最低支持 {ConfigSchema.MinimumSupportedVersion}）";

        var outcome = new FileReadOutcome(
            null,
            ConfigRecovery.Incompatible,
            $"{reason}，无法读取，已按默认配置启动。"
            + (preserved is null ? string.Empty : $"原文件已保留为 {Path.GetFileName(preserved)}。"),
            preserved);

        fallback = outcome;
        return outcome;
    }

    /// <summary>构造"损坏"回退结果，并把原文件保留为 <c>.bak</c>（FR-CFG-02）。</summary>
    private ConfigLoadResult<T> Corrupt<T>(string path, string reason)
    {
        var preserved = PreserveCorrupted(path);
        var message = $"{reason} 已按默认配置启动。"
            + (preserved is null ? string.Empty : $"原文件已保留为 {Path.GetFileName(preserved)} 供排查。");

        return new ConfigLoadResult<T>(
            DefaultValueFor<T>(),
            ConfigRecovery.Corrupted,
            message,
            preserved);
    }

    /// <summary>
    /// 损坏回退时的"默认值"。
    ///
    /// 不能用 <c>default!</c> 一概了事：T 为 <see cref="IReadOnlyList{T}"/> 时它给出的是
    /// <c>null</c>，而调用方拿到的是"一个任务列表"——后续 <c>Assert.Empty</c> / 遍历一碰就炸。
    /// 任务列表的损坏回退应当是**空列表**（与 FileMissing 分支一致），不是 null。
    /// 其余类型（如 <see cref="AppSettings"/>）的 default 就是可用的默认实例，沿用即可。
    /// </summary>
    private static T DefaultValueFor<T>()
    {
        if (typeof(T) == typeof(IReadOnlyList<TaskDefinition>))
        {
            return (T)(object)Array.Empty<TaskDefinition>();
        }

        return default!;
    }

    /// <summary>
    /// 把损坏文件改名保留（FR-CFG-02 明确"保留损坏文件为 <c>.bak</c> 供排查"）。
    ///
    /// 用**改名**而非复制：改名是原子的且不占额外空间；
    /// 复制会留下一份"看起来还正常的"原文件，下次启动又读它、又报损坏，循环不止。
    /// 改名后目标路径空缺，下次保存自然会写入完好内容，报错不会重复出现。
    ///
    /// 保留失败（文件被占用）不是致命问题：用户损失的是"排查材料"，
    /// 而**回退本身**必须继续——故此处吞掉异常并返回 <c>null</c>。
    /// </summary>
    private string? PreserveCorrupted(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var stamped = path + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + BackupSuffix;
            File.Move(path, stamped, overwrite: true);
            PruneBackups(path);
            return stamped;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 清理过量的备份文件，只保留最近的 <see cref="MaxBackups"/> 份（NFR-04）。
    /// 按写入时间倒序保留，删除最旧的。
    /// </summary>
    private void PruneBackups(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory))
            {
                return;
            }

            var pattern = Path.GetFileName(path) + "*" + BackupSuffix;
            var backups = Directory.GetFiles(directory, pattern)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Skip(MaxBackups)
                .ToList();

            foreach (var backup in backups)
            {
                TryDeleteFile(backup);
            }
        }
        catch (Exception)
        {
            // 清理是**尽力而为**：删不掉旧备份只影响磁盘占用，不影响功能，
            // 在这里让整个加载流程失败是本末倒置。
        }
    }

    /// <summary>删除文件，失败即忽略（调用方无需知道清理是否成功）。</summary>
    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 清理失败不改变主流程结论。
        }
    }

    // ── 错误文案 ─────────────────────────────────────────────────────

    /// <summary>
    /// 把 JSON 解析异常转成面向用户的中文说明。
    ///
    /// 带上行号/字节位置：用户手改配置出错时，这条位置信息是唯一能直接定位问题的线索，
    /// 只说"格式错误"等于什么都没说。
    /// </summary>
    private static string DescribeJsonError(JsonException ex)
    {
        var location = ex.LineNumber is { } line
            ? $"（第 {line + 1} 行）"
            : string.Empty;

        return $"配置文件格式不正确{location}：{ex.Message}";
    }

    /// <summary>
    /// <see cref="NotSupportedException"/> 的说明。
    ///
    /// 走到这里的最典型场景是**未知的命令类型判别器**（<c>$type</c> / <c>kind</c>
    /// 取到了本版本不认识的值）——那是"来自更新版本的配置"，
    /// 靠版本号未必能拦住（同版本内不同分支写出的文件），故单独给一条可读提示。
    /// </summary>
    private static string DescribeJsonError(NotSupportedException ex)
        => $"配置文件中存在本程序无法识别的类型：{ex.Message}";

    /// <summary>IO 异常的中文说明（保存失败提示用）。</summary>
    private static string DescribeIoError(Exception ex)
        => $"配置保存失败：{ex.Message}";
}
