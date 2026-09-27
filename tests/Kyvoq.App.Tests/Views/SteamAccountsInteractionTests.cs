using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Kyvoq.App.Services;
using Kyvoq.App.ViewModels;
using Kyvoq.App.Views;
using Kyvoq.Core.Models;
using Kyvoq.Core.Services;

namespace Kyvoq.App.Tests.Views;

/// <summary>在真实 WPF 控件中验证登录确认及右键菜单对象，不操作真实 Steam。</summary>
public sealed class SteamAccountsInteractionTests
{
    /// <summary>验证鼠标、键盘取消不启动 Steam，确认才执行，右键菜单跟随点击对象。</summary>
    [Fact]
    public async Task AccountCards_ShouldConfirmActivationAndTargetTheirOwnContextMenu()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                Window? window = null;
                try
                {
                    var service = new RecordingSteamService();
                    using var model = new SteamAccountsViewModel(service, new FakeSteamAccountNoteStore());
                    model.SetActive(true);
                    await model.PendingRefresh;
                    var confirmations = 0;
                    var view = new SteamAccountsView
                    {
                        DataContext = model,
                        ConfirmAction = (_, _) => { confirmations++; return false; }
                    };
                    window = new Window
                    {
                        Width = 500, Height = 260, Left = -10000, Top = -10000,
                        ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
                        Content = view
                    };
                    window.Show();
                    window.UpdateLayout();
                    var list = Assert.IsType<ListBox>(view.FindName("AccountsListBox"));
                    var first = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0));
                    var second = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(1));
                    var add = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(2));
                    var source = PresentationSource.FromVisual(window)!;

                    first.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    {
                        RoutedEvent = Control.MouseDoubleClickEvent
                    });
                    list.SelectedIndex = 2;
                    list.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter)
                    {
                        RoutedEvent = Keyboard.PreviewKeyDownEvent
                    });
                    Assert.Equal(2, confirmations);
                    Assert.Equal(0, service.SwitchCount);
                    Assert.Equal(0, service.LoginCount);

                    first.IsSelected = true;
                    second.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
                    {
                        RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent
                    });
                    Assert.Same(second.DataContext, list.SelectedItem);
                    var menu = second.ContextMenu;
                    menu.PlacementTarget = second;
                    menu.IsOpen = true;
                    menu.UpdateLayout();
                    await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Assert.All(menu.Items.Cast<MenuItem>(), item => Assert.Same(second.DataContext, item.CommandParameter));
                    Assert.Equal(new[] { "备注", "删除" }, menu.Items.Cast<MenuItem>().Select(item => item.Header));
                    menu.Items.Cast<MenuItem>().Single(item => Equals(item.Header, "删除")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Assert.Equal(3, confirmations);
                    Assert.Equal(0, service.DeleteCount);
                    menu.IsOpen = false;

                    Assert.Equal(160, first.ActualWidth);
                    Assert.Equal(56, first.ActualHeight);
                    Assert.Equal(Visibility.Collapsed, Assert.IsType<TextBlock>(view.FindName("StatusTextBlock")).Visibility);
                    Assert.IsType<SteamAddAccountViewModel>(add.DataContext);

                    var operation = new TaskCompletionSource<SteamOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    service.Operation = () => operation.Task;
                    view.ConfirmAction = (_, _) => { confirmations++; return true; };
                    var pending = view.ActivateAsync(model.Accounts[0]);
                    await view.ActivateAsync(model.Accounts[1]);
                    Assert.Equal(4, confirmations);
                    Assert.Equal(1, service.SwitchCount);
                    operation.SetResult(new SteamOperationResult(true, string.Empty));
                    await pending;
                    service.Operation = () => Task.FromResult(new SteamOperationResult(true, string.Empty));
                    await view.ActivateAsync(model.Items.Last());
                    Assert.Equal(1, service.LoginCount);
                    Assert.Equal(5, confirmations);

                    window.Width = 140;
                    window.UpdateLayout();
                    Assert.InRange(view.CardWidth, 1, 140);
                    completion.SetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
                finally
                {
                    window?.Close();
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
    }

    /// <summary>返回固定的两账号快照并记录实际发出的账号操作。</summary>
    private sealed class RecordingSteamService : ISteamAccountService
    {
        public int SwitchCount { get; private set; }
        public int LoginCount { get; private set; }
        public int DeleteCount { get; private set; }
        public Func<Task<SteamOperationResult>> Operation { get; set; } = () => Task.FromResult(new SteamOperationResult(true, string.Empty));

        /// <summary>提供测试账号，避免扫描本机客户端。</summary>
        public Task<SteamAccountSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SteamAccountSnapshot(true,
            [
                new SteamAccount(SteamLoginFile.IndividualAccountBase + 1, "first", "第一账号", 2, true),
                new SteamAccount(SteamLoginFile.IndividualAccountBase + 2, "second", "第二账号", 1, false)
            ]));

        /// <summary>记录确认后的切换请求。</summary>
        public Task<SteamOperationResult> SwitchAccountAsync(ulong steamId64, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            SwitchCount++;
            return Operation();
        }

        /// <summary>记录确认后的新账号请求。</summary>
        public Task<SteamOperationResult> LoginAnotherAccountAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            LoginCount++;
            return Operation();
        }

        /// <summary>记录确认后的删除请求。</summary>
        public Task<SteamOperationResult> DeleteAccountAsync(ulong steamId64, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            DeleteCount++;
            return Operation();
        }
    }
}
