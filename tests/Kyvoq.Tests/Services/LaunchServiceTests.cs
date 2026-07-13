using System.Diagnostics;
using Kyvoq.Core.Models;
using Kyvoq.Core.Services;

namespace Kyvoq.Tests.Services;

/// <summary>
/// 验证启动项目向 ProcessStartInfo 的转换规则。
/// </summary>
public sealed class LaunchServiceTests
{
    private readonly LaunchService service = new();

    /// <summary>
    /// 验证普通程序保留原始参数、环境变量并使用程序所在目录。
    /// </summary>
    [Fact]
    public void CreateStartInfo_ShouldPreserveApplicationArgumentsAndDefaultDirectory()
    {
        var item = new LauncherItem
        {
            Name = "工具",
            Target = @"C:\Tools\Tool.exe",
            Arguments = "--profile \"Work Space\"",
            EnvironmentVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["KYVOQ_PROFILE"] = "Work Space"
            }
        };

        var startInfo = service.CreateStartInfo(item);

        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(item.Arguments, startInfo.Arguments);
        Assert.Equal(@"C:\Tools", startInfo.WorkingDirectory);
        Assert.Equal("Work Space", startInfo.Environment["kyvoq_profile"]);
    }

    /// <summary>
    /// 验证管理员程序使用 Shell 的 runas 动词触发标准 UAC。
    /// </summary>
    [Fact]
    public void CreateStartInfo_ShouldUseRunAsForAdministratorApplication()
    {
        var item = new LauncherItem
        {
            Name = "管理工具",
            Target = @"C:\Tools\Admin.exe",
            RunAsAdministrator = true
        };

        var startInfo = service.CreateStartInfo(item);

        Assert.True(startInfo.UseShellExecute);
        Assert.Equal("runas", startInfo.Verb);
    }

    /// <summary>
    /// 验证管理员程序包含环境变量时通过提权 Broker 启动。
    /// </summary>
    [Fact]
    public async Task LaunchAsync_ShouldUseElevatedBrokerForAdministratorEnvironment()
    {
        var broker = new RecordingElevatedLaunchBroker();
        var serviceWithBroker = new LaunchService(broker);
        var item = new LauncherItem
        {
            Name = "管理工具",
            Target = @"C:\Tools\Admin.exe",
            RunAsAdministrator = true,
            EnvironmentVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["KYVOQ_MODE"] = "admin"
            }
        };

        var result = await serviceWithBroker.LaunchAsync(
            item,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccessful, result.ErrorMessage);
        Assert.Same(item, broker.LastItem);
    }

    /// <summary>
    /// 验证管理员环境变量不能被错误转换为会忽略变量的直接 Shell 启动信息。
    /// </summary>
    [Fact]
    public void CreateStartInfo_ShouldRejectAdministratorEnvironmentWithoutBroker()
    {
        var item = new LauncherItem
        {
            Name = "管理工具",
            Target = @"C:\Tools\Admin.exe",
            RunAsAdministrator = true,
            EnvironmentVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["KYVOQ_MODE"] = "admin"
            }
        };

        Assert.Throws<InvalidOperationException>(() => service.CreateStartInfo(item));
    }

    /// <summary>
    /// 验证文件和网址交给 Windows Shell 打开且不传递程序参数。
    /// </summary>
    /// <param name="target">文件或网址目标。</param>
    [Theory]
    [InlineData(@"C:\Docs\guide.pdf")]
    [InlineData("https://example.com")]
    public void CreateStartInfo_ShouldUseShellForFilesAndUrls(string target)
    {
        var item = new LauncherItem { Name = "目标", Target = target, Arguments = "ignored" };

        var startInfo = service.CreateStartInfo(item);

        Assert.True(startInfo.UseShellExecute);
        Assert.Equal(string.Empty, startInfo.Arguments);
    }

    /// <summary>
    /// 验证不存在的程序返回可展示失败结果，而不是向界面抛出系统异常。
    /// </summary>
    [Fact]
    public async Task LaunchAsync_ShouldReturnFailureForMissingExecutable()
    {
        var item = new LauncherItem
        {
            Name = "不存在的程序",
            Target = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.exe")
        };

        var result = await service.LaunchAsync(
            item,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccessful);
        Assert.Contains(item.Name, result.ErrorMessage);
    }

    /// <summary>
    /// 验证底层进程启动器暂时阻塞时，异步启动入口会立即把控制权还给调用线程。
    /// </summary>
    [Fact]
    public async Task LaunchAsync_ShouldReturnControlWhileProcessStarterIsBlocked()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var starterEntered = new ManualResetEventSlim();
        using var releaseStarter = new ManualResetEventSlim();
        var serviceWithBlockingStarter = new LaunchService(_ =>
        {
            starterEntered.Set();
            releaseStarter.Wait(TimeSpan.FromSeconds(1), cancellationToken);
            return null;
        });
        var item = new LauncherItem
        {
            Name = "工具",
            Target = @"C:\Tools\Tool.exe"
        };

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var launchTask = serviceWithBlockingStarter.LaunchAsync(
            item,
            cancellationToken);
        stopwatch.Stop();

        Assert.True(
            starterEntered.Wait(TimeSpan.FromSeconds(1), cancellationToken),
            "进程启动器未被调用。");
        releaseStarter.Set();
        var result = await launchTask;

        Assert.True(result.IsSuccessful, result.ErrorMessage);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(500),
            $"异步启动入口同步阻塞了 {stopwatch.Elapsed.TotalMilliseconds:F0} 毫秒。");
    }

    /// <summary>
    /// 验证启动器返回的进程对象会立即释放，而不是等待终结器回收句柄。
    /// </summary>
    [Fact]
    public async Task LaunchAsync_ShouldDisposeStartedProcess()
    {
        var process = new TrackingProcess();
        var serviceWithTrackedProcess = new LaunchService(_ => process);
        var item = new LauncherItem
        {
            Name = "工具",
            Target = @"C:\Tools\Tool.exe"
        };

        var result = await serviceWithTrackedProcess.LaunchAsync(
            item,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccessful, result.ErrorMessage);
        Assert.True(process.IsDisposed);
    }

    /// <summary>
    /// 记录管理员环境变量启动请求的测试替身。
    /// </summary>
    private sealed class RecordingElevatedLaunchBroker : IElevatedLaunchBroker
    {
        public LauncherItem? LastItem { get; private set; }

        /// <summary>
        /// 记录请求并返回成功结果。
        /// </summary>
        /// <param name="item">管理员启动项目。</param>
        /// <param name="cancellationToken">测试取消令牌。</param>
        /// <returns>包含固定成功结果的任务。</returns>
        public Task<LaunchResult> LaunchAsync(
            LauncherItem item,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastItem = item;
            return Task.FromResult(LaunchResult.Success());
        }
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
