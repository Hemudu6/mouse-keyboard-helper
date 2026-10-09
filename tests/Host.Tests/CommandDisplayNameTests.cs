using Core.Keys;
using Core.Model;
using System.Windows.Input;

namespace Host.Tests;

/// <summary>
/// 命令显示名的单测（需求 FR-TAP-06，设计文档 §5.1）。
///
/// 断言的对象是「显示名里的形态后缀」——三类命令都必须按各自的
/// <see cref="TapMode"/> 渲染，而不是写死。这条口径覆盖 AC-14b
/// 里「配置组合键显示名形如 `Ctrl+C·点按`」的同类要求：
/// 显示名是用户唯一的形态指示，写死会让按住形态的组合键在界面上
/// 显示成「点按」，与配置相反。
/// </summary>
public class CommandDisplayNameTests
{
    [Fact]
    public void 组合键_点按形态_显示点按后缀()
    {
        var command = new KeyboardComboCommand
        {
            ModifierKeys = new List<Key> { Key.LeftCtrl },
            VirtualKey = Key.C,
            TapMode = TapMode.Tap,
        };

        Assert.EndsWith("· 点按", command.DisplayName);
    }

    [Fact]
    public void 组合键_按住形态_显示按住后缀()
    {
        var command = new KeyboardComboCommand
        {
            ModifierKeys = new List<Key> { Key.LeftCtrl },
            VirtualKey = Key.C,
            TapMode = TapMode.Hold,
        };

        Assert.EndsWith("· 按住", command.DisplayName);
    }

    [Fact]
    public void 单键与组合键_同一形态_后缀一致()
    {
        var single = new KeyboardKeyCommand { VirtualKey = Key.A, TapMode = TapMode.Hold };
        var combo = new KeyboardComboCommand
        {
            ModifierKeys = new List<Key> { Key.LeftCtrl },
            VirtualKey = Key.C,
            TapMode = TapMode.Hold,
        };

        // 三类命令的形态后缀必须同源（KeyboardKeyCommand.DescribeMode），
        // 否则界面同一种形态会出现两种说法。
        var singleSuffix = single.DisplayName[single.DisplayName.LastIndexOf(' ')..];
        var comboSuffix = combo.DisplayName[combo.DisplayName.LastIndexOf(' ')..];

        Assert.Equal(singleSuffix, comboSuffix);
    }
}
