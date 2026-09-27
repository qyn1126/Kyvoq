using Kyvoq.App.Services;
using Kyvoq.Core.Services;

namespace Kyvoq.App.ViewModels;

/// <summary>
/// 管理 Steam 账号面板的异步读取、切换进度和过期结果抑制。
/// </summary>
public sealed class SteamAccountsViewModel : ObservableObject, IDisposable
{
    private readonly ISteamAccountService service;
    private readonly ISteamAccountNoteStore noteStore;
    private IReadOnlyDictionary<ulong, string> notes = new Dictionary<ulong, string>();
    private readonly SteamAddAccountViewModel addAccount = new();
    private CancellationTokenSource? refreshCancellation;
    private bool active;
    private bool disposed;
    private bool isBusy;
    private bool isRefreshing;
    private bool isInstalled;
    private bool isError;
    private string statusText = string.Empty;
    private int generation;

    public RangeObservableCollection<SteamAccountViewModel> Accounts { get; } = [];
    public RangeObservableCollection<object> Items { get; } = [];
    public Task PendingOperation { get; private set; } = Task.CompletedTask;
    public Task PendingRefresh { get; private set; } = Task.CompletedTask;
    public bool IsBusy => isBusy;
    public bool IsRefreshing => isRefreshing;
    public bool CanRefresh => active && !isBusy && !isRefreshing;
    public bool CanSwitch => CanRefresh && isInstalled;
    public bool IsError => isError;
    public string StatusText => statusText;

    /// <summary>
    /// 保存账号服务，构造时不扫描 Steam。
    /// </summary>
    public SteamAccountsViewModel(ISteamAccountService service, ISteamAccountNoteStore noteStore)
    {
        this.service = service;
        this.noteStore = noteStore;
    }

    /// <summary>
    /// 按分组可见状态启动读取，离开时取消读取并丢弃晚到的结果。
    /// </summary>
    public void SetActive(bool value)
    {
        if (disposed || active == value)
        {
            return;
        }

        active = value;
        generation++;
        refreshCancellation?.Cancel();
        SetProperty(ref isRefreshing, false, nameof(IsRefreshing));
        NotifyAvailability();
        if (active)
        {
            _ = RefreshAsync();
        }
        else
        {
            Accounts.ReplaceAll([]);
            Items.ReplaceAll([]);
            SetStatus(string.Empty, false);
        }
    }

    /// <summary>
    /// 读取本机账号和头像，保持界面可响应并忽略失效请求。
    /// </summary>
    public Task RefreshAsync(bool preserveStatus = false)
    {
        if (!CanRefresh || disposed)
        {
            return isRefreshing ? PendingRefresh : Task.CompletedTask;
        }

        PendingRefresh = RefreshCoreAsync(preserveStatus);
        return PendingRefresh;
    }

    /// <summary>
    /// 执行一次读取，并在完成时核对当前分组与请求代次。
    /// </summary>
    private async Task RefreshCoreAsync(bool preserveStatus)
    {
        var currentGeneration = ++generation;
        using var cancellation = new CancellationTokenSource();
        refreshCancellation = cancellation;
        SetProperty(ref isRefreshing, true, nameof(IsRefreshing));
        NotifyAvailability();
        if (!preserveStatus)
        {
            SetStatus("正在读取 Steam 账号…", false);
        }

        try
        {
            var snapshot = await service.GetSnapshotAsync(cancellation.Token);
            var accounts = await Task.WhenAll(snapshot.Accounts.Select(account => SteamAccountViewModel.CreateAsync(account, cancellation.Token)));
            var noteError = string.Empty;
            try
            {
                var loadedNotes = await noteStore.LoadAsync(cancellation.Token);
                if (currentGeneration == generation)
                {
                    notes = loadedNotes;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException && SteamAccountService.IsExpectedException(exception))
            {
                noteError = "读取 Steam 备注失败，保留已加载的备注。请检查备注文件及访问权限。";
            }

            if (!active || disposed || currentGeneration != generation)
            {
                return;
            }

            isInstalled = snapshot.IsInstalled;
            foreach (var account in accounts)
            {
                account.SetNote(notes.GetValueOrDefault(account.Account.SteamId64, string.Empty));
            }

            Accounts.ReplaceAll(accounts);
            Items.ReplaceAll(snapshot.IsInstalled ? accounts.Cast<object>().Append(addAccount) : []);
            if (noteError.Length > 0)
            {
                SetStatus(preserveStatus && statusText.Length > 0 ? $"{statusText}\n{noteError}" : noteError, true);
            }
            else if (!preserveStatus)
            {
                SetStatus(!snapshot.IsInstalled ? "未找到 Steam，请安装并启动一次 Steam 后刷新。" : string.Empty, false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (SteamAccountService.IsExpectedException(exception))
        {
            if (active && !disposed && currentGeneration == generation)
            {
                Accounts.ReplaceAll([]);
                Items.ReplaceAll([]);
                isInstalled = false;
                SetStatus("读取 Steam 账号失败，请检查配置文件及访问权限后刷新。", true);
                DiagnosticLog.Current.Write("Steam.ReadFailed", $"ErrorType={exception.GetType().Name}; HResult={exception.HResult}");
            }
        }
        finally
        {
            if (ReferenceEquals(refreshCancellation, cancellation))
            {
                refreshCancellation = null;
            }

            if (currentGeneration == generation)
            {
                SetProperty(ref isRefreshing, false, nameof(IsRefreshing));
                NotifyAvailability();
            }
        }
    }

    /// <summary>
    /// 发起一次账号切换，并拒绝重复点击。
    /// </summary>
    public Task SwitchAsync(SteamAccountViewModel account) => Accounts.Contains(account)
        ? BeginOperation(progress => service.SwitchAccountAsync(account.Account.SteamId64, progress))
        : Task.CompletedTask;

    /// <summary>
    /// 打开 Steam 的原生账号选择和登录界面。
    /// </summary>
    public Task LoginAnotherAsync() => BeginOperation(progress => service.LoginAnotherAccountAsync(progress));

    /// <summary>删除账号后清理备注；备注清理失败不回滚已经完成的删除。</summary>
    public Task DeleteAsync(SteamAccountViewModel account) => Accounts.Contains(account)
        ? BeginOperation(async progress =>
        {
            var result = await service.DeleteAccountAsync(account.Account.SteamId64, progress);
            if (result.IsSuccessful)
            {
                try
                {
                    await noteStore.SetAsync(account.Account.SteamId64, string.Empty);
                    UpdateCachedNote(account.Account.SteamId64, string.Empty);
                }
                catch (Exception exception) when (SteamAccountService.IsExpectedException(exception))
                {
                    return new SteamOperationResult(false, "本机登录记录已删除，Steam 保持关闭；对应备注清理失败。请检查备注文件及访问权限。");
                }
            }

            return result;
        }) : Task.CompletedTask;

    /// <summary>等待备注持久化成功后更新显示，并将保存纳入退出等待。</summary>
    public Task SaveNoteAsync(SteamAccountViewModel account, string note) => Accounts.Contains(account)
        ? BeginOperation(async _ =>
        {
            try
            {
                await noteStore.SetAsync(account.Account.SteamId64, note);
                UpdateCachedNote(account.Account.SteamId64, note);
                account.SetNote(note);
                return new SteamOperationResult(true, string.Empty);
            }
            catch (Exception exception) when (SteamAccountService.IsExpectedException(exception))
            {
                return new SteamOperationResult(false, "保存 Steam 备注失败，原备注未更改。请检查备注文件及访问权限。");
            }
        }, refreshAfter: false) : Task.CompletedTask;

    /// <summary>更新最近一次有效备注快照，以便读取失败时继续显示已有备注。</summary>
    private void UpdateCachedNote(ulong steamId64, string note)
    {
        var updated = notes.ToDictionary();
        if (string.IsNullOrWhiteSpace(note))
        {
            updated.Remove(steamId64);
        }
        else
        {
            updated[steamId64] = note.Trim();
        }

        notes = updated;
    }

    /// <summary>
    /// 保存正在进行的操作任务，以便应用正常退出前等待提交结束。
    /// </summary>
    private Task BeginOperation(Func<IProgress<string>, Task<SteamOperationResult>> operation, bool refreshAfter = true)
    {
        if (!CanSwitch || disposed)
        {
            return Task.CompletedTask;
        }

        PendingOperation = RunOperationAsync(operation, refreshAfter);
        return PendingOperation;
    }

    /// <summary>
    /// 将服务进度显示在面板内，完成后刷新真实账号状态。
    /// </summary>
    private async Task RunOperationAsync(Func<IProgress<string>, Task<SteamOperationResult>> operation, bool refreshAfter)
    {
        SetProperty(ref isBusy, true, nameof(IsBusy));
        NotifyAvailability();
        SetStatus("正在准备 Steam 操作…", false);
        var progress = new Progress<string>(message =>
        {
            if (active && !disposed && isBusy)
            {
                SetStatus(message, false);
            }
        });
        try
        {
            var result = await operation(progress);
            if (active && !disposed)
            {
                SetStatus(result.Message, !result.IsSuccessful);
            }
        }
        catch (Exception exception) when (SteamAccountService.IsExpectedException(exception))
        {
            if (active && !disposed)
            {
                SetStatus("Steam 操作失败，请刷新后重试。", true);
            }
        }
        finally
        {
            SetProperty(ref isBusy, false, nameof(IsBusy));
            NotifyAvailability();
        }

        if (refreshAfter || (active && Items.Count == 0))
        {
            await RefreshAsync(preserveStatus: true);
        }
    }

    /// <summary>
    /// 同步更新操作提示及错误样式。
    /// </summary>
    private void SetStatus(string message, bool error)
    {
        SetProperty(ref statusText, message, nameof(StatusText));
        SetProperty(ref isError, error, nameof(IsError));
    }

    /// <summary>
    /// 通知操作按钮的可用状态变化。
    /// </summary>
    private void NotifyAvailability()
    {
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(CanSwitch));
    }

    /// <summary>
    /// 取消后台读取；已经进入提交阶段的切换由应用退出流程等待完成。
    /// </summary>
    public void Dispose()
    {
        disposed = true;
        active = false;
        generation++;
        refreshCancellation?.Cancel();
    }
}
