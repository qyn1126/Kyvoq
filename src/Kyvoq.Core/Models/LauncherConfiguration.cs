namespace Kyvoq.Core.Models;

/// <summary>
/// 表示 Kyvoq 的完整持久化配置。
/// </summary>
public sealed class LauncherConfiguration
{
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public List<LauncherGroup> Groups { get; set; } = [];

    public AppSettings Settings { get; set; } = new();

    /// <summary>
    /// 创建包含默认“常用”分组的新配置。
    /// </summary>
    /// <returns>可直接使用的默认配置。</returns>
    public static LauncherConfiguration CreateDefault() => new()
    {
        Groups =
        [
            new LauncherGroup
            {
                Name = "常用",
                SortOrder = 0
            }
        ]
    };

    /// <summary>
    /// 创建包含独立设置、分组和项目对象的完整配置副本。
    /// </summary>
    /// <returns>可安全用于异步保存的配置快照。</returns>
    public LauncherConfiguration Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        Settings = Settings.Clone(),
        Groups = Groups.Select(group => new LauncherGroup
        {
            Id = group.Id,
            Name = group.Name,
            SortOrder = group.SortOrder,
            Items = group.Items.Select(item => item.Clone()).ToList()
        }).ToList()
    };
}
