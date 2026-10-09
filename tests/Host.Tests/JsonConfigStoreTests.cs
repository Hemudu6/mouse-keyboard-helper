using System.IO;
using System.Text.Json;
using Core.Config;
using Core.Keys;
using Core.Model;
using Xunit;
using KeyBinding = Core.Keys.KeyBinding;
using WpfKey = System.Windows.Input.Key;

namespace Host.Tests;

/// <summary>
/// 配置存储测试（需求 FR-CFG-01~03；验收项 AC-21 / AC-21a / AC-29 / AC-36）。
///
/// 全部用例都在**临时目录**里跑：配置存储是本工程里少数真碰文件系统的组件，
/// 若测试直接打 <c>%APPDATA%</c>，跑一次单测就会覆盖用户真实配置——
/// 这是不可接受的副作用，故每个用例自建独立目录并在结束时清理。
/// </summary>
public sealed class JsonConfigStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly JsonConfigStore _store;

    public JsonConfigStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "KbmConfigTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new JsonConfigStore(_dir);
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

    // ── 辅助 ─────────────────────────────────────────────────────────

    private static TaskDefinition MakeTask(string name, WpfKey toggleKey = WpfKey.F6)
    {
        var task = new TaskDefinition { Name = name, ToggleKey = KeyBinding.FromKey(toggleKey) };
        task.Commands.Add(new KeyboardKeyCommand { VirtualKey = WpfKey.A, Frequency = 10 });
        return task;
    }

    private string TasksPath => Path.Combine(_dir, JsonConfigStore.TasksFileName);

    private string SettingsPath => Path.Combine(_dir, JsonConfigStore.SettingsFileName);

    /// <summary>把任意内容直接写进配置文件（模拟外部编辑 / 损坏），绕过存储的原子写。</summary>
    private void WriteRawFile(string path, string content)
        => File.WriteAllText(path, content);

    /// <summary>目录里所有 .bak 备份文件。</summary>
    private string[] BackupFiles()
        => Directory.Exists(_dir)
            ? Directory.GetFiles(_dir, "*" + JsonConfigStore.BackupSuffix)
            : Array.Empty<string>();

    // ── FR-CFG-01：路径与持久化 ───────────────────────────────────────

    [Fact]
    public void 默认配置目录位于APPDATA下且以软件名为子目录()
    {
        // AC 依据 FR-CFG-01 明文："路径为 %APPDATA%\<软件名>\"
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            JsonConfigStore.AppName);

        Assert.Equal(expected, JsonConfigStore.DefaultConfigDirectory());
        Assert.True(Path.IsPathRooted(JsonConfigStore.DefaultConfigDirectory()));
    }

    [Fact]
    public void 保存后重新加载得到等价任务()
    {
        var original = MakeTask("测试任务");
        original.Commands.Add(new KeyboardComboCommand
        {
            ModifierKeys = new List<WpfKey> { WpfKey.LeftCtrl },
            VirtualKey = WpfKey.C,
            Frequency = 5,
        }

        );

        var save = _store.SaveTasks(new[] { original });
        Assert.True(save.Succeeded, save.Message);

        // 换一个实例读，确保读到的是磁盘内容而非内存缓存（FR-CFG-01 要求重启后保留）。
        var reloaded = new JsonConfigStore(_dir).LoadTasks();

        Assert.Equal(ConfigRecovery.None, reloaded.Recovery);
        var task = Assert.Single(reloaded.Value);
        Assert.Equal("测试任务", task.Name);
        Assert.Equal(original.Id, task.Id);
        Assert.Equal(WpfKey.F6, task.ToggleKey.Trigger.KeyboardVirtualKey);
        Assert.Equal(2, task.Commands.Count);
    }

    [Fact]
    public void 多态命令类型经序列化往返后仍是原类型()
    {
        var task = new TaskDefinition { Name = "多态" };
        task.Commands.Add(new KeyboardKeyCommand { VirtualKey = WpfKey.A });
        task.Commands.Add(new KeyboardComboCommand { ModifierKeys = new List<WpfKey> { WpfKey.LeftAlt }, VirtualKey = WpfKey.F4 });
        task.Commands.Add(new MouseMoveCommand { X = 10, Y = -20 });
        task.Commands.Add(new MouseButtonCommand { Button = MouseKey.Right });
        task.Commands.Add(new MouseScrollCommand { Direction = ScrollDirection.Up, Notches = 3 });

        Assert.True(_store.SaveTasks(new[] { task }).Succeeded);
        var reloaded = _store.LoadTasks();

        Assert.Equal(ConfigRecovery.None, reloaded.Recovery);
        var commands = Assert.Single(reloaded.Value).Commands;
        Assert.IsType<KeyboardKeyCommand>(commands[0]);
        Assert.IsType<KeyboardComboCommand>(commands[1]);
        Assert.IsType<MouseMoveCommand>(commands[2]);
        Assert.IsType<MouseButtonCommand>(commands[3]);
        Assert.IsType<MouseScrollCommand>(commands[4]);

        // 判别器字段名必须是 kind（模型上以 JsonPolymorphic 声明）——
        // 这是与外部手写配置文件之间的隐式契约，值得钉住。
        var json = File.ReadAllText(TasksPath);
        Assert.Contains("\"kind\"", json);
    }

    [Fact]
    public void 中文任务名不被转义为Unicode转义序列()
    {
        // 配置文件是给人排查用的（设计文档 §7.1），转义后中文不可读会毁掉这个价值。
        Assert.True(_store.SaveTasks(new[] { MakeTask("下午茶定时") }).Succeeded);

        var json = File.ReadAllText(TasksPath);
        Assert.Contains("下午茶定时", json);
        Assert.DoesNotContain("\\u4", json);
    }

    // ── FR-CFG-02 / AC-21：损坏文件不崩溃 + 回退 + 提示 ─────────────────

    [Fact]
    public void 配置损坏时回退为空配置且不抛异常()
    {
        WriteRawFile(TasksPath, "{ 这不是合法的 JSON ,,, ");

        var result = _store.LoadTasks();   // 不得抛异常（AC-21 的核心断言）

        Assert.Equal(ConfigRecovery.Corrupted, result.Recovery);
        Assert.Empty(result.Value);
        Assert.True(result.HasNotice, "损坏必须给出面向用户的提示");
    }

    [Fact]
    public void 配置损坏时原文件被保留为bak供排查()
    {
        const string broken = "{ broken";
        WriteRawFile(TasksPath, broken);

        var result = _store.LoadTasks();

        Assert.NotNull(result.PreservedPath);
        Assert.True(File.Exists(result.PreservedPath!), "损坏文件必须保留供排查");
        Assert.Equal(broken, File.ReadAllText(result.PreservedPath!));

        // 原路径已让位：下次保存写入的是完好内容，报错不会反复出现。
        Assert.False(File.Exists(TasksPath));
    }

    [Fact]
    public void 空文件按损坏处理而非静默当空配置()
    {
        // 0 字节文件多半是"写到一半断电"的产物。静默当成空配置会让用户
        // 以为任务凭空消失，故必须提示 + 保留。
        WriteRawFile(TasksPath, string.Empty);

        var result = _store.LoadTasks();

        Assert.Equal(ConfigRecovery.Corrupted, result.Recovery);
        Assert.True(result.HasNotice);
        Assert.Single(BackupFiles());
    }

    [Fact]
    public void JSON字面量null按损坏处理并提示()
    {
        // "null" 是合法 JSON，但作为配置文件无任何意义；
        // 若当作合法反序列化结果，用户会看到"任务全没了"却毫无提示。
        WriteRawFile(TasksPath, "null");

        var result = _store.LoadTasks();

        Assert.Equal(ConfigRecovery.Corrupted, result.Recovery);
        Assert.True(result.HasNotice);
    }

    [Fact]
    public void 文件中缺字段时用默认值补齐()
    {
        // FR-CFG-03 的"读取时用默认值补齐缺失字段"：
        // 只写了任务名的旧字段集合，其余字段应落到模型默认值。
        WriteRawFile(TasksPath, """
            {
              "schemaVersion": 1,
              "tasks": [ { "name": "旧文件任务" } ]
            }
            """);

        var result = _store.LoadTasks();

        Assert.Equal(ConfigRecovery.None, result.Recovery);
        var task = Assert.Single(result.Value);
        Assert.Equal("旧文件任务", task.Name);
        Assert.Empty(task.Commands);
        Assert.False(task.ConsumeToggleInput);
        Assert.True(task.Enabled);
        Assert.Equal(WpfKey.F6, task.ToggleKey.Trigger.KeyboardVirtualKey);  // ToggleKey 默认 F6
    }

    [Fact]
    public void 数组中的null元素被剔除而非传播()
    {
        WriteRawFile(TasksPath, """
            {
              "schemaVersion": 1,
              "tasks": [ null, { "name": "有效任务" }, null ]
            }
            """);

        var result = _store.LoadTasks();

        Assert.Equal(ConfigRecovery.None, result.Recovery);
        Assert.Single(result.Value);
        Assert.Equal("有效任务", result.Value[0].Name);
    }

    // ── FR-CFG-03 / AC-21a：版本兼容 ──────────────────────────────────

    [Fact]
    public void 版本号高于当前版本时提示不兼容并保留原文件()
    {
        WriteRawFile(TasksPath, """
            {
              "schemaVersion": 9999,
              "tasks": [ { "name": "未来任务" } ]
            }
            """);

        var result = _store.LoadTasks();

        Assert.Equal(ConfigRecovery.Incompatible, result.Recovery);
        Assert.True(result.HasNotice);
        Assert.Empty(result.Value);
        Assert.Single(BackupFiles());
    }

    [Fact]
    public void 缺少版本号字段时按当前版本处理()
    {
        // 早期文件可能没写版本号；缺它就当"当前结构"读，是最合理的假定。
        WriteRawFile(TasksPath, """
            { "tasks": [ { "name": "无版本任务" } ] }
            """);

        var result = _store.LoadTasks();

        Assert.Equal(ConfigRecovery.None, result.Recovery);
        Assert.Equal("无版本任务", Assert.Single(result.Value).Name);
    }

    [Fact]
    public void 保存时写入当前版本号()
    {
        Assert.True(_store.SaveTasks(new[] { MakeTask("A") }).Succeeded);

        using var document = JsonDocument.Parse(File.ReadAllText(TasksPath));
        Assert.Equal(
            ConfigSchema.CurrentVersion,
            document.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    // ── FR-RUN-02 / AC-36：默认紧急停止快捷键 ─────────────────────────

    [Fact]
    public void 无配置文件时首次启动紧急停止快捷键为CtrlAltF12()
    {
        // AC-36 原文："新安装（无任何配置文件）后首次启动，紧急停止快捷键即为默认值 Ctrl+Alt+F12"
        var result = _store.LoadSettings();

        Assert.Equal(ConfigRecovery.FileMissing, result.Recovery);
        Assert.False(result.HasNotice);   // 首次启动不是错误，不该弹提示

        var binding = result.Value.EmergencyStopKey;
        Assert.NotNull(binding);
        Assert.Equal(WpfKey.F12, binding!.Trigger.KeyboardVirtualKey);
        Assert.Equal(
            new[] { WpfKey.LeftCtrl, WpfKey.LeftAlt }.ToHashSet(),
            binding.Modifiers.ToHashSet());
    }

    [Fact]
    public void 设置文件中快捷键为null时补齐为默认值()
    {
        // FR-RUN-02 明确"快捷键必须有默认值（不可为空）"——
        // 外部把文件改成 null 也不能让程序拿到空快捷键。
        WriteRawFile(SettingsPath, """
            { "schemaVersion": 1, "settings": { "emergencyStopKey": null } }
            """);

        var result = _store.LoadSettings();

        Assert.Equal(ConfigRecovery.None, result.Recovery);
        Assert.NotNull(result.Value.EmergencyStopKey);
        Assert.Equal(WpfKey.F12, result.Value.EmergencyStopKey.Trigger.KeyboardVirtualKey);
    }

    [Fact]
    public void 设置文件缺失setting节点时返回默认设置()
    {
        WriteRawFile(SettingsPath, """{ "schemaVersion": 1 }""");

        var result = _store.LoadSettings();

        Assert.Equal(ConfigRecovery.None, result.Recovery);
        Assert.NotNull(result.Value.EmergencyStopKey);
    }

    [Fact]
    public void 设置保存后可完整读回()
    {
        var settings = new AppSettings
        {
            EmergencyStopKey = KeyBinding.FromCombo(new[] { WpfKey.LeftCtrl, WpfKey.LeftShift }, WpfKey.F9),
        };

        Assert.True(_store.SaveSettings(settings).Succeeded);

        var reloaded = _store.LoadSettings();
        Assert.Equal(ConfigRecovery.None, reloaded.Recovery);
        Assert.Equal(WpfKey.F9, reloaded.Value.EmergencyStopKey.Trigger.KeyboardVirtualKey);
        Assert.Contains(WpfKey.LeftShift, reloaded.Value.EmergencyStopKey.Modifiers);
    }

    [Fact]
    public void 鼠标键作开关的绑定可往返()
    {
        var task = new TaskDefinition { Name = "鼠标开关", ToggleKey = KeyBinding.FromMouse(MouseKey.X1) };

        Assert.True(_store.SaveTasks(new[] { task }).Succeeded);
        var reloaded = _store.LoadTasks();

        var binding = Assert.Single(reloaded.Value).ToggleKey;
        Assert.Equal(MouseKey.X1, binding.Trigger.Mouse);
        Assert.False(binding.IsCombo);
    }

    // ── 原子写 ───────────────────────────────────────────────────────

    [Fact]
    public void 保存不留下临时文件()
    {
        Assert.True(_store.SaveTasks(new[] { MakeTask("A") }).Succeeded);
        Assert.True(_store.SaveTasks(new[] { MakeTask("B") }).Succeeded);

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void 重复保存覆盖旧内容而非追加()
    {
        _store.SaveTasks(new[] { MakeTask("第一版") });
        _store.SaveTasks(new[] { MakeTask("第二版") });

        var reloaded = _store.LoadTasks();

        var task = Assert.Single(reloaded.Value);
        Assert.Equal("第二版", task.Name);
    }

    [Fact]
    public void 保存会创建不存在的配置目录()
    {
        var nested = Path.Combine(_dir, "sub", "deeper");
        var store = new JsonConfigStore(nested);

        var result = store.SaveTasks(new[] { MakeTask("A") });

        Assert.True(result.Succeeded, result.Message);
        Assert.True(File.Exists(Path.Combine(nested, JsonConfigStore.TasksFileName)));
    }

    [Fact]
    public void 备份文件数量被限制在上限以内()
    {
        // NFR-04 要求磁盘占用稳定：反复损坏不能无限堆积 .bak。
        for (var i = 0; i < 6; i++)
        {
            WriteRawFile(TasksPath, "{ broken " + i);
            _store.LoadTasks();
            Thread.Sleep(5);   // 让时间戳落到不同的秒，避免同名覆盖掩盖问题
        }

        Assert.True(BackupFiles().Length <= 3, $"备份数 {BackupFiles().Length} 超过上限");
    }

    // ── AC-29：命令增删改后重启保留 ───────────────────────────────────

    [Fact]
    public void 命令增删改后重新加载完整保留()
    {
        var task = MakeTask("编辑任务");
        task.Commands.Add(new MouseScrollCommand { Direction = ScrollDirection.Down, Notches = 2 });
        _store.SaveTasks(new[] { task });

        // 模拟用户在界面上改：删掉第一条，改第二条，再加一条。
        var loaded = _store.LoadTasks();
        var edited = loaded.Value[0];
        edited.Commands.RemoveAt(0);
        ((MouseScrollCommand)edited.Commands[0]).Notches = 7;
        edited.Commands.Add(new KeyboardKeyCommand { VirtualKey = WpfKey.Space, TapMode = TapMode.Hold });
        _store.SaveTasks(loaded.Value);

        var final = _store.LoadTasks();
        var commands = Assert.Single(final.Value).Commands;

        Assert.Equal(2, commands.Count);
        Assert.Equal(7, Assert.IsType<MouseScrollCommand>(commands[0]).Notches);
        var added = Assert.IsType<KeyboardKeyCommand>(commands[1]);
        Assert.Equal(WpfKey.Space, added.VirtualKey);
        Assert.Equal(TapMode.Hold, added.TapMode);
    }
}
