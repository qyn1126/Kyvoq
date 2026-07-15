using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Kyvoq.App.Services;
using Kyvoq.App.ViewModels;
using Kyvoq.App.Views;
using Kyvoq.Core.Models;
using Kyvoq.Core.Persistence;
using Kyvoq.Core.Services;

namespace Kyvoq.App;

/// <summary>
/// 负责 Kyvoq 单实例启动、服务装配、托盘驻留和有序退出。
/// </summary>
public partial class App : Application
{
    private SingleInstanceService? singleInstanceService;
    private JsonConfigurationStore? configurationStore;
    private IconCacheService? iconCacheService;
    private MainViewModel? mainViewModel;
    private MainWindow? mainWindow;
    private SearchWindow? searchWindow;
    private GlobalHotkeyService? hotkeyService;
    private TrayIconService? trayIconService;
    private ThemeService? themeService;
    private WindowsStartupService? startupService;
    private ILaunchService? launchService;
    private bool exiting;
    private bool mainWindowConflictNotified;

    /// <summary>
    /// 初始化单实例通信、配置、窗口、托盘和全局快捷键。
    /// </summary>
    /// <param name="eventArgs">应用启动参数。</param>
    protected override async void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);
        if (ElevatedLaunchHost.IsRequest(eventArgs.Args))
        {
            var exitCode = await ElevatedLaunchHost.RunAsync(eventArgs.Args);
            Shutdown(exitCode);
            return;
        }

        DispatcherUnhandledException += HandleDispatcherUnhandledException;

        singleInstanceService = new SingleInstanceService("Kyvoq.FastLauncher");
        if (!singleInstanceService.IsPrimaryInstance())
        {
            await singleInstanceService.NotifyPrimaryAsync();
            Shutdown();
            return;
        }

        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kyvoq");
        configurationStore = new JsonConfigurationStore(dataDirectory);
        var loadResult = await configurationStore.LoadAsync();
        themeService = new ThemeService();
        themeService.ApplyApplicationTheme(loadResult.Configuration.Settings);
        SystemEvents.UserPreferenceChanged += HandleUserPreferenceChanged;
        iconCacheService = new IconCacheService(Path.Combine(dataDirectory, "IconCache"));
        mainViewModel = new MainViewModel(loadResult.Configuration, configurationStore, iconCacheService);
        launchService = new LaunchService(new ElevatedLaunchBroker());
        startupService = new WindowsStartupService();

        mainWindow = new MainWindow(
            mainViewModel,
            launchService,
            configurationStore,
            iconCacheService,
            themeService,
            dataDirectory);
        MainWindow = mainWindow;
        hotkeyService = new GlobalHotkeyService(mainWindow);
        mainWindow.AttachHotkeyService(hotkeyService);
        searchWindow = new SearchWindow(mainViewModel, launchService, themeService);
        trayIconService = new TrayIconService(loadResult.Configuration.Settings.ItemHotkeysEnabled);

        WireEvents();
        ApplySystemSettings(showErrors: false);
        RefreshHotkeys();
        singleInstanceService.StartListening(() =>
            _ = Dispatcher.BeginInvoke(mainWindow.ShowAndActivate));
        _ = iconCacheService.TrimAsync();

        var startInBackground = eventArgs.Args.Any(
            argument => string.Equals(argument, "--background", StringComparison.OrdinalIgnoreCase));
        if (!startInBackground)
        {
            mainWindow.ShowAndActivate();
        }

        if (loadResult.Message.Length > 0)
        {
            MessageBox.Show(
                mainWindow,
                loadResult.Message,
                "Kyvoq 配置恢复",
                MessageBoxButton.OK,
                loadResult.State == ConfigurationLoadState.RecoveredFromBackup
                    ? MessageBoxImage.Warning
                    : MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 在进程退出时兜底释放原生和托盘资源。
    /// </summary>
    /// <param name="eventArgs">应用退出参数。</param>
    protected override void OnExit(ExitEventArgs eventArgs)
    {
        DispatcherUnhandledException -= HandleDispatcherUnhandledException;
        SystemEvents.UserPreferenceChanged -= HandleUserPreferenceChanged;
        trayIconService?.Dispose();
        hotkeyService?.Dispose();
        mainViewModel?.Dispose();
        configurationStore?.Dispose();
        singleInstanceService?.Dispose();
        base.OnExit(eventArgs);
    }

    /// <summary>
    /// 连接窗口、托盘、视图模型和全局快捷键事件。
    /// </summary>
    private void WireEvents()
    {
        if (mainWindow is null
            || mainViewModel is null
            || hotkeyService is null
            || trayIconService is null)
        {
            throw new InvalidOperationException("应用服务尚未完成初始化。");
        }

        mainWindow.SearchPanelRequested += (_, _) => searchWindow?.ShowAndActivate();
        mainWindow.SettingsChanged += (_, _) => HandleSettingsChanged();
        mainWindow.ExitRequested += async (_, _) => await ExitAsync();
        mainViewModel.HotkeyConfigurationChanged += (_, _) => RefreshHotkeys();
        hotkeyService.Invoked += HandleHotkeyInvoked;
        trayIconService.ShowRequested += (_, _) => mainWindow.ShowAndActivate();
        trayIconService.ToggleHotkeysRequested += (_, _) => ToggleItemHotkeys();
        trayIconService.ExitRequested += async (_, _) => await ExitAsync();
    }

    /// <summary>
    /// 处理项目或主界面的全局快捷键动作。
    /// </summary>
    /// <param name="sender">全局快捷键服务。</param>
    /// <param name="actionId">项目标识；空标识表示呼出主界面。</param>
    private async void HandleHotkeyInvoked(object? sender, Guid actionId)
    {
        if (actionId == GlobalHotkeyService.MainWindowActionId)
        {
            mainWindow?.ToggleVisibility();
            return;
        }

        var item = mainViewModel?.FindItem(actionId)?.Item.Model;
        if (item is null || launchService is null)
        {
            return;
        }

        var result = await launchService.LaunchAsync(item);
        if (!result.IsSuccessful)
        {
            MessageBox.Show(
                mainWindow,
                result.ErrorMessage,
                "启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 重新注册主界面快捷键和全部启用的项目快捷键。
    /// </summary>
    private void RefreshHotkeys()
    {
        if (hotkeyService is null || mainViewModel is null)
        {
            return;
        }

        var report = hotkeyService.Apply(
            mainViewModel.Configuration.Settings,
            mainViewModel.GetAllItems().Select(item => item.Model));
        mainViewModel.SetHotkeyConflicts(report.ConflictingItemIds);
        if (!report.MainWindowRegistered && !mainWindowConflictNotified)
        {
            mainWindowConflictNotified = true;
            _ = Dispatcher.BeginInvoke(() => MessageBox.Show(
                mainWindow,
                "呼出主界面的快捷键已被其他程序占用，请在设置中更换。",
                "快捷键冲突",
                MessageBoxButton.OK,
                MessageBoxImage.Warning));
        }
        else if (report.MainWindowRegistered)
        {
            mainWindowConflictNotified = false;
        }
    }

    /// <summary>
    /// 应用设置窗口产生的主题、开机启动、托盘和快捷键变化。
    /// </summary>
    private void HandleSettingsChanged()
    {
        if (themeService is not null && mainViewModel is not null)
        {
            themeService.ApplyApplicationTheme(mainViewModel.Configuration.Settings);
        }
        searchWindow?.RefreshTheme();
        ApplySystemSettings(showErrors: true);
    }

    /// <summary>
    /// 根据当前设置更新开机启动项和托盘菜单。
    /// </summary>
    /// <param name="showErrors">写入系统设置失败时是否提示用户。</param>
    private void ApplySystemSettings(bool showErrors)
    {
        if (mainViewModel is null)
        {
            return;
        }

        var settings = mainViewModel.Configuration.Settings;
        trayIconService?.SetItemHotkeysEnabled(settings.ItemHotkeysEnabled);
        try
        {
            startupService?.SetEnabled(settings.StartWithWindows);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or InvalidOperationException)
        {
            if (showErrors)
            {
                MessageBox.Show(
                    mainWindow,
                    $"无法更新开机启动项：{exception.Message}",
                    "Kyvoq",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }

    /// <summary>
    /// 从托盘切换项目级全局快捷键，并立即保存和重新注册。
    /// </summary>
    private void ToggleItemHotkeys()
    {
        if (mainViewModel is null)
        {
            return;
        }

        var settings = mainViewModel.Configuration.Settings.Clone();
        settings.ItemHotkeysEnabled = !settings.ItemHotkeysEnabled;
        mainViewModel.UpdateSettings(settings);
        trayIconService?.SetItemHotkeysEnabled(settings.ItemHotkeysEnabled);
    }

    /// <summary>
    /// 完成配置刷新、窗口关闭和原生资源释放后退出进程。
    /// </summary>
    /// <returns>退出准备完成时结束的任务。</returns>
    private async Task ExitAsync()
    {
        if (exiting)
        {
            return;
        }

        exiting = true;
        searchWindow?.ClosePermanently();
        mainWindow?.ClosePermanently();
        try
        {
            if (mainViewModel is not null)
            {
                await mainViewModel.FlushSaveAsync();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(
                mainWindow,
                $"退出前保存配置失败：{exception.Message}",
                "Kyvoq",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        trayIconService?.Dispose();
        hotkeyService?.Dispose();
        Shutdown();
    }

    /// <summary>
    /// 在主题或强调色跟随系统时响应 Windows 个性化变化。
    /// </summary>
    /// <param name="sender">系统事件源。</param>
    /// <param name="eventArgs">用户偏好变化参数。</param>
    private void HandleUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs eventArgs)
    {
        var settings = mainViewModel?.Configuration.Settings;
        if (settings is null
            || settings.Theme != AppTheme.System && settings.AccentMode != AccentMode.System)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(() =>
        {
            if (themeService is not null && mainViewModel is not null)
            {
                themeService.ApplyApplicationTheme(mainViewModel.Configuration.Settings);
            }
            if (mainWindow is not null && themeService is not null && settings.Theme == AppTheme.System)
            {
                themeService.ApplyWindowBackdrop(
                    mainWindow,
                    AppTheme.System,
                    settings.WindowMaterial);
            }

            searchWindow?.RefreshTheme();
        });
    }

    /// <summary>
    /// 捕获未处理的界面线程异常，提示用户并执行有序退出。
    /// </summary>
    /// <param name="sender">WPF 应用。</param>
    /// <param name="eventArgs">未处理异常参数。</param>
    private void HandleDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs eventArgs)
    {
        MessageBox.Show(
            mainWindow,
            $"Kyvoq 遇到未预期错误：{eventArgs.Exception.Message}",
            "Kyvoq",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        eventArgs.Handled = true;
        _ = ExitAsync();
    }
}
