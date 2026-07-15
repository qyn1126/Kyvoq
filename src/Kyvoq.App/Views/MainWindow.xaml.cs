using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Kyvoq.App.Services;
using Kyvoq.App.ViewModels;
using Kyvoq.Core.Models;
using Kyvoq.Core.Persistence;
using Kyvoq.Core.Services;
using Snackbar = Wpf.Ui.Controls.Snackbar;

namespace Kyvoq.App.Views;

/// <summary>
/// 提供分组、图标网格、拖放以及全部项目管理入口。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// 表示主窗口按宽度划分的响应式布局档位。
    /// </summary>
    private enum ResponsiveLayoutMode
    {
        Wide,
        Medium,
        Narrow,
        Extreme
    }

    /// <summary>
    /// 表示项目网格根据可用宽高采用的显示密度。
    /// </summary>
    private enum ItemLayoutDensity
    {
        Normal,
        Compact,
        Tiny
    }

    private const string ItemDragFormat = "Kyvoq.LauncherItemId";
    private const int DwmWindowCornerPreference = 33;
    private const int DwmRoundWindow = 2;
    private readonly MainViewModel viewModel;
    private readonly ILaunchService launchService;
    private readonly IConfigurationStore configurationStore;
    private readonly IconCacheService iconCache;
    private IGlobalHotkeyService? hotkeyService;
    private readonly ThemeService themeService;
    private readonly string dataDirectory;
    private Point dragStart;
    private LauncherItemViewModel? dragItem;
    private bool dragStarted;
    private readonly DataTemplate fullItemTemplate;
    private ItemLayoutDensity? currentItemLayoutDensity;
    private readonly Snackbar deletionSnackbar;
    private object? pendingDeletion;
    private bool allowClose;
    private bool isPinned;
    private bool autoHideCheckPending;
    private bool isRepositioningForSummon;
    private WindowState lastNonMinimizedWindowState;

    public event EventHandler? SearchPanelRequested;

    public event EventHandler? SettingsChanged;

    public event EventHandler? ExitRequested;

    /// <summary>
    /// 创建 Kyvoq 主窗口并连接应用服务。
    /// </summary>
    /// <param name="viewModel">主窗口视图模型。</param>
    /// <param name="launchService">项目启动服务。</param>
    /// <param name="configurationStore">配置导入导出服务。</param>
    /// <param name="iconCache">图标缓存服务。</param>
    /// <param name="themeService">主题服务。</param>
    /// <param name="dataDirectory">应用数据目录。</param>
    public MainWindow(
        MainViewModel viewModel,
        ILaunchService launchService,
        IConfigurationStore configurationStore,
        IconCacheService iconCache,
        ThemeService themeService,
        string dataDirectory)
    {
        InitializeComponent();
        fullItemTemplate = ItemsListBox.ItemTemplate;
        if (MoreButton.ContextMenu is { } moreMenu)
        {
            moreMenu.Placement = PlacementMode.Custom;
            moreMenu.CustomPopupPlacementCallback = PlaceMoreMenu;
        }
        deletionSnackbar = new Snackbar(DeletionSnackbarPresenter)
        {
            Timeout = TimeSpan.FromSeconds(5),
            IsCloseButtonEnabled = true
        };
        this.viewModel = viewModel;
        this.launchService = launchService;
        this.configurationStore = configurationStore;
        this.iconCache = iconCache;
        this.themeService = themeService;
        this.dataDirectory = dataDirectory;
        Icon = BrandIconFactory.CreateImageSource();
        DataContext = viewModel;
        lastNonMinimizedWindowState = viewModel.Configuration.Settings.IsMaximized
            ? WindowState.Maximized
            : WindowState.Normal;

        RestoreWindowPlacement();
        viewModel.SaveFailed += HandleSaveFailed;
        SourceInitialized += HandleSourceInitialized;
        Loaded += HandleLoaded;
        Closing += HandleClosing;
        Deactivated += HandleDeactivated;
        StateChanged += HandleStateChanged;
        SizeChanged += HandleSizeChanged;
        UpdateResponsiveLayout();
    }

    /// <summary>
    /// 在窗口句柄创建后连接全局快捷键服务。
    /// </summary>
    /// <param name="service">负责注册和检测快捷键的服务。</param>
    public void AttachHotkeyService(IGlobalHotkeyService service)
    {
        hotkeyService = service ?? throw new ArgumentNullException(nameof(service));
    }

    /// <summary>
    /// 显示并激活主窗口；从隐藏或最小化状态恢复时把窗口定位到鼠标所在显示器。
    /// </summary>
    public void ShowAndActivate()
    {
        var shouldReposition = !IsVisible || WindowState == WindowState.Minimized;
        if (shouldReposition)
        {
            var restoreMaximized = WindowState == WindowState.Maximized
                || (WindowState == WindowState.Minimized
                    && lastNonMinimizedWindowState == WindowState.Maximized)
                || (!IsLoaded
                    && viewModel.Configuration.Settings.IsMaximized);

            isRepositioningForSummon = true;
            try
            {
                if (IsVisible)
                {
                    Hide();
                }

                if (WindowState != WindowState.Normal)
                {
                    WindowState = WindowState.Normal;
                }

                _ = CursorWindowPositioner.TryPositionNearCursor(this);
                if (restoreMaximized)
                {
                    WindowState = WindowState.Maximized;
                }

                Show();
            }
            finally
            {
                isRepositioningForSummon = false;
            }
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    /// <summary>
    /// 在主窗口处于前台时隐藏窗口，否则恢复并置前主窗口。
    /// </summary>
    public void ToggleVisibility()
    {
        if (IsVisible
            && WindowState != WindowState.Minimized
            && (IsActive || isPinned)
            && !HasVisibleModalWindow())
        {
            CaptureWindowPlacement();
            Hide();
            return;
        }

        ShowAndActivate();
    }

    /// <summary>
    /// 允许主窗口在应用退出流程中真正关闭。
    /// </summary>
    public void ClosePermanently()
    {
        allowClose = true;
        CaptureWindowPlacement();
        Close();
    }

    /// <summary>
    /// 为主窗口应用 Windows 11 Mica 与圆角属性。
    /// </summary>
    /// <param name="sender">当前窗口。</param>
    /// <param name="eventArgs">事件参数。</param>
    private void HandleSourceInitialized(object? sender, EventArgs eventArgs)
    {
        themeService.ApplyWindowBackdrop(this, viewModel.Configuration.Settings.Theme);
        var handle = new WindowInteropHelper(this).Handle;
        var cornerPreference = DwmRoundWindow;
        _ = DwmSetWindowAttribute(
            handle,
            DwmWindowCornerPreference,
            ref cornerPreference,
            sizeof(int));
    }

    /// <summary>
    /// 窗口第一次显示后恢复最大化状态并刷新响应式布局。
    /// </summary>
    /// <param name="sender">当前窗口。</param>
    /// <param name="eventArgs">事件参数。</param>
    private void HandleLoaded(object sender, RoutedEventArgs eventArgs)
    {
        if (viewModel.Configuration.Settings.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }

        UpdateResponsiveLayout();
    }

    /// <summary>
    /// 常规关闭请求转换为隐藏到通知区域。
    /// </summary>
    /// <param name="sender">当前窗口。</param>
    /// <param name="eventArgs">关闭事件参数。</param>
    private void HandleClosing(object? sender, CancelEventArgs eventArgs)
    {
        CaptureWindowPlacement();
        if (allowClose)
        {
            return;
        }

        eventArgs.Cancel = true;
        Hide();
    }

    /// <summary>
    /// 主窗口失去激活后延迟确认，并在未固定且没有模态窗口时自动隐藏。
    /// </summary>
    /// <param name="sender">当前窗口。</param>
    /// <param name="eventArgs">激活状态变化参数。</param>
    private void HandleDeactivated(object? sender, EventArgs eventArgs)
    {
        if (isPinned || allowClose || autoHideCheckPending)
        {
            return;
        }

        autoHideCheckPending = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            autoHideCheckPending = false;
            if (isPinned
                || allowClose
                || !IsVisible
                || IsActive
                || HasVisibleModalWindow())
            {
                return;
            }

            CaptureWindowPlacement();
            Hide();
        });
    }

    /// <summary>
    /// 窗口状态变化后保存最大化状态。
    /// </summary>
    /// <param name="sender">当前窗口。</param>
    /// <param name="eventArgs">事件参数。</param>
    private void HandleStateChanged(object? sender, EventArgs eventArgs)
    {
        if (WindowState != WindowState.Minimized)
        {
            lastNonMinimizedWindowState = WindowState;
        }

        if (!isRepositioningForSummon)
        {
            CaptureWindowPlacement();
        }
    }

    /// <summary>
    /// 在 UI 线程向用户展示延迟保存失败信息。
    /// </summary>
    /// <param name="message">错误消息。</param>
    private void HandleSaveFailed(string message)
    {
        _ = Dispatcher.BeginInvoke(() =>
            MessageBox.Show(this, message, "Kyvoq", MessageBoxButton.OK, MessageBoxImage.Warning));
    }

    /// <summary>
    /// 拖动自定义标题区域移动窗口，双击切换最大化。
    /// </summary>
    /// <param name="sender">标题区域。</param>
    /// <param name="eventArgs">鼠标事件参数。</param>
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            return;
        }

        if (eventArgs.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    /// <summary>
    /// 处理主窗口 Ctrl+F 搜索和 Ctrl+N 新建项目快捷操作。
    /// </summary>
    /// <param name="sender">当前窗口。</param>
    /// <param name="eventArgs">键盘事件参数。</param>
    private async void Window_PreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (Keyboard.Modifiers == ModifierKeys.None && eventArgs.Key == Key.Escape)
        {
            CaptureWindowPlacement();
            Hide();
            eventArgs.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && eventArgs.Key == Key.F)
        {
            SearchPanelRequested?.Invoke(this, EventArgs.Empty);
            eventArgs.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && eventArgs.Key == Key.N)
        {
            AddItem_Click(sender, eventArgs);
            eventArgs.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.None
                 && eventArgs.Key == Key.Enter
                 && ItemsListBox.SelectedItem is LauncherItemViewModel item)
        {
            eventArgs.Handled = true;
            await LaunchItemAsync(item.Model);
        }
    }

    /// <summary>
    /// 主窗口尺寸变化后切换完整、紧凑或极简布局。
    /// </summary>
    /// <param name="sender">当前窗口。</param>
    /// <param name="eventArgs">尺寸变化参数。</param>
    private void HandleSizeChanged(object sender, SizeChangedEventArgs eventArgs) => UpdateResponsiveLayout();

    /// <summary>
    /// 鼠标进入分组行时立即切换到该分组，但不改变键盘焦点。
    /// </summary>
    /// <param name="sender">鼠标进入的分组容器。</param>
    /// <param name="eventArgs">鼠标事件参数。</param>
    private void Group_MouseEnter(object sender, MouseEventArgs eventArgs)
    {
        if (sender is ListBoxItem { DataContext: LauncherGroupViewModel group }
            && !ReferenceEquals(viewModel.SelectedGroup, group))
        {
            viewModel.SelectedGroup = group;
        }
    }

    /// <summary>
    /// 新建一个用户命名的分组。
    /// </summary>
    /// <param name="sender">新建分组按钮。</param>
    /// <param name="eventArgs">事件参数。</param>
    private void AddGroup_Click(object sender, RoutedEventArgs eventArgs)
    {
        var dialog = new TextInputDialog(
            "新建分组",
            "分组名称",
            string.Empty,
            themeService,
            viewModel.Configuration.Settings.Theme)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            viewModel.AddGroup(dialog.Value);
        }
    }

    /// <summary>
    /// 修改右键菜单对应分组的名称。
    /// </summary>
    /// <param name="sender">重命名菜单项。</param>
    /// <param name="eventArgs">事件参数。</param>
    private void RenameGroup_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (GetCommandParameter<LauncherGroupViewModel>(sender) is not { } group)
        {
            return;
        }

        var dialog = new TextInputDialog(
            "重命名分组",
            "分组名称",
            group.Name,
            themeService,
            viewModel.Configuration.Settings.Theme)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            viewModel.RenameGroup(group.Id, dialog.Value);
        }
    }

    /// <summary>
    /// 把右键菜单对应分组向上移动一位。
    /// </summary>
    /// <param name="sender">上移菜单项。</param>
    /// <param name="eventArgs">事件参数。</param>
    private void MoveGroupUp_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (GetCommandParameter<LauncherGroupViewModel>(sender) is { } group)
        {
            viewModel.MoveGroup(group.Id, -1);
        }
    }

    /// <summary>
    /// 把右键菜单对应分组向下移动一位。
    /// </summary>
    /// <param name="sender">下移菜单项。</param>
    /// <param name="eventArgs">事件参数。</param>
    private void MoveGroupDown_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (GetCommandParameter<LauncherGroupViewModel>(sender) is { } group)
        {
            viewModel.MoveGroup(group.Id, 1);
        }
    }

    /// <summary>
    /// 删除右键菜单对应分组及其中项目，并提供短时撤销入口。
    /// </summary>
    /// <param name="sender">删除分组菜单项。</param>
    /// <param name="eventArgs">事件参数。</param>
    private void DeleteGroup_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (GetCommandParameter<LauncherGroupViewModel>(sender) is not { } group)
        {
            return;
        }

        if (viewModel.Groups.Count > 1)
        {
            var snapshot = viewModel.RemoveGroup(group.Id);
            ShowDeletionUndo("分组已删除", $"“{group.Name}”及其中 {group.ItemCount} 个项目", snapshot);
        }
    }

    /// <summary>
    /// 打开分组菜单前根据“至少保留一个分组”的约束更新删除项状态。
    /// </summary>
    /// <param name="sender">分组内容边框。</param>
    /// <param name="eventArgs">菜单打开参数。</param>
    private void Group_ContextMenuOpening(object sender, ContextMenuEventArgs eventArgs)
    {
        if (sender is not Border { ContextMenu: { } menu })
        {
            return;
        }

        foreach (var deleteItem in menu.Items
                     .OfType<MenuItem>()
                     .Where(item => string.Equals(item.Header?.ToString(), "删除分组", StringComparison.Ordinal)))
        {
            deleteItem.IsEnabled = viewModel.Groups.Count > 1;
        }
    }

    /// <summary>
    /// 打开空白项目编辑器并把结果加入当前分组。
    /// </summary>
    /// <param name="sender">添加项目菜单或快捷操作入口。</param>
    /// <param name="eventArgs">事件参数。</param>
    private void AddItem_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (viewModel.SelectedGroup is null)
        {
            return;
        }

        var item = new LauncherItem { Id = Guid.NewGuid() };
        var dialog = CreateItemEditor(item, isNew: true);
        if (dialog.ShowDialog() == true && dialog.ResultItem is { } result)
        {
            viewModel.AddItem(viewModel.SelectedGroup.Id, result);
        }
    }

    /// <summary>
    /// 鼠标在同一项目上完成单击且未触发拖动时启动项目。
    /// </summary>
    /// <param name="sender">项目容器。</param>
    /// <param name="eventArgs">鼠标事件参数。</param>
    private async void Item_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs eventArgs)
    {
        LauncherItem? itemToLaunch = null;
        if (!dragStarted
            && sender is ListBoxItem { DataContext: LauncherItemViewModel item }
            && ReferenceEquals(item, dragItem))
        {
            itemToLaunch = item.Model;
            eventArgs.Handled = true;
        }

        dragItem = null;
        dragStarted = false;
        if (itemToLaunch is not null)
        {
            await LaunchItemAsync(itemToLaunch);
        }
    }

    /// <summary>
    /// 启动右键菜单对应项目。
    /// </summary>
    /// <param name="sender">启动菜单项。</param>
    /// <param name="eventArgs">事件参数。</param>
    private async void LaunchItem_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (GetCommandParameter<LauncherItemViewModel>(sender) is { } item)
        {
            await LaunchItemAsync(item.Model);
        }
    }

    /// <summary>
    /// 以管理员身份启动右键菜单对应程序。
    /// </summary>
    /// <param name="sender">管理员启动菜单项。</param>
    /// <param name="eventArgs">事件参数。</param>
    private async void LaunchItemAsAdministrator_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (GetCommandParameter<LauncherItemViewModel>(sender) is not { } item)
        {
            return;
        }

        if (TargetClassifier.Classify(item.Target) != LauncherTargetType.Application)
        {
            MessageBox.Show(this, "只有可执行程序支持管理员身份启动。", "Kyvoq", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var elevated = item.Model.Clone();
        elevated.RunAsAdministrator = true;
        await LaunchItemAsync(elevated);
    }

    /// <summary>
    /// 在资源管理器中定位右键菜单对应目标。
    /// </summary>
    /// <param name="sender">打开位置菜单项。</param>
    /// <param name="eventArgs">事件参数。</param>
    private void OpenItemLocation_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (GetCommandParameter<LauncherItemViewModel>(sender) is not { } item)
        {
            return;
        }

        if (TargetClassifier.Classify(item.Target) == LauncherTargetType.Url)
        {
            MessageBox.Show(this, "网址没有本地文件位置。", "Kyvoq", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var arguments = Directory.Exists(item.Target)
                ? $"\"{item.Target}\""
                : $"/select,\"{item.Target}\"";
            using var process = Process.Start(
                new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is Win32Exception or FileNotFoundException)
        {
            MessageBox.Show(this, exception.Message, "无法打开所在位置", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 把项目目标复制到 Windows 剪贴板。
    /// </summary>
    /// <param name="sender">复制路径菜单项。</param>
    /// <param name="eventArgs">事件参数。</param>
    private void CopyItemTarget_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (GetCommandParameter<LauncherItemViewModel>(sender) is not { } item)
        {
            return;
        }

        try
        {
            Clipboard.SetText(item.Target);
        }
        catch (ExternalException exception)
        {
            MessageBox.Show(this, exception.Message, "无法访问剪贴板", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 打开现有项目编辑器并应用修改。
    /// </summary>
    /// <param name="sender">编辑菜单项。</param>
    /// <param name="eventArgs">事件参数。</param>
    private void EditItem_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (GetCommandParameter<LauncherItemViewModel>(sender) is not { } item)
        {
            return;
        }

        var dialog = CreateItemEditor(item.Model, isNew: false);
        if (dialog.ShowDialog() == true && dialog.ResultItem is { } result)
        {
            viewModel.UpdateItem(result);
        }
    }

    /// <summary>
    /// 立即删除右键菜单对应项目并提供短时撤销入口。
    /// </summary>
    /// <param name="sender">删除菜单项。</param>
    /// <param name="eventArgs">事件参数。</param>
    private void DeleteItem_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (GetCommandParameter<LauncherItemViewModel>(sender) is not { } item)
        {
            return;
        }

        var snapshot = viewModel.RemoveItem(item.Id);
        ShowDeletionUndo("项目已删除", $"“{item.Name}”", snapshot);
    }

    /// <summary>
    /// 记录鼠标按下位置，为项目拖动设置阈值。
    /// </summary>
    /// <param name="sender">项目容器。</param>
    /// <param name="eventArgs">鼠标事件参数。</param>
    private void Item_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        dragStart = eventArgs.GetPosition(ItemsListBox);
        dragItem = (sender as ListBoxItem)?.DataContext as LauncherItemViewModel;
        dragStarted = false;
    }

    /// <summary>
    /// 鼠标移动超过系统阈值后开始项目拖放。
    /// </summary>
    /// <param name="sender">项目容器。</param>
    /// <param name="eventArgs">鼠标事件参数。</param>
    private void Item_PreviewMouseMove(object sender, MouseEventArgs eventArgs)
    {
        if (eventArgs.LeftButton != MouseButtonState.Pressed || dragItem is null)
        {
            return;
        }

        var position = eventArgs.GetPosition(ItemsListBox);
        if (Math.Abs(position.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var data = new DataObject();
        data.SetData(ItemDragFormat, dragItem.Id.ToString("D"));
        dragStarted = true;
        _ = DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move);
        dragItem = null;
    }

    /// <summary>
    /// 项目容器接收到内部项目时显示移动效果。
    /// </summary>
    /// <param name="sender">目标项目容器。</param>
    /// <param name="eventArgs">拖放事件参数。</param>
    private void Item_DragEnter(object sender, DragEventArgs eventArgs)
    {
        if (eventArgs.Data.GetDataPresent(ItemDragFormat))
        {
            eventArgs.Effects = DragDropEffects.Move;
            eventArgs.Handled = true;
        }
    }

    /// <summary>
    /// 把内部拖动项目插入到目标项目之前。
    /// </summary>
    /// <param name="sender">目标项目容器。</param>
    /// <param name="eventArgs">拖放事件参数。</param>
    private void Item_Drop(object sender, DragEventArgs eventArgs)
    {
        if (!TryGetDraggedItemId(eventArgs.Data, out var sourceId)
            || sender is not ListBoxItem { DataContext: LauncherItemViewModel target })
        {
            return;
        }

        if (sourceId != target.Id)
        {
            viewModel.ReorderItem(sourceId, target.Id);
        }

        eventArgs.Handled = true;
    }

    /// <summary>
    /// 判断拖入网格的数据应显示移动还是复制效果。
    /// </summary>
    /// <param name="sender">项目网格。</param>
    /// <param name="eventArgs">拖放事件参数。</param>
    private void ItemsListBox_DragEnter(object sender, DragEventArgs eventArgs)
    {
        eventArgs.Effects = eventArgs.Data.GetDataPresent(ItemDragFormat)
            ? DragDropEffects.Move
            : HasExternalTargets(eventArgs.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        eventArgs.Handled = true;
    }

    /// <summary>
    /// 把内部项目追加到末尾，或把资源管理器目标添加到当前分组。
    /// </summary>
    /// <param name="sender">项目网格。</param>
    /// <param name="eventArgs">拖放事件参数。</param>
    private void ItemsListBox_Drop(object sender, DragEventArgs eventArgs)
    {
        if (viewModel.SelectedGroup is null)
        {
            return;
        }

        if (TryGetDraggedItemId(eventArgs.Data, out var itemId))
        {
            viewModel.ReorderItem(itemId, null);
        }
        else
        {
            AddExternalTargets(viewModel.SelectedGroup.Id, eventArgs.Data);
        }

        eventArgs.Handled = true;
    }

    /// <summary>
    /// 拖到侧边分组时根据数据类型显示移动或复制效果。
    /// </summary>
    /// <param name="sender">分组拖放区域。</param>
    /// <param name="eventArgs">拖放事件参数。</param>
    private void Group_DragEnter(object sender, DragEventArgs eventArgs)
    {
        eventArgs.Effects = eventArgs.Data.GetDataPresent(ItemDragFormat)
            ? DragDropEffects.Move
            : HasExternalTargets(eventArgs.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        eventArgs.Handled = true;
    }

    /// <summary>
    /// 把内部项目移动到目标分组，或直接向该分组导入外部目标。
    /// </summary>
    /// <param name="sender">目标分组区域。</param>
    /// <param name="eventArgs">拖放事件参数。</param>
    private void Group_Drop(object sender, DragEventArgs eventArgs)
    {
        if (sender is not FrameworkElement { Tag: LauncherGroupViewModel group })
        {
            return;
        }

        if (TryGetDraggedItemId(eventArgs.Data, out var itemId))
        {
            viewModel.MoveItem(itemId, group.Id);
        }
        else
        {
            AddExternalTargets(group.Id, eventArgs.Data);
        }

        eventArgs.Handled = true;
    }

    /// <summary>
    /// 打开应用设置窗口并应用保存结果。
    /// </summary>
    /// <param name="sender">设置入口。</param>
    /// <param name="eventArgs">事件参数。</param>
    private void Settings_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (hotkeyService is null)
        {
            return;
        }

        var dialog = new SettingsWindow(
            viewModel.Configuration.Settings,
            dataDirectory,
            hotkeyService,
            themeService)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true || dialog.ResultSettings is not { } settings)
        {
            return;
        }

        viewModel.UpdateSettings(settings);
        themeService.ApplyApplicationTheme(settings);
        themeService.ApplyWindowBackdrop(this, settings.Theme);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 请求显示全局搜索面板。
    /// </summary>
    /// <param name="sender">搜索全部按钮。</param>
    /// <param name="eventArgs">事件参数。</param>
    private void OpenSearch_Click(object sender, RoutedEventArgs eventArgs) =>
        SearchPanelRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 在更多按钮下方打开配置与退出菜单。
    /// </summary>
    /// <param name="sender">更多按钮。</param>
    /// <param name="eventArgs">事件参数。</param>
    private void MoreButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (MoreButton.ContextMenu is { } menu)
        {
            menu.PlacementTarget = MoreButton;
            menu.IsOpen = true;
        }
    }

    /// <summary>
    /// 切换主面板固定状态并刷新按钮图标、提示和无障碍名称。
    /// </summary>
    /// <param name="sender">固定切换按钮。</param>
    /// <param name="eventArgs">点击事件参数。</param>
    private void PinButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        isPinned = PinButton.IsChecked == true;
        var description = isPinned ? "取消固定" : "固定面板";
        PinButton.ToolTip = description;
        PinIcon.Text = isPinned ? "\uE77A" : "\uE718";
        AutomationProperties.SetName(PinButton, description);
    }

    /// <summary>
    /// 将更多菜单的右边缘与触发按钮右边缘对齐，并优先显示在按钮下方。
    /// </summary>
    /// <param name="popupSize">菜单弹出层尺寸。</param>
    /// <param name="targetSize">更多按钮尺寸。</param>
    /// <param name="offset">WPF 提供的附加偏移量。</param>
    /// <returns>按优先级排列的菜单候选位置。</returns>
    private static CustomPopupPlacement[] PlaceMoreMenu(
        Size popupSize,
        Size targetSize,
        Point offset) =>
    [
        new CustomPopupPlacement(
            new Point(targetSize.Width - popupSize.Width, targetSize.Height),
            PopupPrimaryAxis.Horizontal),
        new CustomPopupPlacement(
            new Point(targetSize.Width - popupSize.Width, -popupSize.Height),
            PopupPrimaryAxis.Horizontal)
    ];

    /// <summary>
    /// 从 JSON 文件导入完整配置，并在确认后覆盖当前配置。
    /// </summary>
    /// <param name="sender">导入菜单项。</param>
    /// <param name="eventArgs">事件参数。</param>
    private async void ImportConfiguration_Click(object sender, RoutedEventArgs eventArgs)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入 Kyvoq 配置",
            Filter = "Kyvoq JSON 配置|*.json|所有文件|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var imported = await configurationStore.ImportAsync(dialog.FileName);
            var confirmation = MessageBox.Show(
                this,
                $"将使用 {imported.Groups.Count} 个分组覆盖当前配置。继续吗？\n当前配置会自动保留为 config.json.bak。",
                "导入配置",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }

            await viewModel.FlushSaveAsync();
            await viewModel.ReplaceConfigurationAsync(imported);
            themeService.ApplyApplicationTheme(imported.Settings);
            themeService.ApplyWindowBackdrop(this, imported.Settings.Theme);
            SettingsChanged?.Invoke(this, EventArgs.Empty);
            MessageBox.Show(this, "配置已导入。", "Kyvoq", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(this, exception.Message, "无法导入配置", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 将当前完整配置导出到用户选择的 JSON 文件。
    /// </summary>
    /// <param name="sender">导出菜单项。</param>
    /// <param name="eventArgs">事件参数。</param>
    private async void ExportConfiguration_Click(object sender, RoutedEventArgs eventArgs)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出 Kyvoq 配置",
            Filter = "Kyvoq JSON 配置|*.json",
            DefaultExt = ".json",
            AddExtension = true,
            FileName = $"Kyvoq-{DateTime.Now:yyyyMMdd}.json"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            await configurationStore.ExportAsync(viewModel.Configuration.Clone(), dialog.FileName);
            MessageBox.Show(this, "配置已成功导出。", "Kyvoq", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(this, exception.Message, "无法导出配置", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 使用资源管理器打开 Kyvoq 数据目录。
    /// </summary>
    /// <param name="sender">打开数据目录菜单项。</param>
    /// <param name="eventArgs">事件参数。</param>
    private void OpenDataDirectory_Click(object sender, RoutedEventArgs eventArgs)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            using var process = Process.Start(
                new ProcessStartInfo(dataDirectory) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or Win32Exception)
        {
            MessageBox.Show(this, exception.Message, "无法打开数据目录", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 隐藏主窗口到通知区域。
    /// </summary>
    /// <param name="sender">关闭按钮。</param>
    /// <param name="eventArgs">事件参数。</param>
    private void Close_Click(object sender, RoutedEventArgs eventArgs)
    {
        CaptureWindowPlacement();
        Hide();
    }

    /// <summary>
    /// 请求应用执行完整退出流程。
    /// </summary>
    /// <param name="sender">退出菜单项。</param>
    /// <param name="eventArgs">事件参数。</param>
    private void Exit_Click(object sender, RoutedEventArgs eventArgs) => ExitRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 创建已连接所有校验和主题服务的项目编辑器。
    /// </summary>
    /// <param name="item">待编辑项目。</param>
    /// <param name="isNew">是否为新建操作。</param>
    /// <returns>以主窗口为所有者的编辑器。</returns>
    private ItemEditorWindow CreateItemEditor(LauncherItem item, bool isNew)
    {
        if (hotkeyService is null)
        {
            throw new InvalidOperationException("全局快捷键服务尚未初始化。");
        }

        return new ItemEditorWindow(
            item,
            isNew,
            iconCache,
            hotkeyService,
            themeService,
            viewModel.Configuration.Settings.Theme)
        {
            Owner = this
        };
    }

    /// <summary>
    /// 调用启动服务并展示失败原因。
    /// </summary>
    /// <param name="item">需要启动的项目。</param>
    /// <returns>启动和错误提示处理完成时结束的任务。</returns>
    private async Task LaunchItemAsync(LauncherItem item)
    {
        var result = await launchService.LaunchAsync(item);
        if (!result.IsSuccessful)
        {
            MessageBox.Show(this, result.ErrorMessage, "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 将资源管理器文件或拖入网址批量添加到指定分组。
    /// </summary>
    /// <param name="groupId">目标分组标识。</param>
    /// <param name="data">拖放数据。</param>
    private void AddExternalTargets(Guid groupId, IDataObject data)
    {
        foreach (var target in GetExternalTargets(data))
        {
            var type = TargetClassifier.Classify(target);
            var name = type == LauncherTargetType.Url && Uri.TryCreate(target, UriKind.Absolute, out var uri)
                ? uri.Host
                : Path.GetFileNameWithoutExtension(target.TrimEnd(Path.DirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(name))
            {
                name = target;
            }

            viewModel.AddItem(groupId, new LauncherItem
            {
                Name = name,
                Target = target,
                TargetType = type
            });
        }
    }

    /// <summary>
    /// 从拖放数据提取文件路径或 HTTP/HTTPS 网址。
    /// </summary>
    /// <param name="data">拖放数据。</param>
    /// <returns>去重后的目标集合。</returns>
    private static IReadOnlyList<string> GetExternalTargets(IDataObject data)
    {
        var targets = new List<string>();
        if (data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            targets.AddRange(paths.Where(path => !string.IsNullOrWhiteSpace(path)));
        }

        if (data.GetData(DataFormats.UnicodeText) is string text)
        {
            foreach (var candidate in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                    && uri.Scheme is "http" or "https")
                {
                    targets.Add(uri.AbsoluteUri);
                }
            }
        }

        return targets.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// 判断拖放数据是否包含可添加的外部目标。
    /// </summary>
    /// <param name="data">拖放数据。</param>
    /// <returns>包含文件或网址时返回 <see langword="true"/>。</returns>
    private static bool HasExternalTargets(IDataObject data) => GetExternalTargets(data).Count > 0;

    /// <summary>
    /// 从内部拖放数据解析项目标识。
    /// </summary>
    /// <param name="data">拖放数据。</param>
    /// <param name="itemId">解析出的项目标识。</param>
    /// <returns>解析成功时返回 <see langword="true"/>。</returns>
    private static bool TryGetDraggedItemId(IDataObject data, out Guid itemId)
    {
        var text = data.GetData(ItemDragFormat) as string;
        return Guid.TryParse(text, out itemId);
    }

    /// <summary>
    /// 从菜单控件读取强类型命令参数。
    /// </summary>
    /// <typeparam name="T">期望的参数类型。</typeparam>
    /// <param name="sender">菜单项或其他内容控件。</param>
    /// <returns>类型匹配的命令参数；不匹配时返回空。</returns>
    private static T? GetCommandParameter<T>(object sender) where T : class =>
        sender is MenuItem { CommandParameter: T value } ? value : null;

    /// <summary>
    /// 使用 WPF UI Snackbar 展示最近一次删除及撤销按钮。
    /// </summary>
    /// <param name="title">删除结果标题。</param>
    /// <param name="message">被删除对象说明。</param>
    /// <param name="snapshot">项目或分组删除快照。</param>
    private void ShowDeletionUndo(string title, string message, object snapshot)
    {
        deletionSnackbar.IsShown = false;
        pendingDeletion = snapshot;
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock
        {
            Text = message,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 280
        });
        var undoButton = new Button
        {
            Content = "撤销",
            Margin = new Thickness(12, 0, 0, 0),
            MinWidth = 64
        };
        undoButton.Click += UndoDeletion_Click;
        content.Children.Add(undoButton);
        deletionSnackbar.Title = title;
        deletionSnackbar.Content = content;
        deletionSnackbar.Show();
    }

    /// <summary>
    /// 根据最近一次删除快照恢复项目或分组。
    /// </summary>
    /// <param name="sender">Snackbar 撤销按钮。</param>
    /// <param name="eventArgs">事件参数。</param>
    private void UndoDeletion_Click(object sender, RoutedEventArgs eventArgs)
    {
        switch (pendingDeletion)
        {
            case DeletedItemSnapshot itemSnapshot:
                viewModel.RestoreItem(itemSnapshot);
                break;
            case DeletedGroupSnapshot groupSnapshot:
                viewModel.RestoreGroup(groupSnapshot);
                break;
            default:
                return;
        }

        pendingDeletion = null;
        deletionSnackbar.IsShown = false;
    }

    /// <summary>
    /// 根据窗口可用宽高调整文字侧栏、标题操作区和项目网格密度。
    /// </summary>
    private void UpdateResponsiveLayout()
    {
        if (SidebarColumn is null || ItemsListBox is null)
        {
            return;
        }

        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;
        var mode = width switch
        {
            >= 720 => ResponsiveLayoutMode.Wide,
            >= 420 => ResponsiveLayoutMode.Medium,
            >= 180 => ResponsiveLayoutMode.Narrow,
            _ => ResponsiveLayoutMode.Extreme
        };
        var sidebarWidth = mode switch
        {
            ResponsiveLayoutMode.Wide => 196d,
            ResponsiveLayoutMode.Medium => 112d,
            ResponsiveLayoutMode.Narrow => 72d,
            _ => 44d
        };
        var shortWindow = height < 220;
        var wide = mode == ResponsiveLayoutMode.Wide;
        var extreme = mode == ResponsiveLayoutMode.Extreme;

        SidebarPanel.Visibility = Visibility.Visible;
        SidebarColumn.Width = new GridLength(sidebarWidth);
        SidebarFooterRow.Height = new GridLength(shortWindow ? 0 : 78);
        SidebarFooter.Visibility = shortWindow ? Visibility.Collapsed : Visibility.Visible;
        GroupsListBox.Margin = mode switch
        {
            ResponsiveLayoutMode.Wide => new Thickness(12, 10, 10, 0),
            ResponsiveLayoutMode.Medium => new Thickness(6, 8, 6, 0),
            ResponsiveLayoutMode.Narrow => new Thickness(4, 6, 4, 0),
            _ => new Thickness(2, 4, 2, 0)
        };
        SidebarFooter.Margin = wide
            ? new Thickness(12, 10, 10, 16)
            : new Thickness(3, 10, 3, 16);
        var contentWidth = Math.Max(0, width - sidebarWidth);
        HeaderGrid.Margin = mode switch
        {
            ResponsiveLayoutMode.Wide => new Thickness(20, 0, 10, 0),
            ResponsiveLayoutMode.Medium => new Thickness(12, 0, 8, 0),
            ResponsiveLayoutMode.Narrow => new Thickness(6, 0, 4, 0),
            _ => new Thickness(2, 0, 2, 0)
        };
        var headerWidth = Math.Max(0, contentWidth - HeaderGrid.Margin.Left - HeaderGrid.Margin.Right);
        var commandButtonSize = extreme
            ? Math.Max(0, headerWidth / 3)
            : Math.Min(38, headerWidth / 3);
        var searchAllVisible = !extreme && headerWidth >= 152;
        if (searchAllVisible)
        {
            commandButtonSize = 38;
        }

        SearchAllButton.Visibility = searchAllVisible ? Visibility.Visible : Visibility.Collapsed;
        SearchColumn.Width = new GridLength(searchAllVisible ? commandButtonSize : 0);
        PinColumn.Width = new GridLength(commandButtonSize);
        MoreButton.Visibility = Visibility.Visible;
        MoreColumn.Width = new GridLength(commandButtonSize);
        CloseColumn.Width = new GridLength(commandButtonSize);
        SearchAllButton.Width = commandButtonSize;
        SearchAllButton.Height = commandButtonSize;
        PinButton.Width = commandButtonSize;
        PinButton.Height = commandButtonSize;
        MoreButton.Width = commandButtonSize;
        MoreButton.Height = commandButtonSize;
        CloseButton.Width = commandButtonSize;
        CloseButton.Height = commandButtonSize;
        HeaderRow.Height = new GridLength(extreme ? 32 : 62);
        ItemsHost.Margin = mode switch
        {
            ResponsiveLayoutMode.Wide or ResponsiveLayoutMode.Medium => new Thickness(12, 2, 10, 10),
            ResponsiveLayoutMode.Narrow => new Thickness(4, 2, 4, 4),
            _ => new Thickness(2)
        };

        var itemDensity = extreme || height < 150
            ? ItemLayoutDensity.Tiny
            : mode == ResponsiveLayoutMode.Narrow || height < 260
                ? ItemLayoutDensity.Compact
                : ItemLayoutDensity.Normal;
        if (currentItemLayoutDensity != itemDensity)
        {
            ItemsListBox.ItemTemplate = itemDensity == ItemLayoutDensity.Normal
                ? fullItemTemplate
                : (DataTemplate)Resources["CompactItemTemplate"];
            ItemsListBox.ItemsPanel = itemDensity switch
            {
                ItemLayoutDensity.Normal => (ItemsPanelTemplate)Resources["NormalItemsPanelTemplate"],
                ItemLayoutDensity.Compact => (ItemsPanelTemplate)Resources["CompactItemsPanelTemplate"],
                _ => (ItemsPanelTemplate)Resources["TinyItemsPanelTemplate"]
            };
            currentItemLayoutDensity = itemDensity;
        }
    }

    /// <summary>
    /// 从配置恢复尺寸与仍位于虚拟屏幕范围内的位置。
    /// </summary>
    private void RestoreWindowPlacement()
    {
        var settings = viewModel.Configuration.Settings;
        Width = Math.Max(MinWidth, settings.WindowWidth);
        Height = Math.Max(MinHeight, settings.WindowHeight);
        if (settings.WindowLeft is not double left || settings.WindowTop is not double top)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        var intersectsScreen = left + Width >= SystemParameters.VirtualScreenLeft
            && left <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
            && top + Height >= SystemParameters.VirtualScreenTop
            && top <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
        if (intersectsScreen)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    /// <summary>
    /// 将还原尺寸、位置及最大化状态写回配置。
    /// </summary>
    private void CaptureWindowPlacement()
    {
        if (!IsInitialized)
        {
            return;
        }

        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        viewModel.UpdateWindowPlacement(
            bounds.Width,
            bounds.Height,
            bounds.Left,
            bounds.Top,
            WindowState == WindowState.Maximized
            || (WindowState == WindowState.Minimized
                && lastNonMinimizedWindowState == WindowState.Maximized));
    }

    /// <summary>
    /// 判断当前是否存在应阻止主面板自动隐藏的模态窗口。
    /// </summary>
    /// <returns>主窗口被禁用或存在可见所属窗口时返回 <see langword="true"/>。</returns>
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
    /// 请求桌面窗口管理器应用指定的原生窗口属性。
    /// </summary>
    /// <param name="windowHandle">目标窗口句柄。</param>
    /// <param name="attribute">DWM 属性编号。</param>
    /// <param name="attributeValue">属性值。</param>
    /// <param name="attributeSize">属性值大小。</param>
    /// <returns>HRESULT 状态码。</returns>
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);

    /// <summary>
    /// 判断原生窗口当前是否允许用户输入。
    /// </summary>
    /// <param name="windowHandle">目标窗口句柄。</param>
    /// <returns>窗口启用时返回 <see langword="true"/>。</returns>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr windowHandle);

}
