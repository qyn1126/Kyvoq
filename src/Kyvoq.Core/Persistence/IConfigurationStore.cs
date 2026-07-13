using Kyvoq.Core.Models;

namespace Kyvoq.Core.Persistence;

/// <summary>
/// 定义启动器配置的持久化边界。
/// </summary>
public interface IConfigurationStore
{
    /// <summary>
    /// 加载本地配置，并在主配置损坏时尝试使用备份恢复。
    /// </summary>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>配置和恢复状态。</returns>
    Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 使用原子替换方式保存配置。
    /// </summary>
    /// <param name="configuration">需要保存的配置。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    Task SaveAsync(LauncherConfiguration configuration, CancellationToken cancellationToken = default);

    /// <summary>
    /// 将完整配置导出到指定文件。
    /// </summary>
    /// <param name="configuration">需要导出的配置。</param>
    /// <param name="destinationPath">导出文件路径。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    Task ExportAsync(
        LauncherConfiguration configuration,
        string destinationPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 从指定文件读取并校验完整配置。
    /// </summary>
    /// <param name="sourcePath">导入文件路径。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>完成校验和规范化的配置。</returns>
    Task<LauncherConfiguration> ImportAsync(string sourcePath, CancellationToken cancellationToken = default);
}
