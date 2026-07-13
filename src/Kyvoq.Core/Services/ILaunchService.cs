using System.Diagnostics;
using Kyvoq.Core.Models;

namespace Kyvoq.Core.Services;

/// <summary>
/// 定义启动程序、文件和网址的系统边界。
/// </summary>
public interface ILaunchService
{
    /// <summary>
    /// 异步启动指定项目，避免底层系统调用阻塞调用线程。
    /// </summary>
    /// <param name="item">需要启动的项目。</param>
    /// <param name="cancellationToken">取消启动等待的令牌。</param>
    /// <returns>包含启动请求结果的任务。</returns>
    Task<LaunchResult> LaunchAsync(
        LauncherItem item,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 根据项目构造进程启动信息，供启动和测试共同使用。
    /// </summary>
    /// <param name="item">需要转换的启动项目。</param>
    /// <returns>对应的进程启动信息。</returns>
    ProcessStartInfo CreateStartInfo(LauncherItem item);
}
