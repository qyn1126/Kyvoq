using Kyvoq.Core.Models;

namespace Kyvoq.Core.Services;

/// <summary>
/// 表示跨分组搜索命中的启动项目。
/// </summary>
public sealed record SearchResult(LauncherGroup Group, LauncherItem Item, int Score);
