namespace Kyvoq.Core.Models;

/// <summary>
/// 表示启动器的用户设置。
/// </summary>
public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;

    public WindowMaterial WindowMaterial { get; set; } = WindowMaterial.Mica;

    public HotkeyGesture MainWindowHotkey { get; set; } = HotkeyGesture.CreateDefaultMainWindow();

    public AccentMode AccentMode { get; set; } = AccentMode.System;

    public uint CustomAccentArgb { get; set; } = 0xFF7C5CFC;

    public bool StartWithWindows { get; set; }

    public bool ItemHotkeysEnabled { get; set; } = true;

    public double WindowWidth { get; set; } = 920;

    public double WindowHeight { get; set; } = 620;

    public double? WindowLeft { get; set; }

    public double? WindowTop { get; set; }

    public bool IsMaximized { get; set; }

    /// <summary>
    /// 创建当前设置的独立副本。
    /// </summary>
    /// <returns>属性值相同的新设置对象。</returns>
    public AppSettings Clone() => new()
    {
        Theme = Theme,
        WindowMaterial = WindowMaterial,
        MainWindowHotkey = MainWindowHotkey with { },
        AccentMode = AccentMode,
        CustomAccentArgb = CustomAccentArgb,
        StartWithWindows = StartWithWindows,
        ItemHotkeysEnabled = ItemHotkeysEnabled,
        WindowWidth = WindowWidth,
        WindowHeight = WindowHeight,
        WindowLeft = WindowLeft,
        WindowTop = WindowTop,
        IsMaximized = IsMaximized
    };
}
