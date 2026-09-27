namespace Kyvoq.Core.Models;

/// <summary>
/// 表示 Steam 在本机保存的账号资料，不包含密码或登录令牌。
/// </summary>
public sealed record SteamAccount(
    ulong SteamId64,
    string AccountName,
    string PersonaName,
    long LastLoginTimestamp,
    bool IsLastUsed)
{
    public string DisplayName => string.IsNullOrWhiteSpace(PersonaName) ? AccountName : PersonaName;

    public string? AvatarPath { get; init; }

    public bool IsCurrent { get; init; }
}
