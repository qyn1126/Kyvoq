namespace Kyvoq.App.Services;

/// <summary>
/// 独立保存本机 Steam 账号备注，不参与启动器配置导入导出。
/// </summary>
public interface ISteamAccountNoteStore
{
    /// <summary>读取以 SteamID64 为键的全部备注。</summary>
    Task<IReadOnlyDictionary<ulong, string>> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>保存一个账号的备注，空文本表示清除备注。</summary>
    Task SetAsync(ulong steamId64, string note, CancellationToken cancellationToken = default);
}
