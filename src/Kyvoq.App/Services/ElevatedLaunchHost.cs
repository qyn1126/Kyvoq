using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Kyvoq.App.Services;

/// <summary>
/// 在提权后的 Kyvoq 辅助模式中接收请求并创建最终子进程。
/// </summary>
internal static class ElevatedLaunchHost
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 客户端仅启用异步传输，避免 Windows 把 UAC 前后不同完整性级别判定为不同所有者。
    /// </summary>
    internal const PipeOptions ClientPipeOptions = PipeOptions.Asynchronous;

    /// <summary>
    /// 判断命令行是否请求运行提权辅助模式。
    /// </summary>
    /// <param name="arguments">应用启动参数。</param>
    /// <returns>参数包含完整辅助模式标记时返回 <see langword="true"/>。</returns>
    public static bool IsRequest(IReadOnlyList<string> arguments) =>
        arguments.Count == 3
        && string.Equals(
            arguments[0],
            ElevatedLaunchBroker.CommandSwitch,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 连接主进程管道、验证一次性令牌并启动目标程序。
    /// </summary>
    /// <param name="arguments">辅助模式标记、管道名和令牌。</param>
    /// <returns>辅助模式成功时返回 0，否则返回非零值。</returns>
    public static Task<int> RunAsync(IReadOnlyList<string> arguments) =>
        RunAsync(arguments, Process.Start, CancellationToken.None);

    /// <summary>
    /// 使用可替换进程启动器执行一次提权请求。
    /// </summary>
    /// <param name="arguments">辅助模式标记、管道名和令牌。</param>
    /// <param name="processStarter">创建最终进程的函数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>请求执行结果代码。</returns>
    internal static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        Func<ProcessStartInfo, Process?> processStarter,
        CancellationToken cancellationToken)
    {
        if (!IsRequest(arguments))
        {
            return 2;
        }

        var pipeName = arguments[1];
        var token = arguments[2];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectionTimeout);
        using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            ClientPipeOptions);
        try
        {
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            var request = await ElevatedLaunchProtocol
                .ReadAsync<ElevatedLaunchRequest>(pipe, timeout.Token)
                .ConfigureAwait(false);
            if (!TokensEqual(token, request.Token))
            {
                await ElevatedLaunchProtocol.WriteAsync(
                    pipe,
                    new ElevatedLaunchResponse(false, "管理员启动令牌无效。"),
                    timeout.Token).ConfigureAwait(false);
                return 3;
            }

            var startInfo = CreateStartInfo(request);
            using var process = processStarter(startInfo);
            await ElevatedLaunchProtocol.WriteAsync(
                pipe,
                new ElevatedLaunchResponse(true, string.Empty),
                timeout.Token).ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception) when (exception is Win32Exception
            or IOException
            or InvalidDataException
            or InvalidOperationException
            or OperationCanceledException)
        {
            if (pipe.IsConnected)
            {
                try
                {
                    await ElevatedLaunchProtocol.WriteAsync(
                        pipe,
                        new ElevatedLaunchResponse(
                            false,
                            $"无法启动管理员程序：{exception.Message}"),
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception responseException) when (responseException is IOException or InvalidOperationException)
                {
                }
            }

            return 1;
        }
    }

    /// <summary>
    /// 根据经过令牌验证的请求创建继承管理员令牌的最终启动信息。
    /// </summary>
    /// <param name="request">来自主进程的启动请求。</param>
    /// <returns>关闭 ShellExecute 且带环境变量的启动信息。</returns>
    private static ProcessStartInfo CreateStartInfo(ElevatedLaunchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Target))
        {
            throw new InvalidDataException("管理员启动目标不能为空。");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = request.Target,
            Arguments = request.Arguments,
            WorkingDirectory = Path.GetDirectoryName(request.Target) ?? string.Empty,
            UseShellExecute = false
        };
        foreach (var (name, value) in request.EnvironmentVariables)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Contains('=') || name.Contains('\0'))
            {
                throw new InvalidDataException("管理员启动请求包含无效环境变量名称。");
            }

            startInfo.Environment[name] = value;
        }

        return startInfo;
    }

    /// <summary>
    /// 使用固定时间比较一次性令牌，避免根据比较耗时泄露前缀信息。
    /// </summary>
    /// <param name="expected">命令行携带令牌。</param>
    /// <param name="actual">请求正文携带令牌。</param>
    /// <returns>两个令牌完全一致时返回 <see langword="true"/>。</returns>
    private static bool TokensEqual(string expected, string actual)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        return expectedBytes.Length == actualBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}
