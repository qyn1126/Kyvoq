using System.Globalization;
using Kyvoq.Core.Models;

namespace Kyvoq.Core.Services;

/// <summary>
/// 读取和修改 Steam 本地账号选择信息，同时兼容新旧登录标记。
/// </summary>
public static class SteamLoginFile
{
    public const ulong IndividualAccountBase = 76561197960265728;

    /// <summary>
    /// 提取有效的本机账号；缺失可选字段时使用空昵称、零时间和未选中状态。
    /// </summary>
    public static IReadOnlyList<SteamAccount> ReadAccounts(string source)
    {
        var users = GetUsers(new SteamVdfDocument(source));
        var accounts = new List<SteamAccount>();
        var ids = new HashSet<ulong>();
        foreach (var node in users.Children!)
        {
            if (node.Children is null || !TryGetSteamId(node.Name, out var id))
            {
                continue;
            }

            if (!ids.Add(id))
            {
                throw new InvalidDataException("Steam 配置中存在重复账号。");
            }

            var name = GetValue(node, "AccountName");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            _ = long.TryParse(GetValue(node, "Timestamp"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var timestamp);
            var selected = GetValue(node, "AutoLogin") ?? GetValue(node, "MostRecent");
            accounts.Add(new SteamAccount(id, name, GetValue(node, "PersonaName") ?? string.Empty,
                Math.Max(0, timestamp), selected == "1"));
        }

        return accounts.OrderByDescending(account => account.LastLoginTimestamp)
            .ThenBy(account => account.AccountName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// 选择目标账号，或清除选择以进入 Steam 原生登录界面。
    /// </summary>
    public static string UpdateSelection(string source, ulong? steamId64)
    {
        var accounts = ReadAccounts(source);
        if (steamId64.HasValue && !accounts.Any(account => account.SteamId64 == steamId64.Value))
        {
            throw new InvalidDataException("目标 Steam 账号已不存在，请刷新账号列表。");
        }

        var document = new SteamVdfDocument(source);
        var users = GetUsers(document).Children!.Where(node => node.Children is not null && TryGetSteamId(node.Name, out _)).ToArray();
        var modern = users.Any(node => GetValue(node, "AutoLogin") is not null)
            || !users.Any(node => GetValue(node, "MostRecent") is not null);
        foreach (var node in users)
        {
            _ = TryGetSteamId(node.Name, out var id);
            var selected = steamId64 == id;
            document.Set(node, modern ? "AutoLogin" : "MostRecent", selected ? "1" : "0");
            document.Set(node, modern ? "MostRecent" : "AutoLogin", selected ? "1" : "0", create: false);
            if (selected)
            {
                document.Set(node, "RememberPassword", "1");
                document.Set(node, "AllowAutoLogin", "1", create: !modern);
            }
        }

        return document.Render();
    }

    /// <summary>
    /// 删除指定本机账号的完整记录，保留其余账号及未知配置。
    /// </summary>
    public static string RemoveAccount(string source, ulong steamId64)
    {
        if (!ReadAccounts(source).Any(account => account.SteamId64 == steamId64))
        {
            throw new InvalidDataException("目标 Steam 账号已不存在，请刷新账号列表。");
        }

        var document = new SteamVdfDocument(source);
        var target = GetUsers(document).Children!.Single(node => node.Children is not null
            && TryGetSteamId(node.Name, out var id) && id == steamId64);
        document.Remove(target);
        return document.Render();
    }

    /// <summary>
    /// 仅在原配置已有账号选择器字段时更新其开关。
    /// </summary>
    public static string UpdateUserChooser(string source, bool showChooser)
    {
        var document = new SteamVdfDocument(source);
        var root = SteamVdfDocument.Find(document.Roots, "InstallConfigStore");
        var storage = root?.Children is { } roots ? SteamVdfDocument.Find(roots, "WebStorage") : null;
        var auth = storage?.Children is { } children ? SteamVdfDocument.Find(children, "Auth") : null;
        if (auth?.Children is not null)
        {
            document.Set(auth, "AlwaysShowUserChooser", showChooser ? "1" : "0", create: false);
        }

        return document.Render();
    }

    /// <summary>
    /// 定位唯一的账号根节点。
    /// </summary>
    private static SteamVdfDocument.Node GetUsers(SteamVdfDocument document) =>
        SteamVdfDocument.Find(document.Roots, "users") is { Children: not null } users
            ? users : throw new InvalidDataException("Steam 登录配置缺少 users 对象。");

    /// <summary>
    /// 读取可选的文本字段。
    /// </summary>
    private static string? GetValue(SteamVdfDocument.Node node, string name)
    {
        var field = SteamVdfDocument.Find(node.Children!, name);
        if (field?.Children is not null)
        {
            throw new InvalidDataException("Steam 账号字段结构不正确。");
        }

        return field?.Value;
    }

    /// <summary>
    /// 校验公共个人账号的 SteamID64，避免将未知对象当作账号修改。
    /// </summary>
    private static bool TryGetSteamId(string value, out ulong id) =>
        ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id)
        && id > IndividualAccountBase && id <= IndividualAccountBase + uint.MaxValue;
}
