using System.Diagnostics;
using System.IO;
using Kyvoq.App.Services;
using Kyvoq.Core.Models;

namespace Kyvoq.App.Tests.Services;

/// <summary>
/// 验证管理员环境变量通过当前用户专用命名管道传递。
/// </summary>
public sealed class ElevatedLaunchBrokerTests
{
    /// <summary>
    /// 验证真实 Kyvoq 辅助进程能够通过命名管道接收环境变量并启动无害夹具。
    /// </summary>
    [Fact]
    public async Task LaunchAsync_ShouldCompleteThroughRealHelperProcess()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "Kyvoq.App.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var kyvoqPath = Path.Combine(AppContext.BaseDirectory, "Kyvoq.exe");
            var fixturePath = Path.Combine(AppContext.BaseDirectory, "Kyvoq.LaunchFixture.exe");
            var outputPath = Path.Combine(temporaryDirectory, "elevated-launch-result.txt");
            Assert.True(File.Exists(kyvoqPath), $"找不到 Kyvoq 辅助程序：{kyvoqPath}");
            Assert.True(File.Exists(fixturePath), $"找不到启动测试夹具：{fixturePath}");
            var broker = new ElevatedLaunchBroker(
                kyvoqPath,
                startInfo =>
                {
                    startInfo.UseShellExecute = false;
                    startInfo.Verb = string.Empty;
                    return Process.Start(startInfo);
                },
                TimeSpan.FromSeconds(5));
            var item = new LauncherItem
            {
                Name = "管理员代理夹具",
                Target = fixturePath,
                Arguments = $"\"{outputPath}\" proxy-test",
                RunAsAdministrator = true,
                EnvironmentVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["HTTP_PROXY"] = "http://127.0.0.1:10808",
                    ["HTTPS_PROXY"] = "http://127.0.0.1:10808",
                    ["ALL_PROXY"] = "http://127.0.0.1:10808",
                    ["KYVOQ_TEST_VALUE"] = "pipe environment"
                }
            };

            var result = await broker.LaunchAsync(item, cancellationToken);

            Assert.True(result.IsSuccessful, result.ErrorMessage);
            for (var attempt = 0; attempt < 100 && !File.Exists(outputPath); attempt++)
            {
                await Task.Delay(25, cancellationToken);
            }

            Assert.True(File.Exists(outputPath), "管理员启动夹具没有写入结果文件。");
            var lines = await File.ReadAllLinesAsync(outputPath, cancellationToken);
            Assert.Equal("proxy-test", lines[1]);
            Assert.Equal("pipe environment", lines[2]);
            Assert.Equal("http://127.0.0.1:10808", lines[3]);
            Assert.Equal("http://127.0.0.1:10808", lines[4]);
            Assert.Equal("http://127.0.0.1:10808", lines[5]);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

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
    /// 验证提权辅助进程不会因客户端所有者校验拒绝不同完整性级别的主进程管道。
    /// </summary>
    [Fact]
    public void ClientPipeOptions_ShouldAllowConnectionAcrossElevationLevels()
    {
        Assert.False(ElevatedLaunchHost.ClientPipeOptions.HasFlag(System.IO.Pipes.PipeOptions.CurrentUserOnly));
    }

    /// <summary>
    /// 验证主进程的命名管道服务端仍限制为当前用户，避免放宽客户端校验时削弱服务端边界。
    /// </summary>
    [Fact]
    public void ServerPipeOptions_ShouldRemainRestrictedToCurrentUser()
    {
        Assert.True(ElevatedLaunchBroker.ServerPipeOptions.HasFlag(System.IO.Pipes.PipeOptions.CurrentUserOnly));
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
