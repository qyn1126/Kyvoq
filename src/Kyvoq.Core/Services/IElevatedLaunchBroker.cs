using Kyvoq.Core.Models;

namespace Kyvoq.Core.Services;

/// <summary>
/// 定义需要同时携带环境变量的管理员进程启动边界。
/// </summary>
public interface IElevatedLaunchBroker
{
    /// <summary>
    /// 通过受控提权通道异步启动指定程序。
    /// </summary>
    /// <param name="item">包含管理员标记和环境变量的启动项目。</param>
    /// <param name="cancellationToken">取消提权启动等待的令牌。</param>
    /// <returns>包含提权及最终进程创建结果的任务。</returns>
    Task<LaunchResult> LaunchAsync(
        LauncherItem item,
        CancellationToken cancellationToken = default);
}
