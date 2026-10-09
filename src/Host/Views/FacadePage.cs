using Host.Engine;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Host.Views;

/// <summary>
/// 三个业务页面的共同基类：统一「从导航参数取门面」这条通道。
///
/// <para>
/// 子页面不会被别的页面直接 <c>new</c>——它们只经 <c>Frame.Navigate</c> 产生，
/// 而导航参数里必有门面（<c>MainPage</c> 保证）。把取参这一步收在基类，
/// 是为了让"门面从哪来"这个问题在三个页面上只有一个答案。
/// </para>
/// <para>
/// <b>导航参数有两种形态</b>：裸 <see cref="HostFacade"/>（列表页 / 设置页），
/// 或带补充信息的小记录（编辑页要额外知道编哪个任务）——后者由子类重写
/// <see cref="TakeParameter{T}"/> 解包。基类不猜参数形状，只提供这条唯一的入口。
/// </para>
/// </summary>
public abstract class FacadePage : Page
{
    /// <summary>
    /// 组合根。由 <see cref="OnNavigatedTo"/> 从导航参数取出，故**早于导航不可用**。
    /// </summary>
    internal HostFacade Facade { get; private set; } = null!;

    /// <summary>
    /// 取出导航参数里承载门面的那个对象。
    ///
    /// <para>
    /// 默认认裸门面（<see cref="HostFacade"/>）；参数形态更复杂的页面自行重写
    /// （编辑页认 <see cref="TaskEditArgs"/>）。返回 <c>null</c> = 形态不认识，
    /// 基类据此抛错——**不静默兜底**，界面此时无法工作，问题该摆在明面上。
    /// </para>
    /// </summary>
    /// <typeparam name="T">期望的参数类型。</typeparam>
    /// <param name="parameter">导航参数。</param>
    /// <returns>参数对象；形态不符时返回 <c>null</c>。</returns>
    protected virtual T? TakeParameter<T>(object? parameter) where T : class
        => parameter as T;

    /// <inheritdoc/>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (TakeParameter<HostFacade>(e.Parameter) is not { } facade)
        {
            throw new InvalidOperationException(
                $"{GetType().Name} 必须携带 HostFacade（或含门面的参数）导航进入。");
        }

        Facade = facade;
        OnFacadeReady(facade, e.Parameter);
    }

    /// <summary>
    /// 页面在拿到门面后要做的事（填充数据、订阅事件等）。
    ///
    /// <para>
    /// 之所以是回调而不是让子类重写 <see cref="OnNavigatedTo"/>：重写要记得调
    /// <c>base</c>，漏一次就是一个"门面为 null"的诡异崩溃；回调不存在这个问题。
    /// </para>
    /// </summary>
    /// <param name="facade">进程内唯一的组合根。</param>
    /// <param name="parameter">原始导航参数（子类需要额外信息时自行判定；多数页面不用）。</param>
    protected abstract void OnFacadeReady(HostFacade facade, object parameter);
}
