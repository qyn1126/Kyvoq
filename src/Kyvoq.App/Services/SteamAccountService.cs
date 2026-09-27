using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Kyvoq.Core.Services;

namespace Kyvoq.App.Services;

/// <summary>
/// 串行协调 Steam 退出、登录选择的原子写入及客户端重启。
/// </summary>
public sealed class SteamAccountService : ISteamAccountService
{
    private static readonly UTF8Encoding Encoding = new(false, true);
    private readonly ISteamPlatform platform;
    private readonly Action<string, byte[], bool> replaceFile;
    private readonly SemaphoreSlim operationGate = new(1, 1);

    /// <summary>
    /// 创建使用真实 Windows 环境的账号服务，构造时不访问 Steam。
    /// </summary>
    public SteamAccountService() : this(new SteamPlatform(), ReplaceFile)
    {
    }

    /// <summary>
    /// 注入进程、注册表及文件提交操作以验证失败恢复流程。
    /// </summary>
    internal SteamAccountService(ISteamPlatform platform, Action<string, byte[], bool>? replaceFile = null)
    {
        this.platform = platform;
        this.replaceFile = replaceFile ?? ReplaceFile;
    }

    /// <summary>
    /// 在后台读取账号，等待已开始的切换完成后再读取一致快照。
    /// </summary>
    public async Task<SteamAccountSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var installation = platform.FindInstallation();
                if (installation is null)
                {
                    return new SteamAccountSnapshot(false, []);
                }

                var path = Path.Combine(installation.DirectoryPath, "config", "loginusers.vdf");
                if (!File.Exists(path))
                {
                    return new SteamAccountSnapshot(true, []);
                }

                var activeId = platform.GetActiveAccount(installation);
                var accounts = SteamLoginFile.ReadAccounts(Encoding.GetString(ReadFile(path)))
                    .Select(account => account with
                    {
                        IsCurrent = account.SteamId64 == activeId,
                        AvatarPath = FindAvatar(installation, account.SteamId64)
                    }).ToArray();
                cancellationToken.ThrowIfCancellationRequested();
                return new SteamAccountSnapshot(true, accounts);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    /// <summary>
    /// 发起指定账号的客户端切换。
    /// </summary>
    public Task<SteamOperationResult> SwitchAccountAsync(ulong steamId64, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default) => ExecuteAsync(steamId64, progress, cancellationToken);

    /// <summary>
    /// 打开原生账号选择界面，不删除已有登录资料。
    /// </summary>
    public Task<SteamOperationResult> LoginAnotherAccountAsync(IProgress<string>? progress = null,
        CancellationToken cancellationToken = default) => ExecuteAsync(null, progress, cancellationToken);

    /// <summary>
    /// 删除本机登录记录，不重新启动客户端。
    /// </summary>
    public Task<SteamOperationResult> DeleteAccountAsync(ulong steamId64, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default) => ExecuteAsync(steamId64, progress, cancellationToken, delete: true);

    /// <summary>
    /// 拒绝并发请求，并将磁盘及 Windows 操作移出界面线程。
    /// </summary>
    private async Task<SteamOperationResult> ExecuteAsync(ulong? steamId, IProgress<string>? progress,
        CancellationToken cancellationToken, bool delete = false)
    {
        if (!await operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new SteamOperationResult(false, "Steam 操作正在进行，请稍后重试。");
        }

        try
        {
            return await Task.Run(() => ExecuteCoreAsync(steamId, progress, cancellationToken, delete), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    /// <summary>
    /// 在客户端完全退出后重新读取配置并提交选择，失败时尽力恢复原值。
    /// </summary>
    private async Task<SteamOperationResult> ExecuteCoreAsync(ulong? steamId, IProgress<string>? progress,
        CancellationToken cancellationToken, bool delete)
    {
        var stage = "检查 Steam 配置";
        var timer = Stopwatch.StartNew();
        SteamInstallation? installation = null;
        SteamRegistrySnapshot? registry = null;
        var registryTouched = false;
        var touchedFiles = new List<FileChange>();
        try
        {
            progress?.Report(stage);
            installation = platform.FindInstallation() ?? throw new IOException("未找到 Steam，请安装并启动一次 Steam 后刷新。");
            // 先校验所有将用到的文档，避免在配置损坏时退出客户端。
            _ = PrepareChanges(installation, steamId, delete);
            cancellationToken.ThrowIfCancellationRequested();
            if (platform.IsRunning(installation))
            {
                stage = "等待 Steam 退出";
                progress?.Report("正在退出 Steam，最多等待 15 秒…");
                platform.RequestShutdown(installation);
                if (!await platform.WaitForExitAsync(installation, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false))
                {
                    stage = "结束 Steam 客户端";
                    progress?.Report("Steam 未正常退出，正在结束客户端…");
                    platform.KillClient(installation);
                    if (!await platform.WaitForExitAsync(installation, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false))
                    {
                        throw new IOException("Steam 客户端仍在运行，请手动退出后重试。");
                    }
                }
            }

            stage = delete ? "删除本机登录记录" : "保存登录选择";
            progress?.Report(stage);
            cancellationToken.ThrowIfCancellationRequested();
            var (changes, accountName) = PrepareChanges(installation, steamId, delete);
            registry = platform.ReadLoginRegistry();
            if (platform.IsRunning(installation))
            {
                throw new IOException("Steam 已重新启动，请退出后重试。");
            }

            // 从开始提交到启动客户端不响应取消，避免留下半套登录选择。
            foreach (var change in changes)
            {
                if (!ReadFile(change.Path).AsSpan().SequenceEqual(change.Original))
                {
                    throw new IOException("Steam 配置已被其他程序修改，请刷新后重试。");
                }

                touchedFiles.Add(change);
                replaceFile(change.Path, change.Updated, true);
            }

            if (!delete || string.Equals(registry.AutoLoginUser.Value as string, accountName, StringComparison.OrdinalIgnoreCase))
            {
                registryTouched = true;
                platform.SetLoginRegistry(delete ? string.Empty : accountName);
            }

            if (delete)
            {
                DiagnosticLog.Current.Write("Steam.DeleteCompleted", $"ElapsedMs={timer.ElapsedMilliseconds}");
                return new SteamOperationResult(true, "已删除本机登录记录，Steam 保持关闭。");
            }

            stage = "启动 Steam";
            progress?.Report(stage);
            platform.Start(installation);
            DiagnosticLog.Current.Write("Steam.SwitchRequested", $"NewLogin={!steamId.HasValue}; ElapsedMs={timer.ElapsedMilliseconds}");
            return new SteamOperationResult(true, steamId.HasValue
                ? "已启动 Steam；如需验证，请在 Steam 中完成登录。"
                : "已打开 Steam，请在原生界面登录新账号，完成后刷新列表。");
        }
        catch (Exception exception) when (IsExpectedException(exception))
        {
            var recovery = string.Empty;
            if (installation is not null && (touchedFiles.Count > 0 || registryTouched))
            {
                recovery = Rollback(installation, touchedFiles, registryTouched ? registry : null);
            }

            // 不记录异常消息或配置内容，避免解析错误将敏感资料带入日志。
            DiagnosticLog.Current.Write(delete ? "Steam.DeleteFailed" : "Steam.SwitchFailed",
                $"Stage={stage}; ErrorType={exception.GetType().Name}; HResult={exception.HResult}; ElapsedMs={timer.ElapsedMilliseconds}");
            var reason = exception switch
            {
                OperationCanceledException => "操作已取消。",
                UnauthorizedAccessException or System.Security.SecurityException => "没有足够权限访问 Steam，请检查目录权限和 Steam 的运行权限。",
                Win32Exception => "无法操作 Steam 客户端，请检查其运行权限。",
                DecoderFallbackException => "Steam 配置不是有效的 UTF-8 文本。",
                _ => exception.Message
            };
            return new SteamOperationResult(false, $"{stage}失败：{reason}{recovery}");
        }
    }

    /// <summary>
    /// 根据最新磁盘内容生成局部修改，所有解析均在写入前完成。
    /// </summary>
    private static (List<FileChange> Changes, string AccountName) PrepareChanges(SteamInstallation installation, ulong? steamId, bool delete)
    {
        var changes = new List<FileChange>();
        var loginPath = Path.Combine(installation.DirectoryPath, "config", "loginusers.vdf");
        var accountName = string.Empty;
        if (File.Exists(loginPath))
        {
            var original = ReadFile(loginPath);
            var source = Encoding.GetString(original);
            var accounts = SteamLoginFile.ReadAccounts(source);
            if (steamId.HasValue)
            {
                accountName = accounts.FirstOrDefault(account => account.SteamId64 == steamId.Value)?.AccountName
                    ?? throw new InvalidDataException("目标 Steam 账号已不存在，请刷新账号列表。");
            }

            AddChange(changes, loginPath, original, delete
                ? SteamLoginFile.RemoveAccount(source, steamId!.Value)
                : SteamLoginFile.UpdateSelection(source, steamId));
        }
        else if (steamId.HasValue)
        {
            throw new InvalidDataException("未找到 Steam 登录记录，请先在 Steam 中登录一次。");
        }

        var configPath = Path.Combine(installation.DirectoryPath, "config", "config.vdf");
        if (!delete && File.Exists(configPath))
        {
            var original = ReadFile(configPath);
            AddChange(changes, configPath, original, SteamLoginFile.UpdateUserChooser(Encoding.GetString(original), !steamId.HasValue));
        }

        return (changes, accountName);
    }

    /// <summary>
    /// 只记录实际变化的文件，避免无意义覆盖 Steam 配置。
    /// </summary>
    private static void AddChange(List<FileChange> changes, string path, byte[] original, string updated)
    {
        var bytes = Encoding.GetBytes(updated);
        if (!original.AsSpan().SequenceEqual(bytes))
        {
            changes.Add(new FileChange(path, original, bytes));
        }
    }

    /// <summary>
    /// 在 Steam 尚未启动且文件未被外部修改时恢复本次已提交的变更。
    /// </summary>
    private string Rollback(SteamInstallation installation, List<FileChange> files, SteamRegistrySnapshot? registry)
    {
        try
        {
            if (platform.IsRunning(installation))
            {
                return " Steam 已运行，未覆盖其配置；原文件备份位于 Steam/config 下的 .kyvoq.bak 文件。";
            }
        }
        catch (Exception exception) when (IsExpectedException(exception))
        {
            return " 无法确认 Steam 已退出，未覆盖其配置；请检查 Steam/config 下的 .kyvoq.bak 备份。";
        }

        var failed = false;
        foreach (var file in files.AsEnumerable().Reverse())
        {
            try
            {
                var current = ReadFile(file.Path);
                if (current.AsSpan().SequenceEqual(file.Original))
                {
                    continue;
                }

                if (!current.AsSpan().SequenceEqual(file.Updated))
                {
                    failed = true;
                    continue;
                }

                replaceFile(file.Path, file.Original, false);
            }
            catch (Exception exception) when (IsExpectedException(exception))
            {
                failed = true;
            }
        }

        if (registry is not null)
        {
            try
            {
                platform.RestoreLoginRegistry(registry);
            }
            catch (Exception exception) when (IsExpectedException(exception))
            {
                failed = true;
            }
        }

        return failed ? " 部分原值未能恢复，请检查 Steam/config 下的 .kyvoq.bak 备份。" : " 已恢复原账号配置和登录选择。";
    }

    /// <summary>
    /// 限制读取大小，并允许 Steam 正在运行时共享读取。
    /// </summary>
    private static byte[] ReadFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 32 * 1024 * 1024)
        {
            throw new InvalidDataException("Steam 配置文件过大，无法安全处理。");
        }

        var data = new byte[checked((int)stream.Length)];
        stream.ReadExactly(data);
        return data;
    }

    /// <summary>
    /// 在原目录原子替换文件，正常提交保留原文件备份，回滚保留已有备份。
    /// </summary>
    internal static void ReplaceFile(string path, byte[] data, bool keepBackup)
    {
        var temporary = path + ".kyvoq-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }

            File.Replace(temporary, path, keepBackup ? path + ".kyvoq.bak" : null);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>
    /// 查找客户端自己的本地头像缓存，不发送网络请求。
    /// </summary>
    private static string? FindAvatar(SteamInstallation installation, ulong steamId)
    {
        var path = Path.Combine(installation.DirectoryPath, "config", "avatarcache", $"{steamId}.png");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// 判断可向用户呈现的环境或配置错误。
    /// </summary>
    internal static bool IsExpectedException(Exception exception) => exception is IOException or UnauthorizedAccessException
        or InvalidDataException or Win32Exception or System.Security.SecurityException or DecoderFallbackException
        or InvalidOperationException or OperationCanceledException or ArgumentException;

    /// <summary>
    /// 保存一次文件提交的原始和目标字节。
    /// </summary>
    private sealed record FileChange(string Path, byte[] Original, byte[] Updated);
}
