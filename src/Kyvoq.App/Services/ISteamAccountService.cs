using Kyvoq.Core.Models;

namespace Kyvoq.App.Services;

/// <summary>
/// 提供本机 Steam 账号读取及原生客户端切换操作。
/// </summary>
public interface ISteamAccountService
{
    /// <summary>
    /// 获取安装状态和本机账号快照。
    /// </summary>
    Task<SteamAccountSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 退出客户端并选择指定账号后重新启动。
    /// </summary>
    Task<SteamOperationResult> SwitchAccountAsync(ulong steamId64, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 清除自动登录选择并打开 Steam 原生账号选择界面。
    /// </summary>
    Task<SteamOperationResult> LoginAnotherAccountAsync(IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 退出客户端并删除指定本机登录记录，完成后保持 Steam 关闭。
    /// </summary>
    Task<SteamOperationResult> DeleteAccountAsync(ulong steamId64, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 保存一次账号查询的安装状态和账号列表。
/// </summary>
public sealed record SteamAccountSnapshot(bool IsInstalled, IReadOnlyList<SteamAccount> Accounts);

/// <summary>
/// 描述客户端启动请求的结果，不代表 Steam 已经验证登录成功。
/// </summary>
public sealed record SteamOperationResult(bool IsSuccessful, string Message);
