using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Launcher;

/// <summary>
/// 启动壳（设计文档 §2.2 / §1.4）。
///
/// 职责只有两件事：
///   1. 以普通权限启动宿主（宿主清单 requireAdministrator，由系统弹出 UAC）；
///   2. 把"提权失败"这一结果以用户能读懂的方式告知，而不是静默消失。
///
/// 为什么必须独立于宿主：若同一个 EXE 带 requireAdministrator 清单，
/// 外壳自身也会被强制提权，就丧失了"以普通权限提示失败"的能力（AC-26）。
///
/// 本进程不申请提权，因此也不需要清单文件。
/// </summary>
internal static class Program
{
    /// <summary>宿主可执行体文件名（与 Launcher 同目录）。须与 Host.csproj 的 AssemblyName 一致。</summary>
    private const string HostExecutableName = "鼠标键盘辅助.exe";

    /// <summary>用户取消 UAC 提权时 Win32 返回的错误码（ERROR_CANCELLED）。实测确认，见设计文档 §9.3。</summary>
    private const int ErrorCancelled = 1223;

    /// <summary>启动失败：被系统策略阻止提权（软件限制策略 / 组策略）。</summary>
    private const int ErrorAccessDenied = 5;

    [STAThread]
    private static int Main()
    {
        var hostPath = ResolveHostPath();
        if (hostPath is null)
        {
            ShowError(
                "找不到主程序",
                $"启动壳旁边没有找到 {HostExecutableName}。\n\n" +
                "请确认两个可执行体（Launcher.exe 与宿主程序）位于同一目录，这是本工具的正常分发形态。");
            return 1;
        }

        try
        {
            // UseShellExecute = true 是硬要求：
            // 只有它才会走 ShellExecute 路径去触发 UAC 提权（清单里的 requireAdministrator 才生效）。
            // 一旦设为 false，CreateProcess 会因清单要求提权而直接以 ERROR_ELEVATION_REQUIRED(740) 失败。
            //
            // 注意：Start() 是同步阻塞的——它会一直等到用户在 UAC 对话框上作出选择才返回。
            // 这段等待可能长达数秒（实测约 9.2 秒），且本进程没有界面，
            // 期间表现为"双击后没反应"。这是 Windows 提权机制的固有行为，不是卡死。
            Process.Start(new ProcessStartInfo
            {
                FileName = hostPath,
                WorkingDirectory = Path.GetDirectoryName(hostPath)!,
                UseShellExecute = true,
            });

            // 走到这里即为成功。注意判定方式：
            // 宿主是常驻进程，启动后不会退出，因此绝不能用 ExitCode 或"进程是否结束"来判定成败；
            // 唯一正确的判据是"Start() 有没有抛异常"（即 UAC 是否通过）。
            return 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            // 用户在 UAC 对话框上点了"否"。这不是错误，是用户的选择——静默退出即可，
            // 弹窗反而会变成打扰（用户刚刚明确表达了"不要运行"）。
            return 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorAccessDenied)
        {
            ShowError(
                "无法获得管理员权限",
                "系统拒绝了提权请求。\n\n" +
                "如果这台电脑由单位统一管理，可能是安全策略禁止了提权操作，\n" +
                "请联系管理员，或改用其他方式运行本工具。");
            return 1;
        }
        catch (Win32Exception ex)
        {
            ShowError(
                "启动失败",
                $"无法启动主程序（错误 {ex.NativeErrorCode}）。\n\n{ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// 定位宿主可执行体：与本启动壳位于同一目录。
    /// 不写死绝对路径——本工具是绿色分发，用户可能放在任意位置。
    ///
    /// 用 Environment.ProcessPath 而非 AppContext.BaseDirectory：
    /// 本启动壳是单文件自包含发布，此时 AppContext.BaseDirectory 指向的是
    /// 解压缓存目录（%TEMP%\.net\&lt;exe名&gt;\&lt;哈希&gt;\），而非 exe 所在目录，
    /// 在那里永远找不到宿主。（Assembly.Location 在单文件下为空串，同样不可用。）
    /// Environment.ProcessPath 返回真实 exe 路径，是单文件下唯一正确的定位依据。
    /// —— 该差异已实测确认，见设计文档 §9.3。
    /// </summary>
    private static string? ResolveHostPath()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(processPath);
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        var candidate = Path.Combine(directory, HostExecutableName);
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// 以原生 MessageBox 告知用户。
    /// 用 Win32 而非 WinForms/WPF：不必为一句提示引入整个 UI 框架，
    /// 也避免在提权失败这一路径上增加任何额外依赖。
    /// </summary>
    private static void ShowError(string title, string message)
    {
        MessageBoxW(IntPtr.Zero, message, title, MbOk | MbIconError | MbSetForeground | MbTopmost);
    }

    private const uint MbOk = 0x00000000;
    private const uint MbIconError = 0x00000010;
    private const uint MbSetForeground = 0x00010000;
    private const uint MbTopmost = 0x00040000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
