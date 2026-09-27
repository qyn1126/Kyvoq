using System.Windows;
using System.Windows.Threading;
using Kyvoq.App.Views;

namespace Kyvoq.App.Services;

/// <summary>表示应用消息的提示级别。</summary>
public enum MessageDialogSeverity
{
    Information,
    Question,
    Warning,
    Error
}

/// <summary>以应用主题显示消息及确认，并统一处理所属窗口和 UI 线程。</summary>
public sealed class MessageDialogService
{
    private readonly ThemeService themeService;

    /// <summary>使用应用共享的主题服务创建消息入口。</summary>
    public MessageDialogService(ThemeService themeService) => this.themeService = themeService;

    /// <summary>显示只有确定按钮的提示，关闭后继续原操作。</summary>
    public void ShowMessage(Window? owner, string title, string message,
        MessageDialogSeverity severity = MessageDialogSeverity.Information) =>
        Show(owner, title, message, severity, null);

    /// <summary>显示默认取消的确认框，仅在用户明确确认时返回真。</summary>
    public bool Confirm(Window? owner, string title, string message,
        MessageDialogSeverity severity = MessageDialogSeverity.Question, string confirmText = "确定") =>
        Show(owner, title, message, severity, confirmText);

    /// <summary>在 UI 线程创建独立的模态窗口，后台或退出提示不依赖主窗口句柄。</summary>
    private bool Show(Window? owner, string title, string message, MessageDialogSeverity severity, string? confirmText)
    {
        var dispatcher = Application.Current?.Dispatcher ?? owner?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return false;
        }

        if (!dispatcher.CheckAccess())
        {
            return dispatcher.Invoke(() => Show(owner, title, message, severity, confirmText));
        }

        var visibleOwner = ResolveOwner(owner);
        var dialog = new MessageDialog(title, message, severity, confirmText, themeService)
        {
            Owner = visibleOwner,
            WindowStartupLocation = visibleOwner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = visibleOwner is null
        };
        return dialog.ShowDialog() == true;
    }

    /// <summary>选择可见的调用窗口及其最内层对话框，跳过未显示、隐藏或已关闭的窗口。</summary>
    internal static Window? ResolveOwner(Window? owner)
    {
        if (owner is not { IsVisible: true, WindowState: not WindowState.Minimized })
        {
            owner = Application.Current?.Windows.Cast<Window>()
                .FirstOrDefault(window => window.IsVisible && window.IsActive);
        }

        while (owner is not null)
        {
            var children = owner.OwnedWindows.Cast<Window>()
                .Where(window => window.IsVisible && window.WindowState != WindowState.Minimized).ToArray();
            var child = children.FirstOrDefault(window => window.IsActive) ?? children.LastOrDefault();
            if (child is null)
            {
                return owner;
            }

            owner = child;
        }

        return null;
    }
}
