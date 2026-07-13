namespace Kyvoq.Core.Models;

/// <summary>
/// 表示一个可启动的程序、文件或网址。
/// </summary>
public sealed class LauncherItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string Target { get; set; } = string.Empty;

    public LauncherTargetType TargetType { get; set; } = LauncherTargetType.Application;

    public string Arguments { get; set; } = string.Empty;

    public Dictionary<string, string> EnvironmentVariables { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public string CustomIconPath { get; set; } = string.Empty;

    public bool RunAsAdministrator { get; set; }

    public HotkeyGesture Hotkey { get; set; } = HotkeyGesture.Empty();

    public int SortOrder { get; set; }

    /// <summary>
    /// 创建当前启动项目的独立副本。
    /// </summary>
    /// <returns>属性值相同的新启动项目。</returns>
    public LauncherItem Clone() => new()
    {
        Id = Id,
        Name = Name,
        Target = Target,
        TargetType = TargetType,
        Arguments = Arguments,
        EnvironmentVariables = new Dictionary<string, string>(
            EnvironmentVariables,
            StringComparer.OrdinalIgnoreCase),
        CustomIconPath = CustomIconPath,
        RunAsAdministrator = RunAsAdministrator,
        Hotkey = Hotkey with { },
        SortOrder = SortOrder
    };
}
