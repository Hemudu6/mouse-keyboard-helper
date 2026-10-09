using Core.Config;
using Core.Keys;
using Host.Engine;
using Host.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WpfKey = System.Windows.Input.Key;

namespace Host.Views;

/// <summary>
/// 设置页（设计文档 §6.5.5）。
///
/// <para>
/// <b>为什么持有副本</b>：<c>HostFacade.CurrentSettings</c> 每次返回的都是**同一个实例**
/// （<c>Settings.Value</c>），直接改它等于在用户点「保存」之前就把生效配置改了。
/// 本页因此在导航进入时复制一份自持，只有保存那一步才把它交给门面下盘。
/// </para>
/// <para>
/// <b>本页没有"退出程序"按钮</b>（§6.5.5）：退出走托盘菜单，
/// 设置的职责只是改配置，不承担进程生命周期。
/// </para>
/// </summary>
public sealed partial class SettingsPage : Page
{
    /// <summary>组合根。由导航参数取出（裸 <see cref="HostFacade"/>），早于导航不可用。</summary>
    private HostFacade _facade = null!;

    /// <summary>本页自持的设置副本（见类型注释：绝不能直接改门面里的那一份）。</summary>
    private AppSettings _settings = new();

    /// <summary>捕获进行中的标志：防止连点两次「捕获按键」叠出两个等待者。</summary>
    private bool _capturing;

    /// <summary>
    /// 弹窗串行闸：同一 <c>XamlRoot</c> 只允许一个 <see cref="ContentDialog"/> 在屏，
    /// 并发 <c>ShowAsync()</c> 会抛 <c>COMException (0x80000019)</c>。
    /// <para>
    /// 本页唯一的弹窗点是 <see cref="ShowMessage"/>，但捕获失败（钩子不可用 / 超时 /
    /// 按键被占）与保存被拒可能并发触发。经 <see cref="ShowDialogAsync"/> 串行化后，
    /// 后来的提示排队而非抛异常。
    /// </para>
    /// </summary>
    private readonly SemaphoreSlim _dialogGate = new(1, 1);

    /// <summary>构造设置页。</summary>
    public SettingsPage()
    {
        InitializeComponent();

        CaptureEmergencyStopButton.Click += OnCaptureClick;
        ResetEmergencyStopButton.Click += OnResetClick;
        SaveSettingsButton.Click += OnSaveClick;
        CancelSettingsButton.Click += OnCancelClick;
    }

    /// <inheritdoc/>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not HostFacade facade)
        {
            // 导航调用方写错了。不静默兜底。
            throw new InvalidOperationException(
                $"{nameof(SettingsPage)} 必须携带 HostFacade 导航进入。");
        }

        _facade = facade;

        _settings = CopyOf(facade.CurrentSettings);
        ShowEmergencyStopKey();
    }

    /// <summary>
    /// 复制一份设置。逐字段复制而不是序列化往返：字段少、语义清晰，
    /// 也不会把"保存失败"这种运行态意外卷进副本。
    /// </summary>
    private static AppSettings CopyOf(AppSettings source) => new()
    {
        EmergencyStopKey = source.EmergencyStopKey,
        SchemaVersion = source.SchemaVersion,
    };

    // ── 紧急停止键 ────────────────────────────────────────────────

    /// <summary>
    /// 捕获紧急停止键：进钩子录制态、后台线等待、回 UI 线写入副本。
    ///
    /// <para>
    /// <b>"注册即注销"探测</b>：紧急停止键本身由低级键盘钩子监听生效（设计文档 §3.2），
    /// <b>不依赖</b>系统级热键注册。这里试注册一次再立刻注销，是为了回答另一个问题——
    /// **这个键有没有被别的程序用系统热键占着**，好让用户尽早知道，
    /// 而不是等到真要紧急停止时才发现该组合已被别的软件截获。
    /// 探测失败只提示、不阻止保存：占用是运行环境的事，用户可能就是要先存下来、
    /// 回头关掉占用的程序。
    /// </para>
    /// </summary>
    private async void OnCaptureClick(object sender, RoutedEventArgs e)
    {
        if (_capturing)
        {
            return;
        }

        if (!_facade.Hooks.BeginCapture())
        {
            await ShowMessage("无法捕获", "输入钩子当前不可用（可能尚未启动）。");
            return;
        }

        _capturing = true;
        CaptureEmergencyStopButton.IsEnabled = false;
        CaptureRing.IsActive = true;

        var captured = await Task.Run(() => _facade.Hooks.WaitForCapture(5000));

        // 页面已在等待期间被导航走：控件不在树上，回写与提示均无意义（且提示会抛）。
        if (!IsLoaded)
        {
            return;
        }

        _capturing = false;
        CaptureEmergencyStopButton.IsEnabled = true;
        CaptureRing.IsActive = false;

        if (captured is not { } keyEvent)
        {
            await ShowMessage("未捕获到按键", "等待超时（5 秒）。请重新点击「捕获按键」。");
            return;
        }

        _settings.EmergencyStopKey = BuildBinding(keyEvent);
        ShowEmergencyStopKey();

        // 探测结果一律告知（设计文档 §6.1）：失败说明被别的进程占着，成功也不能当作没事——
        // 本工具的紧急停止**用低级钩子监听**而非 RegisterHotKey（§3.2），所以即使这个组合
        // 空闲，它仍可能与系统快捷键语义重叠（如 Win+L 锁屏），届时两边都会响应。
        // 探测失败只提示、不拦保存：占用是运行环境的事，用户可能就是要先存下来、回头再改。
        if (HotKeyProbe.TryProbe(_settings.EmergencyStopKey, out var failureMessage))
        {
            await ShowMessage(
                "该键可用",
                "该键当前未被其他程序占用。\n\n注意：本工具的紧急停止通过全局键盘钩子监听，"
                    + "因此该组合即使无人注册，仍可能与系统快捷键语义重叠（例如 Win 系组合），"
                    + "届时两边都会响应。");
        }
        else
        {
            await ShowMessage("该键可能不可用", failureMessage);
        }
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        _facade.Hooks.CancelCapture();
        _settings.EmergencyStopKey = new AppSettings().EmergencyStopKey;
        ShowEmergencyStopKey();
    }

    private static KeyBinding BuildBinding(HookService.CapturedKeyEvent keyEvent)
    {
        var modifiers = new List<WpfKey>();
        foreach (var family in keyEvent.ModifierFamilies)
        {
            foreach (var modifier in KeyCatalog.KeysOfFamily(family))
            {
                modifiers.Add(modifier);
                break; // 每族一个代表键（修饰键按族归一）。
            }
        }

        // 鼠标键的 KeyboardVirtualKey 为 null，直接取它就是崩。这里按鼠标单键形态绑定，
        // 让下游 HotKeyProbe 拿到 binding.MainKey == null 后走"鼠标键不能作系统热键"的分支——
        // 那条分支正是为此存在的（见 HotKeyProbe.TryProbe）。
        if (keyEvent.PrimaryKey.Mouse is { } mouseKey)
        {
            return KeyBinding.FromMouse(mouseKey);
        }

        var mainKey = keyEvent.PrimaryKey.KeyboardVirtualKey
            ?? throw new InvalidOperationException(
                "捕获到的紧急停止键既不是键盘键也不是鼠标键，无法构造绑定。");
        return modifiers.Count == 0
            ? KeyBinding.FromKey(mainKey)
            : KeyBinding.FromCombo(modifiers, mainKey);
    }

    private void ShowEmergencyStopKey()
        => EmergencyStopBox.Text = KeyCatalog.Describe(_settings.EmergencyStopKey);

    // ── 保存 / 取消 ───────────────────────────────────────────────

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var result = _facade.SaveSettings(_settings);
        if (!result.Succeeded)
        {
            await ShowMessage("无法保存", result.Message);
            return;
        }

        GoBack();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => GoBack();

    private void GoBack()
    {
        _facade.Hooks.CancelCapture();

        Navigator.ResolveFor(Frame).GoBackToTaskList();
    }

    // ── 工具 ──────────────────────────────────────────────────────

    /// <summary>
    /// 阻断式提示：保存失败必须停在原地让用户读完再改。
    ///
    /// <para>
    /// <b>页面已卸载时直接返回</b>：本页的提示都发生在 <c>await</c> 之后（后台等捕获、保存回写），
    /// 此时用户可能已经保存/取消导航走了。页面一离开视觉树，<c>XamlRoot</c> 就变成 <c>null</c>，
    /// 再拿它构造 <c>ContentDialog</c> 会抛异常——而调用方是 <c>async void</c>，
    /// 异常直接成为进程级未处理异常。没人再看这一页，静默丢弃正是正确的。
    /// </para>
    /// </summary>
    private async Task ShowMessage(string title, string? message)
    {
        if (XamlRoot is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message ?? string.Empty,
            CloseButtonText = "知道了",
        };

        await ShowDialogAsync(dialog);
    }

    /// <summary>
    /// 弹窗统一入口：本页所有 <c>ContentDialog.ShowAsync()</c> 的唯一调用点。
    /// 用 <see cref="_dialogGate"/> 把并发弹窗串行化（同 <c>XamlRoot</c> 不允许两个在屏）。
    /// 锁只包住 <c>ShowAsync</c>；<c>finally</c> 保证信号量归还，不会把页面锁死。
    /// </summary>
    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        await _dialogGate.WaitAsync();
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            _dialogGate.Release();
        }
    }
}
