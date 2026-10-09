using System.Collections.Generic;
using System.IO;
using System.Text;
using Core.Config;

namespace Core.Diagnostics;

/// <summary>
/// 按日切分的文件日志器（设计文档 §7.3、需求 FR-CFG-05 / AC-37）。
///
/// <para><b>为什么落盘而不是只写 Debug 输出</b>：错误事件（注入失败、配置读写失败、
/// 钩子异常）全发生在用户看不见的后台线程上，且往往是"任务某条命令不动了"这类
/// 无法复现的现象的唯一线索。<c>Debug.WriteLine</c> 在发布构建里根本不存在，
/// 等于把线索丢进黑洞——§7.3 要求落盘正是为此。</para>
///
/// <para><b>滚动口径</b>（§7.3「滚动上限」）：文件名带日期（<c>app-yyyyMMdd.log</c>），
/// 天然按日切分；每次首次写入时顺带清理超过 <see cref="DefaultKeepDays"/> 天的旧文件。
/// 选"按日期"而非"按大小"：单日日志量极小（错误事件已被
/// <see cref="Input.InjectionFailureLimiter"/> 按分钟聚合过），按日期切分的文件
/// 对用户排查更友好——"昨天下午出的问题"直接对应一个文件名。</para>
///
/// <para><b>为什么不需要后台队列</b>：§7.3 的"不得在钩子回调路径上写日志"由**调用方**
/// 保证（本类的调用点全部在调度线程 / UI 线程 / 停止路径上，钩子回调与消息泵线程
/// 一律不接）。这里不再自建队列——多一层队列会引入"进程退出时日志还在队列里没落盘"
/// 的新问题，而 AC-37 恰恰要求退出前那次保存失败必须已在文件里。同步写 + 单次
/// <c>File.AppendText</c> 的量级（每条一行）在任何调用点上都不构成阻塞风险。</para>
///
/// <para><b>失败即静默</b>：目录不可写、磁盘满、文件被独占——一律吞掉。
/// 日志器抛异常会把调度器与注入路径拖垮，代价远高于丢几条日志。唯一的副作用是
/// 内部记一笔失联状态，避免每次调用都重复尝试失败的 IO（见 <see cref="_ioFailed"/>）。</para>
/// </summary>
public sealed class AppLogger : ILogSink
{
    /// <summary>日志子目录名（§7.3：配置目录下的 <c>logs\</c>）。</summary>
    public const string LogDirectoryName = "logs";

    /// <summary>日志文件名前缀。</summary>
    public const string FilePrefix = "app-";

    /// <summary>日志文件名后缀。</summary>
    public const string FileSuffix = ".log";

    /// <summary>默认保留天数（§7.3 的「如保留最近 N 天」，取 7）。</summary>
    public const int DefaultKeepDays = 7;

    private readonly string _logDirectory;
    private readonly int _keepDays;
    private readonly Func<DateTime> _clock;
    private readonly Func<IEnumerable<string>> _enumerateLogFiles;
    private readonly object _gate = new();

    /// <summary>
    /// IO 已失效的印记。一旦目录建不出来或写文件连续失败，后续调用直接返回，
    /// 不再每次都去撞一次必然失败的磁盘操作——那会让日志器的存在本身变成负担。
    /// </summary>
    private bool _ioFailed;

    /// <summary>已清理过旧文件的那个日期（<c>null</c> = 本次进程尚未清理）。</summary>
    private string? _prunedForDate;

    /// <summary>
    /// 构造日志器。
    /// </summary>
    /// <param name="logDirectory">
    /// 日志目录。默认 <c>配置目录\logs</c>——与配置文件同处一个目录树下（§7.3），
    /// 用户"打开配置目录"就能看到日志。显式传入是为了单测在临时目录里跑。
    /// </param>
    /// <param name="keepDays">保留最近若干天的日志文件，超出的在写入时清理。</param>
    /// <param name="clock">本地时间源（单测注入固定时间以验证切分与清理）。</param>
    /// <param name="enumerateLogFiles">
    /// 枚举日志目录下候选文件的委托。**抽出来只为可测性**：清理失败域是 §7.3.3 的
    /// 一条明规，而枚举失败这条真实路径依赖文件系统状态，正常环境里造不出来。
    /// 传 <c>null</c> 即默认实现（<c>Directory.EnumerateFiles</c>）。
    /// </param>
    public AppLogger(
        string? logDirectory = null,
        int keepDays = DefaultKeepDays,
        Func<DateTime>? clock = null,
        Func<IEnumerable<string>>? enumerateLogFiles = null)
    {
        _logDirectory = logDirectory ?? DefaultLogDirectory();
        _keepDays = keepDays < 1 ? 1 : keepDays;
        _clock = clock ?? (() => DateTime.Now);
        _enumerateLogFiles = enumerateLogFiles
            ?? (() => Directory.EnumerateFiles(_logDirectory, $"{FilePrefix}*{FileSuffix}"));
    }

    /// <summary>
    /// 默认日志目录：<c>%APPDATA%\&lt;软件名&gt;\logs\</c>（§7.3）。
    ///
    /// <para>
    /// 复用 <see cref="JsonConfigStore.DefaultConfigDirectory"/> 而非另拼一次路径：
    /// 配置目录若因产品改名而迁移，日志目录跟着走才是一致的；两处各拼一次必然漂移。
    /// </para>
    /// </summary>
    public static string DefaultLogDirectory()
        => Path.Combine(JsonConfigStore.DefaultConfigDirectory(), LogDirectoryName);

    /// <summary>本日志器实际使用的目录（供单测与诊断）。</summary>
    public string LogDirectory => _logDirectory;

    /// <summary>当天日志文件的完整路径。</summary>
    public string CurrentFilePath => Path.Combine(_logDirectory, FileNameFor(_clock()));

    /// <inheritdoc/>
    public void Info(string message) => Write(LogLevel.Info, message, null);

    /// <inheritdoc/>
    public void Error(string message, Exception? exception = null)
        => Write(LogLevel.Error, message, exception);

    /// <summary>
    /// 写一条日志。**本方法是唯一的落盘入口**，也是本类唯一会碰 IO 的地方。
    /// </summary>
    private void Write(LogLevel level, string message, Exception? exception)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var now = _clock();
        var line = Format(now, level, message, exception);

        lock (_gate)
        {
            if (_ioFailed)
            {
                return;
            }

            try
            {
                if (!EnsureDirectory())
                {
                    _ioFailed = true;
                    return;
                }

                var fileName = FileNameFor(now);

                // 首次为某个日期写入时清理过期文件。放在写入前：清理失败不影响本次写入，
                // 且"清理过"的印记按日期记，跨天时自然再清一次（进程长期驻留的场景）。
                // 清理自带 try：它的失败域与写入的失败域**必须分开**——否则一次清理异常
                // 会落到外层 catch 置 _ioFailed，把本次这条日志连同此后全部日志一起废掉，
                // 与"清理失败不影响本次写入"这句承诺正相反。
                if (!string.Equals(_prunedForDate, fileName, StringComparison.Ordinal))
                {
                    _prunedForDate = fileName;
                    try
                    {
                        PruneOldFiles(now);
                    }
                    catch (Exception)
                    {
                        // 清理是尽力而为：删不掉旧文件不是故障，只是目录里多留几天。
                    }
                }

                File.AppendAllText(
                    Path.Combine(_logDirectory, fileName),
                    line,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch (Exception)
            {
                // §7.3 未规定"日志写失败"的处理，取最保守的：静默。
                // 理由见类注释——日志器抛异常拖垮调度器/注入路径的代价远高于丢日志。
                _ioFailed = true;
            }
        }
    }

    /// <summary>把一条记录格式化成一行：<c>yyyy-MM-dd HH:mm:ss.fff [级别] 正文</c>。</summary>
    private static string Format(DateTime now, LogLevel level, string message, Exception? exception)
    {
        var builder = new StringBuilder(message.Length + 64);
        builder.Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        builder.Append(" [");
        builder.Append(level == LogLevel.Error ? "错误" : "信息");
        builder.Append("] ");
        builder.Append(message);

        if (exception is not null)
        {
            builder.AppendLine();
            builder.Append("    ");
            builder.Append(exception);
        }

        builder.AppendLine();
        return builder.ToString();
    }

    /// <summary>某时刻对应的日志文件名。</summary>
    private static string FileNameFor(DateTime moment)
        => $"{FilePrefix}{moment:yyyyMMdd}{FileSuffix}";

    /// <summary>确保日志目录存在。返回值表示目录可用。</summary>
    private bool EnsureDirectory()
    {
        if (Directory.Exists(_logDirectory))
        {
            return true;
        }

        Directory.CreateDirectory(_logDirectory);
        return true;
    }

    /// <summary>
    /// 清理超过保留期的日志文件（§7.3「滚动上限」）。
    ///
    /// <para>
    /// 以**文件名里的日期**判定而非文件系统时间戳：日期是写入时定的，时间戳可以被
    /// 复制/同步/外部工具改动。解析不出日期的文件（用户自己丢进来的）一律不动——
    /// 那是用户的东西，不该由日志器删。
    /// </para>
    /// </summary>
    private void PruneOldFiles(DateTime now)
    {
        var cutoff = now.Date.AddDays(-(_keepDays - 1));

        foreach (var path in _enumerateLogFiles())
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.Length <= FilePrefix.Length)
            {
                continue;
            }

            var stamp = name[FilePrefix.Length..];
            if (stamp.Length != 8
                || !DateTime.TryParseExact(
                    stamp, "yyyyMMdd", null,
                    System.Globalization.DateTimeStyles.None, out var date))
            {
                continue;
            }

            if (date.Date < cutoff)
            {
                TryDelete(path);
            }
        }
    }

    /// <summary>删一个过期日志文件，失败吞掉（可能正被别的进程/工具占用）。</summary>
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // 删不掉就留着——比抛异常好，下次写入还会再试。
        }
    }
}
