using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kyvoq.App.ViewModels;

namespace Kyvoq.App.Views;

/// <summary>
/// 展示紧凑账号卡片，在明确确认后执行登录及删除操作。
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

    internal Func<string, string, bool> ConfirmAction { get; set; }

    /// <summary>创建账号面板及带所有者的确认入口。</summary>
    public SteamAccountsView()
    {
        InitializeComponent();
        ConfirmAction = Confirm;
    }

    /// <summary>按可用宽度收缩卡片，并为垂直滚动条及卡片间距留出空间。</summary>
    private void View_SizeChanged(object sender, SizeChangedEventArgs eventArgs) =>
        CardWidth = Math.Max(1, Math.Min(160, ActualWidth - 14 - SystemParameters.VerticalScrollBarWidth));

    /// <summary>显示以主窗口为所有者、默认取消的确认框。</summary>
    private bool Confirm(string title, string message) =>
        MessageBox.Show(Window.GetWindow(this), message, title, MessageBoxButton.YesNo,
            MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    /// <summary>共用鼠标和键盘入口，确认前不调用任何 Steam 操作。</summary>
    internal Task ActivateAsync(object? item)
    {
        if (DataContext is not SteamAccountsViewModel model)
        {
            return Task.CompletedTask;
        }

        return item switch
        {
            SteamAccountViewModel account when model.Accounts.Contains(account) => ConfirmAndRunAsync(
                "登录 Steam 账号", $"确定登录“{account.DisplayName}”（{account.AccountName}）？\nSteam 将退出并重新启动。",
                () => model.SwitchAsync(account)),
            SteamAddAccountViewModel => ConfirmAndRunAsync("登录新账号",
                "确定登录新账号？\nSteam 将退出并重新启动，请在 Steam 原生界面完成登录。", model.LoginAnotherAsync),
            _ => Task.CompletedTask
        };
    }

    /// <summary>避免模态窗口重入，并在确认后交由视图模型再次检查可用状态。</summary>
    private async Task ConfirmAndRunAsync(string title, string message, Func<Task> operation)
    {
        if (dialogOpen || DataContext is not SteamAccountsViewModel { CanSwitch: true })
        {
            return;
        }

        dialogOpen = true;
        try
        {
            if (ConfirmAction(title, message))
            {
                await operation();
            }
        }
        finally
        {
            dialogOpen = false;
        }
    }

    /// <summary>只有账号容器上的左键双击才打开登录确认。</summary>
    private async void Account_DoubleClick(object sender, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ChangedButton == MouseButton.Left && sender is ListBoxItem item)
        {
            eventArgs.Handled = true;
            await ActivateAsync(item.DataContext);
        }
    }

    /// <summary>Enter 与双击共用确认流程，阻止普通启动项目的键盘处理。</summary>
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
                () => model.DeleteAsync(account));
        }
    }
}
