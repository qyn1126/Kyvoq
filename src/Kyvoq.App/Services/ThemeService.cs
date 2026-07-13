using System.Windows;
using System.Windows.Media;
using Kyvoq.Core.Models;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using UiApplicationTheme = Wpf.Ui.Appearance.ApplicationTheme;

namespace Kyvoq.App.Services;

/// <summary>
/// 使用 WPF UI 协调应用主题、系统强调色和窗口背景材质。
/// </summary>
public sealed class ThemeService
{
    private AppSettings currentSettings = new();

    /// <summary>
    /// 获取当前设置实际对应的深色模式状态。
    /// </summary>
    /// <param name="theme">用户选择的主题。</param>
    /// <returns>应该使用深色资源时返回 <see langword="true"/>。</returns>
    public bool IsDark(AppTheme theme) => ResolveTheme(theme) == UiApplicationTheme.Dark;

    /// <summary>
    /// 使用完整设置更新应用主题和强调色。
    /// </summary>
    /// <param name="settings">当前应用设置。</param>
    public void ApplyApplicationTheme(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        currentSettings = settings.Clone();
        var resolvedTheme = ResolveTheme(settings.Theme);
        ApplicationThemeManager.Apply(resolvedTheme, WindowBackdropType.Mica, updateAccent: false);
        if (settings.AccentMode == AccentMode.System || resolvedTheme == UiApplicationTheme.HighContrast)
        {
            ApplicationAccentColorManager.ApplySystemAccent();
        }
        else
        {
            ApplicationAccentColorManager.Apply(
                ToColor(settings.CustomAccentArgb),
                resolvedTheme,
                systemGlassColor: false,
                systemAccentColor: false);
        }

        UpdateCompatibilityResources(resolvedTheme);
    }

    /// <summary>
    /// 仅使用指定主题更新应用外观，并保留当前强调色设置。
    /// </summary>
    /// <param name="theme">需要应用的主题模式。</param>
    public void ApplyApplicationTheme(AppTheme theme)
    {
        var settings = currentSettings.Clone();
        settings.Theme = theme;
        ApplyApplicationTheme(settings);
    }

    /// <summary>
    /// 为窗口启用 WPF UI 的 Mica 背景和系统主题监听。
    /// </summary>
    /// <param name="window">需要设置外观的窗口。</param>
    /// <param name="theme">当前主题模式。</param>
    public void ApplyWindowBackdrop(Window window, AppTheme theme)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window is FluentWindow fluentWindow)
        {
            fluentWindow.WindowBackdropType = WindowBackdropType.Mica;
            fluentWindow.WindowCornerPreference = WindowCornerPreference.Round;
        }

        WindowBackgroundManager.UpdateBackground(window, ResolveTheme(theme), WindowBackdropType.Mica);

        if (theme == AppTheme.System)
        {
            SystemThemeWatcher.Watch(
                window,
                WindowBackdropType.Mica,
                updateAccents: currentSettings.AccentMode == AccentMode.System);
        }
        else
        {
            SystemThemeWatcher.UnWatch(window);
            ApplicationThemeManager.Apply(window);
        }
    }

    /// <summary>
    /// 将持久化的 ARGB 数值转换为 WPF 颜色。
    /// </summary>
    /// <param name="argb">ARGB 颜色值。</param>
    /// <returns>对应的 WPF 颜色。</returns>
    public static Color ToColor(uint argb) => Color.FromArgb(
        (byte)(argb >> 24),
        (byte)(argb >> 16),
        (byte)(argb >> 8),
        (byte)argb);

    /// <summary>
    /// 将 WPF 颜色转换为可持久化的 ARGB 数值。
    /// </summary>
    /// <param name="color">待转换颜色。</param>
    /// <returns>不透明的 ARGB 颜色值。</returns>
    public static uint ToArgb(Color color) =>
        0xFF000000u | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;

    /// <summary>
    /// 把用户主题选择解析为 WPF UI 应用主题。
    /// </summary>
    /// <param name="theme">用户选择的主题。</param>
    /// <returns>当前应该应用的具体主题。</returns>
    private static UiApplicationTheme ResolveTheme(AppTheme theme)
    {
        if (ApplicationThemeManager.IsSystemHighContrast())
        {
            return UiApplicationTheme.HighContrast;
        }

        return theme switch
        {
            AppTheme.Dark => UiApplicationTheme.Dark,
            AppTheme.Light => UiApplicationTheme.Light,
            _ => SystemThemeManager.GetCachedSystemTheme() == SystemTheme.Dark
                ? UiApplicationTheme.Dark
                : UiApplicationTheme.Light
        };
    }

    /// <summary>
    /// 更新旧版视图仍使用的语义颜色资源，使其与 WPF UI 主题保持一致。
    /// </summary>
    /// <param name="theme">当前具体主题。</param>
    private static void UpdateCompatibilityResources(UiApplicationTheme theme)
    {
        if (theme == UiApplicationTheme.HighContrast)
        {
            SetBrush("WindowBackgroundBrush", SystemColors.WindowColor);
            SetBrush("SidebarBackgroundBrush", SystemColors.WindowColor);
            SetBrush("CardBackgroundBrush", SystemColors.WindowColor);
            SetBrush("CardHoverBrush", SystemColors.HighlightColor);
            SetBrush("SelectedBackgroundBrush", SystemColors.HighlightColor);
            SetBrush("TextPrimaryBrush", SystemColors.WindowTextColor);
            SetBrush("TextSecondaryBrush", SystemColors.GrayTextColor);
            SetBrush("BorderBrush", SystemColors.WindowTextColor);
            SetBrush("OverlayBrush", SystemColors.WindowColor);
            Application.Current.Resources["AccentColor"] = SystemColors.HighlightColor;
            Application.Current.Resources["AccentSecondaryColor"] = SystemColors.HighlightColor;
            SetBrush("AccentBrush", SystemColors.HighlightColor);
            SetBrush("AccentSecondaryBrush", SystemColors.HighlightColor);
            return;
        }

        var dark = theme == UiApplicationTheme.Dark;
        SetBrush("WindowBackgroundBrush", dark ? "#E61A1B24" : "#F7F8FA");
        SetBrush("SidebarBackgroundBrush", dark ? "#E6232430" : "#EEF0F5");
        SetBrush("CardBackgroundBrush", dark ? "#F02B2D39" : "#FCFCFE");
        SetBrush("CardHoverBrush", dark ? "#3D7C5CFC" : "#EDE9FF");
        SetBrush("SelectedBackgroundBrush", dark ? "#667C5CFC" : "#DED7FF");
        SetBrush("TextPrimaryBrush", dark ? "#F5F4FA" : "#20212A");
        SetBrush("TextSecondaryBrush", dark ? "#A8A9B6" : "#6F7180");
        SetBrush("BorderBrush", dark ? "#26FFFFFF" : "#18000000");
        SetBrush("OverlayBrush", dark ? "#E61A1B24" : "#E6FFFFFF");
        var accent = ApplicationAccentColorManager.SystemAccent;
        var secondary = ApplicationAccentColorManager.SecondaryAccent;
        Application.Current.Resources["AccentColor"] = accent;
        Application.Current.Resources["AccentSecondaryColor"] = secondary;
        SetBrush("AccentBrush", accent);
        SetBrush("AccentSecondaryBrush", secondary);
    }

    /// <summary>
    /// 使用冻结画刷替换指定应用资源。
    /// </summary>
    /// <param name="resourceKey">画刷资源键。</param>
    /// <param name="colorText">十六进制颜色文本。</param>
    private static void SetBrush(string resourceKey, string colorText) =>
        SetBrush(resourceKey, (Color)ColorConverter.ConvertFromString(colorText));

    /// <summary>
    /// 使用指定颜色的冻结画刷替换应用资源。
    /// </summary>
    /// <param name="resourceKey">画刷资源键。</param>
    /// <param name="color">画刷颜色。</param>
    private static void SetBrush(string resourceKey, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Application.Current.Resources[resourceKey] = brush;
    }
}
