using System.Diagnostics;
using Kyvoq.App.Services;
using Kyvoq.Core.Models;

namespace Kyvoq.App.Tests.Services;

/// <summary>
/// 验证管理员环境变量通过当前用户专用命名管道传递。
/// </summary>
public sealed class ElevatedLaunchBrokerTests
{
    /// <summary>
    /// 验证命令行仅包含一次性连接信息，目标和环境变量由管道交给最终进程。
    /// </summary>
    [Fact]
    public async Task LaunchAsync_ShouldTransferRequestThroughPipeWithoutCommandLinePayload()
    {
        ProcessStartInfo? helperStartInfo = null;
        ProcessStartInfo? finalStartInfo = null;
        var helperProcess = new TrackingProcess();
        var finalProcess = new TrackingProcess();
        Task<int>? hostTask = null;
        var broker = new ElevatedLaunchBroker(
            @"C:\Kyvoq\Kyvoq.exe",
            startInfo =>
            {
                helperStartInfo = startInfo;
                var arguments = startInfo.ArgumentList.ToArray();
                hostTask = Task.Run(() => ElevatedLaunchHost.RunAsync(
                    arguments,
                    finalInfo =>
                    {
                        finalStartInfo = finalInfo;
                        return finalProcess;
                    },
                    TestContext.Current.CancellationToken));
                return helperProcess;
            },
            TimeSpan.FromSeconds(5));
        var item = new LauncherItem
        {
            Name = "管理员工具",
            Target = @"C:\Tools\Admin.exe",
            Arguments = "--mode elevated",
            RunAsAdministrator = true,
            EnvironmentVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["KYVOQ_SECRET"] = "pipe only"
            }
        };

        var result = await broker.LaunchAsync(item, TestContext.Current.CancellationToken);
        Assert.NotNull(hostTask);
        var hostExitCode = await hostTask;

        Assert.True(result.IsSuccessful, result.ErrorMessage);
        Assert.Equal(0, hostExitCode);
        Assert.NotNull(helperStartInfo);
        Assert.True(helperStartInfo.UseShellExecute);
        Assert.Equal("runas", helperStartInfo.Verb);
        Assert.DoesNotContain(item.Target, helperStartInfo.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("pipe only", helperStartInfo.Arguments, StringComparison.Ordinal);
        Assert.NotNull(finalStartInfo);
        Assert.False(finalStartInfo.UseShellExecute);
        Assert.Equal(item.Target, finalStartInfo.FileName);
        Assert.Equal(item.Arguments, finalStartInfo.Arguments);
        Assert.Equal(@"C:\Tools", finalStartInfo.WorkingDirectory);
        Assert.Equal("pipe only", finalStartInfo.Environment["kyvoq_secret"]);
        Assert.True(helperProcess.IsDisposed);
        Assert.True(finalProcess.IsDisposed);
    }

    /// <summary>
    /// 验证辅助进程没有连接时 Broker 在限定时间内返回明确失败。
    /// </summary>
    [Fact]
    public async Task LaunchAsync_ShouldReturnFailureWhenHelperDoesNotConnect()
    {
        var broker = new ElevatedLaunchBroker(
            @"C:\Kyvoq\Kyvoq.exe",
            _ => null,
            TimeSpan.FromMilliseconds(100));
        var item = new LauncherItem
        {
            Name = "管理员工具",
            Target = @"C:\Tools\Admin.exe",
            RunAsAdministrator = true,
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["KYVOQ_MODE"] = "admin"
            }
        };

        var result = await broker.LaunchAsync(item, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccessful);
        Assert.Contains("超时", result.ErrorMessage);
    }

    /// <summary>
    /// 验证 UAC 辅助进程启动器暂时阻塞时，Broker 不会同步阻塞调用线程。
    /// </summary>
    [Fact]
    public async Task LaunchAsync_ShouldReturnControlWhileHelperStarterIsBlocked()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var starterEntered = new ManualResetEventSlim();
        using var releaseStarter = new ManualResetEventSlim();
        Task<int>? hostTask = null;
        var broker = new ElevatedLaunchBroker(
            @"C:\Kyvoq\Kyvoq.exe",
            startInfo =>
            {
                starterEntered.Set();
                releaseStarter.Wait(TimeSpan.FromSeconds(1), cancellationToken);
                var arguments = startInfo.ArgumentList.ToArray();
                hostTask = Task.Run(() => ElevatedLaunchHost.RunAsync(
                    arguments,
                    _ => null,
                    cancellationToken));
                return null;
            },
            TimeSpan.FromSeconds(5));
        var item = new LauncherItem
        {
            Name = "管理员工具",
            Target = @"C:\Tools\Admin.exe",
            RunAsAdministrator = true,
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["KYVOQ_MODE"] = "admin"
            }
        };

        var stopwatch = Stopwatch.StartNew();
        var launchTask = broker.LaunchAsync(item, cancellationToken);
        stopwatch.Stop();

        Assert.True(
            starterEntered.Wait(TimeSpan.FromSeconds(1), cancellationToken),
            "辅助进程启动器未被调用。");
        releaseStarter.Set();
        var result = await launchTask;
        Assert.NotNull(hostTask);
        var hostExitCode = await hostTask;

        Assert.True(result.IsSuccessful, result.ErrorMessage);
        Assert.Equal(0, hostExitCode);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(500),
            $"Broker 异步入口同步阻塞了 {stopwatch.Elapsed.TotalMilliseconds:F0} 毫秒。");
    }

    /// <summary>
    /// 验证提权辅助进程仅连接当前用户创建的命名管道服务端。
    /// </summary>
    [Fact]
    public void ClientPipeOptions_ShouldRestrictConnectionToCurrentUser()
    {
        Assert.True(ElevatedLaunchHost.ClientPipeOptions.HasFlag(System.IO.Pipes.PipeOptions.CurrentUserOnly));
    }

    /// <summary>
    /// 记录进程对象是否已释放的测试替身。
    /// </summary>
    private sealed class TrackingProcess : Process
    {
        public bool IsDisposed { get; private set; }

        /// <summary>
        /// 记录释放动作并调用进程对象的标准释放逻辑。
        /// </summary>
        /// <param name="disposing">是否正在执行托管资源释放。</param>
        protected override void Dispose(bool disposing)
        {
            IsDisposed = disposing;
            base.Dispose(disposing);
        }
    }
}
