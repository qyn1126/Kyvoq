using Kyvoq.Core.Services;

namespace Kyvoq.Tests.Services;

/// <summary>
/// 验证 Steam 新旧账号配置兼容及只修改登录选择的行为。
/// </summary>
public sealed class SteamLoginFileTests
{
    private const ulong FirstId = SteamLoginFile.IndividualAccountBase + 1;
    private const ulong SecondId = SteamLoginFile.IndividualAccountBase + 2;

    /// <summary>
    /// 验证新旧及大小写变体均可读取，缺少可选字段不会丢失账号。
    /// </summary>
    [Theory]
    [InlineData("AutoLogin")]
    [InlineData("MostRecent")]
    [InlineData("mostrecent")]
    [InlineData("autologin")]
    public void ReadAndSwitch_ShouldSupportMarkerVersions(string marker)
    {
        var source = CreateAccounts(marker);
        var accounts = SteamLoginFile.ReadAccounts(source);
        Assert.Equal(2, accounts.Count);
        Assert.Equal(SecondId, accounts[0].SteamId64);
        Assert.True(accounts.Single(account => account.SteamId64 == FirstId).IsLastUsed);
        Assert.Equal("second", accounts[0].DisplayName);

        var updated = SteamLoginFile.UpdateSelection(source, SecondId);
        var switched = SteamLoginFile.ReadAccounts(updated);
        Assert.Equal(SecondId, Assert.Single(switched, account => account.IsLastUsed).SteamId64);
        Assert.Contains("\"Unknown\" { \"nested\" \"保留\" }", updated);
        Assert.Contains("// 保留注释", updated);
        Assert.DoesNotContain(marker.Equals("AutoLogin", StringComparison.OrdinalIgnoreCase) ? "\"MostRecent\"" : "\"AutoLogin\"", updated);
    }

    /// <summary>
    /// 验证新版标记优先且混合版本更新后不会出现多个被选中的账号。
    /// </summary>
    [Fact]
    public void MixedMarkers_ShouldPreferModernAndSynchronizeExistingLegacyValues()
    {
        var source = CreateAccounts("AutoLogin").Replace("\"AutoLogin\" \"1\"", "\"AutoLogin\" \"0\" \"mostrecent\" \"1\"");
        Assert.DoesNotContain(SteamLoginFile.ReadAccounts(source), account => account.IsLastUsed);
        var updated = SteamLoginFile.UpdateSelection(source, SecondId);
        Assert.Contains("\"mostrecent\" \"0\"", updated);
        Assert.Equal(SecondId, Assert.Single(SteamLoginFile.ReadAccounts(updated), account => account.IsLastUsed).SteamId64);
    }

    /// <summary>
    /// 验证 BOM、中文、转义、无引号字段及缺失标记的文件可以更新并再次读取。
    /// </summary>
    [Fact]
    public void UpdateSelection_ShouldPreserveBomEscapesAndUnrelatedValues()
    {
        var source = "\uFEFF" + $$"""
            users {
                "{{FirstId}}" {
                    AccountName "first"
                    PersonaName "中文\"昵称\\路径"
                    future "opaque-value"
                }
            }
            """;
        var original = Assert.Single(SteamLoginFile.ReadAccounts(source));
        Assert.Equal("中文\"昵称\\路径", original.DisplayName);
        Assert.Equal(0, original.LastLoginTimestamp);
        var updated = SteamLoginFile.UpdateSelection(source, FirstId);
        Assert.StartsWith("\uFEFF", updated);
        Assert.Contains("future \"opaque-value\"", updated);
        Assert.Contains("\"AutoLogin\"", updated);
        Assert.True(Assert.Single(SteamLoginFile.ReadAccounts(updated)).IsLastUsed);
    }

    /// <summary>
    /// 验证登录其他账号只清除选择，不删除账号或改动记住密码设置。
    /// </summary>
    [Fact]
    public void ClearSelection_ShouldKeepAccountsAndPasswordPreferences()
    {
        var source = CreateAccounts("AutoLogin");
        var updated = SteamLoginFile.UpdateSelection(source, null);
        Assert.Equal(2, SteamLoginFile.ReadAccounts(updated).Count);
        Assert.DoesNotContain(SteamLoginFile.ReadAccounts(updated), account => account.IsLastUsed);
        Assert.Contains("\"RememberPassword\" \"0\"", updated);
        Assert.DoesNotContain("AllowAutoLogin", updated);
    }

    /// <summary>
    /// 验证损坏或存在歧义的配置不能被重写。
    /// </summary>
    [Theory]
    [InlineData("\"users\" { \"123\" {")]
    [InlineData("\"users\" { \"x\" \"unterminated }")]
    [InlineData("\"users\" { } /* unclosed")]
    [InlineData("\"users\" { } }")]
    [InlineData("\"users\" { } \"USERS\" { }")]
    [InlineData("\"wrongRoot\" { }")]
    public void InvalidDocuments_ShouldRejectUpdates(string source)
    {
        Assert.Throws<InvalidDataException>(() => SteamLoginFile.UpdateSelection(source, null));
        Assert.Throws<InvalidDataException>(() => SteamLoginFile.RemoveAccount(source, FirstId));
    }

    /// <summary>
    /// 验证失效账号不能触发改写，缺少必需登录名的账号不会出现在列表中。
    /// </summary>
    [Fact]
    public void MissingAccount_ShouldRejectSelection()
    {
        var source = $$"""
            "users" { "{{FirstId}}" { "PersonaName" "名字" } "unknown" { "data" "value" } }
            """;
        Assert.Empty(SteamLoginFile.ReadAccounts(source));
        Assert.Throws<InvalidDataException>(() => SteamLoginFile.UpdateSelection(source, FirstId));
    }

    /// <summary>
    /// 验证选择器仅修改已有目标字段，其他 Auth 数据保持逐字不变。
    /// </summary>
    [Fact]
    public void UserChooser_ShouldOnlyChangeExistingSetting()
    {
        const string source = "\"InstallConfigStore\" { \"WebStorage\" { \"Auth\" { \"AlwaysShowUserChooser\" \"1\" \"other\" \"opaque\" } } }";
        var updated = SteamLoginFile.UpdateUserChooser(source, false);
        Assert.Equal(source.Replace("\"1\"", "\"0\""), updated);
        Assert.Equal(source, SteamLoginFile.UpdateUserChooser(updated, true));
        const string absent = "\"InstallConfigStore\" { \"Software\" { \"other\" \"value\" } }";
        Assert.Equal(absent, SteamLoginFile.UpdateUserChooser(absent, true));
    }

    /// <summary>
    /// 验证只移除目标对象，保留 BOM、注释和其他对象的每个字符。
    /// </summary>
    [Theory]
    [InlineData("AutoLogin")]
    [InlineData("MostRecent")]
    public void Remove_ShouldPreserveOtherAccountsAndAllowLastAccountRemoval(string marker)
    {
        var source = "\uFEFF" + CreateAccounts(marker);
        var target = $"\"{FirstId}\" {{ \"AccountName\" \"first\" \"PersonaName\" \"第一号\" \"{marker}\" \"1\" \"RememberPassword\" \"0\" }}";
        var removed = SteamLoginFile.RemoveAccount(source, FirstId);
        Assert.Equal(source.Replace(target, string.Empty), removed);
        Assert.Equal(SecondId, Assert.Single(SteamLoginFile.ReadAccounts(removed)).SteamId64);
        var empty = SteamLoginFile.RemoveAccount(removed, SecondId);
        Assert.Empty(SteamLoginFile.ReadAccounts(empty));
        Assert.Contains("// 保留注释", empty);
        Assert.Throws<InvalidDataException>(() => SteamLoginFile.RemoveAccount(removed, FirstId));
    }

    /// <summary>验证存在重复目标账号时拒绝含糊的删除。</summary>
    [Fact]
    public void Remove_ShouldRejectDuplicateAccountIds()
    {
        var source = CreateAccounts("AutoLogin").Replace(SecondId.ToString(), FirstId.ToString());
        Assert.Throws<InvalidDataException>(() => SteamLoginFile.RemoveAccount(source, FirstId));
    }

    /// <summary>
    /// 创建含未知字段、注释与缺省可选值的双账号配置。
    /// </summary>
    private static string CreateAccounts(string marker) => $$"""
        "users" {
            // 保留注释
            "{{FirstId}}" { "AccountName" "first" "PersonaName" "第一号" "{{marker}}" "1" "RememberPassword" "0" }
            "{{SecondId}}" { "AccountName" "second" "{{marker}}" "0" "timestamp" "100" "Unknown" { "nested" "保留" } }
        }
        """;
}
