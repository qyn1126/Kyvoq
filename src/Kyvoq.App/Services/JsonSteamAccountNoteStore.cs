using System.Text.Json;

namespace Kyvoq.App.Services;

/// <summary>
/// 串行读取及原子保存独立备注文件，损坏的原文件不会被空备注覆盖。
/// </summary>
public sealed class JsonSteamAccountNoteStore(string dataDirectory) : ISteamAccountNoteStore
{
    private readonly string path = Path.Combine(dataDirectory, "steam-account-notes.json");
    private readonly SemaphoreSlim ioLock = new(1, 1);

    /// <summary>读取备注快照，不在文件缺失时创建文件。</summary>
    public async Task<IReadOnlyDictionary<ulong, string>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ioLock.Release();
        }
    }

    /// <summary>重新读取最新备注，再通过临时文件提交单个账号的修改。</summary>
    public async Task SetAsync(ulong steamId64, string note, CancellationToken cancellationToken = default)
    {
        await ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var notes = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var normalized = note.Trim();
            if (normalized.Length == 0)
            {
                if (!notes.Remove(steamId64))
                {
                    return;
                }
            }
            else
            {
                notes[steamId64] = normalized;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.Asynchronous))
                {
                    await JsonSerializer.SerializeAsync(stream, notes, cancellationToken: cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(path))
                {
                    File.Replace(temporary, path, path + ".bak");
                }
                else
                {
                    File.Move(temporary, path);
                }
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        finally
        {
            ioLock.Release();
        }
    }

    /// <summary>完整解析备注文件，对损坏内容报告错误而不回写默认值。</summary>
    private async Task<Dictionary<ulong, string>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous);
            var notes = await JsonSerializer.DeserializeAsync<Dictionary<ulong, string>>(stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (notes is null || notes.Values.Any(value => value is null))
            {
                throw new JsonException("备注文件结构不正确。");
            }

            return notes;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Steam 备注文件损坏，请检查数据目录中的 steam-account-notes.json。", exception);
        }
    }
}
