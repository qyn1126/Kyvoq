using System.Buffers.Binary;
using System.Text.Json;

namespace Kyvoq.App.Services;

/// <summary>
/// 表示主进程发送给提权辅助进程的启动请求。
/// </summary>
internal sealed record ElevatedLaunchRequest(
    string Token,
    string Name,
    string Target,
    string Arguments,
    Dictionary<string, string> EnvironmentVariables);

/// <summary>
/// 表示提权辅助进程返回的最终启动结果。
/// </summary>
internal sealed record ElevatedLaunchResponse(bool IsSuccessful, string ErrorMessage);

/// <summary>
/// 使用有长度上限的 JSON 帧在命名管道上传输提权启动消息。
/// </summary>
internal static class ElevatedLaunchProtocol
{
    private const int MaximumPayloadLength = 1024 * 1024;

    /// <summary>
    /// 把一条消息序列化为带四字节长度前缀的 JSON 帧。
    /// </summary>
    /// <typeparam name="T">消息类型。</typeparam>
    /// <param name="stream">可写管道流。</param>
    /// <param name="message">待发送消息。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>消息写入完成时结束的任务。</returns>
    public static async Task WriteAsync<T>(
        Stream stream,
        T message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);
        if (payload.Length > MaximumPayloadLength)
        {
            throw new InvalidDataException("提权启动请求超过允许大小。");
        }

        var prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 从管道读取并反序列化一条带长度前缀的 JSON 消息。
    /// </summary>
    /// <typeparam name="T">消息类型。</typeparam>
    /// <param name="stream">可读管道流。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>反序列化后的消息。</returns>
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var prefix = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, prefix, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is <= 0 or > MaximumPayloadLength)
        {
            throw new InvalidDataException("提权启动消息长度无效。");
        }

        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload)
            ?? throw new InvalidDataException("提权启动消息内容无效。");
    }

    /// <summary>
    /// 持续读取直到缓冲区填满，避免管道分段读取造成短消息。
    /// </summary>
    /// <param name="stream">可读流。</param>
    /// <param name="buffer">目标缓冲区。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>缓冲区填满时结束的任务。</returns>
    /// <exception cref="EndOfStreamException">对端提前断开时抛出。</exception>
    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new EndOfStreamException("提权启动管道提前关闭。");
            }

            offset += count;
        }
    }
}
