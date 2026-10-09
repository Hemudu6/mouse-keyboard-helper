using System.IO;
using System.Text;
using Core.Diagnostics;
using Xunit;

namespace Host.Tests;

/// <summary>
/// 日志器测试（需求 FR-CFG-05 / 验收项 AC-37；设计文档 §7.3）。
///
/// <para>
/// 与配置存储测试同样的理由：日志器真碰文件系统，全部用例在**临时目录**里跑，
/// 绝不打用户真实的 <c>%APPDATA%</c>。
/// </para>
///
/// <para>
/// <b>时间源可注入</b>是本测试集能成立的前提：按日切分与过期清理若只能读
/// <see cref="DateTime.Now"/>，就只能靠"改系统时间"来测——那是无法自动化的。
/// </para>
/// </summary>
public sealed class AppLoggerTests : IDisposable
{
    private readonly string _dir;

    /// <summary>可拨动的假时钟（默认取一个固定的、与真实时间无关的时刻）。</summary>
    private DateTime _now = new(2026, 10, 9, 14, 30, 0, DateTimeKind.Local);

    public AppLoggerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "KbmLogTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (Exception)
        {
            // 清理失败不影响测试结论。
        }
    }

    /// <summary>造一个目录还没建出来的日志器（验证"首次写入自建目录"）。</summary>
    private AppLogger NewLogger(int keepDays = AppLogger.DefaultKeepDays)
    {
        var logDir = Path.Combine(_dir, "logs");
        return new AppLogger(logDir, keepDays, () => _now);
    }

    private string ReadAll(string? logDir = null)
    {
        var path = Path.Combine(logDir ?? Path.Combine(_dir, "logs"), CurrentFileName());
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    private string CurrentFileName() => $"{AppLogger.FilePrefix}{_now:yyyyMMdd}{AppLogger.FileSuffix}";

    /// <summary>
    /// 把文件内容拆成“非空”行。
    ///
    /// <para>
    /// 必须按<see cref="Environment.NewLine"/>拆：本工程的磁盘文本是 <b>CRLF</b>
    /// （仓库工作树的换行口径，全仓一致，其他测试亦同一假设）。
    /// 只按 <c>'\n'</c> 拆会在每行尾留下一个 <c>'\r'</c>，
    /// 表现为“正则在行尾锚不上”、“EndsWith 差最后一个字符不匹配”这类假失败。
    /// </para>
    /// </summary>
    private static string[] Lines(string text)
        => text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    // ── 落盘 ───────────────────────────────────────────────────────

    [Fact]
    public void 首次写入会自建目录与当天文件()
    {
        var logger = NewLogger();
        var logDir = Path.Combine(_dir, "logs");

        Assert.False(Directory.Exists(logDir));

        logger.Info("任务启动：测试任务");

        Assert.True(Directory.Exists(logDir));
        Assert.True(File.Exists(Path.Combine(logDir, CurrentFileName())));
    }

    [Fact]
    public void 文件名按日切分()
    {
        var logger = NewLogger();

        logger.Info("第一天");
        var first = CurrentFileName();

        _now = _now.AddDays(1);
        logger.Info("第二天");
        var second = CurrentFileName();

        _now = _now.AddDays(1);
        logger.Info("第三天");
        var third = CurrentFileName();

        Assert.NotEqual(first, second);
        Assert.NotEqual(second, third);

        var files = Directory.GetFiles(Path.Combine(_dir, "logs"), "*.log");
        Assert.Equal(3, files.Length);

        // 各日文件只含当日那条——切分若是"只换名字不换文件"，这里会串。
        Assert.Contains("第一天", File.ReadAllText(Path.Combine(_dir, "logs", first)));
        Assert.DoesNotContain("第二天", File.ReadAllText(Path.Combine(_dir, "logs", first)));
    }

    [Fact]
    public void 行格式含时间戳级别与正文()
    {
        var logger = NewLogger();

        logger.Info("任务启动：测试任务");

        var line = Assert.Single(Lines(ReadAll()));
        Assert.StartsWith("2026-10-09 14:30:00.000 [信息] ", line);
        Assert.EndsWith("任务启动：测试任务", line);
    }

    [Fact]
    public void 错误级别可区分且携带异常栈()
    {
        var logger = NewLogger();

        logger.Info("正常一条");
        logger.Error("注入失败", new InvalidOperationException("底层原因"));

        var lines = Lines(ReadAll());
        Assert.Equal(3, lines.Length);

        Assert.Contains("[信息]", lines[0]);
        Assert.Contains("[错误]", lines[1]);
        Assert.Contains("注入失败", lines[1]);

        // 异常对象是原样交出来的，栈由落地实现展开——消息与类型都要在。
        Assert.Contains("底层原因", lines[2]);
        Assert.Contains(nameof(InvalidOperationException), lines[2]);
    }

    [Fact]
    public void 异常可省略()
    {
        var logger = NewLogger();

        logger.Error("只有消息");

        var line = Assert.Single(Lines(ReadAll()));
        Assert.Contains("[错误]", line);
        Assert.Contains("只有消息", line);
    }

    [Fact]
    public void 空消息被忽略且不建文件()
    {
        var logger = NewLogger();

        logger.Info("   ");
        logger.Error(string.Empty);

        Assert.False(File.Exists(Path.Combine(_dir, "logs", CurrentFileName())));
    }

    [Fact]
    public void 追加而非覆盖()
    {
        var logger = NewLogger();

        logger.Info("第一条");
        logger.Info("第二条");

        var lines = Lines(ReadAll());
        Assert.Equal(2, lines.Length);
        Assert.Contains("第一条", lines[0]);
        Assert.Contains("第二条", lines[1]);
    }

    // ── 滚动清理 ───────────────────────────────────────────────────

    [Fact]
    public void 超过保留期的旧文件被清理()
    {
        var logDir = Path.Combine(_dir, "logs");
        Directory.CreateDirectory(logDir);

        // 预置 5 个旧文件：保留期 3 天，则只剩今天 + 前两天，更早的三个被删。
        for (var back = 1; back <= 5; back++)
        {
            var stamp = _now.AddDays(-back).ToString("yyyyMMdd");
            File.WriteAllText(Path.Combine(logDir, $"{AppLogger.FilePrefix}{stamp}.log"), "旧内容");
        }

        var logger = new AppLogger(logDir, keepDays: 3, () => _now);
        logger.Info("今天");

        Assert.True(File.Exists(Path.Combine(logDir, CurrentFileName())));
        Assert.True(File.Exists(Path.Combine(logDir, $"{AppLogger.FilePrefix}{_now.AddDays(-2):yyyyMMdd}.log")));
        Assert.False(File.Exists(Path.Combine(logDir, $"{AppLogger.FilePrefix}{_now.AddDays(-3):yyyyMMdd}.log")));
        Assert.False(File.Exists(Path.Combine(logDir, $"{AppLogger.FilePrefix}{_now.AddDays(-5):yyyyMMdd}.log")));
    }

    [Fact]
    public void 保留期内的文件不动()
    {
        var logDir = Path.Combine(_dir, "logs");
        Directory.CreateDirectory(logDir);
        var keepName = $"{AppLogger.FilePrefix}{_now.AddDays(-6):yyyyMMdd}{AppLogger.FileSuffix}";
        File.WriteAllText(Path.Combine(logDir, keepName), "应保留");

        var logger = new AppLogger(logDir, keepDays: 7, () => _now);
        logger.Info("今天");

        Assert.True(File.Exists(Path.Combine(logDir, keepName)));
    }

    [Fact]
    public void 不删用户自己放的文件()
    {
        var logDir = Path.Combine(_dir, "logs");
        Directory.CreateDirectory(logDir);

        // 两类"不是本日志器产的"文件：名字认不出日期、以及根本不符合命名规则。
        var noStamp = Path.Combine(logDir, $"{AppLogger.FilePrefix}备份{AppLogger.FileSuffix}");
        var foreign = Path.Combine(logDir, "我的笔记.txt");
        File.WriteAllText(noStamp, "手工改名的");
        File.WriteAllText(foreign, "无关文件");

        var logger = new AppLogger(logDir, keepDays: 1, () => _now);
        logger.Info("今天");

        Assert.True(File.Exists(noStamp));
        Assert.True(File.Exists(foreign));
    }

    [Fact]
    public void 跨天时再次清理()
    {
        var logDir = Path.Combine(_dir, "logs");
        Directory.CreateDirectory(logDir);

        var logger = new AppLogger(logDir, keepDays: 1, () => _now);
        logger.Info("第一天");

        // 进程长期驻留：跨天后应再清一次，把"当时还没过期"的文件清掉。
        _now = _now.AddDays(10);
        var stale = Path.Combine(logDir, $"{AppLogger.FilePrefix}{_now.AddDays(-1):yyyyMMdd}.log");
        File.WriteAllText(stale, "第一天那份");

        logger.Info("第十一天");

        Assert.False(File.Exists(stale));
    }

    // ── 目录不可用时不拖垮调用方 ───────────────────────────────────

    [Fact]
    public void 目录建不出来时不抛异常()
    {
        // 拿一个**文件**当目录用——CreateDirectory 必然失败。
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "占位");

        var logger = new AppLogger(blocker, AppLogger.DefaultKeepDays, () => _now);

        // 关键断言是"不抛"：日志器坏掉不得把注入热路径或调度器拖垮。
        logger.Info("写不进去");
        logger.Error("也写不进去", new InvalidOperationException("x"));
    }

    /// <summary>
    /// 清理阶段的异常**不得**废掉本次写入，也不得短路此后全部写入。
    ///
    /// <para>
    /// 这是"清理失败不影响本次写入"这句承诺的直接回归：曾经的实现把
    /// <c>PruneOldFiles</c> 放在与写入同一个 try 里，一次清理异常会落到外层 catch
    /// 置 <c>_ioFailed</c>——当次这条日志连同此后**整个进程**的日志一起没了。
    /// </para>
    /// <para>
    /// 逼出清理异常的手法：注入一个必抛的枚举委托。直接往日志目录里塞"同名目录"是
    /// **无效**的——<c>Directory.EnumerateFiles</c> 只返回文件、从不返回目录，清理根本
    /// 碰不到它，异常永远不会抛（此前的版本就是这么写的：测试恒过、却什么也没验证）。
    /// </para>
    /// </summary>
    [Fact]
    public void 清理失败不影响本次写入也不短路后续写入()
    {
        var logDir = Path.Combine(_dir, "logs");
        Directory.CreateDirectory(logDir);

        var logger = new AppLogger(
            logDir, keepDays: 1, () => _now, () => throw new IOException("枚举失败（测试注入）"));

        // 本次写入必须落盘（清理抛了，但不得牵连写入）。
        logger.Info("第一条");

        Assert.Contains("第一条", ReadAll());

        // 后续写入同样不得被短路（_ioFailed 未被误置）。
        _now = _now.AddMinutes(1);
        logger.Error("第二条", new InvalidOperationException("boom"));

        var content = ReadAll();
        Assert.Contains("第一条", content);
        Assert.Contains("第二条", content);
    }

    /// <summary>
    /// 清理失败不置 <c>_ioFailed</c> 的另一面：清理不成功时**不重复清**当日，
    /// 但跨天后仍会再尝试（<c>_prunedForDate</c> 按日期记）。
    /// </summary>
    [Fact]
    public void 清理失败后跨天仍会再次尝试清理()
    {
        var logDir = Path.Combine(_dir, "logs");
        Directory.CreateDirectory(logDir);

        var attempts = 0;
        var logger = new AppLogger(
            logDir,
            keepDays: 1,
            clock: () => _now,
            enumerateLogFiles: () =>
            {
                attempts++;
                throw new IOException("枚举失败（测试注入）");
            });

        logger.Info("第一天");
        Assert.Equal(1, attempts);

        // 同一天再写：清理是"当日一次"，不得重复尝试。
        _now = _now.AddMinutes(1);
        logger.Info("第一天稍后");
        Assert.Equal(1, attempts);

        // 第二天：重新尝试清理（且写入照常成功，不因清理抛异常而崩溃）。
        _now = _now.AddDays(1);
        logger.Info("第二天");

        Assert.Equal(2, attempts);
        Assert.Contains("第二天", ReadAll());
    }

    // ── 线程安全 ───────────────────────────────────────────────────

    [Fact]
    public void 多线程并发写入不丢行不撕裂()
    {
        var logger = NewLogger();
        const int threads = 8;
        const int perThread = 50;

        var workers = new Thread[threads];
        for (var i = 0; i < threads; i++)
        {
            var id = i;
            workers[i] = new Thread(() =>
            {
                for (var n = 0; n < perThread; n++)
                {
                    logger.Info($"线程{id}-序号{n}");
                }
            });
        }

        foreach (var worker in workers)
        {
            worker.Start();
        }

        foreach (var worker in workers)
        {
            worker.Join();
        }

        var lines = Lines(ReadAll());
        Assert.Equal(threads * perThread, lines.Length);

        // 每行都完整：若两个线程交错写同一行，会出现"半行"或缺前缀的行。
        foreach (var line in lines)
        {
            Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} \[信息\] 线程\d+-序号\d+$", line);
        }
    }

    [Fact]
    public void 并发下未丢行()
    {
        var logger = NewLogger();
        var expected = new System.Collections.Concurrent.ConcurrentBag<string>();

        Parallel.For(0, 200, i =>
        {
            var message = $"事件{i}";
            expected.Add(message);
            logger.Info(message);
        });

        var text = ReadAll();
        foreach (var message in expected)
        {
            Assert.Contains(message, text);
        }
    }

    // ── 默认落点 ───────────────────────────────────────────────────

    [Fact]
    public void 默认目录在配置目录下的logs子目录()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "鼠标键盘辅助",
            AppLogger.LogDirectoryName);

        Assert.Equal(expected, AppLogger.DefaultLogDirectory());
        Assert.EndsWith(Path.Combine("鼠标键盘辅助", "logs"), AppLogger.DefaultLogDirectory());
    }

    [Fact]
    public void 保留天数非法时收敛为1()
    {
        var logDir = Path.Combine(_dir, "logs");
        Directory.CreateDirectory(logDir);
        File.WriteAllText(
            Path.Combine(logDir, $"{AppLogger.FilePrefix}{_now.AddDays(-1):yyyyMMdd}.log"),
            "昨天");

        var logger = new AppLogger(logDir, keepDays: 0, () => _now);
        logger.Info("今天");

        // keepDays=0 若不收敛，剪切线会落在"今天之前"从而把今天也判为过期；
        // 收敛为 1 后只保留今天。
        Assert.False(File.Exists(Path.Combine(logDir, $"{AppLogger.FilePrefix}{_now.AddDays(-1):yyyyMMdd}.log")));
        Assert.True(File.Exists(Path.Combine(logDir, CurrentFileName())));
    }

    [Fact]
    public void 文件的完整路径指向当天文件()
    {
        var logger = NewLogger();

        Assert.Equal(
            Path.Combine(_dir, "logs", CurrentFileName()),
            logger.CurrentFilePath);
    }

    [Fact]
    public void 写入使用UTF8无BOM()
    {
        var logger = NewLogger();

        logger.Info("中文内容");

        var bytes = File.ReadAllBytes(Path.Combine(_dir, "logs", CurrentFileName()));
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Contains(
            "中文内容",
            new UTF8Encoding(false).GetString(bytes));
    }
}
