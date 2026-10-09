using System.Runtime.InteropServices;
using Core.Keys;
using Microsoft.UI.Input;
using WpfKey = System.Windows.Input.Key;
using KeyInterop = System.Windows.Input.KeyInterop;

namespace Host.Input;

/// <summary>
/// 系统级热键的**可用性探测**（设计文档 §6.1）。
///
/// <para>
/// 本工具的紧急停止**不依赖 <c>RegisterHotKey</c>**：它由低级键盘钩子监听（§3.2），
/// 因此这里注册成功与否**不决定紧急停止能否生效**。
/// 探测的意义在于回答另一个问题——"这个键有没有被别的程序用系统热键占着"，
/// 让用户在挑键的那一刻就知道，而不是等到某天真的要紧急停止时才发现
/// 该组合已被别的软件截获（§5.2 对紧急停止键的要求）。
/// </para>
/// <para>
/// 该 API 会因"这个键已被别的程序注册"而失败（<c>ERROR_HOTKEY_ALREADY_REGISTERED</c>）。
/// 这一结果只作**提示**，不阻断保存——占用是运行环境的事，用户可能先存下来、回头再改。
/// </para>
/// <para>
/// <b>探测 = 注册即注销</b>：注册到手（拿到独占）就说明没人占用，随即注销，
/// 不留副作用；注册失败则把系统错误原文交给调用方去提示。
/// 本类**不持有**任何长生命期的热键注册——运行期的监听由钩子承担，不在这里。
/// </para>
/// </summary>
internal static class HotKeyProbe
{
    private const int HotKeyId = 0x4B4D; // 任意值，仅用于探测期的这一次注册。

    /// <summary>RegisterHotKey 失败时「已被别的程序占用」的错误码（winerror.h）。</summary>
    private const int ErrorHotKeyAlreadyRegistered = 1409;

    // 修饰键常量（RegisterHotKey 的 fsModifiers 参数，winuser.h）。
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;

    private const uint ModNoRepeat = 0x4000;

    /// <summary>
    /// 试注册再注销，判断该按键能否被系统热键占用。
    /// </summary>
    /// <param name="binding">待测绑定；鼠标键无法被系统热键注册，直接失败。</param>
    /// <param name="message">失败时写给用户看的中文原因；成功时为 <c>null</c>。</param>
    /// <returns>可注册为真。</returns>
    public static bool TryProbe(KeyBinding? binding, out string? message)
    {
        message = null;

        if (binding?.MainKey is not { } mainKey)
        {
            message = "该按键不是键盘键，无法用作系统级热键——无法探测该键是否已被其他程序占用。";
            return false;
        }

        // VirtualKeyFromKey 对无法映射为虚拟键码的 enum 值会返回 0（如某些未定义项）。
        // 0 不是合法的虚拟键码：拿它去注册必然失败，且错误码会指向「已被占用」之外的原因，
        // 把用户引到错误的排查方向。这里当场说清楚是「这个键不能用作热键」。
        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(mainKey);
        if (virtualKey == 0)
        {
            message = "该按键无法映射为系统虚拟键码，不能用作系统级热键。";
            return false;
        }

        var modifiers = DescribeModifiers(binding.Modifiers);

        // 传 IntPtr.Zero 作窗口句柄：探测不关心通知投递给谁，只关心系统肯不肯把这键交出来。
        // 注册成功立刻注销，其他程序完全感受不到这次探测。
        if (RegisterHotKey(IntPtr.Zero, HotKeyId, modifiers, virtualKey))
        {
            UnregisterHotKey(IntPtr.Zero, HotKeyId);
            return true;
        }

        var error = Marshal.GetLastWin32Error();

        // 只有「已被别的程序注册」才是「占用」——其余失败（权限、无效句柄等）另有原因。
        // 一律说成「已被占用」会让用户白费力气去关别的软件。
        message = error == ErrorHotKeyAlreadyRegistered
            ? "该组合已被其他程序注册（错误码 1409）占用。不影响保存，但该键在运行时可能不生效——建议换一个组合。"
            : $"系统拒绝注册该热键（错误码 {error}），原因不是「已被占用」，请联系开发者排查。";
        return false;
    }

    /// <summary>把修饰键集合折算成 <c>RegisterHotKey</c> 的位标志。</summary>
    private static uint DescribeModifiers(IReadOnlySet<WpfKey> modifiers)
    {
        var flags = ModNoRepeat;

        foreach (var modifier in modifiers)
        {
            flags |= modifier switch
            {
                WpfKey.LeftCtrl or WpfKey.RightCtrl => ModControl,
                WpfKey.LeftAlt or WpfKey.RightAlt => ModAlt,
                WpfKey.LeftShift or WpfKey.RightShift => ModShift,
                WpfKey.LWin or WpfKey.RWin => ModWin,
                _ => 0,
            };
        }

        return flags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);
}
