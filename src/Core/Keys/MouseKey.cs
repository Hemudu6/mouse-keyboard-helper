namespace Core.Keys;

/// <summary>
/// 可作为开关按键、或作为鼠标按键命令实体的鼠标键。
///
/// 刻意不直接复用 <c>System.Windows.Forms.MouseButtons</c>：
/// 那个枚举带着 WinForms 的引用包袱，且命名（Left/Right/Middle/XButton1/XButton2）
/// 与本项目界面上的中文文案不一致。此处是**领域枚举**，名字即界面文案来源。
/// </summary>
public enum MouseKey
{
    /// <summary>鼠标左键。</summary>
    Left = 0,

    /// <summary>鼠标右键。</summary>
    Right = 1,

    /// <summary>鼠标中键（滚轮按下）。</summary>
    Middle = 2,

    /// <summary>鼠标侧键 X1（后退键）。</summary>
    X1 = 3,

    /// <summary>鼠标侧键 X2（前进键）。</summary>
    X2 = 4,
}
