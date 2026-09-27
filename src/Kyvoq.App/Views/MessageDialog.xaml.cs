using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Kyvoq.App.Services;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;
using Screen = System.Windows.Forms.Screen;

namespace Kyvoq.App.Views;

/// <summary>沿用应用标题栏、按钮和主题资源的消息及确认窗口。</summary>
public partial class MessageDialog : FluentWindow
{
    private readonly ThemeService themeService;
    private readonly bool isConfirmation;

    /// <summary>创建消息窗口；提供确认按钮文字时显示取消按钮。</summary>
    public MessageDialog(string title, string message, MessageDialogSeverity severity,
        string? confirmText, ThemeService themeService)
    {
        InitializeComponent();
        this.themeService = themeService;
        isConfirmation = confirmText is not null;
        Title = title;
        TitleText.Text = title;
        TitleText.ToolTip = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText ?? "确定";
        CancelButton.Visibility = isConfirmation ? Visibility.Visible : Visibility.Collapsed;
        SeverityIcon.Text = severity switch
        {
            MessageDialogSeverity.Question => "\uE897",
            MessageDialogSeverity.Warning => "\uE7BA",
            MessageDialogSeverity.Error => "\uEA39",
            _ => "\uE946"
        };
        if (severity == MessageDialogSeverity.Error)
        {
            SeverityIcon.SetResourceReference(ForegroundProperty, "DangerBrush");
        }

        SourceInitialized += HandleSourceInitialized;
        Loaded += HandleLoaded;
    }

    /// <summary>应用当前窗口材质，并按目标显示器工作区限制长消息的高度。</summary>
    private void HandleSourceInitialized(object? sender, EventArgs eventArgs)
    {
        themeService.ApplyCurrentWindowBackdrop(this);
        var handle = new WindowInteropHelper(Owner ?? this).Handle;
        var workArea = Screen.FromHandle(handle).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(Owner ?? this);
        MaxHeight = Math.Max(180, workArea.Height / dpi.DpiScaleY - 48);
        Width = Math.Min(440, Math.Max(240, workArea.Width / dpi.DpiScaleX - 48));
        MessageScrollViewer.MaxHeight = Math.Max(32, MaxHeight - 154);
    }

    /// <summary>确认操作默认聚焦取消，普通消息默认聚焦确定。</summary>
    private void HandleLoaded(object sender, RoutedEventArgs eventArgs)
    {
        var button = isConfirmation ? CancelButton : ConfirmButton;
        button.Focus();
        Keyboard.Focus(button);
    }

    /// <summary>拖动自定义标题区域移动窗口。</summary>
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    /// <summary>明确确认后关闭模态窗口。</summary>
    private void Confirm_Click(object sender, RoutedEventArgs eventArgs) => DialogResult = true;

    /// <summary>取消或关闭窗口，不提交确认操作。</summary>
    private void Cancel_Click(object sender, RoutedEventArgs eventArgs) => DialogResult = false;

    /// <summary>Escape 取消，Enter 执行聚焦按钮，并忽略长按产生的重复按键。</summary>
    private void Dialog_PreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key is not (Key.Enter or Key.Escape))
        {
            return;
        }

        eventArgs.Handled = true;
        if (!eventArgs.IsRepeat)
        {
            DialogResult = eventArgs.Key == Key.Enter
                && (ConfirmButton.IsKeyboardFocusWithin
                    || (!isConfirmation && !CloseButton.IsKeyboardFocusWithin));
        }
    }
}
