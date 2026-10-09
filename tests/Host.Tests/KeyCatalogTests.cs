using System.Windows.Input;
using Core.Keys;

// System.Windows.Input 与 Core.Keys 都有 KeyBinding。本文件要的是领域模型那个，
// 显式别名消歧（WPF 那个是输入手势绑定，与此无关）。
using KeyBinding = Core.Keys.KeyBinding;

namespace Host.Tests;

/// <summary>
/// 键名录与键标识的单测。
/// 对应需求：FR-UI-05（界面全中文，AC-19a）、§6.1 开关按键的绑定与描述。
/// </summary>
public class KeyCatalogTests
{
    [Theory]
    [InlineData(Key.A, "A")]
    [InlineData(Key.Z, "Z")]
    [InlineData(Key.D0, "0")]
    [InlineData(Key.D9, "9")]
    [InlineData(Key.F1, "F1")]
    [InlineData(Key.F12, "F12")]
    [InlineData(Key.NumPad0, "小键盘 0")]
    [InlineData(Key.NumPad9, "小键盘 9")]
    [InlineData(Key.Space, "空格")]
    [InlineData(Key.Enter, "回车")]
    [InlineData(Key.Escape, "Esc")]
    [InlineData(Key.Up, "方向键 上")]
    [InlineData(Key.LeftCtrl, "左 Ctrl")]
    public void 常用键有中文短名(Key key, string expected)
    {
        Assert.Equal(expected, KeyCatalog.Describe(key));
    }

    [Fact]
    public void 未知键回退为枚举名而不是空白()
    {
        // 显示空白会让用户以为捕获失败而反复重试；回退名至少能对上号。
        var name = KeyCatalog.Describe(Key.LaunchMail);

        Assert.False(string.IsNullOrWhiteSpace(name));
        Assert.Equal("LaunchMail", name);
    }

    [Theory]
    [InlineData(Key.LeftCtrl)]
    [InlineData(Key.RightCtrl)]
    [InlineData(Key.LeftAlt)]
    [InlineData(Key.RightAlt)]
    [InlineData(Key.LeftShift)]
    [InlineData(Key.RightShift)]
    [InlineData(Key.LWin)]
    [InlineData(Key.RWin)]
    public void 修饰键被正确识别(Key key)
    {
        Assert.True(KeyCatalog.IsModifier(key));
        Assert.NotNull(KeyCatalog.DescribeModifier(key));
    }

    [Theory]
    [InlineData(Key.A)]
    [InlineData(Key.F6)]
    [InlineData(Key.Space)]
    public void 非修饰键不被当作修饰键(Key key)
    {
        Assert.False(KeyCatalog.IsModifier(key));
        Assert.Null(KeyCatalog.DescribeModifier(key));
    }

    [Theory]
    [InlineData(Key.LeftCtrl, Key.RightCtrl)]
    [InlineData(Key.LeftAlt, Key.RightAlt)]
    [InlineData(Key.LeftShift, Key.RightShift)]
    [InlineData(Key.LWin, Key.RWin)]
    public void 修饰键左右归一(Key a, Key b)
    {
        // 用户按左 Ctrl、配置里存右 Ctrl 时必须仍然匹配；
        // 否则"按住 Ctrl 点开关键"会莫名其妙失效。
        Assert.True(KeyCatalog.IsSameModifier(a, b));
    }

    [Fact]
    public void 不同族的修饰键不等价()
    {
        Assert.False(KeyCatalog.IsSameModifier(Key.LeftCtrl, Key.LeftAlt));
        Assert.False(KeyCatalog.IsSameModifier(Key.LeftShift, Key.LWin));
    }

    [Fact]
    public void 单键绑定的描述是键名本身()
    {
        Assert.Equal("F6", KeyCatalog.Describe(KeyBinding.FromKey(Key.F6)));
    }

    [Fact]
    public void 鼠标键绑定的描述是中文鼠标键名()
    {
        Assert.Equal("鼠标侧键 X1", KeyCatalog.Describe(KeyBinding.FromMouse(MouseKey.X1)));
    }

    [Fact]
    public void 组合键按固定顺序排版()
    {
        // 固定 Ctrl → Alt → Shift → Win：不按枚举值也不按用户按下顺序，
        // 否则每次显示都可能不一样，用户会以为配置变了。
        var binding = KeyBinding.FromCombo(new[] { Key.LWin, Key.LeftShift, Key.LeftAlt, Key.LeftCtrl }, Key.F12);

        Assert.Equal("Ctrl+Alt+Shift+Win+F12", KeyCatalog.Describe(binding));
    }

    [Fact]
    public void 组合键只给出部分修饰键时按存在者排版()
    {
        var binding = KeyBinding.FromCombo(new[] { Key.LeftAlt, Key.LeftCtrl }, Key.C);

        Assert.Equal("Ctrl+Alt+C", KeyCatalog.Describe(binding));
    }

    [Fact]
    public void 组合键形态判定()
    {
        Assert.True(KeyBinding.FromCombo(new[] { Key.LeftCtrl }, Key.C).IsCombo);
        Assert.False(KeyBinding.FromKey(Key.F6).IsCombo);
        Assert.False(KeyBinding.FromMouse(MouseKey.X1).IsCombo);
    }

    [Fact]
    public void 修饰键为null的绑定不崩溃且按单键排版()
    {
        // C 回归：畸形文件里 "modifiers": null 是合法 JSON，读回后绑定的修饰键为 null。
        // 描述键名/判冲突都不得因此抛 NRE（AC-21：投毒文件不崩溃）。
        var binding = new KeyBinding(PhysicalKey.FromKeyboard(Key.C), null!);

        Assert.Equal("C", KeyCatalog.Describe(binding));
        Assert.False(binding.IsCombo);
    }

    [Fact]
    public void 族代表键把右侧修饰键映射到左侧()
    {
        // D 的归一基础：占用表的字典键统一取族代表，左右两侧映射到同一个键。
        Assert.Equal(Key.LeftCtrl, KeyCatalog.RepresentativeOfFamily(Key.RightCtrl));
        Assert.Equal(Key.LeftCtrl, KeyCatalog.RepresentativeOfFamily(Key.LeftCtrl));
        Assert.Equal(Key.LeftShift, KeyCatalog.RepresentativeOfFamily(Key.RightShift));
        Assert.Equal(Key.LeftAlt, KeyCatalog.RepresentativeOfFamily(Key.RightAlt));
        Assert.Equal(Key.LWin, KeyCatalog.RepresentativeOfFamily(Key.RWin));
    }

    [Fact]
    public void 非修饰键的族代表是它自己()
    {
        Assert.Equal(Key.A, KeyCatalog.RepresentativeOfFamily(Key.A));
        Assert.Equal(Key.F6, KeyCatalog.RepresentativeOfFamily(Key.F6));
    }

    [Fact]
    public void 族名反查物理键为左右两键_且与族归一口径一致()
    {
        // 设计文档 §3.4.1「无重同步路径」的对账依据：拿到族名后要能查出该族的全部物理键。
        // 左右归一的存在意味着**任何**一侧按下都要被这条逆映射覆盖。
        Assert.Equal(new[] { Key.LeftCtrl, Key.RightCtrl }, KeyCatalog.KeysOfFamily("Ctrl"));
        Assert.Equal(new[] { Key.LeftAlt, Key.RightAlt }, KeyCatalog.KeysOfFamily("Alt"));
        Assert.Equal(new[] { Key.LeftShift, Key.RightShift }, KeyCatalog.KeysOfFamily("Shift"));
        Assert.Equal(new[] { Key.LWin, Key.RWin }, KeyCatalog.KeysOfFamily("Win"));

        // 与 ModifierFamily 同口径：族名 → 物理键 → 族名，回到同一个族（防两表增删不同步）。
        foreach (var family in new[] { "Ctrl", "Alt", "Shift", "Win" })
        {
            var keys = KeyCatalog.KeysOfFamily(family);
            Assert.Equal(2, keys.Count);
            Assert.All(keys, k => Assert.Equal(family, KeyCatalog.ModifierFamily(k)));
        }
    }

    [Fact]
    public void 未知族名反查为空数组而不是抛异常()
    {
        // 热路径上的查询函数：调用方按"查无此族"处理，崩溃点不在这里。
        Assert.Empty(KeyCatalog.KeysOfFamily("ctrl"));      // 大小写敏感：族名是内部常量，不做宽松匹配
        Assert.Empty(KeyCatalog.KeysOfFamily("Meta"));
        Assert.Empty(KeyCatalog.KeysOfFamily(string.Empty));
    }



    [Fact]
    public void 实体键按值判等()
    {
        // FR-TOGGLE-07 / FR-SEQ-10 的冲突比对全部依赖这个判等口径。
        Assert.Equal(PhysicalKey.FromKeyboard(Key.A), PhysicalKey.FromKeyboard(Key.A));
        Assert.NotEqual(PhysicalKey.FromKeyboard(Key.A), PhysicalKey.FromKeyboard(Key.B));
        Assert.NotEqual(PhysicalKey.FromKeyboard(Key.A), PhysicalKey.FromMouse(MouseKey.Left));
        Assert.Equal(PhysicalKey.FromMouse(MouseKey.X1), PhysicalKey.FromMouse(MouseKey.X1));
    }

    [Fact]
    public void 实体键可作字典键()
    {
        // 运行期要用"实体键 → 占用者"的表做开关归属查找；
        // 判等口径若不一致（如按引用），同一实体键会被当成两个不同键，冲突检测就失效了。
        var map = new Dictionary<PhysicalKey, string>
        {
            [PhysicalKey.FromKeyboard(Key.F6)] = "任务甲",
            [PhysicalKey.FromMouse(MouseKey.X1)] = "任务乙",
        };

        Assert.Equal("任务甲", map[PhysicalKey.FromKeyboard(Key.F6)]);
        Assert.Equal("任务乙", map[PhysicalKey.FromMouse(MouseKey.X1)]);
        Assert.False(map.ContainsKey(PhysicalKey.FromKeyboard(Key.F7)));
    }

    [Fact]
    public void 实体键类型互斥()
    {
        var keyboard = PhysicalKey.FromKeyboard(Key.A);
        Assert.True(keyboard.IsKeyboard);
        Assert.False(keyboard.IsMouse);

        var mouse = PhysicalKey.FromMouse(MouseKey.Left);
        Assert.True(mouse.IsMouse);
        Assert.False(mouse.IsKeyboard);
    }
}
