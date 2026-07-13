using System.IO;
using Kyvoq.App.Services;

namespace Kyvoq.App.Tests.Services;

/// <summary>
/// 验证单实例命名管道的激活通知。
/// </summary>
public sealed class SingleInstanceServiceTests
{
    /// <summary>
    /// 验证辅助实例可以通知主实例执行窗口激活回调。
    /// </summary>
    [Fact]
    public async Task NotifyPrimaryAsync_ShouldReachPrimaryListener()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var applicationId = $"Kyvoq.Tests.{Guid.NewGuid():N}";
        using var primary = new SingleInstanceService(applicationId);
        using var secondary = new SingleInstanceService(applicationId);
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.StartListening(() => activated.TrySetResult());

        Assert.True(primary.IsPrimaryInstance());
        Assert.False(secondary.IsPrimaryInstance());
        await secondary.NotifyPrimaryAsync(cancellationToken);
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
    }

    /// <summary>
    /// 验证命名管道持续监听失败时按指数退避，并在达到上限后停止重试。
    /// </summary>
    [Fact]
    public void StartListening_ShouldBackOffAndStopAfterRepeatedIoFailures()
    {
        var applicationId = $"Kyvoq.Tests.{Guid.NewGuid():N}";
        var attempts = 0;
        var delays = new List<TimeSpan>();
        using var primary = new SingleInstanceService(
            applicationId,
            _ =>
            {
                attempts++;
                throw new IOException("模拟持续管道故障。");
            },
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        primary.StartListening(() => { });

        Assert.True(primary.IsPrimaryInstance());
        Assert.Equal(SingleInstanceService.MaxListenerFailures, attempts);
        Assert.Equal(
            [
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(200),
                TimeSpan.FromMilliseconds(400),
                TimeSpan.FromMilliseconds(800)
            ],
            delays);
    }
}
