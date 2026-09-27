using Kyvoq.App.Services;

namespace Kyvoq.App.Tests;

/// <summary>提供可控备注读写，不访问真实用户数据目录。</summary>
internal sealed class FakeSteamAccountNoteStore : ISteamAccountNoteStore
{
    public Dictionary<ulong, string> Notes { get; } = [];
    public bool FailRead { get; set; }
    public bool FailWrite { get; set; }
    public Func<Task>? BeforeSave { get; set; }

    /// <summary>返回独立备注快照或模拟读取失败。</summary>
    public Task<IReadOnlyDictionary<ulong, string>> LoadAsync(CancellationToken cancellationToken = default) =>
        FailRead ? throw new UnauthorizedAccessException()
        : Task.FromResult<IReadOnlyDictionary<ulong, string>>(new Dictionary<ulong, string>(Notes));

    /// <summary>模拟持久化等待、写入失败及清除备注。</summary>
    public async Task SetAsync(ulong steamId64, string note, CancellationToken cancellationToken = default)
    {
        if (BeforeSave is not null)
        {
            await BeforeSave();
        }

        if (FailWrite)
        {
            throw new UnauthorizedAccessException();
        }

        if (string.IsNullOrWhiteSpace(note))
        {
            Notes.Remove(steamId64);
        }
        else
        {
            Notes[steamId64] = note.Trim();
        }
    }
}
