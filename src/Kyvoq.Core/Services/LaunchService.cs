using System.ComponentModel;
using System.Diagnostics;
using Kyvoq.Core.Models;

namespace Kyvoq.Core.Services;

/// <summary>
/// 使用 Windows 进程和 Shell 启动配置项目。
/// </summary>
public sealed class LaunchService : ILaunchService
{
    private readonly Func<ProcessStartInfo, Process?> processStarter;
    private readonly IElevatedLaunchBroker? elevatedLaunchBroker;

    /// <summary>
    /// 创建使用系统默认进程启动器的服务。
    /// </summary>
    public LaunchService()
        : this(null, Process.Start)
    {
    }

    /// <summary>
    /// 创建能够转交管理员环境变量请求的启动服务。
    /// </summary>
    /// <param name="elevatedLaunchBroker">负责提权启动的系统边界。</param>
    public LaunchService(IElevatedLaunchBroker elevatedLaunchBroker)
        : this(elevatedLaunchBroker ?? throw new ArgumentNullException(nameof(elevatedLaunchBroker)), Process.Start)
    {
    }

    /// <summary>
    /// 使用可替换的进程启动函数创建服务。
    /// </summary>
    /// <param name="processStarter">执行启动请求的函数。</param>
    internal LaunchService(Func<ProcessStartInfo, Process?> processStarter)
        : this(null, processStarter)
    {
    }

    /// <summary>
    /// 使用可替换的提权边界和进程启动函数创建服务。
    /// </summary>
    /// <param name="elevatedLaunchBroker">提权启动边界；为空时不支持管理员环境变量。</param>
    /// <param name="processStarter">执行普通启动请求的函数。</param>
    internal LaunchService(
        IElevatedLaunchBroker? elevatedLaunchBroker,
        Func<ProcessStartInfo, Process?> processStarter)
    {
        this.elevatedLaunchBroker = elevatedLaunchBroker;
        this.processStarter = processStarter ?? throw new ArgumentNullException(nameof(processStarter));
    }

    /// <summary>
    /// 异步启动指定项目，并将常见系统错误转换为用户可读结果。
    /// </summary>
    /// <param name="item">需要启动的项目。</param>
    /// <param name="cancellationToken">取消启动等待的令牌。</param>
    /// <returns>包含启动请求结果的任务。</returns>
    public async Task<LaunchResult> LaunchAsync(
        LauncherItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        try
        {
            if (TargetClassifier.Classify(item.Target) == LauncherTargetType.Application
                && item.RunAsAdministrator
                && item.EnvironmentVariables.Count > 0)
            {
                return elevatedLaunchBroker is null
                    ? LaunchResult.Failure("管理员环境变量启动服务不可用。")
                    : await elevatedLaunchBroker
                        .LaunchAsync(item, cancellationToken)
                        .ConfigureAwait(false);
            }

            var startInfo = CreateStartInfo(item);
            await Task.Run(
                () =>
                {
                    using var process = processStarter(startInfo);
                },
                cancellationToken).ConfigureAwait(false);
            return LaunchResult.Success();
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return LaunchResult.Failure("已取消管理员权限请求。");
        }
        catch (Exception exception) when (exception is Win32Exception
            or FileNotFoundException
            or DirectoryNotFoundException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            return LaunchResult.Failure($"无法启动“{item.Name}”：{exception.Message}");
        }
    }

    /// <summary>
    /// 根据目标类型、参数和管理员设置创建进程启动信息。
    /// </summary>
    /// <param name="item">需要转换的项目。</param>
    /// <returns>可交给系统启动的进程信息。</returns>
    public ProcessStartInfo CreateStartInfo(LauncherItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(item.Target))
        {
            throw new ArgumentException("启动目标不能为空。", nameof(item));
        }

        var targetType = TargetClassifier.Classify(item.Target);
        if (targetType != LauncherTargetType.Application)
        {
            return new ProcessStartInfo
            {
                FileName = item.Target,
                UseShellExecute = true
            };
        }

        var useShell = item.RunAsAdministrator;
        if (useShell && item.EnvironmentVariables.Count > 0)
        {
            throw new InvalidOperationException("管理员环境变量必须通过提权启动服务执行。");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = item.Target,
            Arguments = item.Arguments,
            WorkingDirectory = ResolveWorkingDirectory(item.Target),
            UseShellExecute = useShell,
            Verb = useShell ? "runas" : string.Empty
        };
        if (!useShell)
        {
            foreach (var (name, value) in item.EnvironmentVariables)
            {
                startInfo.Environment[name] = value;
            }
        }

        return startInfo;
    }

    /// <summary>
    /// 获取程序所在目录作为固定工作目录。
    /// </summary>
    /// <param name="target">程序目标路径。</param>
    /// <returns>用于进程启动的工作目录。</returns>
    private static string ResolveWorkingDirectory(string target) =>
        Path.GetDirectoryName(target) ?? string.Empty;
}
