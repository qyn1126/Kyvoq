using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using Kyvoq.Core.Models;
using Kyvoq.Core.Services;

namespace Kyvoq.App.Services;

/// <summary>
/// 通过 UAC 辅助进程和当前用户专用命名管道执行管理员环境变量启动。
/// </summary>
internal sealed class ElevatedLaunchBroker : IElevatedLaunchBroker
{
    internal const string CommandSwitch = "--elevated-launch";

    /// <summary>
    /// 服务端仍限制为创建管道的当前用户，并启用异步传输。
    /// </summary>
    internal const PipeOptions ServerPipeOptions =
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
    private readonly string executablePath;
    private readonly Func<ProcessStartInfo, Process?> helperStarter;
    private readonly TimeSpan timeout;

    /// <summary>
    /// 使用当前 Kyvoq 可执行文件和标准两分钟超时创建 Broker。
    /// </summary>
    public ElevatedLaunchBroker()
        : this(
            Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定 Kyvoq 可执行文件路径。"),
            Process.Start,
            TimeSpan.FromMinutes(2))
    {
    }

    /// <summary>
    /// 使用可替换辅助进程启动器创建可测试 Broker。
    /// </summary>
    /// <param name="executablePath">辅助模式使用的可执行文件路径。</param>
    /// <param name="helperStarter">启动辅助进程的函数。</param>
    /// <param name="timeout">等待 UAC 和管道响应的最长时间。</param>
    internal ElevatedLaunchBroker(
        string executablePath,
        Func<ProcessStartInfo, Process?> helperStarter,
        TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        this.executablePath = executablePath;
        this.helperStarter = helperStarter ?? throw new ArgumentNullException(nameof(helperStarter));
        this.timeout = timeout;
    }

    /// <summary>
    /// 异步启动提权辅助模式并通过一次性命名管道发送最终进程信息。
    /// </summary>
    /// <param name="item">包含管理员标记和环境变量的程序项目。</param>
    /// <param name="cancellationToken">取消提权启动等待的令牌。</param>
    /// <returns>包含 UAC 和最终进程创建结果的任务。</returns>
    public async Task<LaunchResult> LaunchAsync(
        LauncherItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var pipeName = $"Kyvoq.ElevatedLaunch.{Guid.NewGuid():N}";
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            ServerPipeOptions);
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = true,
            Verb = "runas"
        };
        startInfo.ArgumentList.Add(CommandSwitch);
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add(token);

        try
        {
            await Task.Run(
                () =>
                {
                    using var helperProcess = helperStarter(startInfo);
                },
                cancellationToken).ConfigureAwait(false);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cancellation.CancelAfter(timeout);
            await pipe.WaitForConnectionAsync(cancellation.Token).ConfigureAwait(false);
            var request = new ElevatedLaunchRequest(
                token,
                item.Name,
                item.Target,
                item.Arguments,
                new Dictionary<string, string>(
                    item.EnvironmentVariables,
                    StringComparer.OrdinalIgnoreCase));
            await ElevatedLaunchProtocol
                .WriteAsync(pipe, request, cancellation.Token)
                .ConfigureAwait(false);
            var response = await ElevatedLaunchProtocol
                .ReadAsync<ElevatedLaunchResponse>(pipe, cancellation.Token)
                .ConfigureAwait(false);
            return response.IsSuccessful
                ? LaunchResult.Success()
                : LaunchResult.Failure(response.ErrorMessage);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return LaunchResult.Failure("已取消管理员权限请求。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return LaunchResult.Failure("管理员启动请求等待超时。");
        }
        catch (Exception exception) when (exception is Win32Exception
            or IOException
            or InvalidDataException
            or InvalidOperationException)
        {
            return LaunchResult.Failure($"无法完成管理员启动：{exception.Message}");
        }
    }
}
