using System.IO;
using Kyvoq.App.Services;

namespace Kyvoq.App.Tests.Services;

/// <summary>验证独立备注文件的持久化及损坏时保留原文件。</summary>
public sealed class JsonSteamAccountNoteStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "Kyvoq.Note.Tests", Guid.NewGuid().ToString("N"));

    /// <summary>验证修改、清空及重新创建存储后仍可读取其他账号备注。</summary>
    [Fact]
    public async Task Notes_ShouldPersistUpdateAndClearPerAccount()
    {
        var store = new JsonSteamAccountNoteStore(directory);
        var cancellation = TestContext.Current.CancellationToken;
        Assert.Empty(await store.LoadAsync(cancellation));
        Assert.False(Directory.Exists(directory));
        await store.SetAsync(76561197960265729, "  主号  ", cancellation);
        await store.SetAsync(76561197960265730, "小号", cancellation);
        await store.SetAsync(76561197960265729, "新备注", cancellation);
        var reloaded = new JsonSteamAccountNoteStore(directory);
        Assert.Equal("新备注", (await reloaded.LoadAsync(cancellation))[76561197960265729]);
        await reloaded.SetAsync(76561197960265729, " ", cancellation);
        Assert.Equal("小号", Assert.Single(await store.LoadAsync(cancellation)).Value);
        Assert.True(File.Exists(Path.Combine(directory, "steam-account-notes.json.bak")));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
    }

    /// <summary>验证损坏文件不能被保存操作静默覆盖。</summary>
    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"76561197960265729\":null}")]
    public async Task CorruptNotes_ShouldRemainUntouched(string original)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "steam-account-notes.json");
        await File.WriteAllTextAsync(path, original, TestContext.Current.CancellationToken);
        var store = new JsonSteamAccountNoteStore(directory);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SetAsync(1, "备注", TestContext.Current.CancellationToken));
        Assert.Equal(original, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    /// <summary>验证同一存储上的并发修改不会丢失其他账号备注。</summary>
    [Fact]
    public async Task ConcurrentNotes_ShouldAllBeRetained()
    {
        var store = new JsonSteamAccountNoteStore(directory);
        await Task.WhenAll(Enumerable.Range(1, 8).Select(id => store.SetAsync((ulong)id, $"备注{id}", TestContext.Current.CancellationToken)));
        Assert.Equal(8, (await store.LoadAsync(TestContext.Current.CancellationToken)).Count);
    }

    /// <summary>仅清理本测试创建的临时目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
