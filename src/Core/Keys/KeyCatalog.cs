using System.Windows.Input;

namespace Core.Keys;

/// <summary>
/// 键名录（设计文档 §5.2 的"按键捕获"配套物）。
///
/// 职责**只有一项**：把键标识翻译成界面上的中文短名。
/// 它不做捕获、不做合法性判定、不持有任何状态——捕获由输入层的钩子完成，
/// 是否合法由 <c>TaskValidator</c> 判定。这一层保持为纯函数表，
/// 才能被单测穷举，也才不会与 Input 层互相牵动。
///
/// 关于"哪些键可以作为开关按键"：本目录**不做白名单**。
/// 用户按了什么键就捕获什么键，能捕获到的键就是可用作开关的键——
/// 加白名单会凭空制造"某些键不能用"的限制，而需求中并无此限制。
/// 唯一在 <c>TaskValidator</c> 层被拒绝的是"与命令实体键重叠""已被其他任务占用"
/// 这类**关系性冲突**，与键本身是什么无关。
/// </summary>
public static class KeyCatalog
{
    /// <summary>鼠标键的中文短名（需求 FR-UI-05：界面全中文）。</summary>
    public static string Describe(MouseKey mouseKey) => mouseKey switch
    {
        MouseKey.Left => "鼠标左键",
        MouseKey.Right => "鼠标右键",
        MouseKey.Middle => "鼠标中键",
        MouseKey.X1 => "鼠标侧键 X1",
        MouseKey.X2 => "鼠标侧键 X2",
        _ => "鼠标键",
    };

    /// <summary>
    /// 修饰键的中文短名。非修饰键返回 <c>null</c>。
    /// 用于把捕获到的键归类为"修饰键"还是"主键"——一次组合捕获要区分这两者。
    /// </summary>
    public static string? DescribeModifier(Key virtualKey) => virtualKey switch
    {
        Key.LeftCtrl or Key.RightCtrl => "Ctrl",
        Key.LeftAlt or Key.RightAlt => "Alt",
        Key.LeftShift or Key.RightShift => "Shift",
        Key.LWin or Key.RWin => "Win",
        _ => null,
    };

    /// <summary>该键盘键是否为修饰键。</summary>
    public static bool IsModifier(Key virtualKey) => DescribeModifier(virtualKey) is not null;

    /// <summary>
    /// 键盘虚拟键的中文/通用短名。
    ///
    /// 覆盖范围：字母、数字、功能键、数字小键盘、常用编辑与符号键。
    /// **未覆盖的键不返回 null，而是回退为 <c>"{键的英文名} 键"</c>**——
    /// 宁可显示一个不够漂亮的英文名，也不显示空白：
    /// 空白会让用户以为捕获失败，从而反复重试。
    /// </summary>
    public static string Describe(Key virtualKey)
    {
        var known = DescribeKnown(virtualKey);
        if (known is not null)
        {
            return known;
        }

        // 回退：Key 枚举名本身（如 "Oem8"、"LaunchMail"）。不加"键"字，
        // 避免出现"LaunchMail 键"这种半中半英的别扭读法。
        return virtualKey.ToString();
    }

    private static string? DescribeKnown(Key virtualKey)
    {
        // 字母（A~Z）与数字（0~9）的枚举名本身就够短，直接用。
        if (virtualKey is >= Key.A and <= Key.Z)
        {
            return virtualKey.ToString();
        }

        if (virtualKey is >= Key.D0 and <= Key.D9)
        {
            return ((int)virtualKey - (int)Key.D0).ToString();
        }

        // 数字小键盘（Key.NumPad0~NumPad9）。
        if (virtualKey is >= Key.NumPad0 and <= Key.NumPad9)
        {
            return "小键盘 " + ((int)virtualKey - (int)Key.NumPad0);
        }

        if (virtualKey is >= Key.F1 and <= Key.F24)
        {
            return virtualKey.ToString();
        }

        return virtualKey switch
        {
            Key.LeftCtrl => "左 Ctrl",
            Key.RightCtrl => "右 Ctrl",
            Key.LeftAlt => "左 Alt",
            Key.RightAlt => "右 Alt",
            Key.LeftShift => "左 Shift",
            Key.RightShift => "右 Shift",
            Key.LWin => "左 Win",
            Key.RWin => "右 Win",
            Key.Space => "空格",
            Key.Enter => "回车",
            Key.Escape => "Esc",
            Key.Tab => "Tab",
            Key.Back => "退格",
            Key.Delete => "Delete",
            Key.Insert => "Insert",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Up => "方向键 上",
            Key.Down => "方向键 下",
            Key.Left => "方向键 左",
            Key.Right => "方向键 右",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemPipe => "\\",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemQuestion => "/",
            Key.OemTilde => "`",
            Key.CapsLock => "CapsLock",
            Key.NumLock => "NumLock",
            Key.Scroll => "ScrollLock",
            Key.PrintScreen => "PrintScreen",
            Key.Pause => "Pause",
            _ => null,
        };
    }

    /// <summary>
    /// 描述一个开关按键绑定（含组合形态），供界面显示。
    /// 组合形态的输出形如 <c>"Ctrl+Alt+F12"</c>，单键即键名本身；
    /// <paramref name="binding"/> 为 <c>null</c> 时返回「未设置」（未设置不是错误状态）。
    /// </summary>
    public static string Describe(KeyBinding? binding)
    {
        // 绑定可能为 null（投毒文件/外部构造）——描述文本不该成为崩溃点（AC-21）。
        if (binding is null)
        {
            return "（未设置）";
        }

        if (binding.Trigger.Mouse is { } mouseKey)
        {
            return Describe(mouseKey);
        }

        if (binding.Trigger.KeyboardVirtualKey is not { } mainKey)
        {
            // 理论上不可达：PhysicalKey 必然二选一。防御性回退而非抛异常——
            // 界面文案函数不该成为崩溃点。
            return "（未设置）";
        }

        if (binding.Modifiers is null || binding.Modifiers.Count == 0)
        {
            return Describe(mainKey);
        }

        // 修饰键顺序固定为 Ctrl → Alt → Shift → Win：
        // 不按枚举值排序（枚举值顺序是 Ctrl/Alt/Shift/Win 的混合），
        // 也不按用户按下顺序（每次可能不同，显示会跳）。固定顺序才稳定可读。
        var ordered = new List<string>();
        foreach (var modifier in new[] { Key.LeftCtrl, Key.LeftAlt, Key.LeftShift, Key.LWin })
        {
            if (ContainsModifier(binding.Modifiers, modifier))
            {
                ordered.Add(DescribeModifier(modifier)!);
            }
        }

        ordered.Add(Describe(mainKey));
        return string.Join("+", ordered);
    }

    /// <summary>
    /// 修饰键判等：左右两侧视为同一个修饰键。
    /// 用户按左 Ctrl 而配置里存的是右 Ctrl 时，必须仍然匹配——
    /// 否则"按住左 Ctrl 点开关键"会莫名其妙失效。
    /// </summary>
    private static bool ContainsModifier(IReadOnlySet<Key>? modifiers, Key probe)
        => modifiers is not null && modifiers.Any(m => IsSameModifier(m, probe));

    /// <summary>两个键盘键在"修饰键"语义下是否等价（左右归一）。</summary>
    public static bool IsSameModifier(Key a, Key b)
    {
        var (familyA, familyB) = (ModifierFamily(a), ModifierFamily(b));
        return familyA is not null && familyA == familyB;
    }

    /// <summary>修饰键族名（左右归一）；非修饰键返回 <c>null</c>。</summary>
    public static string? ModifierFamily(Key virtualKey) => virtualKey switch
    {
        Key.LeftCtrl or Key.RightCtrl => "Ctrl",
        Key.LeftAlt or Key.RightAlt => "Alt",
        Key.LeftShift or Key.RightShift => "Shift",
        Key.LWin or Key.RWin => "Win",
        _ => null,
    };

    /// <summary>
    /// 修饰键族的**代表键**（左侧那个）。非修饰键原样返回。
    ///
    /// 用于需要"归一到同一个值"的场合（如冲突占用表的键）：
    /// 左 Ctrl 与右 Ctrl 归到同一个代表键后，字典判等才能把两者当同一个实体。
    /// 不能用 <see cref="ModifierFamily"/> 的族名当字典键——
    /// 字典要同时装键盘键与鼠标键，只有 <see cref="PhysicalKey"/> 能统一表示。
    /// </summary>
    public static Key RepresentativeOfFamily(Key virtualKey) => virtualKey switch
    {
        Key.RightCtrl => Key.LeftCtrl,
        Key.RightAlt => Key.LeftAlt,
        Key.RightShift => Key.LeftShift,
        Key.RWin => Key.LWin,
        _ => virtualKey,
    };

    /// <summary>
    /// 某个修饰键族名对应的**全部物理键**（左 + 右）。
    ///
    /// <para>
    /// <see cref="ModifierFamily"/> 的逆映射，供"按族查物理按下状态"的场合使用
    /// （设计文档 §3.4.1「无重同步路径」的对账据此逐一族核对）。
    /// 与 <see cref="ModifierFamily"/> 用同一批族名字符串，两者必须同步增删。
    /// </para>
    /// <para>
    /// 未知族名返回空数组（调用方按"查无此族"处理，不抛异常——
    /// 这是热路径上的查询函数，不该成为崩溃点）。
    /// </para>
    /// </summary>
    public static IReadOnlyList<Key> KeysOfFamily(string family) => family switch
    {
        "Ctrl" => new[] { Key.LeftCtrl, Key.RightCtrl },
        "Alt" => new[] { Key.LeftAlt, Key.RightAlt },
        "Shift" => new[] { Key.LeftShift, Key.RightShift },
        "Win" => new[] { Key.LWin, Key.RWin },
        _ => System.Array.Empty<Key>(),
    };
}
