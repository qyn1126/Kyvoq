using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Kyvoq.App.Services;
using Kyvoq.App.ViewModels;
using Kyvoq.Core.Models;
using Kyvoq.Core.Services;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;

namespace Kyvoq.App.Views;

/// <summary>
/// 提供从主界面打开的跨分组键盘搜索面板。
/// </summary>
public partial class SearchWindow : FluentWindow
{
    private static readonly TimeSpan SearchDebounceDelay = TimeSpan.FromMilliseconds(50);
    private readonly MainViewModel mainViewModel;
    private readonly ILaunchService launchService;
    private readonly ThemeService themeService;
    private readonly DispatcherTimer searchDebounceTimer;
    private bool allowClose;
    private bool autoHideCheckPending;

    public RangeObservableCollection<SearchEntryViewModel> Results { get; } = [];

    /// <summary>
    /// 创建快速搜索面板。
    /// </summary>
    /// <param name="mainViewModel">主窗口数据源。</param>
    /// <param name="launchService">启动服务。</param>
    /// <param name="themeService">主题服务。</param>
    public SearchWindow(
        MainViewModel mainViewModel,
        ILaunchService launchService,
        ThemeService themeService)
    {
        searchDebounceTimer = new DispatcherTimer
        {
            Interval = SearchDebounceDelay
        };
        searchDebounceTimer.Tick += HandleSearchDebounceElapsed;
        InitializeComponent();
        this.mainViewModel = mainViewModel;
        this.launchService = launchService;
        this.themeService = themeService;
        DataContext = this;
        SourceInitialized += HandleSourceInitialized;
        Deactivated += HandleDeactivated;
        Closing += HandleClosing;
    }

    /// <summary>
    /// 显示搜索面板、清空查询并聚焦输入框。
    /// </summary>
    public void ShowAndActivate()
    {
        QueryTextBox.Text = string.Empty;
        searchDebounceTimer.Stop();
        RefreshResults();
        if (!IsVisible)
        {
            Show();
        }

        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
        QueryTextBox.Focus();
        Keyboard.Focus(QueryTextBox);
    }

    /// <summary>
    /// 允许搜索窗口在应用退出时真正关闭。
    /// </summary>
    public void ClosePermanently()
    {
        allowClose = true;
        searchDebounceTimer.Stop();
        searchDebounceTimer.Tick -= HandleSearchDebounceElapsed;
        Close();
    }

    /// <summary>
    /// 在主题设置变化后刷新搜索窗口的 DWM 属性。
    /// </summary>
    public void RefreshTheme()
    {
        if (new System.Windows.Interop.WindowInteropHelper(this).Handle != IntPtr.Zero)
        {
            var settings = mainViewModel.Configuration.Settings;
            themeService.ApplyWindowBackdrop(this, settings.Theme, settings.WindowMaterial);
        }
    }

    /// <summary>
    /// 应用搜索窗口的原生背景材质和深浅色标题属性。
    /// </summary>
    /// <param name="sender">当前窗口。</param>
    /// <param name="eventArgs">事件参数。</param>
    private void HandleSourceInitialized(object? sender, EventArgs eventArgs)
    {
        var settings = mainViewModel.Configuration.Settings;
        themeService.ApplyWindowBackdrop(this, settings.Theme, settings.WindowMaterial);
    }

    /// <summary>
    /// 搜索面板失去激活状态时自动隐藏。
    /// </summary>
    /// <param name="sender">当前窗口。</param>
    /// <param name="eventArgs">事件参数。</param>
    private void HandleDeactivated(object? sender, EventArgs eventArgs)
    {
        if (allowClose || autoHideCheckPending)
        {
            return;
        }

        autoHideCheckPending = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            autoHideCheckPending = false;
            if (allowClose
                || !IsVisible
                || IsActive
                || HasVisibleModalWindow())
            {
                return;
            }

            searchDebounceTimer.Stop();
            Hide();
        });
    }

    /// <summary>
    /// 常规关闭请求转换为隐藏，以便重复呼出。
    /// </summary>
    /// <param name="sender">当前窗口。</param>
    /// <param name="eventArgs">关闭事件参数。</param>
    private void HandleClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (allowClose)
        {
            return;
        }

        eventArgs.Cancel = true;
        searchDebounceTimer.Stop();
        Hide();
    }

    /// <summary>
    /// 查询文本变化后重新执行跨分组搜索。
    /// </summary>
    /// <param name="sender">查询输入框。</param>
    /// <param name="eventArgs">文本变化参数。</param>
    private void QueryTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs eventArgs)
    {
        searchDebounceTimer.Stop();
        searchDebounceTimer.Start();
    }

    /// <summary>
    /// 输入停止指定时间后执行一次搜索刷新。
    /// </summary>
    /// <param name="sender">搜索防抖计时器。</param>
    /// <param name="eventArgs">计时器事件参数。</param>
    private void HandleSearchDebounceElapsed(object? sender, EventArgs eventArgs)
    {
        searchDebounceTimer.Stop();
        RefreshResults();
    }

    /// <summary>
    /// 处理上下选择、Enter 启动和 Escape 隐藏。
    /// </summary>
    /// <param name="sender">当前窗口。</param>
    /// <param name="eventArgs">键盘事件参数。</param>
    private async void Window_PreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape)
        {
            searchDebounceTimer.Stop();
            Hide();
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.Down)
        {
            MoveSelection(1);
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.Up)
        {
            MoveSelection(-1);
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.Enter)
        {
            eventArgs.Handled = true;
            await LaunchSelectedAsync();
        }
    }

    /// <summary>
    /// 单击搜索结果时启动对应项目。
    /// </summary>
    /// <param name="sender">结果列表。</param>
    /// <param name="eventArgs">鼠标事件参数。</param>
    private async void ResultsListBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs eventArgs)
    {
        if (ItemsControl.ContainerFromElement(
                ResultsListBox,
                eventArgs.OriginalSource as DependencyObject) is not ListBoxItem item)
        {
            return;
        }

        ResultsListBox.SelectedItem = item.DataContext;
        eventArgs.Handled = true;
        await LaunchSelectedAsync();
    }

    /// <summary>
    /// 根据当前查询填充结果列表并选择第一项。
    /// </summary>
    private void RefreshResults()
    {
        if (QueryTextBox is null || ResultsListBox is null)
        {
            return;
        }

        var itemsById = mainViewModel.GetAllItems().ToDictionary(item => item.Id);
        var entries = LauncherSearch
            .Search(mainViewModel.Configuration, QueryTextBox.Text, 100)
            .Select(result => itemsById.TryGetValue(result.Item.Id, out var itemViewModel)
                ? new SearchEntryViewModel(result.Group.Name, itemViewModel)
                : null)
            .OfType<SearchEntryViewModel>();
        Results.ReplaceAll(entries);

        ResultsListBox.SelectedIndex = Results.Count > 0 ? 0 : -1;
        if (EmptyText is not null)
        {
            EmptyText.Visibility = Results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>
    /// 相对移动当前搜索结果选择并滚动到可见位置。
    /// </summary>
    /// <param name="offset">选择索引的相对变化。</param>
    private void MoveSelection(int offset)
    {
        if (Results.Count == 0)
        {
            return;
        }

        var current = Math.Max(0, ResultsListBox.SelectedIndex);
        ResultsListBox.SelectedIndex = Math.Clamp(current + offset, 0, Results.Count - 1);
        ResultsListBox.ScrollIntoView(ResultsListBox.SelectedItem);
    }

    /// <summary>
    /// 启动当前选中搜索结果，并在成功后隐藏面板。
    /// </summary>
    /// <returns>启动和面板状态更新完成时结束的任务。</returns>
    private async Task LaunchSelectedAsync()
    {
        if (ResultsListBox.SelectedItem is not SearchEntryViewModel entry)
        {
            return;
        }

        var result = await launchService.LaunchAsync(entry.Item);
        if (!result.IsSuccessful)
        {
            MessageBox.Show(this, result.ErrorMessage, "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        searchDebounceTimer.Stop();
        Hide();
    }

    /// <summary>
    /// 判断是否存在应阻止搜索面板自动隐藏的模态窗口。
    /// </summary>
    /// <returns>搜索窗口被禁用或存在可见所属窗口时返回 <see langword="true"/>。</returns>
    private bool HasVisibleModalWindow()
    {
        if (OwnedWindows.Cast<Window>().Any(window => window.IsVisible))
        {
            return true;
        }

        var handle = new WindowInteropHelper(this).Handle;
        return handle != IntPtr.Zero && !IsWindowEnabled(handle);
    }

    /// <summary>
    /// 判断原生窗口当前是否允许用户输入。
    /// </summary>
    /// <param name="windowHandle">目标窗口句柄。</param>
    /// <returns>窗口启用时返回 <see langword="true"/>。</returns>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr windowHandle);
}
