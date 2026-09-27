using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;
using Kyvoq.Core.Services;

namespace Kyvoq.App.Services;

/// <summary>
/// 保存已验证的 Steam 安装位置。
/// </summary>
internal sealed record SteamInstallation(string DirectoryPath, string ExecutablePath);

/// <summary>
/// 保存注册表值及类型，允许准确恢复原先不存在的值。
/// </summary>
internal sealed record SteamRegistryValue(object? Value, RegistryValueKind Kind = RegistryValueKind.Unknown);

/// <summary>
/// 保存此次操作会修改的 Steam 登录注册表值。
/// </summary>
internal sealed record SteamRegistrySnapshot(SteamRegistryValue AutoLoginUser, SteamRegistryValue RememberPassword);

/// <summary>
/// 隔离 Windows 进程和注册表访问，供账号服务使用测试替身。
/// </summary>
internal interface ISteamPlatform
{
    /// <summary>查找 Steam 安装位置。</summary>
    SteamInstallation? FindInstallation();
    /// <summary>判断此安装目录是否仍有客户端进程。</summary>
    bool IsRunning(SteamInstallation installation);
    /// <summary>读取真正运行中的活动账号。</summary>
    ulong? GetActiveAccount(SteamInstallation installation);
    /// <summary>请求客户端正常退出。</summary>
    void RequestShutdown(SteamInstallation installation);
    /// <summary>等待客户端进程退出。</summary>
    Task<bool> WaitForExitAsync(SteamInstallation installation, TimeSpan timeout, CancellationToken cancellationToken);
    /// <summary>仅结束此目录及当前会话中的客户端进程。</summary>
    void KillClient(SteamInstallation installation);
    /// <summary>启动原生客户端。</summary>
    void Start(SteamInstallation installation);
    /// <summary>保存原登录注册表值。</summary>
    SteamRegistrySnapshot ReadLoginRegistry();
    /// <summary>设置自动登录账号，空字符串表示登录其他账号。</summary>
    void SetLoginRegistry(string accountName);
    /// <summary>恢复登录注册表的原始值。</summary>
    void RestoreLoginRegistry(SteamRegistrySnapshot snapshot);
}

/// <summary>
/// 通过注册表和精确进程路径操作当前 Windows 会话中的 Steam。
/// </summary>
internal sealed class SteamPlatform : ISteamPlatform
{
    private const string RegistryPath = @"Software\Valve\Steam";

    /// <summary>
    /// 优先读取当前用户安装位置，再查询机器安装记录。
    /// </summary>
    public SteamInstallation? FindInstallation()
    {
        using var userKey = Registry.CurrentUser.OpenSubKey(RegistryPath);
        var candidates = new List<string?>
        {
            userKey?.GetValue("SteamExe") as string,
            MakeExecutablePath(userKey?.GetValue("SteamPath") as string)
        };
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var machineKey = machine.OpenSubKey(RegistryPath);
            candidates.Add(MakeExecutablePath(machineKey?.GetValue("InstallPath") as string));
        }

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate) || !File.Exists(candidate))
            {
                continue;
            }

            var executable = Path.GetFullPath(candidate);
            if (string.Equals(Path.GetFileName(executable), "steam.exe", StringComparison.OrdinalIgnoreCase))
            {
                return new SteamInstallation(Path.GetDirectoryName(executable)!, executable);
            }
        }

        return null;
    }

    /// <summary>
    /// 将有效目录转换为客户端文件路径。
    /// </summary>
    private static string? MakeExecutablePath(string? directory) =>
        string.IsNullOrWhiteSpace(directory) ? null : Path.Combine(directory, "steam.exe");

    /// <summary>
    /// 检查匹配安装目录与会话的客户端进程。
    /// </summary>
    public bool IsRunning(SteamInstallation installation) => GetClientProcessIds(installation).Count > 0;

    /// <summary>
    /// 仅在注册表 PID 对应本次安装的主进程时接受活动账号信息。
    /// </summary>
    public ulong? GetActiveAccount(SteamInstallation installation)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath + @"\ActiveProcess");
        if (key?.GetValue("pid") is not int pid || key.GetValue("ActiveUser") is not int accountId || accountId == 0)
        {
            return null;
        }

        try
        {
            using var current = Process.GetCurrentProcess();
            using var process = Process.GetProcessById(pid);
            return IsClientProcess(installation, process.MainModule?.FileName, process.SessionId,
                current.SessionId) && process.ProcessName.Equals("steam", StringComparison.OrdinalIgnoreCase)
                ? SteamLoginFile.IndividualAccountBase + unchecked((uint)accountId) : null;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 通过 Steam 官方客户端参数请求正常退出。
    /// </summary>
    public void RequestShutdown(SteamInstallation installation) => StartProcess(installation, "-shutdown");

    /// <summary>
    /// 异步轮询退出状态，时间到达后返回失败。
    /// </summary>
    public async Task<bool> WaitForExitAsync(SteamInstallation installation, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        while (IsRunning(installation))
        {
            if (watch.Elapsed >= timeout)
            {
                return false;
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// 按进程重新校验路径和会话后结束客户端，不递归结束子进程。
    /// </summary>
    public void KillClient(SteamInstallation installation)
    {
        using var current = Process.GetCurrentProcess();
        foreach (var pid in GetClientProcessIds(installation))
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (!process.HasExited && IsClientProcess(installation, process.MainModule?.FileName, process.SessionId, current.SessionId))
                {
                    process.Kill(entireProcessTree: false);
                }
            }
            catch (ArgumentException)
            {
                // 进程已经退出。
            }
        }
    }

    /// <summary>
    /// 启动客户端，由 Steam 自行完成身份验证。
    /// </summary>
    public void Start(SteamInstallation installation) => StartProcess(installation, null);

    /// <summary>
    /// 直接创建 Steam 进程，不经过命令解释器。
    /// </summary>
    private static void StartProcess(SteamInstallation installation, string? argument)
    {
        var info = new ProcessStartInfo(installation.ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = installation.DirectoryPath
        };
        if (argument is not null)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new IOException("无法创建 Steam 客户端进程。");
    }

    /// <summary>
    /// 枚举并释放进程句柄，仅返回匹配的客户端 PID。
    /// </summary>
    private static List<int> GetClientProcessIds(SteamInstallation installation)
    {
        using var current = Process.GetCurrentProcess();
        var ids = new List<int>();
        foreach (var name in new[] { "steam", "steamwebhelper" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        if (process.HasExited || process.SessionId != current.SessionId)
                        {
                            continue;
                        }

                        var executable = process.MainModule?.FileName
                            ?? throw new IOException("无法确认 Steam 客户端进程路径。");
                        if (IsClientProcess(installation, executable, process.SessionId, current.SessionId))
                        {
                            ids.Add(process.Id);
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // 枚举期间进程已经退出。
                    }
                    // 无权读取进程路径时向上报告，不能假定 Steam 已经退出。
                }
            }
        }

        return ids;
    }

    /// <summary>
    /// 校验客户端名称、安装目录边界与会话，供进程操作和回归测试复用。
    /// </summary>
    internal static bool IsClientProcess(SteamInstallation installation, string? executablePath, int sessionId, int currentSessionId)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || sessionId != currentSessionId)
        {
            return false;
        }

        var path = Path.GetFullPath(executablePath);
        var name = Path.GetFileName(path);
        return name.Equals("steam.exe", StringComparison.OrdinalIgnoreCase)
            ? path.Equals(installation.ExecutablePath, StringComparison.OrdinalIgnoreCase)
            : name.Equals("steamwebhelper.exe", StringComparison.OrdinalIgnoreCase)
                && path.StartsWith(Path.TrimEndingDirectorySeparator(installation.DirectoryPath) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 保存将被修改的两个注册表值和类型。
    /// </summary>
    public SteamRegistrySnapshot ReadLoginRegistry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
        return new SteamRegistrySnapshot(ReadValue(key, "AutoLoginUser"), ReadValue(key, "RememberPassword"));
    }

    /// <summary>
    /// 设置账号选择；新账号入口不修改已有记住密码设置。
    /// </summary>
    public void SetLoginRegistry(string accountName)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);
        key.SetValue("AutoLoginUser", accountName, RegistryValueKind.String);
        if (accountName.Length > 0)
        {
            key.SetValue("RememberPassword", 1, RegistryValueKind.DWord);
        }
    }

    /// <summary>
    /// 恢复值的原始类型，原来不存在的值会被移除。
    /// </summary>
    public void RestoreLoginRegistry(SteamRegistrySnapshot snapshot)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);
        RestoreValue(key, "AutoLoginUser", snapshot.AutoLoginUser);
        RestoreValue(key, "RememberPassword", snapshot.RememberPassword);
    }

    /// <summary>
    /// 读取原始注册表值，避免展开环境变量。
    /// </summary>
    private static SteamRegistryValue ReadValue(RegistryKey? key, string name) =>
        key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is { } value
            ? new SteamRegistryValue(value, key.GetValueKind(name)) : new SteamRegistryValue(null);

    /// <summary>
    /// 恢复一个原始注册表值。
    /// </summary>
    private static void RestoreValue(RegistryKey key, string name, SteamRegistryValue value)
    {
        if (value.Value is null)
        {
            key.DeleteValue(name, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(name, value.Value, value.Kind);
        }
    }
}
