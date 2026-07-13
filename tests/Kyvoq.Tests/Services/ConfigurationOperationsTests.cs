using Kyvoq.Core.Models;
using Kyvoq.Core.Services;

namespace Kyvoq.Tests.Services;

/// <summary>
/// 验证跨分组移动、排序和删除策略。
/// </summary>
public sealed class ConfigurationOperationsTests
{
    /// <summary>
    /// 验证项目拖到其他分组后从原分组移除并追加到目标末尾。
    /// </summary>
    [Fact]
    public void MoveItem_ShouldMoveAcrossGroupsAndReindex()
    {
        var item = CreateItem("A", 0);
        var destinationItem = CreateItem("B", 0);
        var source = CreateGroup("源", 0, item);
        var destination = CreateGroup("目标", 1, destinationItem);
        var configuration = new LauncherConfiguration { Groups = [source, destination] };

        ConfigurationOperations.MoveItem(configuration, item.Id, destination.Id);

        Assert.Empty(source.Items);
        Assert.Equal([destinationItem.Id, item.Id], destination.Items.Select(candidate => candidate.Id));
        Assert.Equal([0, 1], destination.Items.Select(candidate => candidate.SortOrder));
    }

    /// <summary>
    /// 验证网格拖放可以把项目插入到另一个项目之前。
    /// </summary>
    [Fact]
    public void ReorderItem_ShouldInsertBeforeTarget()
    {
        var first = CreateItem("A", 0);
        var second = CreateItem("B", 1);
        var third = CreateItem("C", 2);
        var group = CreateGroup("常用", 0, first, second, third);

        ConfigurationOperations.ReorderItem(group, third.Id, first.Id);

        Assert.Equal([third.Id, first.Id, second.Id], group.Items.Select(item => item.Id));
        Assert.Equal([0, 1, 2], group.Items.Select(item => item.SortOrder));
    }

    /// <summary>
    /// 验证删除项目会返回可逆快照，撤销后恢复到原分组和原索引。
    /// </summary>
    [Fact]
    public void RemoveAndRestoreItem_ShouldPreserveOriginalPosition()
    {
        var first = CreateItem("A", 0);
        var second = CreateItem("B", 1);
        var group = CreateGroup("常用", 0, first, second);
        var configuration = new LauncherConfiguration { Groups = [group] };

        var snapshot = ConfigurationOperations.RemoveItem(configuration, first.Id);
        ConfigurationOperations.RestoreItem(configuration, snapshot);

        Assert.Equal([first.Id, second.Id], group.Items.Select(item => item.Id));
        Assert.Equal([0, 1], group.Items.Select(item => item.SortOrder));
    }

    /// <summary>
    /// 验证删除含项目分组并撤销后会恢复完整内容和原分组顺序。
    /// </summary>
    [Fact]
    public void RemoveAndRestoreGroup_ShouldPreserveContentsAndPosition()
    {
        var first = CreateGroup("A", 0, CreateItem("项目", 0));
        var second = CreateGroup("B", 1);
        var configuration = new LauncherConfiguration { Groups = [first, second] };

        var snapshot = ConfigurationOperations.RemoveGroup(configuration, first.Id);
        ConfigurationOperations.RestoreGroup(configuration, snapshot);

        Assert.Equal([first.Id, second.Id], configuration.Groups.Select(group => group.Id));
        Assert.Equal("项目", Assert.Single(configuration.Groups[0].Items).Name);
    }

    /// <summary>
    /// 验证可逆删除同样拒绝移除配置中的唯一分组。
    /// </summary>
    [Fact]
    public void RemoveGroup_ShouldRejectDeletingOnlyGroup()
    {
        var group = CreateGroup("唯一", 0);
        var configuration = new LauncherConfiguration { Groups = [group] };

        Assert.Throws<InvalidOperationException>(() =>
            ConfigurationOperations.RemoveGroup(configuration, group.Id));
    }

    /// <summary>
    /// 创建测试使用的启动项目。
    /// </summary>
    /// <param name="name">项目名称。</param>
    /// <param name="sortOrder">排序号。</param>
    /// <returns>包含有效目标的项目。</returns>
    private static LauncherItem CreateItem(string name, int sortOrder) => new()
    {
        Name = name,
        Target = $@"C:\{name}.exe",
        SortOrder = sortOrder
    };

    /// <summary>
    /// 创建测试使用的分组。
    /// </summary>
    /// <param name="name">分组名称。</param>
    /// <param name="sortOrder">排序号。</param>
    /// <param name="items">分组内项目。</param>
    /// <returns>测试分组。</returns>
    private static LauncherGroup CreateGroup(string name, int sortOrder, params LauncherItem[] items) => new()
    {
        Name = name,
        SortOrder = sortOrder,
        Items = [.. items]
    };
}
