namespace Kyvoq.Core.Models;

/// <summary>
/// 表示启动项目的一个有序分组。
/// </summary>
public sealed class LauncherGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public List<LauncherItem> Items { get; set; } = [];
}
