using Kyvoq.Core.Models;
using Kyvoq.Core.Services;

namespace Kyvoq.Tests.Services;

/// <summary>
/// 验证跨分组搜索范围和相关性排序。
/// </summary>
public sealed class LauncherSearchTests
{
    /// <summary>
    /// 验证名称前缀结果优先于路径匹配结果。
    /// </summary>
    [Fact]
    public void Search_ShouldRankNamePrefixAheadOfTargetMatch()
    {
        var prefix = new LauncherItem { Name = "Steam", Target = @"C:\Apps\Steam.exe" };
        var path = new LauncherItem { Name = "游戏平台", Target = @"C:\Steam\Launcher.exe", SortOrder = 1 };
        var configuration = CreateConfiguration("游戏", prefix, path);

        var results = LauncherSearch.Search(configuration, "steam");

        Assert.Equal([prefix.Id, path.Id], results.Select(result => result.Item.Id));
    }

    /// <summary>
    /// 验证分组名称和项目名称可以共同满足多个搜索词。
    /// </summary>
    [Fact]
    public void Search_ShouldMatchTokensAcrossNameAndGroup()
    {
        var item = new LauncherItem { Name = "Visual Studio", Target = @"C:\VS\devenv.exe" };
        var configuration = CreateConfiguration("开发工具", item);

        var result = Assert.Single(LauncherSearch.Search(configuration, "开发 studio"));

        Assert.Equal(item.Id, result.Item.Id);
    }

    /// <summary>
    /// 验证空查询按原始分组及项目顺序返回全部项目。
    /// </summary>
    [Fact]
    public void Search_ShouldReturnAllItemsInOriginalOrderForEmptyQuery()
    {
        var first = new LauncherItem { Name = "A", Target = @"C:\A.exe", SortOrder = 0 };
        var second = new LauncherItem { Name = "B", Target = @"C:\B.exe", SortOrder = 1 };
        var configuration = CreateConfiguration("常用", first, second);

        var results = LauncherSearch.Search(configuration, string.Empty);

        Assert.Equal([first.Id, second.Id], results.Select(result => result.Item.Id));
    }

    /// <summary>
    /// 验证搜索直接信任已经规范化的列表顺序，不再为每次查询重复按排序号重排。
    /// </summary>
    [Fact]
    public void Search_ShouldUseNormalizedListOrderWithoutResorting()
    {
        var first = new LauncherItem { Name = "A", Target = @"C:\A.exe", SortOrder = 1 };
        var second = new LauncherItem { Name = "B", Target = @"C:\B.exe", SortOrder = 0 };
        var configuration = CreateConfiguration("常用", first, second);

        var results = LauncherSearch.Search(configuration, string.Empty);

        Assert.Equal([first.Id, second.Id], results.Select(result => result.Item.Id));
    }

    /// <summary>
    /// 创建单分组搜索配置。
    /// </summary>
    /// <param name="groupName">分组名称。</param>
    /// <param name="items">分组项目。</param>
    /// <returns>测试配置。</returns>
    private static LauncherConfiguration CreateConfiguration(string groupName, params LauncherItem[] items) => new()
    {
        Groups =
        [
            new LauncherGroup
            {
                Name = groupName,
                Items = [.. items]
            }
        ]
    };
}
