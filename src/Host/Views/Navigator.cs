using Host.Engine;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Host.Views;

/// <summary>
/// 子页面回到<b>导航宿主</b>（<see cref="MainPage"/>）的唯一通道。
///
/// <para>
/// <b>为什么需要它</b>：三个子页面都在 <c>MainPage.ContentFrame</c> 里被托管，
/// 它们要「跳到编辑页」或「退回列表页」时必须找到宿主。早先的实现写成
/// <c>Frame.Parent is MainPage</c> —— 这在 WinUI 里<b>永远不成立</b>：
/// <c>Frame.Parent</c> 是<b>视觉树父级</b>，也就是包着它的 <c>NavigationView</c>
/// （见 <c>MainPage.xaml</c>），不是 <c>MainPage</c> 本身。于是这几个跳转全都静默失效。
/// </para>
/// <para>
/// <b>为什么这样修正</b>：视觉树里 <c>Frame → NavigationView → Grid → MainPage</c>，
/// 逐级向上找总能命中 <see cref="MainPage"/>。用「向上遍历找宿主」而不是缓存引用，
/// 是因为页面实例随导航新建/回收，缓存会指向已死的页面。
/// </para>
/// <para>
/// <b>找不到就抛</b>：返回 <c>null</c> 让调用方静默跳过，正是上面那个缺陷的成因
/// （按钮点了没反应且无人知道）。这里直接抛出，让"挂错宿主"当场可见。
/// </para>
/// </summary>
internal static class Navigator
{
    /// <summary>
    /// 从子页面的 <see cref="Frame"/> 向上找到导航宿主 <see cref="MainPage"/>。
    /// </summary>
    /// <param name="frame">子页面的 <c>Frame</c>（即 <c>Page.Frame</c>）。</param>
    /// <returns>托管该 <paramref name="frame"/> 的 <see cref="MainPage"/>。</returns>
    /// <exception cref="InvalidOperationException">
    /// 向上遍历到根仍未找到 <see cref="MainPage"/>——说明本页被挂到了预想之外的宿主上，
    /// 界面导航此时不可用，直接暴露问题而不是静默丢弃用户的点击。
    /// </exception>
    internal static MainPage ResolveFor(Frame frame)
    {
        for (DependencyObject? node = frame; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is MainPage host)
            {
                return host;
            }
        }

        throw new InvalidOperationException(
            "子页面未托管在 MainPage 下，导航不可用（界面应只由 MainPage.ContentFrame 承载）。");
    }
}
