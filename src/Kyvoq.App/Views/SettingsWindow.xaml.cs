using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kyvoq.App.Services;
using Kyvoq.Core.Models;
using Wpf.Ui.Controls;
using Forms = System.Windows.Forms;

namespace Kyvoq.App.Views;

/// <summary>
/// 编辑主题、呼出快捷键及系统启动行为。
/// </summary>
public partial class SettingsWindow : FluentWindow
{
    private readonly AppSettings sourceSettings;
    private readonly IGlobalHotkeyService hotkeyService;
    private readonly ThemeService themeService;
    private Color customAccentColor;

    public AppSettings? ResultSettings { get; private set; }

    /// <summary>
    /// 创建应用设置窗口。
    /// </summary>
    /// <param name="settings">当前设置。</param>
    /// <param name="dataDirectory">配置数据目录。</param>
    /// <param name="hotkeyService">快捷键冲突检测服务。</param>
    /// <param name="themeService">主题服务。</param>
    public SettingsWindow(
        AppSettings settings,
        string dataDirectory,
        IGlobalHotkeyService hotkeyService,
        ThemeService themeService)
    {
        InitializeComponent();
        sourceSettings = settings?.Clone() ?? throw new ArgumentNullException(nameof(settings));
        this.hotkeyService = hotkeyService;
        this.themeService = themeService;
        ThemeComboBox.SelectedIndex = (int)sourceSettings.Theme;
        MaterialComboBox.SelectedItem = MaterialComboBox.Items
            .OfType<ComboBoxItem>()
            .First(item => string.Equals(
                item.Tag?.ToString(),
                sourceSettings.WindowMaterial.ToString(),
                StringComparison.Ordinal));
        AccentModeComboBox.SelectedIndex = (int)sourceSettings.AccentMode;
        customAccentColor = ThemeService.ToColor(sourceSettings.CustomAccentArgb);
        UpdateAccentPreview();
        MainWindowHotkeyInput.SetGesture(sourceSettings.MainWindowHotkey);
        ItemHotkeysEnabledCheckBox.IsChecked = sourceSettings.ItemHotkeysEnabled;
        StartWithWindowsCheckBox.IsChecked = sourceSettings.StartWithWindows;
        DataDirectoryText.Text = dataDirectory;
        SourceInitialized += HandleSourceInitialized;
    }

    /// <summary>
    /// 应用当前主题对应的窗口 DWM 外观。
    /// </summary>
    /// <param name="sender">当前窗口。</param>
    /// <param name="eventArgs">事件参数。</param>
    private void HandleSourceInitialized(object? sender, EventArgs eventArgs) =>
        themeService.ApplyWindowBackdrop(
            this,
            sourceSettings.Theme,
            sourceSettings.WindowMaterial);

    /// <summary>
    /// 根据强调色模式显示或隐藏自定义颜色选项。
    /// </summary>
    /// <param name="sender">强调色模式下拉框。</param>
    /// <param name="eventArgs">选择变化参数。</param>
    private void AccentModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        CustomAccentPanel.Visibility = AccentModeComboBox.SelectedIndex == (int)AccentMode.Custom
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// 打开系统颜色选择器并保存用户选择的自定义强调色。
    /// </summary>
    /// <param name="sender">选择颜色按钮。</param>
    /// <param name="eventArgs">事件参数。</param>
    private void ChooseAccentColor_Click(object sender, RoutedEventArgs eventArgs)
    {
        using var dialog = new Forms.ColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(customAccentColor.R, customAccentColor.G, customAccentColor.B)
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        customAccentColor = Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B);
        UpdateAccentPreview();
    }

    /// <summary>
    /// 校验呼出快捷键并生成新的设置副本。
    /// </summary>
    /// <param name="sender">保存按钮。</param>
    /// <param name="eventArgs">事件参数。</param>
    private void Confirm_Click(object sender, RoutedEventArgs eventArgs)
    {
        ValidationText.Text = string.Empty;
        var gesture = MainWindowHotkeyInput.Gesture;
        if (!gesture.IsValid())
        {
            ValidationText.Text = "呼出快捷键必须包含按键和至少一个修饰键。";
            MainWindowHotkeyInput.Focus();
            return;
        }

        if (!hotkeyService.IsAvailable(gesture, GlobalHotkeyService.MainWindowActionId))
        {
            ValidationText.Text = "该组合键已被其他项目或系统程序占用。";
            MainWindowHotkeyInput.Focus();
            return;
        }

        var selectedTheme = ThemeComboBox.SelectedItem is ComboBoxItem item
            && Enum.TryParse<AppTheme>(item.Tag?.ToString(), out var parsedTheme)
                ? parsedTheme
                : AppTheme.System;
        ResultSettings = sourceSettings.Clone();
        ResultSettings.Theme = selectedTheme;
        ResultSettings.WindowMaterial = MaterialComboBox.SelectedItem is ComboBoxItem materialItem
            && Enum.TryParse<WindowMaterial>(materialItem.Tag?.ToString(), out var material)
                ? material
                : WindowMaterial.Mica;
        ResultSettings.AccentMode = AccentModeComboBox.SelectedItem is ComboBoxItem accentItem
            && Enum.TryParse<AccentMode>(accentItem.Tag?.ToString(), out var accentMode)
                ? accentMode
                : AccentMode.System;
        ResultSettings.CustomAccentArgb = ThemeService.ToArgb(customAccentColor);
        ResultSettings.MainWindowHotkey = gesture;
        ResultSettings.ItemHotkeysEnabled = ItemHotkeysEnabledCheckBox.IsChecked == true;
        ResultSettings.StartWithWindows = StartWithWindowsCheckBox.IsChecked == true;
        DialogResult = true;
    }

    /// <summary>
    /// 取消设置修改并关闭窗口。
    /// </summary>
    /// <param name="sender">取消控件。</param>
    /// <param name="eventArgs">事件参数。</param>
    private void Cancel_Click(object sender, RoutedEventArgs eventArgs) => DialogResult = false;

    /// <summary>
    /// 更新自定义强调色的色块和十六进制文本。
    /// </summary>
    private void UpdateAccentPreview()
    {
        AccentColorPreview.Background = new SolidColorBrush(customAccentColor);
        AccentHexText.Text = $"#{customAccentColor.R:X2}{customAccentColor.G:X2}{customAccentColor.B:X2}";
        CustomAccentPanel.Visibility = AccentModeComboBox.SelectedIndex == (int)AccentMode.Custom
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
}
