using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kyvoq.App.Services;
using Kyvoq.App.ViewModels;

namespace Kyvoq.App.Views;

/// <summary>
/// 展示紧凑账号卡片，直接切换已有账号，确认后新增登录或删除记录。
/// </summary>
public partial class SteamAccountsView : UserControl
{
    private bool dialogOpen;
    public static readonly DependencyProperty CardWidthProperty = DependencyProperty.Register(
        nameof(CardWidth), typeof(double), typeof(SteamAccountsView), new PropertyMetadata(160d));

    public double CardWidth
    {
        get => (double)GetValue(CardWidthProperty);
        private set => SetValue(CardWidthProperty, value);
    }

    internal Func<string, string, MessageDialogSeverity, string, bool>? ConfirmAction { get; set; }

    /// <summary>创建账号面板，由主窗口提供使用共享消息服务的确认入口。</summary>
    public SteamAccountsView()
    {
        InitializeComponent();
    }

    /// <summary>按可用宽度收缩卡片，并为垂直滚动条及卡片间距留出空间。</summary>
    private void View_SizeChanged(object sender, SizeChangedEventArgs eventArgs) =>
        CardWidth = Math.Max(1, Math.Min(160, ActualWidth - 14 - SystemParameters.VerticalScrollBarWidth));

    /// <summary>共用鼠标和键盘入口，已有账号直接切换，加号入口先确认。</summary>
    internal Task ActivateAsync(object? item)
    {
        if (dialogOpen || DataContext is not SteamAccountsViewModel { CanSwitch: true } model)
        {
            return Task.CompletedTask;
        }

        return item switch
        {
            SteamAccountViewModel account when model.Accounts.Contains(account) => model.SwitchAsync(account),
            SteamAddAccountViewModel => ConfirmAndRunAsync("登录新账号",
                "确定登录新账号？\nSteam 将退出并重新启动，请在 Steam 客户端完成登录。",
                MessageDialogSeverity.Question, "登录新账号", model.LoginAnotherAsync),
            _ => Task.CompletedTask
        };
    }

    /// <summary>避免模态窗口重入，并在确认后交由视图模型再次检查可用状态。</summary>
    private async Task ConfirmAndRunAsync(string title, string message, MessageDialogSeverity severity,
        string confirmText, Func<Task> operation)
    {
        if (dialogOpen || DataContext is not SteamAccountsViewModel { CanSwitch: true })
        {
            return;
        }

        dialogOpen = true;
        try
        {
            if (ConfirmAction?.Invoke(title, message, severity, confirmText) == true)
            {
                await operation();
            }
        }
        finally
        {
            dialogOpen = false;
        }
    }

    /// <summary>账号容器上的左键双击触发切换或新增登录。</summary>
    private async void Account_DoubleClick(object sender, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ChangedButton == MouseButton.Left && sender is ListBoxItem item)
        {
            eventArgs.Handled = true;
            await ActivateAsync(item.DataContext);
        }
    }

    /// <summary>Enter 与双击共用激活流程，阻止普通启动项目的键盘处理。</summary>
    private async void Accounts_KeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            eventArgs.Handled = true;
            if (!eventArgs.IsRepeat)
            {
                await ActivateAsync(AccountsListBox.SelectedItem);
            }
        }
    }

    /// <summary>右键先选中实际点击的卡片，菜单参数始终来自该卡片。</summary>
    private void Account_RightClick(object sender, MouseButtonEventArgs eventArgs)
    {
        if (sender is ListBoxItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    /// <summary>加号入口及操作期间不显示账号管理菜单。</summary>
    private void Account_ContextMenuOpening(object sender, ContextMenuEventArgs eventArgs)
    {
        if (sender is not ListBoxItem { DataContext: SteamAccountViewModel }
            || dialogOpen || DataContext is not SteamAccountsViewModel { CanSwitch: true })
        {
            eventArgs.Handled = true;
        }
    }

    /// <summary>编辑备注，允许用户通过空内容清除原备注。</summary>
    private async void Note_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (dialogOpen || sender is not MenuItem { CommandParameter: SteamAccountViewModel account }
            || DataContext is not SteamAccountsViewModel { CanSwitch: true } model
            || Window.GetWindow(this) is not MainWindow owner)
        {
            return;
        }

        dialogOpen = true;
        try
        {
            if (owner.EditSteamAccountNote(account.Note) is { } note)
            {
                await model.SaveNoteAsync(account, note);
            }
        }
        finally
        {
            dialogOpen = false;
        }
    }

    /// <summary>说明删除及退出行为，确认后才删除本机登录记录。</summary>
    private async void Delete_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is MenuItem { CommandParameter: SteamAccountViewModel account }
            && DataContext is SteamAccountsViewModel model)
        {
            await ConfirmAndRunAsync("删除 Steam 账号",
                $"确定删除“{account.DisplayName}”（{account.AccountName}）的本机登录记录？\n"
                + "如果 Steam 正在运行，会先退出；删除完成后保持关闭。\n游戏文件和存档会保留。",
                MessageDialogSeverity.Warning, "删除",
                () => model.DeleteAsync(account));
        }
    }
}
