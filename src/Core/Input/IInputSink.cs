using System.Windows.Input;
using Core.Keys;

namespace Core.Input;

/// <summary>
/// 输入注入的**底层出口**（设计文档 §3.1）。
///
/// </summary>
/// <remarks>
/// <para>
/// 存在意义：把"输入注入"与"按住表的引用计数 / 补发顺序"两类逻辑拆开。
/// 后者是纯逻辑（可被单测穷举），前者是操作系统调用（无法在单测里验证实际效果）。
/// 抽成接口后，<see cref="InputSimulator"/> 的全部行为都能用假实现驱动，
/// 而真正的 <c>SendInput</c> 调用只存在于宿主侧的一个薄类里。
/// </para>
/// <para>
/// **实现约束**：每个方法在系统层面是一次**单项**注入动作（一个按下、一个松开、
/// 一次移动或一次滚轮），不做任何"组合""成对""引用计数"的加工——
/// 组合语义与成对语义由 <see cref="InputSimulator"/> 负责。
/// </para>
/// <para>
/// **返回值语义**：返回**实际注入成功的事件数**。适配器（<c>SendInput</c>）
/// 在失败时可能返回 0 或偏少而**不抛异常**，故调用方必须检查返回值而不是依赖异常；
/// 纯逻辑层据此判定注入成败并走日志聚合（设计文档 §3.1.2），
/// 但**不因此中断任何任务**。
/// </para>
/// <para>
/// **失败类型**：调用方在判定失败后读取 <see cref="LastInjectionError"/> 取得
/// **失败原因**（平台错误码，0 表示无可用原因）。这对应设计文档 §3.1.2 要求记录的
/// "失败类型"——只记"失败了"而不知"为何失败"，日志对排障没有价值。
/// </para>
/// </remarks>
public interface IInputSink
{
    /// <summary>
    /// 最近一次注入操作的**失败原因**（平台错误码；<c>0</c> = 无失败 / 无可用原因）。
    ///
    /// <para>
    /// 语义是"诊断线索"，不是"成功与否"的判据——成败一律以各注入方法的
    /// 返回事件数为准。<b>并发下该值可能被后续注入覆盖</b>：调用方应在检测到失败后
    /// **紧接着**读取它（<see cref="InputSimulator"/> 在持锁状态下读，故对它而言是稳定的）。
    /// </para>
    /// </summary>
    int LastInjectionError { get; }

    /// <summary>注入一次键盘按下，返回实际注入的事件数。</summary>
    int KeyDown(Key virtualKey);

    /// <summary>注入一次键盘松开，返回实际注入的事件数。</summary>
    int KeyUp(Key virtualKey);

    /// <summary>注入一次鼠标按键按下，返回实际注入的事件数。</summary>
    int MouseDown(MouseKey button);

    /// <summary>注入一次鼠标按键松开，返回实际注入的事件数。</summary>
    int MouseUp(MouseKey button);

    /// <summary>
    /// 把鼠标指针移动到屏幕**绝对坐标**（设计文档 §5.x FR-CMD-M1），
    /// 返回实际注入的事件数。
    /// </summary>
    /// <param name="x">屏幕绝对坐标 X（像素，可超出单屏范围，多屏拼接时由系统处理）。</param>
    /// <param name="y">屏幕绝对坐标 Y（像素）。</param>
    int MoveAbsolute(int x, int y);

    /// <summary>
    /// 注入滚轮（FR-CMD-M3）。
    /// </summary>
    /// <param name="vertical">是否为垂直滚轮；<c>false</c> 表示水平滚轮。</param>
    /// <param name="notches">
    /// 格数：正数为上/右，负数为下/左（与 Windows <c>WHEEL_DELTA</c> 的方向约定一致，
    /// 具体含义由适配器按 <c>MOUSEEVENTF_WHEEL</c> 的语义落实）。
    /// </param>
    /// <returns>实际注入的事件数。</returns>
    int Scroll(bool vertical, int notches);
}
