using System.Text.Json;
using Kyvoq.Core.Services;
using Microsoft.Extensions.Time.Testing;

namespace Kyvoq.Tests.Services;

/// <summary>
/// 验证诊断日志的缓存期限、大小阈值、开关、并发写入及文件滚动行为。
/// </summary>
public sealed class DiagnosticLogTests : IDisposable
{
    private readonly string temporaryDirectory = Path.Combine(
        Path.GetTempPath(), "Kyvoq.Tests", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// 验证默认关闭时不会创建目录或日志文件。
    /// </summary>
    [Fact]
    public void Write_ShouldNotCreateFilesWhileDisabled()
    {
        using var log = new DiagnosticLog(temporaryDirectory);

        log.Write("Ignored", "不应写入磁盘。");

        Assert.False(log.IsEnabled);
        Assert.False(Directory.Exists(temporaryDirectory));
    }

    /// <summary>
    /// 验证开关立即生效，关闭时排空记录，重新开启后继续记录且保留异常上下文。
    /// </summary>
    [Fact]
    public void SetEnabled_ShouldFlushAndRespectToggle()
    {
        using var log = new DiagnosticLog(temporaryDirectory);
        Assert.True(log.SetEnabled(true));
        log.Write("First", "第一条\n包含换行", new InvalidOperationException(
            "outer", new IOException("inner")));
        Assert.True(log.SetEnabled(false));
        log.Write("Ignored", "关闭后的记录。");
        Assert.True(log.SetEnabled(true));
        log.Write("Second", "重新启用后的记录。");
        log.Dispose();

        var entries = ReadEntries();
        var first = Assert.Single(entries, entry => entry.GetProperty("eventName").GetString() == "First");
        Assert.Equal("第一条\n包含换行", first.GetProperty("message").GetString());
        Assert.Contains("System.InvalidOperationException: outer", first.GetProperty("exception").GetString());
        Assert.Contains("System.IO.IOException: inner", first.GetProperty("exception").GetString());
        Assert.Equal(Environment.ProcessId, first.GetProperty("processId").GetInt32());
        Assert.True(first.GetProperty("threadId").GetInt32() > 0);
        Assert.NotEqual(default, first.GetProperty("timestamp").GetDateTimeOffset());
        Assert.Single(entries, entry => entry.GetProperty("eventName").GetString() == "Second");
        Assert.DoesNotContain(entries, entry => entry.GetProperty("eventName").GetString() == "Ignored");
        Assert.False(log.IsEnabled);
        Assert.Null(log.LastError);
    }

    /// <summary>
    /// 验证小于大小阈值的记录留在内存中，新记录会将空闲写入期限延后两分钟。
    /// </summary>
    [Fact]
    public async Task Write_ShouldFlushAfterTwoMinutesWithoutNewEntries()
    {
        var clock = new FakeTimeProvider();
        using var log = new DiagnosticLog(temporaryDirectory, 5 * 1024 * 1024, 5, clock);
        Assert.True(log.SetEnabled(true));
        log.Write("First", new string('x', 8192));
        clock.Advance(TimeSpan.FromSeconds(119));
        await AssertFileLengthStaysAsync(0);

        log.Write("Second", "重新开始计算空闲期限。");
        clock.Advance(TimeSpan.FromSeconds(119));
        await AssertFileLengthStaysAsync(0);

        clock.Advance(TimeSpan.FromSeconds(1));
        var entries = await WaitForEntryAsync("Second");
        Assert.Single(entries, entry => entry.GetProperty("eventName").GetString() == "First");
        Assert.True(log.IsEnabled);
        Assert.Null(log.LastError);
    }

    /// <summary>
    /// 验证持续收到日志时，最早记录满五分钟仍会写入，不被空闲期限反复推迟。
    /// </summary>
    [Fact]
    public async Task Write_ShouldFlushAtFiveMinutesDuringContinuousLogging()
    {
        var clock = new FakeTimeProvider();
        using var log = new DiagnosticLog(temporaryDirectory, 5 * 1024 * 1024, 5, clock);
        Assert.True(log.SetEnabled(true));
        log.Write("First", "五分钟期限起点。");
        for (var minute = 1; minute <= 4; minute++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            log.Write($"Minute{minute}", "持续写入，尚未空闲两分钟。");
        }

        clock.Advance(TimeSpan.FromSeconds(59));
        await AssertFileLengthStaysAsync(0);
        clock.Advance(TimeSpan.FromSeconds(1));

        var entries = await WaitForEntryAsync("Minute4");
        Assert.Equal(6, entries.Length);
        Assert.Single(entries, entry => entry.GetProperty("eventName").GetString() == "First");
        Assert.Null(log.LastError);
    }

    /// <summary>
    /// 验证一 MiB 阈值按 UTF-8 字节数计算，多字节文本达到阈值后无需等待定时器。
    /// </summary>
    [Fact]
    public async Task Write_ShouldFlushWhenUtf8BytesReachOneMebibyte()
    {
        var clock = new FakeTimeProvider();
        using var log = new DiagnosticLog(temporaryDirectory, 5 * 1024 * 1024, 5, clock);
        Assert.True(log.SetEnabled(true));
        var message = new string('测', 200_000);
        log.Write("First", message);
        await AssertFileLengthStaysAsync(0);

        log.Write("Second", message);
        var entries = await WaitForEntryAsync("Second");

        Assert.Equal(3, entries.Length);
        Assert.True(GetLogFileBytes() >= 1024 * 1024);
        Assert.All(entries.Where(entry => entry.GetProperty("eventName").GetString() != "Log.Enabled"),
            entry => Assert.Equal(message, entry.GetProperty("message").GetString()));
        Assert.Null(log.LastError);
    }

    /// <summary>
    /// 验证一次写入后空缓存不再触发磁盘操作，下一批从第一条新记录重新计算期限。
    /// </summary>
    [Fact]
    public async Task Write_ShouldResetDeadlinesAfterFlushing()
    {
        var clock = new FakeTimeProvider();
        using var log = new DiagnosticLog(temporaryDirectory, 5 * 1024 * 1024, 5, clock);
        Assert.True(log.SetEnabled(true));
        log.Write("First", "第一批。");
        clock.Advance(TimeSpan.FromMinutes(2));
        await WaitForEntryAsync("First");
        var writtenBytes = GetLogFileBytes();

        clock.Advance(TimeSpan.FromMinutes(20));
        await AssertFileLengthStaysAsync(writtenBytes);
        log.Write("Second", "新的缓存期限。");
        clock.Advance(TimeSpan.FromSeconds(119));
        await AssertFileLengthStaysAsync(writtenBytes);
        clock.Advance(TimeSpan.FromSeconds(1));

        var entries = await WaitForEntryAsync("Second");
        Assert.Single(entries, entry => entry.GetProperty("eventName").GetString() == "First");
        Assert.Single(entries, entry => entry.GetProperty("eventName").GetString() == "Second");
        Assert.Null(log.LastError);
    }

    /// <summary>
    /// 验证达到大小阈值的批次和退出前剩余缓存均按顺序写完且不会重复。
    /// </summary>
    [Fact]
    public void Dispose_ShouldDrainSizeTriggeredBatchAndRemainingBufferInOrder()
    {
        using var log = new DiagnosticLog(temporaryDirectory);
        Assert.True(log.SetEnabled(true));
        var message = new string('x', 100_000);
        for (var index = 0; index < 15; index++)
        {
            log.Write($"Entry{index}", message);
        }

        log.Dispose();

        var events = ReadEntries()
            .Select(entry => entry.GetProperty("eventName").GetString())
            .ToArray();
        Assert.Equal(
            new[] { "Log.Enabled" }.Concat(Enumerable.Range(0, 15).Select(index => $"Entry{index}"))
                .Append("Log.Disabled"),
            events);
        Assert.Null(log.LastError);
    }

    /// <summary>
    /// 验证不同线程同时提交的事件不会丢失或破坏日志行。
    /// </summary>
    [Fact]
    public async Task Write_ShouldPreserveConcurrentEntries()
    {
        using var log = new DiagnosticLog(temporaryDirectory);
        Assert.True(log.SetEnabled(true));

        await Task.WhenAll(Enumerable.Range(0, 100).Select(index => Task.Run(
            () => log.Write("Concurrent", index.ToString()), TestContext.Current.CancellationToken)));
        log.Dispose();

        var messages = ReadEntries()
            .Where(entry => entry.GetProperty("eventName").GetString() == "Concurrent")
            .Select(entry => entry.GetProperty("message").GetString())
            .ToArray();
        Assert.Equal(100, messages.Length);
        Assert.Equal(100, messages.Distinct().Count());
        Assert.Null(log.LastError);
    }

    /// <summary>
    /// 验证文件滚动会保留最近记录且不会删除非日志文件。
    /// </summary>
    [Fact]
    public void Write_ShouldRotateFilesAndKeepLatestEntries()
    {
        Directory.CreateDirectory(temporaryDirectory);
        var unrelatedFile = Path.Combine(temporaryDirectory, "keep.txt");
        File.WriteAllText(unrelatedFile, "保留");
        using var log = new DiagnosticLog(temporaryDirectory, maximumFileBytes: 1024, maximumFiles: 3);
        Assert.True(log.SetEnabled(true));
        for (var index = 0; index < 30; index++)
        {
            log.Write("Rolling", $"{index}:{new string('x', 400)}");
        }
        log.Dispose();

        var files = Directory.GetFiles(temporaryDirectory, "kyvoq-*.log");
        Assert.Equal(3, files.Length);
        Assert.All(files, file => Assert.InRange(new FileInfo(file).Length, 1, 1024));
        Assert.Contains(ReadEntries(), entry =>
            entry.GetProperty("eventName").GetString() == "Rolling"
            && entry.GetProperty("message").GetString()!.StartsWith("29:", StringComparison.Ordinal));
        Assert.True(File.Exists(unrelatedFile));
        Assert.Null(log.LastError);
    }

    /// <summary>
    /// 验证无法创建日志目录时可读取失败原因，且不会阻断后续应用操作。
    /// </summary>
    [Fact]
    public void SetEnabled_ShouldFailSafelyWhenDirectoryIsAFile()
    {
        Directory.CreateDirectory(temporaryDirectory);
        var occupiedPath = Path.Combine(temporaryDirectory, "occupied");
        File.WriteAllText(occupiedPath, "file");
        using var log = new DiagnosticLog(occupiedPath);

        Assert.False(log.SetEnabled(true));
        log.Write("Ignored", "写入失败后仍可调用日志接口。");

        Assert.False(log.IsEnabled);
        Assert.NotNull(log.LastError);
    }

    /// <summary>
    /// 等待后台批次中的指定事件可读，忽略正在写入时暂时不完整的 JSON 行。
    /// </summary>
    /// <param name="eventName">等待写入的事件名称。</param>
    /// <returns>包含目标事件的完整日志记录。</returns>
    private async Task<JsonElement[]> WaitForEntryAsync(string eventName)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (true)
        {
            try
            {
                var entries = ReadEntries();
                if (entries.Any(entry => entry.GetProperty("eventName").GetString() == eventName))
                {
                    return entries;
                }
            }
            catch (JsonException)
            {
            }

            await Task.Delay(10, timeout.Token);
        }
    }

    /// <summary>
    /// 给后台写入任务运行机会，并验证在虚拟时钟尚未达到期限时没有提前写入。
    /// </summary>
    /// <param name="expectedBytes">预期已写入的文件总字节数。</param>
    private async Task AssertFileLengthStaysAsync(long expectedBytes)
    {
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(expectedBytes, GetLogFileBytes());
    }

    /// <summary>
    /// 计算当前日志文件总字节数，以验证内容是否已从内存提交到文件。
    /// </summary>
    /// <returns>日志文件总字节数。</returns>
    private long GetLogFileBytes() => Directory.GetFiles(temporaryDirectory, "kyvoq-*.log")
        .Sum(file => new FileInfo(file).Length);

    /// <summary>
    /// 读取测试日志中的独立 JSON 记录。
    /// </summary>
    /// <returns>已脱离文档生命周期的日志条目。</returns>
    private JsonElement[] ReadEntries() => Directory.GetFiles(temporaryDirectory, "kyvoq-*.log")
        .SelectMany(ReadSharedEntries)
        .ToArray();

    /// <summary>
    /// 以允许后台继续写入的共享方式读取日志，支持验证尚未关闭的活动日志文件。
    /// </summary>
    /// <param name="path">日志文件路径。</param>
    /// <returns>已脱离文档生命周期的日志条目。</returns>
    private static IEnumerable<JsonElement> ReadSharedEntries(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            yield return document.RootElement.Clone();
        }
    }

    /// <summary>
    /// 仅删除测试创建的独立临时目录。
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(temporaryDirectory))
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }
}
