using System.Runtime.InteropServices;

namespace Host.Ui;

/// <summary>
/// 托盘图标的 Win32 层封装（设计文档 §6.3、§6.5.5）。
///
/// <para>
/// <b>为什么自己写</b>：WinUI 3 **无内建托盘 API**（§1.3）。方案是 P/Invoke
/// <c>Shell_NotifyIcon</c>，并把回调消息挂到 **WinUI 主窗口的 HWND** 上——
/// 已实测可行（§9.3 V-3），故**不另建专用 Win32 消息窗口**。
/// </para>
///
/// <para>
/// <b>本类只做三件 Win32 的事</b>：加/删托盘图标、弹右键菜单、显示窗口。
/// "菜单项被点了该干什么"由 <see cref="TrayIcon"/> 的调用方（App）决定——
/// 本类不认识 Scheduler，也不认识门面。
/// </para>
/// </summary>
internal static class TrayNative
{
    /// <summary>托盘回调消息的 <c>lParam</c> 取值（%u 段，对应鼠标/事件类型）。</summary>
    internal const int WM_LBUTTONUP = 0x0202;

    /// <summary>右键抬起：弹出菜单（§6.3 未指定触发键，用常规的右键抬起）。</summary>
    internal const int WM_RBUTTONUP = 0x0205;

    /// <summary><c>NOTIFYICONDATA.uFlags</c>：使用 <c>hWnd</c> + <c>uID</c>。</summary>
    internal const int NIF_MESSAGE = 0x01;

    /// <summary>使用 <c>hIcon</c>。</summary>
    internal const int NIF_ICON = 0x02;

    /// <summary>使用 <c>szTip</c>。</summary>
    internal const int NIF_TIP = 0x04;

    internal const int NIM_ADD = 0x00000000;
    internal const int NIM_MODIFY = 0x00000001;
    internal const int NIM_DELETE = 0x00000002;

    /// <summary><c>TrackPopupMenu</c>：返回被选中项的 Id（而不是发 <c>WM_COMMAND</c>）。</summary>
    internal const int TPM_RETURNCMD = 0x0100;

    /// <summary><c>TrackPopupMenu</c>：不向窗口发 <c>WM_COMMAND</c>。</summary>
    internal const int TPM_NONOTIFY = 0x0080;

    internal const int MF_STRING = 0x00000000;

    /// <summary>取光标位置（`TrackPopupMenu` 需要一个屏幕坐标锚点）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        internal int X;
        internal int Y;
    }

    /// <summary>
    /// 托盘图标数据。<c>Shell_NotifyIcon</c> 的 <c>NOTIFYICONDATA</c> 结构。
    ///
    /// <para>
    /// 字段名保持 Win32 原名（<c>cbSize</c> 等）以便与文档对照——这里刻意不用
    /// C# 命名规范：这个结构是要按字节布局交给系统的，名字改了反而增加核对成本。
    /// </para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NOTIFYICONDATA
    {
        internal int cbSize;
        internal IntPtr hWnd;
        internal int uID;
        internal int uFlags;
        internal int uCallbackMessage;
        internal IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        internal string szTip;

        // 后续字段本工程不使用，但 cbSize 必须覆盖到系统期望的长度，
        // 故用固定保留区把结构撑到正确大小（见 CreateNotifyIconData 注释）。
        internal int dwState;
        internal int dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        internal string szInfo;

        internal int uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        internal string szInfoTitle;

        internal int dwInfoFlags;
        internal Guid guidItem;
        internal IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AppendMenu(IntPtr hMenu, int uFlags, IntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyMenu(IntPtr hMenu);

    /// <summary>
    /// 弹出右键菜单并返回被选中项的 Id（0 = 用户没选而取消）。
    ///
    /// <para>
    /// <b>必须用 <c>TPM_RETURNCMD</c></b>：本工程的托盘菜单挂在经过子类化的
    /// WinUI 主窗口 HWND 上，若让系统发 <c>WM_COMMAND</c>，那条消息会先进入
    /// WinUI 自己的消息处理，能不能穿透到子类过程取决于框架实现——返回命令码
    /// 则完全绕过这个消息派发环节，行为确定。
    /// </para>
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int TrackPopupMenuEx(
        IntPtr hMenu,
        int fuFlags,
        int x,
        int y,
        IntPtr hWnd,
        IntPtr lptpm);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    internal const int SW_SHOW = 5;
    internal const int SW_RESTORE = 9;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>从可执行文件里取图标（托盘图标来源）。</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr ExtractIcon(IntPtr hInst, string lpszExeFileName, int nIconIndex);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr GetModuleHandle(string? lpModuleName);
}
