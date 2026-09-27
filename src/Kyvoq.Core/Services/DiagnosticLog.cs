using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;

namespace Kyvoq.Core.Services;

/// <summary>
/// 按需启用异步诊断日志，按时间和大小缓存后批量写入，并滚动保留有限文件。
/// </summary>
public sealed class DiagnosticLog : IDisposable
{
    private const int MaximumBufferedBytes = 1024 * 1024;
    private static readonly TimeSpan MaximumBufferAge = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan IdleFlushDelay = TimeSpan.FromMinutes(2);
    private static readonly int NewLineBytes = Encoding.UTF8.GetByteCount(Environment.NewLine);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly object sync = new();
    private readonly long maximumFileBytes;
    private readonly int maximumFiles;
    private readonly TimeProvider timeProvider;
    private readonly List<string> bufferedEntries = [];
    private Channel<string[]>? batches;
    private ITimer? flushTimer;
    private long bufferedBytes;
    private long firstBufferedTimestamp;
    private long lastEntryTimestamp;
    private Task? writerTask;
    private string? lastError;
    private bool disposed;

    public static DiagnosticLog Current { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kyvoq", "Logs"));

    public string DirectoryPath { get; }

    public string? LastError => Volatile.Read(ref lastError);

    public bool IsEnabled
    {
        get
        {
            lock (sync)
            {
                return batches is not null && writerTask is { IsCompleted: false };
            }
        }
    }

    /// <summary>
    /// 创建默认关闭的日志服务，最多保留五个五 MiB 的日志文件。
    /// </summary>
    /// <param name="directoryPath">日志文件所在目录。</param>
    public DiagnosticLog(string directoryPath)
        : this(directoryPath, 5 * 1024 * 1024, 5)
    {
    }

    /// <summary>
    /// 使用可配置的滚动大小创建可测试日志服务。
    /// </summary>
    /// <param name="directoryPath">日志文件所在目录。</param>
    /// <param name="maximumFileBytes">单个文件的滚动阈值。</param>
    /// <param name="maximumFiles">最多保留的日志文件数。</param>
    /// <param name="timeProvider">用于缓存期限和定时器的时钟，默认使用系统时钟。</param>
    internal DiagnosticLog(
        string directoryPath,
        long maximumFileBytes,
        int maximumFiles,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFileBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFiles);
        DirectoryPath = Path.GetFullPath(directoryPath);
        this.maximumFileBytes = maximumFileBytes;
        this.maximumFiles = maximumFiles;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// 启用或关闭日志；关闭时写完已接收的记录，文件错误不会向调用者抛出。
    /// </summary>
    /// <param name="enabled">是否继续接收诊断记录。</param>
    /// <returns>设置成功时返回 <see langword="true"/>，失败原因可由 LastError 读取。</returns>
    public bool SetEnabled(bool enabled)
    {
        lock (sync)
        {
            if (disposed)
            {
                return !enabled;
            }

            if (enabled && batches is not null && writerTask is { IsCompleted: false })
            {
                return true;
            }

            StopWriter();
            if (!enabled)
            {
                return true;
            }

            try
            {
                var writer = OpenWriter();
                Volatile.Write(ref lastError, null);
                var channel = Channel.CreateUnbounded<string[]>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    AllowSynchronousContinuations = false
                });
                batches = channel;
                flushTimer = timeProvider.CreateTimer(
                    _ => FlushWhenDue(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                writerTask = Task.Run(() => WriteEntriesAsync(channel.Reader, writer));
                Write("Log.Enabled", $"Version={typeof(DiagnosticLog).Assembly.GetName().Version}; "
                    + $"OS={Environment.OSVersion}; Runtime={Environment.Version}; ProcessPath={Environment.ProcessPath}");
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Volatile.Write(ref lastError, exception.Message);
                return false;
            }
        }
    }

    /// <summary>
    /// 缓存带时间、进程、线程、事件及完整异常信息的记录，达到条件后交给后台写入。
    /// </summary>
    /// <param name="eventName">可检索的事件名称。</param>
    /// <param name="message">事件上下文。</param>
    /// <param name="exception">可选的完整异常。</param>
    public void Write(string eventName, string message, Exception? exception = null)
    {
        lock (sync)
        {
            if (batches is null || writerTask is not { IsCompleted: false })
            {
                return;
            }

            var entry = JsonSerializer.Serialize(new
            {
                timestamp = timeProvider.GetLocalNow(),
                processId = Environment.ProcessId,
                threadId = Environment.CurrentManagedThreadId,
                eventName,
                message,
                exception = exception?.ToString()
            }, SerializerOptions);
            var now = timeProvider.GetTimestamp();
            if (bufferedEntries.Count > 0 && GetFlushDelay(now) <= TimeSpan.Zero)
            {
                QueueBufferedEntries();
            }

            if (bufferedEntries.Count == 0)
            {
                firstBufferedTimestamp = now;
            }

            lastEntryTimestamp = now;
            bufferedEntries.Add(entry);
            bufferedBytes += Encoding.UTF8.GetByteCount(entry) + NewLineBytes;
            if (bufferedBytes >= MaximumBufferedBytes)
            {
                QueueBufferedEntries();
            }
            else
            {
                flushTimer?.Change(GetFlushDelay(now), Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>
    /// 计算最早记录满五分钟或最后记录空闲两分钟的较早期限，调用时须持有同步锁。
    /// </summary>
    /// <param name="now">当前单调时钟时间戳。</param>
    /// <returns>距离下一次批量写入的剩余时间。</returns>
    private TimeSpan GetFlushDelay(long now)
    {
        var maximumDelay = MaximumBufferAge - timeProvider.GetElapsedTime(firstBufferedTimestamp, now);
        var idleDelay = IdleFlushDelay - timeProvider.GetElapsedTime(lastEntryTimestamp, now);
        return maximumDelay < idleDelay ? maximumDelay : idleDelay;
    }

    /// <summary>
    /// 定时器到期时重新核对期限，避免旧回调提前写入刚收到的日志。
    /// </summary>
    private void FlushWhenDue()
    {
        lock (sync)
        {
            if (batches is null || bufferedEntries.Count == 0 || writerTask is not { IsCompleted: false })
            {
                return;
            }

            var delay = GetFlushDelay(timeProvider.GetTimestamp());
            if (delay > TimeSpan.Zero)
            {
                flushTimer?.Change(delay, Timeout.InfiniteTimeSpan);
            }
            else
            {
                QueueBufferedEntries();
            }
        }
    }

    /// <summary>
    /// 将当前缓存作为独立批次交给后台并停止空缓存的定时器，调用时须持有同步锁。
    /// </summary>
    private void QueueBufferedEntries()
    {
        if (bufferedEntries.Count == 0)
        {
            return;
        }

        batches!.Writer.TryWrite(bufferedEntries.ToArray());
        bufferedEntries.Clear();
        bufferedBytes = 0;
        flushTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// 写完全部排队日志并释放文件资源。
    /// </summary>
    public void Dispose()
    {
        lock (sync)
        {
            StopWriter();
            disposed = true;
        }
    }

    /// <summary>
    /// 在持有同步锁时停止接收记录并等待后台写入完成。
    /// </summary>
    private void StopWriter()
    {
        if (batches is null)
        {
            return;
        }

        Write("Log.Disabled", "诊断日志结束，刷新已排队记录。");
        QueueBufferedEntries();
        flushTimer?.Dispose();
        flushTimer = null;
        batches.Writer.TryComplete();
        batches = null;
        writerTask?.GetAwaiter().GetResult();
        writerTask = null;
    }

    /// <summary>
    /// 串行写入日志批次，每批刷新一次，并按包含写入器缓存的实际字节数滚动文件。
    /// </summary>
    /// <param name="reader">待写入批次队列。</param>
    /// <param name="writer">启用日志时已打开的文件。</param>
    /// <returns>队列排空或文件写入失败时结束的任务。</returns>
    private async Task WriteEntriesAsync(ChannelReader<string[]> reader, StreamWriter writer)
    {
        try
        {
            var fileBytes = writer.BaseStream.Length;
            await foreach (var batch in reader.ReadAllAsync().ConfigureAwait(false))
            {
                foreach (var entry in batch)
                {
                    var entryBytes = Encoding.UTF8.GetByteCount(entry) + NewLineBytes;
                    if (fileBytes > 0 && fileBytes + entryBytes > maximumFileBytes)
                    {
                        await writer.DisposeAsync().ConfigureAwait(false);
                        writer = OpenWriter();
                        fileBytes = 0;
                    }

                    await writer.WriteLineAsync(entry).ConfigureAwait(false);
                    fileBytes += entryBytes;
                }

                await writer.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Volatile.Write(ref lastError, exception.Message);
        }
        finally
        {
            try
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Volatile.Write(ref lastError, exception.Message);
            }
        }
    }

    /// <summary>
    /// 创建独立日志文件并尽力清理超过保留数量的旧日志。
    /// </summary>
    /// <returns>使用 UTF-8 的异步文件写入器。</returns>
    private StreamWriter OpenWriter()
    {
        Directory.CreateDirectory(DirectoryPath);
        var path = Path.Combine(DirectoryPath,
            $"kyvoq-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Environment.ProcessId}-{Guid.NewGuid():N}.log");
        var writer = new StreamWriter(new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            4096, FileOptions.Asynchronous), new UTF8Encoding(false));
        try
        {
            foreach (var oldFile in Directory.EnumerateFiles(DirectoryPath, "kyvoq-*.log")
                         .Where(file => !string.Equals(file, path, StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(File.GetLastWriteTimeUtc)
                         .Skip(maximumFiles - 1))
            {
                try
                {
                    File.Delete(oldFile);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return writer;
    }
}
