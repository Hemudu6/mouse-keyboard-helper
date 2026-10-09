using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace Host.Ui;

/// <summary>
/// 自定义窗口消息取值表（设计文档 §6.3 / §6.5.5）。
///
/// <para>
/// <b>纪律：各占一个，不得复用</b>（§6.3）。<c>WM_APP</c> 段的定义就是"应用自用"，
/// 把两个用途塞进同一个消息号，接收方就只能靠附带参数去猜意图——
/// 一旦某个用途多带了个参数，另一个就会被误触发。故这里集中登记，
/// 加新用途时在这里追加一项，而不是就近挑一个空闲的 <c>WM_APP + n</c>。
/// </para>
/// </summary>
internal static class AppMessages
{
    /// <summary>
    /// 第二实例唤起（§6.4）：已有实例收到本消息 → 把主窗口显示出来并抢焦点。
    /// </summary>
    internal const int SecondInstanceActivate = 0x8000 + 1; // WM_APP + 1

    /// <summary>托盘图标回调消息（§6.3）：<c>Shell_NotifyIcon</c> 的回调消息号。</summary>
    internal const int TrayCallback = 0x8000 + 2; // WM_APP + 2
}
