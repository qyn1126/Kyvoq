using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Kyvoq.App.Services;

/// <summary>
/// 使用命名互斥锁和命名管道保证每位用户只运行一个 Kyvoq 实例。
/// </summary>
public sealed class SingleInstanceService : IDisposable
{
    internal const int MaxListenerFailures = 5;
    private const int InitialListenerRetryDelayMilliseconds = 100;
    private const int MaxListenerRetryDelayMilliseconds = 2_000;
    private readonly string pipeName;
    private readonly Mutex mutex;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Func<string, NamedPipeServerStream> serverFactory;
    private readonly Func<TimeSpan, CancellationToken, Task> retryDelayAsync;
    private Task? listenerTask;
    private bool ownsMutex;
    private bool disposed;

    /// <summary>
    /// 创建指定应用标识的单实例服务。
    /// </summary>
    /// <param name="applicationId">用于互斥锁和管道的稳定应用标识。</param>
    public SingleInstanceService(string applicationId)
        : this(
            applicationId,
            static name => new NamedPipeServerStream(
                name,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly),
            Task.Delay)
    {
    }

    /// <summary>
    /// 使用可替换的管道创建器和退避等待函数创建可测试的单实例服务。
    /// </summary>
    /// <param name="applicationId">用于互斥锁和管道的稳定应用标识。</param>
    /// <param name="serverFactory">按管道名创建监听服务端的函数。</param>
    /// <param name="retryDelayAsync">监听失败后的异步退避函数。</param>
    internal SingleInstanceService(
        string applicationId,
        Func<string, NamedPipeServerStream> serverFactory,
        Func<TimeSpan, CancellationToken, Task> retryDelayAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        this.serverFactory = serverFactory ?? throw new ArgumentNullException(nameof(serverFactory));
        this.retryDelayAsync = retryDelayAsync ?? throw new ArgumentNullException(nameof(retryDelayAsync));
        var userBytes = SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName));
        var userId = Convert.ToHexString(userBytes)[..12];
        pipeName = $"{applicationId}.{userId}.pipe";
        mutex = new Mutex(true, $"Local\\{applicationId}.{userId}.mutex", out ownsMutex);
    }

    /// <summary>
    /// 获取当前实例是否持有应用互斥锁。
    /// </summary>
    /// <returns>当前实例为主实例时返回 <see langword="true"/>。</returns>
    public bool IsPrimaryInstance() => ownsMutex;

    /// <summary>
    /// 启动后台命名管道监听，并在收到信号时调用回调。
    /// </summary>
    /// <param name="activationRequested">需要唤醒主窗口时执行的回调。</param>
    public void StartListening(Action activationRequested)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(activationRequested);
        if (!ownsMutex || listenerTask is not null)
        {
            return;
        }

        listenerTask = ListenAsync(activationRequested, cancellation.Token);
    }

    /// <summary>
    /// 向已经运行的主实例发送窗口激活信号。
    /// </summary>
    /// <param name="cancellationToken">用于取消连接的令牌。</param>
    /// <returns>发送完成或超时时结束的任务。</returns>
    public async Task NotifyPrimaryAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await using var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(timeout.Token);
            await client.WriteAsync(Encoding.UTF8.GetBytes("activate"), timeout.Token);
            await client.FlushAsync(timeout.Token);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// 取消监听并释放互斥锁和管道资源。
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        cancellation.Cancel();
        cancellation.Dispose();
        if (ownsMutex)
        {
            try
            {
                mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }

            ownsMutex = false;
        }

        mutex.Dispose();
    }

    /// <summary>
    /// 循环接受辅助实例连接并触发激活回调。
    /// </summary>
    /// <param name="activationRequested">窗口激活回调。</param>
    /// <param name="cancellationToken">停止监听的令牌。</param>
    /// <returns>监听结束时完成的任务。</returns>
    private async Task ListenAsync(Action activationRequested, CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = serverFactory(pipeName);
                await server.WaitForConnectionAsync(cancellationToken);
                var buffer = new byte[32];
                var count = await server.ReadAsync(buffer, cancellationToken);
                if (count > 0)
                {
                    activationRequested();
                }

                consecutiveFailures = 0;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                consecutiveFailures++;
                if (consecutiveFailures >= MaxListenerFailures)
                {
                    break;
                }

                var delayMilliseconds = Math.Min(
                    InitialListenerRetryDelayMilliseconds * (1 << (consecutiveFailures - 1)),
                    MaxListenerRetryDelayMilliseconds);
                try
                {
                    await retryDelayAsync(
                        TimeSpan.FromMilliseconds(delayMilliseconds),
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
