using Kyvoq.App.Services;
using Kyvoq.App.ViewModels;
using Kyvoq.Core.Models;
using Kyvoq.Core.Services;

namespace Kyvoq.App.Tests.ViewModels;

/// <summary>
/// 验证账号面板不会在隐藏时读取或显示过期结果，并串行处理切换操作。
/// </summary>
public sealed class SteamAccountsViewModelTests
{
    /// <summary>
    /// 验证未启用时不读取，离开分组后晚到的读取结果不会重新显示。
    /// </summary>
    [Fact]
    public async Task InactivePanel_ShouldNotReadOrAcceptStaleResults()
    {
        var read = new TaskCompletionSource<SteamAccountSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeService { Read = () => read.Task };
        using var model = new SteamAccountsViewModel(service, new FakeSteamAccountNoteStore());
        await model.RefreshAsync();
        Assert.Equal(0, service.ReadCount);
        model.SetActive(true);
        var pending = model.PendingRefresh;
        Assert.Equal(1, service.ReadCount);
        model.SetActive(false);
        read.SetResult(FakeService.Snapshot);
        await pending;
        Assert.Empty(model.Accounts);
        Assert.False(model.CanSwitch);
        Assert.False(model.IsRefreshing);

        service.Read = () => Task.FromResult(FakeService.Snapshot);
        model.SetActive(true);
        await model.PendingRefresh;
        Assert.Single(model.Accounts);
        Assert.True(model.CanSwitch);
    }

    /// <summary>
    /// 验证一次切换尚未结束时重复双击及新账号操作均不会重复提交。
    /// </summary>
    [Fact]
    public async Task BusyPanel_ShouldRejectDuplicateOperationsAndRefreshAfterCompletion()
    {
        var completion = new TaskCompletionSource<SteamOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeService { Operation = () => completion.Task };
        using var model = new SteamAccountsViewModel(service, new FakeSteamAccountNoteStore());
        model.SetActive(true);
        await model.PendingRefresh;
        var account = Assert.Single(model.Accounts);
        var operation = model.SwitchAsync(account);
        Assert.True(model.IsBusy);
        Assert.False(model.CanRefresh);
        await model.SwitchAsync(account);
        await model.LoginAnotherAsync();
        Assert.Equal(1, service.SwitchCount);
        Assert.Equal(0, service.LoginCount);
        completion.SetResult(new SteamOperationResult(true, "已启动 Steam"));
        await operation;
        Assert.False(model.IsBusy);
        Assert.True(model.CanSwitch);
        Assert.Equal("已启动 Steam", model.StatusText);
        Assert.Equal(2, service.ReadCount);
    }

    /// <summary>
    /// 验证未安装时允许刷新，读取异常后仍可通过刷新恢复。
    /// </summary>
    [Fact]
    public async Task MissingOrUnreadableSteam_ShouldRemainRefreshable()
    {
        var service = new FakeService { Read = () => Task.FromResult(new SteamAccountSnapshot(false, [])) };
        using var model = new SteamAccountsViewModel(service, new FakeSteamAccountNoteStore());
        model.SetActive(true);
        await model.PendingRefresh;
        Assert.False(model.CanSwitch);
        Assert.True(model.CanRefresh);
        Assert.Contains("未找到", model.StatusText);
        service.Read = () => throw new UnauthorizedAccessException();
        await model.RefreshAsync();
        Assert.True(model.IsError);
        Assert.True(model.CanRefresh);
        service.Read = () => Task.FromResult(FakeService.Snapshot);
        await model.RefreshAsync();
        Assert.False(model.IsError);
        Assert.Single(model.Accounts);
    }

    /// <summary>
    /// 验证切换时关闭面板不会被操作结果重新打开，原操作仍完成。
    /// </summary>
    [Fact]
    public async Task DisableDuringSwitch_ShouldFinishWithoutRestoringPanelData()
    {
        var completion = new TaskCompletionSource<SteamOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeService { Operation = () => completion.Task };
        using var model = new SteamAccountsViewModel(service, new FakeSteamAccountNoteStore());
        model.SetActive(true);
        await model.PendingRefresh;
        var operation = model.LoginAnotherAsync();
        model.SetActive(false);
        completion.SetResult(new SteamOperationResult(true, "已启动 Steam"));
        await operation;
        Assert.Equal(1, service.LoginCount);
        Assert.Equal(1, service.ReadCount);
        Assert.Empty(model.Accounts);
        Assert.Empty(model.StatusText);
    }

    /// <summary>
    /// 验证没有账号时仍提供独立加号入口，正常读取不显示数量和操作提示。
    /// </summary>
    [Fact]
    public async Task EmptyInstalledPanel_ShouldShowOnlyAddEntry()
    {
        var service = new FakeService { Read = () => Task.FromResult(new SteamAccountSnapshot(true, [])) };
        using var model = new SteamAccountsViewModel(service, new FakeSteamAccountNoteStore());
        model.SetActive(true);
        await model.PendingRefresh;
        Assert.Empty(model.Accounts);
        Assert.IsType<SteamAddAccountViewModel>(Assert.Single(model.Items));
        Assert.Empty(model.StatusText);
        Assert.True(model.CanSwitch);
    }

    /// <summary>验证备注优先展示、清空回退昵称，并在刷新后保留。</summary>
    [Fact]
    public async Task Note_ShouldOverrideTitleAndClearWithoutChangingIdentity()
    {
        var notes = new FakeSteamAccountNoteStore();
        using var model = new SteamAccountsViewModel(new FakeService(), notes);
        model.SetActive(true);
        await model.PendingRefresh;
        var account = Assert.Single(model.Accounts);
        await model.SaveNoteAsync(account, "  常用账号  ");
        Assert.Equal("常用账号", account.DisplayName);
        Assert.Equal("test", account.AccountName);
        Assert.Contains("测试", account.ToolTip);
        await model.RefreshAsync();
        account = Assert.Single(model.Accounts);
        Assert.Equal("常用账号", account.DisplayName);
        await model.SaveNoteAsync(account, "  ");
        Assert.Equal("测试", account.DisplayName);
        Assert.Empty(notes.Notes);
    }

    /// <summary>验证备注读写失败不丢失已显示内容，也不阻止账号登录。</summary>
    [Fact]
    public async Task NoteFailure_ShouldPreserveExistingNoteAndKeepAccountsUsable()
    {
        var notes = new FakeSteamAccountNoteStore();
        notes.Notes[FakeService.Snapshot.Accounts[0].SteamId64] = "原备注";
        using var model = new SteamAccountsViewModel(new FakeService(), notes);
        model.SetActive(true);
        await model.PendingRefresh;
        notes.FailWrite = true;
        await model.SaveNoteAsync(Assert.Single(model.Accounts), "新备注");
        Assert.Equal("原备注", Assert.Single(model.Accounts).DisplayName);
        Assert.True(model.IsError);
        notes.FailRead = true;
        await model.RefreshAsync();
        Assert.Equal("原备注", Assert.Single(model.Accounts).DisplayName);
        Assert.True(model.CanSwitch);
        Assert.True(model.IsError);
    }

    /// <summary>验证备注保存参与忙碌及退出等待，隐藏后仍完成持久化。</summary>
    [Fact]
    public async Task PendingNote_ShouldBlockOperationsAndFinishAfterHiding()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notes = new FakeSteamAccountNoteStore { BeforeSave = () => completion.Task };
        var service = new FakeService();
        using var model = new SteamAccountsViewModel(service, notes);
        model.SetActive(true);
        await model.PendingRefresh;
        var operation = model.SaveNoteAsync(Assert.Single(model.Accounts), "备注");
        Assert.Same(operation, model.PendingOperation);
        Assert.True(model.IsBusy);
        await model.LoginAnotherAsync();
        Assert.Equal(0, service.LoginCount);
        model.SetActive(false);
        completion.SetResult();
        await operation;
        Assert.Single(notes.Notes);
        Assert.Empty(model.Items);
        Assert.Empty(model.StatusText);
    }

    /// <summary>验证保存备注时离开再进入分组，保存结束后仍会恢复账号卡片。</summary>
    [Fact]
    public async Task ReactivateDuringNoteSave_ShouldRestoreCardsAfterCompletion()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notes = new FakeSteamAccountNoteStore { BeforeSave = () => completion.Task };
        using var model = new SteamAccountsViewModel(new FakeService(), notes);
        model.SetActive(true);
        await model.PendingRefresh;
        var operation = model.SaveNoteAsync(Assert.Single(model.Accounts), "更新备注");
        model.SetActive(false);
        model.SetActive(true);
        completion.SetResult();
        await operation;
        Assert.Equal("更新备注", Assert.Single(model.Accounts).DisplayName);
        Assert.Equal(2, model.Items.Count);
    }

    /// <summary>验证删除成功后刷新列表，备注清理失败不会恢复已删除账号。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_ShouldRefreshEvenWhenNoteCleanupFails(bool failNoteCleanup)
    {
        var service = new FakeService();
        var notes = new FakeSteamAccountNoteStore { FailWrite = failNoteCleanup };
        notes.Notes[FakeService.Snapshot.Accounts[0].SteamId64] = "备注";
        using var model = new SteamAccountsViewModel(service, notes);
        model.SetActive(true);
        await model.PendingRefresh;
        await model.DeleteAsync(Assert.Single(model.Accounts));
        Assert.Equal(1, service.DeleteCount);
        Assert.Empty(model.Accounts);
        Assert.IsType<SteamAddAccountViewModel>(Assert.Single(model.Items));
        Assert.Equal(failNoteCleanup, model.IsError);
        Assert.Equal(failNoteCleanup ? 1 : 0, notes.Notes.Count);
    }

    /// <summary>
    /// 提供可控的异步读取和切换，不接触 Steam。
    /// </summary>
    private sealed class FakeService : ISteamAccountService
    {
        public static SteamAccountSnapshot Snapshot { get; } = new(true,
            [new SteamAccount(SteamLoginFile.IndividualAccountBase + 1, "test", "测试", 1, true)]);
        public Func<Task<SteamAccountSnapshot>> Read { get; set; } = () => Task.FromResult(Snapshot);
        public Func<Task<SteamOperationResult>> Operation { get; set; } = () => Task.FromResult(new SteamOperationResult(true, "已启动 Steam"));
        public int ReadCount { get; private set; }
        public int SwitchCount { get; private set; }
        public int LoginCount { get; private set; }
        public int DeleteCount { get; private set; }

        /// <summary>模拟删除成功后本机快照不再包含该账号。</summary>
        public async Task<SteamOperationResult> DeleteAccountAsync(ulong id, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            DeleteCount++;
            var result = await Operation();
            if (result.IsSuccessful)
            {
                Read = () => Task.FromResult(new SteamAccountSnapshot(true, []));
            }

            return result;
        }

        /// <summary>记录读取次数并返回可控结果。</summary>
        public Task<SteamAccountSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return Read();
        }

        /// <summary>记录账号切换。</summary>
        public Task<SteamOperationResult> SwitchAccountAsync(ulong id, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            SwitchCount++;
            return Operation();
        }

        /// <summary>记录原生登录入口操作。</summary>
        public Task<SteamOperationResult> LoginAnotherAccountAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            LoginCount++;
            return Operation();
        }
    }
}
