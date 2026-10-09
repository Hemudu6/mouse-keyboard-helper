namespace Core.Keys;

/// <summary>
/// 鼠标滚轮的滚动方向（需求 FR-CMD-M3 只定义上下；固定集合，界面用选择控件）。
///
/// <para>
/// <b>为什么住在 <c>Core.Keys</c></b>：它被<b>两侧</b>同时引用——模型侧
/// （<c>Core.Model.MouseScrollCommand.Direction</c>，进配置持久化）与输入侧
/// （<c>Core.Input.InputSimulator.ScrollWheel</c>，注入入口参数）。放在任一侧都会
/// 让另一侧产生反向依赖（输入层不认识任务模型，见 §2.1 单向依赖），故与
/// <see cref="PhysicalKey"/> / <see cref="MouseKey"/> 同处"共享键词汇表"。
/// </para>
/// <para>
/// <b>落盘取值是持久化契约</b>：<c>Up = 0</c> / <c>Down = 1</c> 一旦发布即不可变
/// （与 <see cref="Core.Model.TapMode"/> 同受设计文档 §7.1 的枚举落盘规则约束）。
/// </para>
/// </summary>
public enum ScrollDirection
{
    /// <summary>向上滚动。</summary>
    Up = 0,

    /// <summary>向下滚动。</summary>
    Down = 1,
}
