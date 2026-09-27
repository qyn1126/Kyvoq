using System.IO;
using System.Text;
using Kyvoq.App.Services;
using Kyvoq.Core.Services;
using Microsoft.Win32;

namespace Kyvoq.App.Tests.Services;

/// <summary>
/// 使用临时文件及进程注册表替身验证切换，不操作本机真实 Steam。
/// </summary>
public sealed class SteamAccountServiceTests : IDisposable
{
    private const ulong FirstId = SteamLoginFile.IndividualAccountBase + 1;
    private const ulong SecondId = SteamLoginFile.IndividualAccountBase + 2;
    private readonly string directory = Path.Combine(Path.GetTempPath(), "Kyvoq.Steam.Tests", Guid.NewGuid().ToString("N"));
    private readonly FakePlatform platform;
    private string LoginPath => Path.Combine(directory, "config", "loginusers.vdf");
    private string ConfigPath => Path.Combine(directory, "config", "config.vdf");

    /// <summary>
    /// 为每个测试创建独立的双账号 Steam 配置。
    /// </summary>
    public SteamAccountServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(directory, "config"));
        File.WriteAllText(LoginPath, $$"""
            "users" {
                "{{FirstId}}" { "AccountName" "first" "AutoLogin" "1" "RememberPassword" "1" }
                "{{SecondId}}" { "AccountName" "second" "AutoLogin" "0" "RememberPassword" "1" }
            }
            """, new UTF8Encoding(true));
        File.WriteAllText(ConfigPath,
            "\"InstallConfigStore\" { \"WebStorage\" { \"Auth\" { \"AlwaysShowUserChooser\" \"1\" \"other\" \"keep\" } } }");
        platform = new FakePlatform(new SteamInstallation(directory, Path.Combine(directory, "steam.exe")));
    }

    /// <summary>
    /// 删除本测试独有的临时目录。
    /// </summary>
    public void Dispose() => Directory.Delete(directory, recursive: true);

    /// <summary>
    /// 验证正常退出后重新读取 Steam 保存的最新字段、保留 BOM 和备份，并更新真实活动标记。
    /// </summary>
    [Fact]
    public async Task Switch_ShouldReadAfterShutdownAndCommitBeforeStarting()
    {
        var before = File.ReadAllBytes(LoginPath);
        platform.OnWait = () =>
        {
            var text = File.ReadAllText(LoginPath).Replace("\"first\"", "\"first-renamed\"");
            File.WriteAllText(LoginPath, text, new UTF8Encoding(true));
            return Task.CompletedTask;
        };
        var service = new SteamAccountService(platform);
        var result = await service.SwitchAccountAsync(SecondId, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccessful, result.Message);
        Assert.Equal(1, platform.ShutdownCount);
        Assert.Equal(0, platform.KillCount);
        Assert.Equal(1, platform.StartCount);
        Assert.Equal("second", platform.Registry.AutoLoginUser.Value);
        Assert.Equal(SecondId, Assert.Single(SteamLoginFile.ReadAccounts(File.ReadAllText(LoginPath)), account => account.IsLastUsed).SteamId64);
        Assert.Contains("first-renamed", File.ReadAllText(LoginPath));
        Assert.Equal(before[..3], File.ReadAllBytes(LoginPath)[..3]);
        Assert.Contains("first-renamed", File.ReadAllText(LoginPath + ".kyvoq.bak"));
        Assert.Contains("\"AlwaysShowUserChooser\" \"0\"", File.ReadAllText(ConfigPath));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(LoginPath)!, "*.tmp"));

        var snapshot = await service.GetSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.True(snapshot.IsInstalled);
        Assert.True(snapshot.Accounts.Single(account => account.SteamId64 == FirstId).IsCurrent);
        Assert.False(snapshot.Accounts.Single(account => account.SteamId64 == SecondId).IsCurrent);
    }

    /// <summary>
    /// 验证正常退出超时后才结束客户端，并再次等待退出。
    /// </summary>
    [Fact]
    public async Task ShutdownTimeout_ShouldKillAndWaitAgain()
    {
        platform.GracefulExit = false;
        var result = await new SteamAccountService(platform).SwitchAccountAsync(SecondId, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccessful, result.Message);
        Assert.Equal(1, platform.KillCount);
        Assert.Equal(new[] { TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(5) }, platform.Timeouts);
    }

    /// <summary>
    /// 验证强制退出仍失败时不修改文件或注册表、不重启客户端。
    /// </summary>
    [Fact]
    public async Task KillFailure_ShouldLeaveOriginalSelectionUntouched()
    {
        var original = File.ReadAllBytes(LoginPath);
        platform.GracefulExit = false;
        platform.ForcedExit = false;
        var result = await new SteamAccountService(platform).SwitchAccountAsync(SecondId, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.IsSuccessful);
        Assert.Equal(original, File.ReadAllBytes(LoginPath));
        Assert.Equal("first", platform.Registry.AutoLoginUser.Value);
        Assert.Equal(0, platform.StartCount);
    }

    /// <summary>
    /// 验证文档损坏和目标消失会在退出 Steam 前报告失败。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidInput_ShouldNotStopSteam(bool corrupt)
    {
        if (corrupt)
        {
            File.WriteAllText(LoginPath, "\"users\" {");
        }

        var original = File.ReadAllBytes(LoginPath);
        var result = await new SteamAccountService(platform).SwitchAccountAsync(corrupt ? SecondId : SecondId + 10,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.IsSuccessful);
        Assert.Equal(0, platform.ShutdownCount);
        Assert.Equal(0, platform.StartCount);
        Assert.Equal(original, File.ReadAllBytes(LoginPath));
    }

    /// <summary>
    /// 验证第二份文件提交失败时恢复第一份文件，原备份仍然可用。
    /// </summary>
    [Fact]
    public async Task PartialFileFailure_ShouldRollbackPreviousFile()
    {
        var original = File.ReadAllBytes(LoginPath);
        var config = File.ReadAllBytes(ConfigPath);
        var service = new SteamAccountService(platform, (path, data, backup) =>
        {
            if (path == ConfigPath && backup)
            {
                throw new UnauthorizedAccessException();
            }

            SteamAccountService.ReplaceFile(path, data, backup);
        });
        var result = await service.SwitchAccountAsync(SecondId, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.IsSuccessful);
        Assert.Contains("已恢复", result.Message);
        Assert.Equal(original, File.ReadAllBytes(LoginPath));
        Assert.Equal(original, File.ReadAllBytes(LoginPath + ".kyvoq.bak"));
        Assert.Equal(config, File.ReadAllBytes(ConfigPath));
        Assert.Equal("first", platform.Registry.AutoLoginUser.Value);
        Assert.Equal(0, platform.StartCount);
    }

    /// <summary>
    /// 验证注册表部分写入或客户端启动失败时恢复全部原值及注册表类型。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RegistryOrStartFailure_ShouldRestoreFilesAndRegistry(bool registryFailure)
    {
        var original = File.ReadAllBytes(LoginPath);
        var config = File.ReadAllBytes(ConfigPath);
        var registry = platform.Registry;
        platform.FailRegistryWrite = registryFailure;
        platform.FailStart = !registryFailure;
        var result = await new SteamAccountService(platform).SwitchAccountAsync(SecondId, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.IsSuccessful);
        Assert.Contains("已恢复", result.Message);
        Assert.Equal(original, File.ReadAllBytes(LoginPath));
        Assert.Equal(config, File.ReadAllBytes(ConfigPath));
        Assert.Equal(registry, platform.Registry);
    }

    /// <summary>
    /// 验证启动异常时如果 Steam 已运行，不回滚覆盖正在使用的配置。
    /// </summary>
    [Fact]
    public async Task StartFailureWithRunningClient_ShouldKeepCommittedSelection()
    {
        platform.FailStart = true;
        platform.RunningAfterFailedStart = true;
        var result = await new SteamAccountService(platform).SwitchAccountAsync(SecondId, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.IsSuccessful);
        Assert.Contains("Steam 已运行", result.Message);
        Assert.Equal("second", platform.Registry.AutoLoginUser.Value);
        Assert.Equal(SecondId, Assert.Single(SteamLoginFile.ReadAccounts(File.ReadAllText(LoginPath)), account => account.IsLastUsed).SteamId64);
    }

    /// <summary>
    /// 验证新账号入口清除选择、保留账号，并允许首次登录时没有账号文件。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LoginAnother_ShouldKeepRememberedAccounts(bool hasAccounts)
    {
        if (!hasAccounts)
        {
            File.Delete(LoginPath);
        }

        var result = await new SteamAccountService(platform).LoginAnotherAccountAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccessful, result.Message);
        Assert.Equal(string.Empty, platform.Registry.AutoLoginUser.Value);
        Assert.Equal(1, platform.StartCount);
        Assert.Equal(hasAccounts, File.Exists(LoginPath));
        if (hasAccounts)
        {
            var accounts = SteamLoginFile.ReadAccounts(File.ReadAllText(LoginPath));
            Assert.Equal(2, accounts.Count);
            Assert.DoesNotContain(accounts, account => account.IsLastUsed);
        }
    }

    /// <summary>
    /// 验证重复操作不会排队造成连续两次客户端重启。
    /// </summary>
    [Fact]
    public async Task ConcurrentSwitch_ShouldRejectSecondRequest()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        platform.OnWait = async () =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        var service = new SteamAccountService(platform);
        var first = service.SwitchAccountAsync(SecondId, cancellationToken: TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            var second = await service.LoginAnotherAccountAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(second.IsSuccessful);
            Assert.Contains("正在进行", second.Message);
        }
        finally
        {
            release.TrySetResult();
            await first;
        }

        Assert.Equal(1, platform.StartCount);
    }

    /// <summary>
    /// 验证卸载状态下不会访问配置或尝试启动客户端。
    /// </summary>
    [Fact]
    public async Task MissingInstallation_ShouldReturnEmptySnapshot()
    {
        platform.Installed = false;
        var service = new SteamAccountService(platform);
        Assert.False((await service.GetSnapshotAsync(TestContext.Current.CancellationToken)).IsInstalled);
        Assert.False((await service.LoginAnotherAccountAsync(cancellationToken: TestContext.Current.CancellationToken)).IsSuccessful);
        Assert.Equal(0, platform.StartCount);
    }

    /// <summary>
    /// 验证结束进程的筛选同时约束目录边界、文件名和 Windows 会话。
    /// </summary>
    [Theory]
    [InlineData(@"C:\Steam\steam.exe", 1, true)]
    [InlineData(@"C:\Steam\bin\cef\steamwebhelper.exe", 1, true)]
    [InlineData(@"C:\SteamOther\steamwebhelper.exe", 1, false)]
    [InlineData(@"C:\Steam\steamapps\game.exe", 1, false)]
    [InlineData(@"C:\Steam\steamservice.exe", 1, false)]
    [InlineData(@"C:\Steam\steam.exe", 2, false)]
    public void ProcessFilter_ShouldOnlyMatchThisClient(string path, int session, bool expected) =>
        Assert.Equal(expected, SteamPlatform.IsClientProcess(new SteamInstallation(@"C:\Steam", @"C:\Steam\steam.exe"), path, session, 1));

    /// <summary>
    /// 验证删除当前或普通账号后保持关闭，且只清除指向目标的自动登录选择。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Delete_ShouldPreserveOtherAccountsAndKeepSteamClosed(bool current)
    {
        var before = File.ReadAllBytes(LoginPath);
        var config = File.ReadAllBytes(ConfigPath);
        var registry = platform.Registry;
        var id = current ? FirstId : SecondId;
        var result = await new SteamAccountService(platform).DeleteAccountAsync(id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccessful, result.Message);
        Assert.False(platform.Running);
        Assert.Equal(1, platform.ShutdownCount);
        Assert.Equal(0, platform.StartCount);
        Assert.Equal(current ? SecondId : FirstId, Assert.Single(SteamLoginFile.ReadAccounts(File.ReadAllText(LoginPath))).SteamId64);
        Assert.Equal(before, File.ReadAllBytes(LoginPath + ".kyvoq.bak"));
        Assert.Equal(before[..3], File.ReadAllBytes(LoginPath)[..3]);
        Assert.Equal(config, File.ReadAllBytes(ConfigPath));
        Assert.Equal(current ? string.Empty : registry.AutoLoginUser.Value, platform.Registry.AutoLoginUser.Value);
        Assert.Equal(registry.RememberPassword, platform.Registry.RememberPassword);
    }

    /// <summary>验证退出后的最新配置会被保留，最后一个账号也能删除。</summary>
    [Fact]
    public async Task Delete_ShouldRereadAfterShutdownAndAllowEmptyAccounts()
    {
        platform.OnWait = () =>
        {
            File.WriteAllText(LoginPath, File.ReadAllText(LoginPath).Replace("\"second\"", "\"second-updated\""));
            return Task.CompletedTask;
        };
        var service = new SteamAccountService(platform);
        Assert.True((await service.DeleteAccountAsync(FirstId, cancellationToken: TestContext.Current.CancellationToken)).IsSuccessful);
        Assert.Equal("second-updated", Assert.Single(SteamLoginFile.ReadAccounts(File.ReadAllText(LoginPath))).AccountName);
        Assert.True((await service.DeleteAccountAsync(SecondId, cancellationToken: TestContext.Current.CancellationToken)).IsSuccessful);
        Assert.Empty(SteamLoginFile.ReadAccounts(File.ReadAllText(LoginPath)));
        Assert.Equal(1, platform.ShutdownCount);
        Assert.Equal(0, platform.StartCount);
    }

    /// <summary>验证配置损坏、目标不存在或客户端无法退出时不会删除数据。</summary>
    [Theory]
    [InlineData("corrupt")]
    [InlineData("missing")]
    [InlineData("shutdown")]
    public async Task DeleteFailure_ShouldLeaveFilesAndRegistryUntouched(string failure)
    {
        if (failure == "corrupt")
        {
            File.WriteAllText(LoginPath, "users {");
        }

        platform.GracefulExit = failure != "shutdown";
        platform.ForcedExit = failure != "shutdown";
        var original = File.ReadAllBytes(LoginPath);
        var registry = platform.Registry;
        var result = await new SteamAccountService(platform).DeleteAccountAsync(failure == "missing" ? FirstId + 100 : FirstId,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.IsSuccessful);
        Assert.Equal(original, File.ReadAllBytes(LoginPath));
        Assert.Equal(registry, platform.Registry);
        Assert.Equal(failure == "shutdown" ? 1 : 0, platform.ShutdownCount);
        Assert.Equal(0, platform.StartCount);
    }

    /// <summary>验证注册表或文件提交失败会恢复被删除的账号，备份保持可用。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeleteCommitFailure_ShouldRestoreAccount(bool failRegistry)
    {
        var original = File.ReadAllBytes(LoginPath);
        var registry = platform.Registry;
        platform.FailRegistryWrite = failRegistry;
        var service = new SteamAccountService(platform, (path, bytes, backup) =>
        {
            SteamAccountService.ReplaceFile(path, bytes, backup);
            if (!failRegistry && backup)
            {
                throw new IOException("模拟提交后失败");
            }
        });
        var result = await service.DeleteAccountAsync(FirstId, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.IsSuccessful);
        Assert.Equal(original, File.ReadAllBytes(LoginPath));
        Assert.Equal(original, File.ReadAllBytes(LoginPath + ".kyvoq.bak"));
        Assert.Equal(registry, platform.Registry);
        Assert.Equal(0, platform.StartCount);
    }

    /// <summary>验证读取准备结果后文件被外部修改时拒绝覆盖。</summary>
    [Fact]
    public async Task DeleteConcurrentEdit_ShouldNotOverwriteExternalChanges()
    {
        var external = File.ReadAllText(LoginPath) + "\n// external update";
        platform.OnReadRegistry = () => File.WriteAllText(LoginPath, external);
        var result = await new SteamAccountService(platform).DeleteAccountAsync(FirstId, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.IsSuccessful);
        Assert.Equal(external, File.ReadAllText(LoginPath));
        Assert.Equal(0, platform.StartCount);
    }

    /// <summary>
    /// 记录进程动作和注册表变更，不访问系统 Steam。
    /// </summary>
    private sealed class FakePlatform(SteamInstallation installation) : ISteamPlatform
    {
        public bool Installed { get; set; } = true;
        public bool Running { get; set; } = true;
        public bool GracefulExit { get; set; } = true;
        public bool ForcedExit { get; set; } = true;
        public bool FailRegistryWrite { get; set; }
        public bool FailStart { get; set; }
        public bool RunningAfterFailedStart { get; set; }
        public int ShutdownCount { get; private set; }
        public int KillCount { get; private set; }
        public int StartCount { get; private set; }
        public Func<Task>? OnWait { get; set; }
        public Action? OnReadRegistry { get; set; }
        public List<TimeSpan> Timeouts { get; } = [];
        public SteamRegistrySnapshot Registry { get; private set; } = new(new("first", RegistryValueKind.String), new(null));

        /// <summary>返回测试安装信息。</summary>
        public SteamInstallation? FindInstallation() => Installed ? installation : null;
        /// <summary>返回模拟进程状态。</summary>
        public bool IsRunning(SteamInstallation value) => Running;
        /// <summary>保留原活动账号以验证选中标记不等于成功登录。</summary>
        public ulong? GetActiveAccount(SteamInstallation value) => FirstId;
        /// <summary>记录正常退出请求。</summary>
        public void RequestShutdown(SteamInstallation value) => ShutdownCount++;
        /// <summary>模拟等待及 Steam 退出时的配置更新。</summary>
        public async Task<bool> WaitForExitAsync(SteamInstallation value, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Timeouts.Add(timeout);
            if (OnWait is not null)
            {
                await OnWait();
            }

            Running = !(KillCount > 0 ? ForcedExit : GracefulExit);
            return !Running;
        }

        /// <summary>记录强制退出请求。</summary>
        public void KillClient(SteamInstallation value) => KillCount++;
        /// <summary>模拟启动成功或失败。</summary>
        public void Start(SteamInstallation value)
        {
            StartCount++;
            Running = !FailStart || RunningAfterFailedStart;
            if (FailStart)
            {
                throw new IOException("启动失败");
            }
        }

        /// <summary>返回原始注册表值。</summary>
        public SteamRegistrySnapshot ReadLoginRegistry()
        {
            OnReadRegistry?.Invoke();
            return Registry;
        }
        /// <summary>模拟包含部分失败的注册表写入。</summary>
        public void SetLoginRegistry(string accountName)
        {
            Registry = Registry with { AutoLoginUser = new(accountName, RegistryValueKind.String) };
            if (FailRegistryWrite)
            {
                throw new UnauthorizedAccessException();
            }
        }

        /// <summary>恢复原始注册表值。</summary>
        public void RestoreLoginRegistry(SteamRegistrySnapshot snapshot) => Registry = snapshot;
    }
}
