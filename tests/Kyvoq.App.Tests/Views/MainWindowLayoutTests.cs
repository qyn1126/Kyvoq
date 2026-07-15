using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Kyvoq.App.Controls;
using Kyvoq.App.Services;
using Kyvoq.App.ViewModels;
using Kyvoq.App.Views;
using Kyvoq.Core.Models;
using Kyvoq.Core.Persistence;
using Kyvoq.Core.Services;
using UiFluentWindow = Wpf.Ui.Controls.FluentWindow;
using UiMenuItem = Wpf.Ui.Controls.MenuItem;
using UiSymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using UiSymbolRegular = Wpf.Ui.Controls.SymbolRegular;
using UiVirtualizingWrapPanel = Wpf.Ui.Controls.VirtualizingWrapPanel;
using UiWindowBackdropType = Wpf.Ui.Controls.WindowBackdropType;
using UiWindowCornerPreference = Wpf.Ui.Controls.WindowCornerPreference;

namespace Kyvoq.App.Tests.Views;

/// <summary>
/// 禁止依赖全局 WPF Application 的界面测试与其他测试并行执行。
/// </summary>
[CollectionDefinition("WPF UI", DisableParallelization = true)]
public sealed class WpfUiCollection;

/// <summary>
/// 验证主窗口在完整、紧凑和极小尺寸下的响应式布局。
/// </summary>
[Collection("WPF UI")]
public sealed class MainWindowLayoutTests
{
    /// <summary>
    /// 验证主窗口响应式下限、弹窗和菜单布局，以及无动画呼出、可见时不重复定位和最大化恢复行为。
    /// </summary>
    [Fact]
    public void Resize_ShouldApplyResponsiveBreakpointsDownToMinimumSize()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                "Kyvoq.App.Tests",
                Guid.NewGuid().ToString("N"));
            MainViewModel? viewModel = null;
            IConfigurationStore? store = null;
            MainWindow? window = null;
            Window? hotkeyWindow = null;
            Window? focusWindow = null;
            Window? ownedWindow = null;
            SettingsWindow? settingsWindow = null;
            ItemEditorWindow? itemEditorWindow = null;
            TextInputDialog? textInputDialog = null;
            SearchWindow? searchWindow = null;
            App? application = null;
            var ownsApplication = false;
            try
            {
                if (Application.Current is null)
                {
                    application = new App();
                    application.InitializeComponent();
                    ownsApplication = true;
                }

                var configuration = LauncherConfiguration.CreateDefault();
                configuration.Groups[0].Items.Add(new LauncherItem
                {
                    Name = "Visual Studio Code",
                    Target = @"C:\Test.exe",
                    EnvironmentVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["KYVOQ_TEST"] = "value"
                    }
                });
                configuration.Groups.Add(new LauncherGroup
                {
                    Name = "第二组",
                    SortOrder = 1
                });
                store = new NoOpConfigurationStore();
                var iconCache = new IconCacheService(Path.Combine(temporaryDirectory, "icons"));
                var themeService = new ThemeService();
                themeService.ApplyApplicationTheme(configuration.Settings);
                var customAccentSettings = configuration.Settings.Clone();
                customAccentSettings.AccentMode = AccentMode.Custom;
                customAccentSettings.CustomAccentArgb = 0xFF336699;
                themeService.ApplyApplicationTheme(customAccentSettings);
                Assert.Equal(
                    Color.FromRgb(0x33, 0x66, 0x99),
                    Assert.IsType<Color>(Application.Current!.Resources["AccentColor"]));
                var solidSettings = configuration.Settings.Clone();
                solidSettings.WindowMaterial = WindowMaterial.Solid;
                themeService.ApplyApplicationTheme(solidSettings);
                Assert.Equal(
                    byte.MaxValue,
                    Assert.IsType<SolidColorBrush>(
                        Application.Current.Resources["WindowBackgroundBrush"]).Color.A);
                themeService.ApplyApplicationTheme(configuration.Settings);
                var windowBackground = Assert.IsType<SolidColorBrush>(
                    Application.Current.Resources["WindowBackgroundBrush"]);
                Assert.Equal(
                    Wpf.Ui.Controls.WindowBackdrop.IsSupported(UiWindowBackdropType.Mica)
                        ? byte.MinValue
                        : byte.MaxValue,
                    windowBackground.Color.A);
                Assert.InRange(
                    Assert.IsType<SolidColorBrush>(
                        Application.Current.Resources["ItemHoverBrush"]).Color.A,
                    (byte)1,
                    (byte)40);
                viewModel = new MainViewModel(configuration, store, iconCache);
                var launchService = new RecordingLaunchService();
                window = new MainWindow(
                    viewModel,
                    launchService,
                    store,
                    iconCache,
                    themeService,
                    temporaryDirectory)
                {
                    Left = -10000,
                    Top = -10000,
                    Opacity = 0,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };
                window.Show();

                window.Width = 920;
                window.Height = 620;
                window.UpdateLayout();
                var windowChrome = Assert.IsType<WindowChrome>(WindowChrome.GetWindowChrome(window));
                Assert.Equal(new CornerRadius(0), windowChrome.CornerRadius);
                Assert.IsType<Grid>(window.Content);
                Assert.Equal(196, ((ColumnDefinition)window.FindName("SidebarColumn")).ActualWidth, 1);
                Assert.Null(window.FindName("EmptyState"));
                Assert.Null(window.FindName("AddItemButton"));
                Assert.Null(window.FindName("MinimizeButton"));
                Assert.Null(window.FindName("SettingsButton"));
                Assert.Null(window.FindName("BrandHeader"));
                Assert.Null(window.FindName("BrandTextPanel"));
                Assert.Null(window.FindName("SectionHeader"));
                Assert.Null(window.FindName("SectionTitlePanel"));
                Assert.Null(window.FindName("SectionRow"));
                Assert.Null(window.FindName("HeaderSearchBox"));
                Assert.Null(window.FindName("CurrentGroupSearchTextBox"));
                var itemsHost = Assert.IsType<Grid>(window.FindName("ItemsHost"));
                Assert.Equal(1, Grid.GetRow(itemsHost));

                searchWindow = new SearchWindow(viewModel, launchService, themeService)
                {
                    Left = -10000,
                    Top = -10000,
                    Opacity = 0,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };
                searchWindow.ShowAndActivate();
                searchWindow.UpdateLayout();
                var searchResult = Assert.Single(searchWindow.Results);
                Assert.Equal(configuration.Groups[0].Name, searchResult.GroupName);
                Assert.Equal(configuration.Groups[0].Items[0].Target, searchResult.Target);
                searchWindow.Hide();
                window.ShowAndActivate();

                var toolTip = new ToolTip
                {
                    Content = "更多",
                    Style = Assert.IsType<Style>(Application.Current!.FindResource(typeof(ToolTip)))
                };
                toolTip.ApplyTemplate();
                var toolTipBorder = Assert.IsType<Border>(
                    toolTip.Template.FindName("ToolTipBorder", toolTip));
                var toolTipText = Assert.IsType<TextBlock>(
                    toolTip.Template.FindName("ToolTipText", toolTip));
                Assert.True(toolTipBorder.CornerRadius.TopLeft > 0);
                Assert.Equal(
                    toolTip.Foreground,
                    toolTipBorder.GetValue(TextElement.ForegroundProperty));
                Assert.Equal(toolTip.Foreground, toolTipText.Foreground);
                Assert.Equal("更多", toolTipText.Text);

                var addGroupButton = Assert.IsType<Button>(window.FindName("AddGroupButton"));
                var addGroupIcon = Assert.IsType<TextBlock>(window.FindName("AddGroupIcon"));
                Assert.Null(window.FindName("AddGroupText"));
                Assert.InRange(addGroupButton.ActualWidth, 170, 176);
                Assert.Equal(38, addGroupButton.ActualHeight, 1);
                var buttonCenter = addGroupButton.TranslatePoint(
                    new Point(addGroupButton.ActualWidth / 2, addGroupButton.ActualHeight / 2),
                    window);
                var iconCenter = addGroupIcon.TranslatePoint(
                    new Point(addGroupIcon.ActualWidth / 2, addGroupIcon.ActualHeight / 2),
                    window);
                Assert.Equal(buttonCenter.X, iconCenter.X, 1);
                Assert.Equal(buttonCenter.Y, iconCenter.Y, 1);

                var searchRequested = false;
                window.SearchPanelRequested += (_, _) => searchRequested = true;
                var searchAllButton = Assert.IsType<Button>(window.FindName("SearchAllButton"));
                var pinButton = Assert.IsType<ToggleButton>(window.FindName("PinButton"));
                var pinIcon = Assert.IsType<TextBlock>(window.FindName("PinIcon"));
                Assert.False(pinButton.IsChecked);
                Assert.Equal("固定面板", pinButton.ToolTip);
                Assert.Equal("\uE718", pinIcon.Text);
                Assert.Equal(38, pinButton.ActualWidth, 1);
                Assert.Equal(38, pinButton.ActualHeight, 1);
                var normalItemsPanel = Assert.IsType<UiVirtualizingWrapPanel>(
                    Assert.IsType<ItemsPanelTemplate>(window.FindResource("NormalItemsPanelTemplate"))
                        .LoadContent());
                Assert.Equal(new Size(104, 112), normalItemsPanel.ItemSize);
                Assert.Equal(Wpf.Ui.Controls.SpacingMode.None, normalItemsPanel.SpacingMode);
                var compactItemsPanel = Assert.IsType<UiVirtualizingWrapPanel>(
                    Assert.IsType<ItemsPanelTemplate>(window.FindResource("CompactItemsPanelTemplate"))
                        .LoadContent());
                var tinyItemsPanel = Assert.IsType<UiVirtualizingWrapPanel>(
                    Assert.IsType<ItemsPanelTemplate>(window.FindResource("TinyItemsPanelTemplate"))
                        .LoadContent());
                Assert.Equal(Wpf.Ui.Controls.SpacingMode.None, compactItemsPanel.SpacingMode);
                Assert.Equal(Wpf.Ui.Controls.SpacingMode.None, tinyItemsPanel.SpacingMode);
                searchAllButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(searchRequested);
                var moreButton = Assert.IsType<Button>(window.FindName("MoreButton"));
                var menu = Assert.IsType<ContextMenu>(moreButton.ContextMenu);
                Assert.Equal(PlacementMode.Custom, menu.Placement);
                Assert.NotNull(menu.CustomPopupPlacementCallback);
                var placements = menu.CustomPopupPlacementCallback(
                    new Size(180, 300),
                    new Size(38, 38),
                    new Point());
                Assert.Equal(2, placements.Length);
                var placement = placements[0];
                Assert.Equal(-142, placement.Point.X, 1);
                Assert.Equal(38, placement.Point.Y, 1);
                Assert.Equal(
                    ["导入配置…", "导出配置…", "添加项目…", "打开数据目录", "设置", "退出 Kyvoq"],
                    menu.Items.OfType<UiMenuItem>().Select(item => item.Header?.ToString()));
                menu.PlacementTarget = moreButton;
                menu.IsOpen = true;
                menu.UpdateLayout();
                Assert.InRange(menu.ActualWidth, 160, 320);
                AssertMenuAlignment(menu);
                var menuItems = menu.Items.OfType<UiMenuItem>().ToArray();
                Assert.Equal(
                    UiSymbolRegular.ArrowDownload24,
                    Assert.IsType<UiSymbolIcon>(menuItems[0].Icon).Symbol);
                Assert.Equal(
                    UiSymbolRegular.ArrowUpload24,
                    Assert.IsType<UiSymbolIcon>(menuItems[1].Icon).Symbol);
                menu.IsOpen = false;

                var items = (ListBox)window.FindName("ItemsListBox");
                var itemContainer = Assert.IsType<ListBoxItem>(items.ItemContainerGenerator.ContainerFromIndex(0));
                Assert.Null(items.FocusVisualStyle);
                Assert.Null(itemContainer.FocusVisualStyle);
                AssertTransparentIconFrame(itemContainer, "FullIconFrame");
                var tileSelectionBorder = Assert.Single(
                    FindVisualDescendants<Border>(itemContainer),
                    border => border.Name == "TileSelectionBorder");
                itemContainer.IsSelected = true;
                itemContainer.UpdateLayout();
                Assert.Equal(
                    byte.MinValue,
                    Assert.IsType<SolidColorBrush>(tileSelectionBorder.Background).Color.A);
                itemContainer.IsSelected = false;
                var itemScrollViewers = FindVisualDescendants<ScrollViewer>(items).ToArray();
                Assert.NotEmpty(itemScrollViewers);
                Assert.All(itemScrollViewers, scrollViewer => Assert.Null(scrollViewer.FocusVisualStyle));
                itemContainer.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent
                });
                itemContainer.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 1, MouseButton.Left)
                {
                    RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent
                });
                Assert.Equal(1, launchService.LaunchCount);
                var groups = Assert.IsType<ListBox>(window.FindName("GroupsListBox"));
                var groupTemplate = groups.ItemTemplate;
                var firstGroup = Assert.IsType<ListBoxItem>(
                    groups.ItemContainerGenerator.ContainerFromIndex(0));
                Assert.Null(groups.FocusVisualStyle);
                Assert.Null(firstGroup.FocusVisualStyle);
                var groupScrollViewers = FindVisualDescendants<ScrollViewer>(groups).ToArray();
                Assert.NotEmpty(groupScrollViewers);
                Assert.All(groupScrollViewers, scrollViewer => Assert.Null(scrollViewer.FocusVisualStyle));
                Assert.InRange(firstGroup.TranslatePoint(new Point(), window).Y, 0, 70);
                var firstGroupText = Assert.Single(
                    FindVisualDescendants<TextBlock>(firstGroup),
                    textBlock => textBlock.Text == configuration.Groups[0].Name);
                Assert.Equal(14, firstGroupText.FontSize, 1);
                Assert.Equal(TextAlignment.Center, firstGroupText.TextAlignment);
                var groupMenu = Assert.Single(
                    FindVisualDescendants<Border>(firstGroup)
                        .Select(border => border.ContextMenu)
                        .OfType<ContextMenu>());
                groupMenu.PlacementTarget = firstGroup;
                groupMenu.IsOpen = true;
                groupMenu.UpdateLayout();
                AssertMenuAlignment(groupMenu);
                groupMenu.IsOpen = false;
                var itemTemplateGrid = Assert.Single(
                    FindVisualDescendants<Grid>(itemContainer),
                    grid => grid.ContextMenu is not null);
                Assert.Equal(new Thickness(4), itemTemplateGrid.Margin);
                Assert.Equal(new Thickness(0, 0, 1, 1), itemContainer.Margin);
                var itemNameText = Assert.Single(
                    FindVisualDescendants<TextBlock>(itemContainer),
                    textBlock => textBlock.Text.Replace("\u200B", string.Empty, StringComparison.Ordinal)
                        == configuration.Groups[0].Items[0].Name);
                Assert.Equal(TextWrapping.Wrap, itemNameText.TextWrapping);
                Assert.Equal(TextTrimming.None, itemNameText.TextTrimming);
                Assert.Equal(HorizontalAlignment.Stretch, itemNameText.HorizontalAlignment);
                Assert.Equal(92, itemNameText.Width, 1);
                Assert.True(itemNameText.ActualWidth <= itemTemplateGrid.ActualWidth);
                Assert.True(
                    itemNameText.ActualHeight >= 27,
                    $"名称实际尺寸为 {itemNameText.ActualWidth:F1}×{itemNameText.ActualHeight:F1}，期望显示两行。");
                var itemMenu = Assert.IsType<ContextMenu>(itemTemplateGrid.ContextMenu);
                itemMenu.PlacementTarget = itemContainer;
                itemMenu.IsOpen = true;
                itemMenu.UpdateLayout();
                AssertMenuAlignment(itemMenu);
                itemMenu.IsOpen = false;
                Assert.Contains(
                    FindVisualDescendants<TextBlock>(firstGroup),
                    textBlock => textBlock.Text == configuration.Groups[0].Name);
                Assert.DoesNotContain(
                    FindVisualDescendants<TextBlock>(firstGroup),
                    textBlock => textBlock.Text == "\uE8B7");
                var secondGroup = Assert.IsType<ListBoxItem>(groups.ItemContainerGenerator.ContainerFromIndex(1));
                secondGroup.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 2)
                {
                    RoutedEvent = Mouse.MouseEnterEvent
                });
                Assert.Equal(configuration.Groups[1].Id, viewModel.SelectedGroup?.Id);

                window.Width = 570;
                window.Height = 500;
                window.UpdateLayout();
                Assert.Equal(112, ((ColumnDefinition)window.FindName("SidebarColumn")).ActualWidth, 1);
                Assert.InRange(addGroupButton.ActualWidth, 100, 108);
                firstGroup = Assert.IsType<ListBoxItem>(
                    groups.ItemContainerGenerator.ContainerFromIndex(0));
                Assert.Contains(
                    FindVisualDescendants<TextBlock>(firstGroup),
                    textBlock => textBlock.Text == configuration.Groups[0].Name);
                Assert.DoesNotContain(
                    FindVisualDescendants<TextBlock>(firstGroup),
                    textBlock => textBlock.Text == "\uE8B7");
                buttonCenter = addGroupButton.TranslatePoint(
                    new Point(addGroupButton.ActualWidth / 2, addGroupButton.ActualHeight / 2),
                    window);
                iconCenter = addGroupIcon.TranslatePoint(
                    new Point(addGroupIcon.ActualWidth / 2, addGroupIcon.ActualHeight / 2),
                    window);
                Assert.Equal(buttonCenter.X, iconCenter.X, 1);
                Assert.Equal(buttonCenter.Y, iconCenter.Y, 1);

                viewModel.SelectedGroup = viewModel.Groups[0];
                window.Width = 376;
                window.Height = 500;
                window.UpdateLayout();
                Assert.Equal(72, ((ColumnDefinition)window.FindName("SidebarColumn")).ActualWidth, 1);
                Assert.Same(groupTemplate, groups.ItemTemplate);
                firstGroup = Assert.IsType<ListBoxItem>(
                    groups.ItemContainerGenerator.ContainerFromIndex(0));
                firstGroupText = Assert.Single(
                    FindVisualDescendants<TextBlock>(firstGroup),
                    textBlock => textBlock.Text == configuration.Groups[0].Name);
                Assert.Equal(14, firstGroupText.FontSize, 1);
                Assert.Equal(TextAlignment.Center, firstGroupText.TextAlignment);
                Assert.InRange(addGroupButton.ActualWidth, 60, 68);
                itemContainer = Assert.IsType<ListBoxItem>(
                    items.ItemContainerGenerator.ContainerFromIndex(0));
                Assert.True(itemContainer.ActualWidth > 0);
                Assert.True(itemContainer.ActualHeight > 0);
                AssertTransparentIconFrame(itemContainer, "CompactIconFrame");

                window.Width = 100;
                window.Height = 100;
                window.UpdateLayout();
                Assert.Equal(100, window.MinWidth, 1);
                Assert.Equal(100, window.MinHeight, 1);
                Assert.Equal(100, window.ActualWidth, 1);
                Assert.Equal(100, window.ActualHeight, 1);
                Assert.Equal(44, ((ColumnDefinition)window.FindName("SidebarColumn")).ActualWidth, 1);
                Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("MoreButton")).Visibility);
                Assert.Equal(Visibility.Visible, pinButton.Visibility);
                Assert.True(pinButton.ActualWidth > 0);
                Assert.True(pinButton.ActualHeight > 0);
                Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("SearchAllButton")).Visibility);
                itemContainer = Assert.IsType<ListBoxItem>(
                    items.ItemContainerGenerator.ContainerFromIndex(0));
                Assert.True(itemContainer.ActualWidth > 0);
                Assert.True(itemContainer.ActualHeight > 0);

                var hotkeyBox = new HotkeyBox();
                hotkeyBox.SetGesture(HotkeyGesture.CreateDefaultMainWindow());
                hotkeyWindow = new Window
                {
                    Content = hotkeyBox,
                    SizeToContent = SizeToContent.WidthAndHeight,
                    Left = -10000,
                    Top = -10000,
                    Opacity = 0,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };
                hotkeyWindow.Show();
                hotkeyWindow.UpdateLayout();
                var contentHost = Assert.IsAssignableFrom<ScrollViewer>(
                    hotkeyBox.Template.FindName("PART_ContentHost", hotkeyBox));
                var text = new FormattedText(
                    hotkeyBox.Text,
                    CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,
                    new Typeface(
                        hotkeyBox.FontFamily,
                        hotkeyBox.FontStyle,
                        hotkeyBox.FontWeight,
                        hotkeyBox.FontStretch),
                    hotkeyBox.FontSize,
                    Brushes.Black,
                    VisualTreeHelper.GetDpi(hotkeyBox).PixelsPerDip);
                Assert.True(contentHost.ActualHeight >= text.Height - 0.5);

                textInputDialog = new TextInputDialog(
                    "新建分组",
                    "分组名称",
                    string.Empty,
                    themeService,
                    AppTheme.Light,
                    WindowMaterial.Mica)
                {
                    Owner = window,
                    Left = -10000,
                    Top = -10000,
                    Opacity = 0,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };
                Assert.False(textInputDialog.IsLoaded);
                themeService.ApplyWindowBackdrop(
                    textInputDialog,
                    AppTheme.Light,
                    WindowMaterial.Mica);
                textInputDialog.Show();
                textInputDialog.UpdateLayout();
                var fluentTextInputDialog = Assert.IsAssignableFrom<UiFluentWindow>(textInputDialog);
                Assert.True(fluentTextInputDialog.ExtendsContentIntoTitleBar);
                Assert.Equal(UiWindowBackdropType.Mica, fluentTextInputDialog.WindowBackdropType);
                Assert.Equal(UiWindowCornerPreference.Round, fluentTextInputDialog.WindowCornerPreference);
                Assert.Equal(400, fluentTextInputDialog.MinWidth, 1);
                Assert.Equal(210, fluentTextInputDialog.MinHeight, 1);
                Assert.Equal(400, fluentTextInputDialog.ActualWidth, 1);
                Assert.Equal(210, fluentTextInputDialog.ActualHeight, 1);
                var textInputRoot = Assert.IsType<Grid>(fluentTextInputDialog.Content);
                Assert.Equal(
                    byte.MinValue,
                    Assert.IsType<SolidColorBrush>(textInputRoot.Background).Color.A);
                textInputDialog.Close();
                textInputDialog = null;

                settingsWindow = new SettingsWindow(
                    configuration.Settings,
                    temporaryDirectory,
                    new AvailableHotkeyService(),
                    themeService)
                {
                    Owner = window,
                    Left = -10000,
                    Top = -10000,
                    Opacity = 0,
                    ShowActivated = false,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };
                settingsWindow.Show();
                settingsWindow.UpdateLayout();
                var mainWindowHotkey = Assert.IsType<HotkeyBox>(
                    settingsWindow.FindName("MainWindowHotkeyInput"));
                var materialComboBox = Assert.IsType<ComboBox>(
                    settingsWindow.FindName("MaterialComboBox"));
                Assert.Equal(
                    WindowMaterial.Mica.ToString(),
                    Assert.IsType<ComboBoxItem>(materialComboBox.SelectedItem).Tag?.ToString());
                Assert.Equal("Alt+Space", mainWindowHotkey.Text);
                Assert.Equal(560, settingsWindow.ActualWidth, 1);
                Assert.InRange(settingsWindow.ActualHeight, 1, 760);

                itemEditorWindow = new ItemEditorWindow(
                    configuration.Groups[0].Items[0],
                    isNew: false,
                    iconCache,
                    new AvailableHotkeyService(),
                    themeService,
                    configuration.Settings.Theme,
                    configuration.Settings.WindowMaterial)
                {
                    Owner = window,
                    Left = -10000,
                    Top = -10000,
                    Opacity = 0,
                    ShowActivated = false,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };
                itemEditorWindow.Show();
                itemEditorWindow.UpdateLayout();
                var focusableEditorScrollViewers = FindVisualDescendants<ScrollViewer>(itemEditorWindow)
                    .Where(scrollViewer => scrollViewer.Focusable)
                    .ToArray();
                Assert.NotEmpty(focusableEditorScrollViewers);
                Assert.Contains(focusableEditorScrollViewers, scrollViewer => scrollViewer.IsKeyboardFocusWithin);
                Assert.All(
                    focusableEditorScrollViewers,
                    scrollViewer => Assert.Null(scrollViewer.FocusVisualStyle));
                var itemHotkey = Assert.IsType<HotkeyBox>(itemEditorWindow.FindName("HotkeyInput"));
                Assert.Null(itemEditorWindow.FindName("WorkingDirectoryTextBox"));
                var environmentVariables = Assert.IsType<ItemsControl>(
                    itemEditorWindow.FindName("EnvironmentVariablesList"));
                Assert.Single(environmentVariables.Items);
                var addEnvironmentVariableButton = Assert.IsType<Button>(
                    itemEditorWindow.FindName("AddEnvironmentVariableButton"));
                addEnvironmentVariableButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                itemEditorWindow.UpdateLayout();
                Assert.Equal(2, environmentVariables.Items.Count);
                var addedEnvironmentRow = Assert.IsAssignableFrom<DependencyObject>(
                    environmentVariables.ItemContainerGenerator.ContainerFromIndex(1));
                var deleteEnvironmentVariableButton = Assert.Single(
                    FindVisualDescendants<Button>(addedEnvironmentRow),
                    button => string.Equals(button.ToolTip?.ToString(), "删除环境变量", StringComparison.Ordinal));
                deleteEnvironmentVariableButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Single(environmentVariables.Items);
                Assert.True(itemHotkey.ActualHeight >= 36);
                Assert.Equal(680, itemEditorWindow.ActualWidth, 1);
                Assert.Equal(700, itemEditorWindow.ActualHeight, 1);

                itemEditorWindow.Close();
                itemEditorWindow = null;
                settingsWindow.Close();
                settingsWindow = null;

                window.ShowAndActivate();
                Assert.False(window.HasAnimatedProperties);
                var visibleLeft = window.Left;
                var visibleTop = window.Top;
                window.ShowAndActivate();
                Assert.Equal(visibleLeft, window.Left);
                Assert.Equal(visibleTop, window.Top);
                pinButton.IsChecked = true;
                pinButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal("取消固定", pinButton.ToolTip);
                Assert.Equal("\uE77A", pinIcon.Text);
                Assert.True(window.Topmost);
                focusWindow = CreateFocusWindow();
                focusWindow.Show();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                focusWindow.Topmost = true;
                focusWindow.Activate();
                focusWindow.Focus();
                focusWindow.Topmost = false;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.True(window.IsVisible);

                var closeButton = Assert.IsType<Button>(window.FindName("CloseButton"));
                closeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(window.IsVisible);
                window.ShowAndActivate();
                Assert.True(pinButton.IsChecked);
                Assert.True(window.Topmost);

                var escapeEvent = new KeyEventArgs(
                    Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(window),
                    Environment.TickCount,
                    Key.Escape)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent
                };
                window.RaiseEvent(escapeEvent);
                Assert.False(window.IsVisible);
                Assert.True(pinButton.IsChecked);

                window.ShowAndActivate();
                pinButton.IsChecked = false;
                pinButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal("固定面板", pinButton.ToolTip);
                Assert.False(window.Topmost);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                focusWindow.Topmost = true;
                focusWindow.Activate();
                focusWindow.Focus();
                focusWindow.Topmost = false;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.False(window.IsVisible);

                window.ShowAndActivate();
                ownedWindow = CreateFocusWindow();
                ownedWindow.Owner = window;
                ownedWindow.Show();
                ownedWindow.Activate();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.True(window.IsVisible);
                ownedWindow.Close();
                ownedWindow = null;

                pinButton.IsChecked = true;
                pinButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

                var toggleVisibility = typeof(MainWindow).GetMethod("ToggleVisibility");
                Assert.NotNull(toggleVisibility);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                focusWindow.Topmost = true;
                focusWindow.Activate();
                focusWindow.Focus();
                focusWindow.Topmost = false;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.True(window.IsVisible);
                toggleVisibility.Invoke(window, null);
                Assert.False(window.IsVisible);

                window.ShowActivated = true;
                window.WindowState = WindowState.Maximized;
                window.ShowAndActivate();
                Assert.Equal(WindowState.Maximized, window.WindowState);
                Assert.False(window.HasAnimatedProperties);
                window.Hide();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                searchWindow?.ClosePermanently();
                textInputDialog?.Close();
                itemEditorWindow?.Close();
                settingsWindow?.Close();
                hotkeyWindow?.Close();
                ownedWindow?.Close();
                focusWindow?.Close();
                window?.ClosePermanently();
                viewModel?.Dispose();
                if (ownsApplication)
                {
                    application?.Shutdown();
                }

                if (Directory.Exists(temporaryDirectory))
                {
                    Directory.Delete(temporaryDirectory, recursive: true);
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        Assert.Null(failure);
    }

    /// <summary>
    /// 验证弹出菜单使用一致的行高以及固定尺寸的居中图标槽。
    /// </summary>
    /// <param name="menu">需要验证的弹出菜单。</param>
    private static void AssertMenuAlignment(ContextMenu menu)
    {
        Assert.All(menu.Items.OfType<UiMenuItem>(), item =>
        {
            Assert.True(item.ActualHeight >= 38);
            var icon = Assert.IsType<UiSymbolIcon>(item.Icon);
            Assert.Equal(18, icon.ActualWidth, 1);
            Assert.Equal(18, icon.ActualHeight, 1);
            Assert.Equal(VerticalAlignment.Center, icon.VerticalAlignment);
            Assert.Equal(HorizontalAlignment.Center, icon.HorizontalAlignment);
            var opticalOffset = Assert.IsType<TranslateTransform>(icon.RenderTransform);
            Assert.Equal(-1, opticalOffset.Y, 1);
        });
    }

    /// <summary>
    /// 验证启动项目图标框不再绘制白色背景或边框。
    /// </summary>
    /// <param name="parent">包含图标框的项目容器。</param>
    /// <param name="frameName">待查找的图标框名称。</param>
    private static void AssertTransparentIconFrame(DependencyObject parent, string frameName)
    {
        var frame = Assert.Single(
            FindVisualDescendants<Border>(parent),
            border => border.Name == frameName);
        Assert.Equal(new Thickness(0), frame.BorderThickness);
        Assert.Equal(
            byte.MinValue,
            Assert.IsType<SolidColorBrush>(frame.Background).Color.A);
    }

    /// <summary>
    /// 创建用于切换窗口激活状态的透明测试窗口。
    /// </summary>
    /// <returns>位于屏幕外且不会显示在任务栏中的窗口。</returns>
    private static Window CreateFocusWindow() => new()
    {
        Width = 80,
        Height = 80,
        Left = -10000,
        Top = -10000,
        Opacity = 0,
        ShowInTaskbar = false,
        Style = new Style(typeof(Window)),
        WindowStyle = WindowStyle.None,
        WindowStartupLocation = WindowStartupLocation.Manual
    };

    /// <summary>
    /// 枚举指定视觉节点下所有给定类型的后代元素。
    /// </summary>
    /// <typeparam name="T">需要查找的视觉元素类型。</typeparam>
    /// <param name="parent">视觉树查找起点。</param>
    /// <returns>按视觉树顺序返回匹配的后代元素。</returns>
    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>
    /// 记录界面发出的启动请求而不创建真实进程。
    /// </summary>
    private sealed class RecordingLaunchService : ILaunchService
    {
        public int LaunchCount { get; private set; }

        /// <summary>
        /// 记录一次项目启动并返回成功结果。
        /// </summary>
        /// <param name="item">被启动的项目。</param>
        /// <param name="cancellationToken">测试取消令牌。</param>
        /// <returns>包含固定成功结果的任务。</returns>
        public Task<LaunchResult> LaunchAsync(
            LauncherItem item,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(item);
            cancellationToken.ThrowIfCancellationRequested();
            LaunchCount++;
            return Task.FromResult(LaunchResult.Success());
        }

        /// <summary>
        /// 创建测试所需的最小进程启动信息。
        /// </summary>
        /// <param name="item">待转换项目。</param>
        /// <returns>包含项目目标的启动信息。</returns>
        public ProcessStartInfo CreateStartInfo(LauncherItem item)
        {
            ArgumentNullException.ThrowIfNull(item);
            return new ProcessStartInfo(item.Target);
        }
    }

    /// <summary>
    /// 为布局测试提供不访问磁盘的配置存储替身。
    /// </summary>
    private sealed class NoOpConfigurationStore : IConfigurationStore
    {
        /// <summary>
        /// 返回默认配置加载结果。
        /// </summary>
        /// <param name="cancellationToken">测试取消令牌。</param>
        /// <returns>默认配置加载结果。</returns>
        public Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConfigurationLoadResult(
                LauncherConfiguration.CreateDefault(),
                ConfigurationLoadState.CreatedDefault,
                string.Empty));

        /// <summary>
        /// 模拟配置保存成功。
        /// </summary>
        /// <param name="configuration">待保存配置。</param>
        /// <param name="cancellationToken">测试取消令牌。</param>
        /// <returns>已完成任务。</returns>
        public Task SaveAsync(
            LauncherConfiguration configuration,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// 模拟配置导出成功。
        /// </summary>
        /// <param name="configuration">待导出配置。</param>
        /// <param name="destinationPath">导出路径。</param>
        /// <param name="cancellationToken">测试取消令牌。</param>
        /// <returns>已完成任务。</returns>
        public Task ExportAsync(
            LauncherConfiguration configuration,
            string destinationPath,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// 返回默认导入配置。
        /// </summary>
        /// <param name="sourcePath">导入路径。</param>
        /// <param name="cancellationToken">测试取消令牌。</param>
        /// <returns>默认配置。</returns>
        public Task<LauncherConfiguration> ImportAsync(
            string sourcePath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(LauncherConfiguration.CreateDefault());
    }

    /// <summary>
    /// 为设置窗口提供始终可用的快捷键检测替身。
    /// </summary>
    private sealed class AvailableHotkeyService : IGlobalHotkeyService
    {
        public event EventHandler<Guid>? Invoked
        {
            add { }
            remove { }
        }

        /// <summary>
        /// 返回主界面快捷键注册成功的固定结果。
        /// </summary>
        /// <param name="settings">待应用设置。</param>
        /// <param name="items">待注册项目。</param>
        /// <returns>无冲突的注册结果。</returns>
        public HotkeyRegistrationReport Apply(AppSettings settings, IEnumerable<LauncherItem> items)
        {
            ArgumentNullException.ThrowIfNull(settings);
            ArgumentNullException.ThrowIfNull(items);
            return new HotkeyRegistrationReport(true, new HashSet<Guid>());
        }

        /// <summary>
        /// 将所有有效快捷键视为可用。
        /// </summary>
        /// <param name="gesture">待检测快捷键。</param>
        /// <param name="excludedActionId">需要排除的动作标识。</param>
        /// <returns>快捷键有效时返回 <see langword="true"/>。</returns>
        public bool IsAvailable(HotkeyGesture gesture, Guid? excludedActionId = null)
        {
            ArgumentNullException.ThrowIfNull(gesture);
            return gesture.IsValid();
        }

        /// <summary>
        /// 释放测试替身；该实现没有持有资源。
        /// </summary>
        public void Dispose()
        {
        }
    }
}
