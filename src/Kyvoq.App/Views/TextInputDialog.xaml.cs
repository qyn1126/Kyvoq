using System.Windows;
using System.Windows.Input;
using Kyvoq.App.Services;
using Kyvoq.Core.Models;

namespace Kyvoq.App.Views;

/// <summary>
/// 提供名称等短文本输入的现代化模态窗口。
/// </summary>
public partial class TextInputDialog : Window
{
    private readonly ThemeService themeService;
    private readonly AppTheme theme;

    public string Value => ValueTextBox.Text.Trim();

    /// <summary>
    /// 创建文本输入窗口。
    /// </summary>
    /// <param name="title">窗口标题。</param>
    /// <param name="prompt">输入框提示。</param>
    /// <param name="initialValue">初始文本。</param>
    /// <param name="themeService">主题服务。</param>
    /// <param name="theme">当前主题。</param>
    public TextInputDialog(
        string title,
        string prompt,
        string initialValue,
        ThemeService themeService,
        AppTheme theme)
    {
        InitializeComponent();
        this.themeService = themeService;
        this.theme = theme;
        Title = title;
        TitleText.Text = title;
        PromptText.Text = prompt;
        ValueTextBox.Text = initialValue;
        SourceInitialized += HandleSourceInitialized;
        Loaded += HandleLoaded;
    }

    /// <summary>
    /// 应用窗口 DWM 外观。
    /// </summary>
    /// <param name="sender">当前窗口。</param>
    /// <param name="eventArgs">事件参数。</param>
    private void HandleSourceInitialized(object? sender, EventArgs eventArgs) =>
        themeService.ApplyWindowBackdrop(this, theme);

    /// <summary>
    /// 在窗口显示后选中初始文本并聚焦输入框。
    /// </summary>
    /// <param name="sender">当前窗口。</param>
    /// <param name="eventArgs">事件参数。</param>
    private void HandleLoaded(object sender, RoutedEventArgs eventArgs)
    {
        ValueTextBox.Focus();
        ValueTextBox.SelectAll();
    }

    /// <summary>
    /// 拖动自定义标题栏移动窗口。
    /// </summary>
    /// <param name="sender">标题栏。</param>
    /// <param name="eventArgs">鼠标事件参数。</param>
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    /// <summary>
    /// 校验文本后确认对话框。
    /// </summary>
    /// <param name="sender">确定按钮。</param>
    /// <param name="eventArgs">事件参数。</param>
    private void Confirm_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (Value.Length == 0)
        {
            MessageBox.Show(this, "输入内容不能为空。", "Kyvoq", MessageBoxButton.OK, MessageBoxImage.Information);
            ValueTextBox.Focus();
            return;
        }

        DialogResult = true;
    }

    /// <summary>
    /// 取消并关闭对话框。
    /// </summary>
    /// <param name="sender">取消控件。</param>
    /// <param name="eventArgs">事件参数。</param>
    private void Cancel_Click(object sender, RoutedEventArgs eventArgs) => DialogResult = false;

    /// <summary>
    /// 允许用户按 Enter 快速确认或按 Escape 取消。
    /// </summary>
    /// <param name="sender">文本输入框。</param>
    /// <param name="eventArgs">键盘事件参数。</param>
    private void ValueTextBox_KeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Enter)
        {
            Confirm_Click(sender, eventArgs);
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.Escape)
        {
            DialogResult = false;
            eventArgs.Handled = true;
        }
    }
}
