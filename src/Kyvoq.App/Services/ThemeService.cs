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
        var backdropType = ResolveBackdrop(resolvedTheme, settings.WindowMaterial);
        ApplicationThemeManager.Apply(resolvedTheme, backdropType, updateAccent: false);
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

        UpdateCompatibilityResources(resolvedTheme, backdropType);
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
    /// 为窗口安全启用指定的 WPF UI 背景材质和系统主题监听。
    /// </summary>
    /// <param name="window">需要设置外观的窗口。</param>
    /// <param name="theme">当前主题模式。</param>
    /// <param name="material">用户选择的窗口材质。</param>
    public void ApplyWindowBackdrop(Window window, AppTheme theme, WindowMaterial material)
    {
        ArgumentNullException.ThrowIfNull(window);
        var resolvedTheme = ResolveTheme(theme);
        var backdropType = ResolveBackdrop(resolvedTheme, material);
        if (window is FluentWindow fluentWindow)
        {
            fluentWindow.WindowBackdropType = backdropType;
            fluentWindow.WindowCornerPreference = WindowCornerPreference.Round;
        }

        WindowBackgroundManager.UpdateBackground(window, resolvedTheme, backdropType);

        if (theme == AppTheme.System)
        {
            SystemThemeWatcher.Watch(
                window,
                backdropType,
                updateAccents: currentSettings.AccentMode == AccentMode.System);
        }
        else
        {
            if (window.IsLoaded)
            {
                SystemThemeWatcher.UnWatch(window);
            }

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
    /// 将与界面无关的材质设置映射为 WPF UI 背景类型。
    /// </summary>
    /// <param name="material">用户选择的窗口材质。</param>
    /// <returns>对应的 WPF UI 背景类型。</returns>
    internal static WindowBackdropType ToBackdropType(WindowMaterial material) => material switch
    {
        WindowMaterial.Solid => WindowBackdropType.None,
        WindowMaterial.MicaAlt => WindowBackdropType.Tabbed,
        WindowMaterial.Acrylic => WindowBackdropType.Acrylic,
        _ => WindowBackdropType.Mica
    };

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
    /// 解析当前平台实际可用的窗口材质，高对比度或不支持时安全降级。
    /// </summary>
    /// <param name="theme">已经解析的应用主题。</param>
    /// <param name="material">用户选择的窗口材质。</param>
    /// <returns>当前平台应该实际应用的背景类型。</returns>
    private static WindowBackdropType ResolveBackdrop(
        UiApplicationTheme theme,
        WindowMaterial material)
    {
        if (theme == UiApplicationTheme.HighContrast)
        {
            return WindowBackdropType.None;
        }

        var requestedBackdrop = ToBackdropType(material);
        if (requestedBackdrop == WindowBackdropType.None
            || WindowBackdrop.IsSupported(requestedBackdrop))
        {
            return requestedBackdrop;
        }

        return WindowBackdrop.IsSupported(WindowBackdropType.Mica)
            ? WindowBackdropType.Mica
            : WindowBackdropType.None;
    }

    /// <summary>
    /// 更新旧版视图仍使用的语义颜色资源，使其与 WPF UI 主题保持一致。
    /// </summary>
    /// <param name="theme">当前具体主题。</param>
    /// <param name="backdropType">当前实际使用的窗口背景类型。</param>
    private static void UpdateCompatibilityResources(
        UiApplicationTheme theme,
        WindowBackdropType backdropType)
    {
        if (theme == UiApplicationTheme.HighContrast)
        {
            SetBrush("WindowBackgroundBrush", SystemColors.WindowColor);
            SetBrush("SidebarBackgroundBrush", SystemColors.WindowColor);
            SetBrush("CardBackgroundBrush", SystemColors.WindowColor);
            SetBrush("CardHoverBrush", SystemColors.HighlightColor);
            SetBrush("ItemHoverBrush", SystemColors.HighlightColor);
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
        var usesBackdrop = backdropType != WindowBackdropType.None;
        SetBrush(
            "WindowBackgroundBrush",
            usesBackdrop ? "#00FFFFFF" : dark ? "#1A1B24" : "#F7F8FA");
        SetBrush(
            "SidebarBackgroundBrush",
            usesBackdrop
                ? dark ? "#D9232430" : "#D9EEF0F5"
                : dark ? "#232430" : "#EEF0F5");
        SetBrush(
            "CardBackgroundBrush",
            usesBackdrop
                ? dark ? "#D92B2D39" : "#D9FCFCFE"
                : dark ? "#2B2D39" : "#FCFCFE");
        SetBrush("CardHoverBrush", dark ? "#3D7C5CFC" : "#EDE9FF");
        SetBrush("ItemHoverBrush", dark ? "#18FFFFFF" : "#0D000000");
        SetBrush("SelectedBackgroundBrush", dark ? "#667C5CFC" : "#DED7FF");
        SetBrush("TextPrimaryBrush", dark ? "#F5F4FA" : "#20212A");
        SetBrush("TextSecondaryBrush", dark ? "#A8A9B6" : "#6F7180");
        SetBrush("BorderBrush", dark ? "#26FFFFFF" : "#18000000");
        SetBrush(
            "OverlayBrush",
            usesBackdrop
                ? dark ? "#D91A1B24" : "#D9FFFFFF"
                : dark ? "#1A1B24" : "#FFFFFF");
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
