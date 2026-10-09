using Core.Config;
using Core.Diagnostics;
using Core.Keys;
using Core.Model;
using Core.Validation;

namespace Host.Engine;

/// <summary>
/// 任务注册表（设计文档 §5；需求 FR-TASK-01~09）——内存中任务列表的**唯一真相来源**。
///
/// <para>
/// 职责边界：本类只管「列表里有哪些任务、增删改怎么落地」，
/// **不管**任务的运行状态（跑没跑、跑到第几次），也不管命令怎么执行。
/// 运行状态属于 <c>TaskRuntime</c>（设计文档 §4），执行属于 <c>Core.Scheduling</c>。
/// 这样切分的理由：界面增删任务与任务正在运行是两件独立的事——
/// 用户可以在任务 A 运行时改任务 B，两者不应互相阻塞。
/// </para>
/// <para>
/// <b>所有写操作都过校验</b>（设计文档 §5.3 七条）：本类不提供绕过校验的写入入口。
/// 界面拿到的 <see cref="ValidationResult"/> 直接用于高亮问题控件（AC-09）。
/// </para>
/// <para>
/// <b>线程安全</b>：内部持一把锁。界面线程与钩子线程会并发读写
/// （钩子要按开关按键查任务），锁保护的是列表快照与索引，
/// 不覆盖调用方对单个 <see cref="TaskDefinition"/> 对象的后续使用——
/// 拿到引用后请当只读用，需要改就通过本类的写入口。
/// </para>
/// </summary>
public sealed class TaskRegistry
{
    private readonly IConfigStore _store;
    private readonly ILogSink? _log;
    private readonly object _gate = new();
    private readonly List<TaskDefinition> _tasks = new();

    /// <summary>
    /// 构造注册表。
    /// </summary>
    /// <param name="store">配置存储（读取初始列表 + 落盘）。</param>
    /// <param name="log">日志接收器（可空）。仅用于记录**落盘失败**（§7.3「错误事件」）：
    /// 保存失败目前只写进 <see cref="LoadNotice">，而那个属性是**覆盖语义**——
    /// 连续两次失败只剩后一条，前一次的痕迹会被抹掉。落盘日志保留完整序列。</param>
    public TaskRegistry(IConfigStore store, ILogSink? log = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _log = log;
    }

    /// <summary>
    /// 加载失败时给界面的提示（<c>null</c> 表示无异常）。
    /// 读取配置永不抛异常（FR-CFG-02），失败信息由此暴露给界面（AC-37）。
    /// </summary>
    public string? LoadNotice { get; private set; }

    /// <summary>
    /// 从配置存储载入任务列表（构造后调用一次）。
    ///
    /// 载入的是**引用**还是副本？——配置存储每次读取都返回新对象
    /// （<c>JsonConfigStore</c> 反序列化产出），故直接接管即可，无需再复制。
    /// </summary>
    public void Load()
    {
        var result = _store.LoadTasks();

        lock (_gate)
        {
            _tasks.Clear();
            if (result.Value is not null)
            {
                // 载入时逐条过一遍校验：配置可能是手工改坏的（AC-21 投毒文件），
                // 坏数据不该带着"看起来正常"的样子进入界面。
                // 这里只记录不剔除——剔除会让用户以为自己的任务凭空消失，
                // 而界面用 IsValid 标出可疑项，用户自己决定改还是删。
                _tasks.AddRange(result.Value.Where(t => t is not null));
            }

            LoadNotice = result.HasNotice ? result.Message : null;
        }

        // 载入后立刻回写一次，把"读取时做的归一/补默认值"固化下来，
        // 免得每次启动都重复同一次修复。（配置目录不存在时由存储自行创建。）
        Persist();
    }

    /// <summary>
    /// 当前任务列表的快照。
    ///
    /// <para>
    /// <b>返回的只是"列表结构"的副本，元素是内部引用</b>。本方法是
    /// <c>_tasks.ToArray()</c>——复制的是数组本身，数组里的
    /// <see cref="TaskDefinition"/> 与注册表持有的**是同一批对象**。
    /// 因此：<b>遍历安全，改动不安全</b>。只读使用（界面展示、快照构建）
    /// 没有问题；要修改某个任务，必须先拷一份（例如经 <c>TaskDraft</c>），
    /// 否则就是在就地改生效中的对象——校验失败时无处回滚。
    /// </para>
    /// </summary>
    public IReadOnlyList<TaskDefinition> Snapshot()
    {
        lock (_gate)
        {
            return _tasks.ToArray();
        }
    }

    /// <summary>按 Id 取任务（返回内部引用，调用方当只读用）。</summary>
    public TaskDefinition? Find(Guid id)
    {
        lock (_gate)
        {
            return _tasks.FirstOrDefault(t => t.Id == id);
        }
    }

    /// <summary>
    /// 新增任务（FR-TASK-01）。校验不过则**不写入**，返回问题清单。
    /// </summary>
    public ValidationResult Add(TaskDefinition task, KeyBinding emergencyStopKey)
    {
        ArgumentNullException.ThrowIfNull(task);

        lock (_gate)
        {
            var result = TaskValidator.Validate(task, _tasks, emergencyStopKey);
            if (!result.IsValid)
            {
                return result;
            }

            _tasks.Add(task);
        }

        Persist();
        return ValidationResult.Valid;
    }

    /// <summary>
    /// 更新任务（FR-TASK-02）。按 <see cref="TaskDefinition.Id"/> 定位，
    /// 校验时自动排除自身（改自己的名字不算重名）。
    /// </summary>
    public ValidationResult Update(TaskDefinition task, KeyBinding emergencyStopKey)
    {
        ArgumentNullException.ThrowIfNull(task);

        lock (_gate)
        {
            var index = _tasks.FindIndex(t => t.Id == task.Id);
            if (index < 0)
            {
                // 不可达的防御：界面只能编辑列表里已有的任务。
                // 报"命令集合为空"过于误导，直接用一条通用问题表达"目标不存在"。
                return new ValidationResult(new[]
                {
                    new ValidationIssue(ValidationRule.TaskNameRequiredAndUnique, "要修改的任务已不存在，请刷新列表。"),
                });
            }

            var result = TaskValidator.Validate(task, _tasks, emergencyStopKey);
            if (!result.IsValid)
            {
                return result;
            }

            _tasks[index] = task;
        }

        Persist();
        return ValidationResult.Valid;
    }

    /// <summary>
    /// 删除任务（FR-TASK-03）。返回被删除的任务；不存在则返回 <c>null</c>。
    /// </summary>
    public TaskDefinition? Remove(Guid id)
    {
        TaskDefinition? removed;
        lock (_gate)
        {
            var index = _tasks.FindIndex(t => t.Id == id);
            if (index < 0)
            {
                return null;
            }

            removed = _tasks[index];
            _tasks.RemoveAt(index);
        }

        Persist();
        return removed;
    }

    /// <summary>
    /// 复制任务（FR-TASK-04）：深拷贝 + 名字加「 - 副本」+ 重过校验。
    ///
    /// 名字加后缀后仍可能重名（已有「A - 副本」时再复制 A），
    /// 此时按校验规则**拒绝**并返回问题，由用户在编辑界面自己改名——
    /// 自动加"(2)(3)"这类编号看似贴心，实际会让用户面对一堆自己没起过的名字。
    /// </summary>
    public (TaskDefinition? Copy, ValidationResult Result) Duplicate(Guid id, KeyBinding emergencyStopKey)
    {
        TaskDefinition source;
        lock (_gate)
        {
            var found = _tasks.FirstOrDefault(t => t.Id == id);
            if (found is null)
            {
                return (null, new ValidationResult(new[]
                {
                    new ValidationIssue(ValidationRule.TaskNameRequiredAndUnique, "要复制的任务已不存在，请刷新列表。"),
                }));
            }

            source = found;
        }

        var copy = Clone(source, $"{source.DisplayName} - 副本");

        lock (_gate)
        {
            var result = TaskValidator.Validate(copy, _tasks, emergencyStopKey);
            if (!result.IsValid)
            {
                return (null, result);
            }

            _tasks.Add(copy);
        }

        Persist();
        return (copy, ValidationResult.Valid);
    }

    /// <summary>
    /// 保存整个列表（界面在批量改动后一次性提交）。同样过校验，任一条不过则整体不写入。
    /// </summary>
    public ValidationResult ReplaceAll(IEnumerable<TaskDefinition> tasks, KeyBinding emergencyStopKey)
    {
        var incoming = (tasks ?? Array.Empty<TaskDefinition>()).Where(t => t is not null).ToList();

        lock (_gate)
        {
            var allIssues = new List<ValidationIssue>();
            foreach (var candidate in incoming)
            {
                // 逐条对"其余全部任务"校验：既要与列表内其他项比，
                // 也要防止同一次提交里出现两个同名任务。
                var result = TaskValidator.Validate(candidate, incoming, emergencyStopKey);
                if (!result.IsValid)
                {
                    allIssues.AddRange(result.Issues);
                }
            }

            if (allIssues.Count > 0)
            {
                return new ValidationResult(allIssues);
            }

            _tasks.Clear();
            _tasks.AddRange(incoming);
        }

        Persist();
        return ValidationResult.Valid;
    }

    /// <summary>把当前列表落盘。失败不抛异常，错误进 <see cref="LoadNotice"/> 由界面提示。</summary>
    private void Persist()
    {
        List<TaskDefinition> snapshot;
        lock (_gate)
        {
            snapshot = _tasks.ToList();
        }

        var saved = _store.SaveTasks(snapshot);
        if (!saved.Succeeded)
        {
            // 落盘是唯一入口，两处都要留痕：界面提示（LoadNotice，供 AC-37 的界面路径）
            // 与日志文件（供事后排查）。只写一处的后果已经吃过一次：
            // 提示弹窗一关就什么都不剩了。
            _log?.Error($"任务列表保存失败：{saved.Message}");
            LoadNotice = saved.Message;
        }
    }

    /// <summary>
    /// 深拷贝一个任务：新 <see cref="TaskDefinition.Id"/> + 指定名字，命令逐条重建。
    ///
    /// 不用"序列化再反序列化"的通用深拷贝：那条路依赖 JSON 配置且会绕开编译期检查，
    /// 命令类型一旦新增字段而漏配，拷贝会静默丢字段。这里逐类显式复制，
    /// 新增命令类型时**编译器会逼着补一行**。
    /// </summary>
    private static TaskDefinition Clone(TaskDefinition source, string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Enabled = source.Enabled,
        ToggleKey = source.ToggleKey,
        ConsumeToggleInput = source.ConsumeToggleInput,
        Commands = source.Commands.Select(CloneCommand).ToList(),
    };

    /// <summary>深拷贝一条命令（保持 <see cref="CommandDefinition.TapMode"/> 与频率）。</summary>
    private static CommandDefinition CloneCommand(CommandDefinition command) => command switch
    {
        KeyboardKeyCommand k => new KeyboardKeyCommand { TapMode = k.TapMode, Frequency = k.Frequency, VirtualKey = k.VirtualKey, },
        KeyboardComboCommand c => new KeyboardComboCommand
        {
            TapMode = c.TapMode,
            Frequency = c.Frequency,
            VirtualKey = c.VirtualKey,
            ModifierKeys = new List<System.Windows.Input.Key>(c.ModifierKeys),
        },
        MouseButtonCommand m => new MouseButtonCommand { TapMode = m.TapMode, Frequency = m.Frequency, Button = m.Button, },
        MouseMoveCommand mm => new MouseMoveCommand { TapMode = mm.TapMode, Frequency = mm.Frequency, X = mm.X, Y = mm.Y, },
        MouseScrollCommand ms => new MouseScrollCommand { TapMode = ms.TapMode, Frequency = ms.Frequency, Direction = ms.Direction, Notches = ms.Notches, },
        _ => throw new NotSupportedException($"未知命令类型：{command.GetType().Name}"),
    };
}
