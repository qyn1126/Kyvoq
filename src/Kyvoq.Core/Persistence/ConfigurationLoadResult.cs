using Kyvoq.Core.Models;

namespace Kyvoq.Core.Persistence;

/// <summary>
/// 表示配置加载结果及可能需要展示给用户的提示。
/// </summary>
public sealed record ConfigurationLoadResult(
    LauncherConfiguration Configuration,
    ConfigurationLoadState State,
    string Message);
