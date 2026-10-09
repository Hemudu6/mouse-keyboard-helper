using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Core.Keys;
using Core.Model;
using Core.Scheduling;
using WpfKey = System.Windows.Input.Key;

namespace Host.Views;

/// <summary>
/// 命令类型（编辑页类型下拉的**固定集合**，FR-UI-04 / AC-20：不得手输）。
///
/// 与 <see cref="CommandDefinition"/> 的子类一一对应；<see cref="CommandRow"/>
/// 负责在该枚举与具体子类之间转换。之所以另立一个枚举而不是直接用子类
/// <c>Type</c>：下拉控件需要一组**稳定、可排序、可本地化**的选项，
/// 子类类型对象既不能直接绑定也不该把类名暴露成界面文案。
/// </summary>
public enum CommandKind
{
    /// <summary>键盘单键（FR-CMD-K1）。</summary>
    KeyboardKey,

    /// <summary>键盘组合键（FR-CMD-K2）。</summary>
    KeyboardCombo,

    /// <summary>鼠标移动（FR-CMD-M1）。</summary>
    MouseMove,

    /// <summary>鼠标按键（FR-CMD-M2）。</summary>
    MouseButton,

    /// <summary>鼠标滚轮（FR-CMD-M3）。</summary>
    MouseScroll,
}

/// <summary>
/// 命令编辑行（编辑页命令列表的一行）。
///
/// <para>
/// <b>存在的理由</b>：<see cref="CommandDefinition"/> 是领域对象，各子类的"参数"
/// 形状差异极大（键盘键有虚拟键码、组合键有修饰集、移动有坐标、滚轮有方向与格数）。
/// 界面若直接绑领域对象，就得在 XAML 里为每种类型写一套可见性切换，且"未完成的编辑"
/// （如组合键还没选主键）无处安放。本类把这些差异**收敛成一组可绑定的属性**，
/// 让 XAML 只需要对 <see cref="Kind"/> 做可见性切换。
/// </para>
/// <para>
/// <b>它是可变的</b>（实现 <see cref="INotifyPropertyChanged"/>）：编辑页是草稿语义
/// （保存才提交），每次改属性只更新草稿，不回写领域对象、不落盘。
/// </para>
/// </summary>
public sealed class CommandRow : INotifyPropertyChanged
{
    /// <summary>频率上下限（FR-TAP-05，界面数字框据此设最小/最大值）。</summary>
    public static int MinFrequency => TapSchedule.MinFrequency;

    /// <summary>频率上限（FR-TAP-05）。</summary>
    public static int MaxFrequency => TapSchedule.MaxFrequency;

    /// <summary>滚轮格数上限（与校验器同源，避免界面与校验口径不一）。</summary>
    public static int MaxScrollNotches => Core.Validation.TaskValidator.MaxScrollNotches;

    private CommandKind _kind;
    private TapMode _tapMode = TapMode.Tap;
    private int _frequency = 10;

    // ── 键盘单键 / 组合键主键 ──────────────────────────────────
    private WpfKey _keyboardKey = WpfKey.A;

    // ── 组合键修饰键 ──────────────────────────────────────────
    private bool _useCtrl;
    private bool _useAlt;
    private bool _useShift;
    private bool _useWin;

    // ── 鼠标移动 ──────────────────────────────────────────────
    private int _x;
    private int _y;

    // ── 鼠标按键 ──────────────────────────────────────────────
    private MouseKey _mouseButton = MouseKey.Left;

    // ── 鼠标滚轮 ──────────────────────────────────────────────
    private ScrollDirection _scrollDirection = ScrollDirection.Down;
    private int _notches = 1;

    /// <summary>命令类型（固定集合，界面用下拉）。</summary>
    public CommandKind Kind
    {
        get => _kind;
        set
        {
            if (Set(ref _kind, value))
            {
                Raise(nameof(IsKeyboard));
                Raise(nameof(IsCombo));
                Raise(nameof(IsMouseMove));
                Raise(nameof(IsMouseButton));
                Raise(nameof(IsMouseScroll));
                Raise(nameof(TapModeApplies));
                Raise(nameof(Summary));
            }
        }
    }

    /// <summary>执行形态（点按 / 按住；固定集合，界面用下拉）。</summary>
    public TapMode TapMode
    {
        get => _tapMode;
        set
        {
            if (Set(ref _tapMode, value))
            {
                Raise(nameof(Summary));
            }
        }
    }

    /// <summary>点按频率（次/秒，1~1000，FR-TAP-05）。</summary>
    public int Frequency
    {
        get => _frequency;
        set
        {
            if (Set(ref _frequency, value))
            {
                Raise(nameof(Summary));
            }
        }
    }

    /// <summary>键盘键 / 组合键主键。</summary>
    public WpfKey KeyboardKey
    {
        get => _keyboardKey;
        set
        {
            if (Set(ref _keyboardKey, value))
            {
                Raise(nameof(Summary));
            }
        }
    }

    /// <summary>组合键含 Ctrl。</summary>
    public bool UseCtrl
    {
        get => _useCtrl;
        set
        {
            if (Set(ref _useCtrl, value))
            {
                Raise(nameof(Summary));
            }
        }
    }

    /// <summary>组合键含 Alt。</summary>
    public bool UseAlt
    {
        get => _useAlt;
        set
        {
            if (Set(ref _useAlt, value))
            {
                Raise(nameof(Summary));
            }
        }
    }

    /// <summary>组合键含 Shift。</summary>
    public bool UseShift
    {
        get => _useShift;
        set
        {
            if (Set(ref _useShift, value))
            {
                Raise(nameof(Summary));
            }
        }
    }

    /// <summary>组合键含 Win。</summary>
    public bool UseWin
    {
        get => _useWin;
        set
        {
            if (Set(ref _useWin, value))
            {
                Raise(nameof(Summary));
            }
        }
    }

    /// <summary>鼠标移动目标 X（虚拟桌面坐标系，可为负）。</summary>
    public int X
    {
        get => _x;
        set
        {
            if (Set(ref _x, value))
            {
                Raise(nameof(Summary));
            }
        }
    }

    /// <summary>鼠标移动目标 Y（虚拟桌面坐标系，可为负）。</summary>
    public int Y
    {
        get => _y;
        set
        {
            if (Set(ref _y, value))
            {
                Raise(nameof(Summary));
            }
        }
    }

    /// <summary>鼠标按键（固定集合，界面用下拉）。</summary>
    public MouseKey MouseButton
    {
        get => _mouseButton;
        set
        {
            if (Set(ref _mouseButton, value))
            {
                Raise(nameof(Summary));
            }
        }
    }

    /// <summary>滚轮方向（固定集合，界面用下拉）。</summary>
    public ScrollDirection ScrollDirection
    {
        get => _scrollDirection;
        set
        {
            if (Set(ref _scrollDirection, value))
            {
                Raise(nameof(Summary));
            }
        }
    }

    /// <summary>滚轮每次格数（1~100）。</summary>
    public int Notches
    {
        get => _notches;
        set
        {
            if (Set(ref _notches, value))
            {
                Raise(nameof(Summary));
            }
        }
    }

    // ── 下拉/列表绑定桥（SelectedIndex 只认整数索引）─────────────
    //
    // WinUI 的下拉控件绑定 SelectedIndex 而不是选中项本身（ComboBoxItem 内联在
    // XAML 里无法直接绑 SelectedItem）。故为每个固定集合补一对「序号 ⇄ 值」属性。
    // 序号一律取枚举的落盘整数值（枚举的顺序即界面顺序，禁重排——见 CommandKind
    // 等枚举的注释：整数编码已落盘）。

    /// <summary>命令类型下拉的选中序号（<see cref="CommandKind"/> 的整数值）。</summary>
    public int KindIndex
    {
        get => (int)Kind;
        set
        {
            if (value >= 0 && value <= (int)CommandKind.MouseScroll)
            {
                Kind = (CommandKind)value;
            }
        }
    }

    /// <summary>执行形态下拉的选中序号（<see cref="TapMode"/> 的整数值：单次点按 / 按住 / 不适用）。</summary>
    public int TapModeIndex
    {
        get => (int)TapMode;
        set
        {
            if (value >= 0 && value <= (int)TapMode.Hold)
            {
                TapMode = (TapMode)value;
            }
        }
    }

    /// <summary>鼠标按键下拉的选中序号（<see cref="MouseKey"/> 的整数值）。</summary>
    public int MouseButtonIndex
    {
        get => (int)MouseButton;
        set
        {
            if (value >= 0 && value <= (int)MouseKey.X2)
            {
                MouseButton = (MouseKey)value;
            }
        }
    }

    /// <summary>滚动方向下拉的选中序号（<see cref="ScrollDirection"/> 的整数值：向上 / 向下）。</summary>
    public int ScrollDirectionIndex
    {
        get => (int)ScrollDirection;
        set
        {
            if (value >= 0 && value <= (int)ScrollDirection.Down)
            {
                ScrollDirection = (ScrollDirection)value;
            }
        }
    }

    /// <summary>按键下拉的候选列表（<c>ItemsSource</c>）。</summary>
    public IReadOnlyList<CommandRowKeyOption> KeyboardKeyItems => KeyboardKeyOptions;

    /// <summary>按键下拉的选中序号（在 <see cref="KeyboardKeyItems"/> 中的位置）。</summary>
    public int KeyboardKeyIndex
    {
        get => IndexOfKey(KeyboardKey);
        set
        {
            if (value >= 0 && value < KeyboardKeyOptions.Count)
            {
                KeyboardKey = KeyboardKeyOptions[value].Key;
            }
        }
    }

    /// <summary>该类型是否显示"键盘键"选择区（键盘单键与组合键共用主键控件）。</summary>
    public bool IsKeyboard => Kind is CommandKind.KeyboardKey or CommandKind.KeyboardCombo;

    /// <summary>该类型是否显示修饰键复选区（仅组合键）。</summary>
    public bool IsCombo => Kind == CommandKind.KeyboardCombo;

    /// <summary>该类型是否为鼠标移动。</summary>
    public bool IsMouseMove => Kind == CommandKind.MouseMove;

    /// <summary>该类型是否为鼠标按键。</summary>
    public bool IsMouseButton => Kind == CommandKind.MouseButton;

    /// <summary>该类型是否为鼠标滚轮。</summary>
    public bool IsMouseScroll => Kind == CommandKind.MouseScroll;

    /// <summary>
    /// 该类型是否适用"执行形态"。鼠标移动与滚轮无按住语义（<see cref="TapMode.None"/>），
    /// 界面据此隐藏形态下拉与频率之外的相关控件。
    /// </summary>
    public bool TapModeApplies => Kind is CommandKind.KeyboardKey or CommandKind.KeyboardCombo or CommandKind.MouseButton;

    /// <summary>行内摘要文本（列表右侧展示，随任一参数变化刷新）。</summary>
    public string Summary => BuildDefinition().DisplayName;

    /// <summary>
    /// 由当前行参数构造领域命令对象。
    ///
    /// <para>
    /// <b>不校验合法性</b>——频率是否越界、组合键是否选了修饰键，都由
    /// <c>TaskValidator</c> 在保存时统一判定（§5.3：规则集中在一个模块）。
    /// 本方法只做"界面字段 → 领域对象"的形状转换。
    /// </para>
    /// </summary>
    public CommandDefinition BuildDefinition()
    {
        switch (Kind)
        {
            case CommandKind.KeyboardKey:
                return new KeyboardKeyCommand
                {
                    VirtualKey = KeyboardKey,
                    TapMode = TapMode,
                    Frequency = Frequency,
                };

            case CommandKind.KeyboardCombo:
                return new KeyboardComboCommand
                {
                    ModifierKeys = SelectedModifiers(),
                    VirtualKey = KeyboardKey,
                    TapMode = TapMode,
                    Frequency = Frequency,
                };

            case CommandKind.MouseMove:
                return new MouseMoveCommand
                {
                    X = X,
                    Y = Y,
                    // 移动命令无执行形态（TapMode.None），频率仍由基类承载。
                    TapMode = TapMode.None,
                    Frequency = Frequency,
                };

            case CommandKind.MouseButton:
                return new MouseButtonCommand
                {
                    Button = MouseButton,
                    TapMode = TapMode,
                    Frequency = Frequency,
                };

            case CommandKind.MouseScroll:
                return new MouseScrollCommand
                {
                    Direction = ScrollDirection,
                    Notches = Notches,
                    TapMode = TapMode.None,
                    Frequency = Frequency,
                };

            default:
                throw new NotSupportedException($"未处理的命令类型：{Kind}");
        }
    }

    /// <summary>
    /// 由领域命令对象反填一行（编辑既有任务时为每条命令建一行）。
    /// </summary>
    /// <param name="command">领域命令；<c>null</c> 时创建一条默认的键盘单键行。</param>
    public static CommandRow FromDefinition(CommandDefinition? command)
    {
        var row = new CommandRow();

        switch (command)
        {
            case KeyboardKeyCommand key:
                row.Kind = CommandKind.KeyboardKey;
                row.KeyboardKey = key.VirtualKey;
                row.TapMode = key.TapMode;
                row.Frequency = key.Frequency;
                break;

            case KeyboardComboCommand combo:
                row.Kind = CommandKind.KeyboardCombo;
                row.KeyboardKey = combo.VirtualKey;
                row.TapMode = combo.TapMode;
                row.Frequency = combo.Frequency;
                row.UseCtrl = combo.Modifiers.Contains(WpfKey.LeftCtrl) || combo.Modifiers.Contains(WpfKey.RightCtrl);
                row.UseAlt = combo.Modifiers.Contains(WpfKey.LeftAlt) || combo.Modifiers.Contains(WpfKey.RightAlt);
                row.UseShift = combo.Modifiers.Contains(WpfKey.LeftShift) || combo.Modifiers.Contains(WpfKey.RightShift);
                row.UseWin = combo.Modifiers.Contains(WpfKey.LWin) || combo.Modifiers.Contains(WpfKey.RWin);
                break;

            case MouseMoveCommand move:
                row.Kind = CommandKind.MouseMove;
                row.X = move.X;
                row.Y = move.Y;
                row.Frequency = move.Frequency;
                break;

            case MouseButtonCommand button:
                row.Kind = CommandKind.MouseButton;
                row.MouseButton = button.Button;
                row.TapMode = button.TapMode;
                row.Frequency = button.Frequency;
                break;

            case MouseScrollCommand scroll:
                row.Kind = CommandKind.MouseScroll;
                row.ScrollDirection = scroll.Direction;
                row.Notches = scroll.Notches;
                row.Frequency = scroll.Frequency;
                break;

            case null:
                // 新建行：默认键盘单键 A · 点按 · 10 次/秒（与领域默认值一致）。
                row.Kind = CommandKind.KeyboardKey;
                break;

            default:
                throw new NotSupportedException($"未处理的命令类型：{command.GetType().Name}");
        }

        return row;
    }
    // ── 键盘键候选集 ──────────────────────────────────────────────
    //
    // KeyCatalog 只提供「单键 → 中文名」的映射，没有「全部键盘键」的清单，
    // 且界面不该出现无中文名的键（会退化成一串枚举名，对用户没有意义）。
    // 故候选集在此显式列举：字母、数字、F1-F24、编辑键、小键盘——覆盖实际会用到的全部。

    /// <summary>键盘键下拉的候选集（按 <see cref="CommandRowKeyOption"/> 的顺序）。</summary>
    private static readonly IReadOnlyList<CommandRowKeyOption> KeyboardKeyOptions = BuildKeyOptions();

    private static List<CommandRowKeyOption> BuildKeyOptions()
    {
        var keys = new List<WpfKey>();

        // 字母 A-Z
        for (var key = WpfKey.A; key <= WpfKey.Z; key++)
        {
            keys.Add(key);
        }

        // 顶部数字 0-9 与 D0-D9 并存：两者虚拟键码不同（D0 是不带 Shift 的物理数字行）。
        for (var key = WpfKey.D0; key <= WpfKey.D9; key++)
        {
            keys.Add(key);
        }

        // 功能键 F1-F24
        for (var key = WpfKey.F1; key <= WpfKey.F24; key++)
        {
            keys.Add(key);
        }

        keys.AddRange(new[]
        {
            WpfKey.Space, WpfKey.Enter, WpfKey.Tab, WpfKey.Escape, WpfKey.Back,
            WpfKey.Delete, WpfKey.Insert, WpfKey.Home, WpfKey.End,
            WpfKey.PageUp, WpfKey.PageDown,
            WpfKey.Up, WpfKey.Down, WpfKey.Left, WpfKey.Right,
            WpfKey.OemMinus, WpfKey.OemPlus, WpfKey.OemComma, WpfKey.OemPeriod,
            WpfKey.OemQuestion, WpfKey.OemSemicolon, WpfKey.OemQuotes,
            WpfKey.OemOpenBrackets, WpfKey.OemCloseBrackets, WpfKey.OemPipe, WpfKey.OemTilde,
            WpfKey.NumPad0, WpfKey.NumPad1, WpfKey.NumPad2, WpfKey.NumPad3, WpfKey.NumPad4,
            WpfKey.NumPad5, WpfKey.NumPad6, WpfKey.NumPad7, WpfKey.NumPad8, WpfKey.NumPad9,
            WpfKey.Multiply, WpfKey.Add, WpfKey.Subtract, WpfKey.Decimal, WpfKey.Divide,

            // 以下为「合法但少见」的键：不能因为少见就把它们排除在候选集外——
            // 一旦排除，载入旧任务时 IndexOfKey 找不到它，界面就会把用户原本绑定的键
            // 静默改写成别的键（AC-29 数据不丢失）。列出来才有得选。
            WpfKey.CapsLock, WpfKey.NumLock, WpfKey.Scroll, WpfKey.PrintScreen,
            WpfKey.Pause, WpfKey.Apps, WpfKey.Sleep,
            WpfKey.LeftShift, WpfKey.RightShift, WpfKey.LeftCtrl, WpfKey.RightCtrl,
            WpfKey.LeftAlt, WpfKey.RightAlt, WpfKey.LWin, WpfKey.RWin,
        });

        var options = new List<CommandRowKeyOption>(keys.Count);
        var seen = new HashSet<WpfKey>();
        foreach (var key in keys)
        {
            if (seen.Add(key))
            {
                options.Add(new CommandRowKeyOption(key));
            }
        }

        return options;
    }

    /// <summary>
    /// 在候选集中定位某键，返回其序号；<b>找不到时返回 -1</b>（下拉框据此显示为「未选中」）。
    ///
    /// <para>
    /// <b>绝不返回 0 代替「找不到」</b>：0 是候选集里的真实位置（A 键），
    /// 用它兜底会让「载入了候选集之外的键」伪装成「用户选的就是 A」，
    /// 保存时把原键静默改写掉（AC-29）。返回 -1 时下拉框自然空着，
    /// 用户能看出「这行的键不在列表里」，而不是被蒙在鼓里。
    /// </para>
    /// </summary>
    private static int IndexOfKey(WpfKey key)
    {
        for (var index = 0; index < KeyboardKeyOptions.Count; index++)
        {
            if (KeyboardKeyOptions[index].Key == key)
            {
                return index;
            }
        }

        return -1;
    }



    /// <summary>把复选状态折算成修饰键集合（固定取左键代表，与 KeyCatalog 序一致）。</summary>
    private List<WpfKey> SelectedModifiers()
    {
        var modifiers = new List<WpfKey>();
        if (UseCtrl)
        {
            modifiers.Add(WpfKey.LeftCtrl);
        }

        if (UseAlt)
        {
            modifiers.Add(WpfKey.LeftAlt);
        }

        if (UseShift)
        {
            modifiers.Add(WpfKey.LeftShift);
        }

        if (UseWin)
        {
            modifiers.Add(WpfKey.LWin);
        }

        return modifiers;
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 键盘键下拉的一个候选项。
///
/// <para>
/// 存在的理由：下拉要绑 <c>SelectedIndex</c>，而它的内容又必须是中文名。
/// 直接绑 <c>WpfKey</c> 枚举会显示枚举名（"OemMinus" 之类），故包一层，
/// 由 <see cref="KeyCatalog.Describe(WpfKey)"/> 提供中文名——
/// 与列表页、编辑页摘要用的是同一条命名口径，不会出现两处叫法不一致。
/// </para>
/// </summary>
public sealed class CommandRowKeyOption
{
    internal CommandRowKeyOption(WpfKey key)
    {
        Key = key;
        Name = KeyCatalog.Describe(key);
    }

    /// <summary>该候选对应的键。</summary>
    public WpfKey Key { get; }

    /// <summary>下拉中显示的中文名。</summary>
    public string Name { get; }

    /// <summary>下拉绑定 <c>DisplayMemberPath</c>/默认 <c>ToString</c> 时用中文名。</summary>
    public override string ToString() => Name;
}

