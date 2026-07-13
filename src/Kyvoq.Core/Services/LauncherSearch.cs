using Kyvoq.Core.Models;

namespace Kyvoq.Core.Services;

/// <summary>
/// 提供针对名称、分组和目标路径的快速搜索。
/// </summary>
public static class LauncherSearch
{
    /// <summary>
    /// 搜索配置中的启动项目并按相关性与原始顺序排序。
    /// </summary>
    /// <param name="configuration">需要搜索的配置。</param>
    /// <param name="query">空格分隔的搜索文本。</param>
    /// <param name="maximumResults">最多返回的结果数量。</param>
    /// <returns>排序后的匹配结果。</returns>
    public static IReadOnlyList<SearchResult> Search(
        LauncherConfiguration configuration,
        string? query,
        int maximumResults = 100)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (maximumResults <= 0)
        {
            return [];
        }

        var tokens = (query ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var results = new List<SearchResult>();
        foreach (var group in configuration.Groups)
        {
            foreach (var item in group.Items)
            {
                var score = Score(group, item, tokens);
                if (score >= 0)
                {
                    results.Add(new SearchResult(group, item, score));
                }
            }
        }

        return results
            .OrderByDescending(result => result.Score)
            .Take(maximumResults)
            .ToArray();
    }

    /// <summary>
    /// 计算单个项目对全部搜索词的匹配分数。
    /// </summary>
    /// <param name="group">项目所在分组。</param>
    /// <param name="item">待评分项目。</param>
    /// <param name="tokens">规范化后的搜索词。</param>
    /// <returns>匹配分数；不匹配时返回 -1。</returns>
    private static int Score(LauncherGroup group, LauncherItem item, IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0)
        {
            return 0;
        }

        var total = 0;
        foreach (var token in tokens)
        {
            if (item.Name.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            {
                total += 100;
            }
            else if (item.Name.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                total += 70;
            }
            else if (group.Name.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                total += 35;
            }
            else if (item.Target.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                total += 20;
            }
            else
            {
                return -1;
            }
        }

        return total;
    }
}
