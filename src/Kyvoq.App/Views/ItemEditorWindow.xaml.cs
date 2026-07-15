using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Kyvoq.App.Services;
using Kyvoq.Core.Models;
using Kyvoq.Core.Services;
using Wpf.Ui.Controls;

namespace Kyvoq.App.Views;

/// <summary>
/// 编辑启动目标、参数、环境变量、图标、权限和全局快捷键。
/// </summary>
public partial class ItemEditorWindow : FluentWindow
{
    /// <summary>
    /// 表示编辑器中的一行环境变量键值。
    /// </summary>
    private sealed class EnvironmentVariableEntry
    {
        public string Key { get; set; } = string.Empty;

        public string Value { get; set; } = string.Empty;
    }

    private readonly LauncherItem sourceItem;
    private readonly IconCacheService iconCache;
    private readonly IGlobalHotkeyService hotkeyService;
    private readonly ThemeService themeService;
    private readonly AppTheme theme;
    private readonly WindowMaterial material;
    private readonly bool isNew;
    private readonly ObservableCollection<EnvironmentVariableEntry> environmentVariables = [];
    private string customIconPath;

    public LauncherItem? ResultItem { get; private set; }

    /// <summary>
    /// 创建启动项目编辑窗口。
    /// </summary>
    /// <param name="item">待编辑项目；新项目可传入空白模型。</param>
    /// <param name="isNew">是否正在创建新项目。</param>
    /// <param name="iconCache">图标缓存服务。</param>
    /// <param name="hotkeyService">快捷键冲突检测服务。</param>
    /// <param name="themeService">主题服务。</param>
    /// <param name="theme">当前主题。</param>
    /// <param name="material">当前窗口材质。</param>
    public ItemEditorWindow(
        LauncherItem item,
        bool isNew,
        IconCacheService iconCache,
        IGlobalHotkeyService hotkeyService,
        ThemeService themeService,
        AppTheme theme,
        WindowMaterial material)
    {
        InitializeComponent();
        sourceItem = item?.Clone() ?? throw new ArgumentNullException(nameof(item));
        this.isNew = isNew;
        this.iconCache = iconCache;
        this.hotkeyService = hotkeyService;
        this.themeService = themeService;
        this.theme = theme;
        this.material = material;
        customIconPath = sourceItem.CustomIconPath;

        Title = isNew ? "新建启动项目" : "编辑启动项目";
        NameTextBox.Text = sourceItem.Name;
        TargetTextBox.Text = sourceItem.Target;
        ArgumentsTextBox.Text = sourceItem.Arguments;
        EnvironmentVariablesList.ItemsSource = environmentVariables;
        foreach (var (key, value) in sourceItem.EnvironmentVariables)
        {
            environmentVariables.Add(new EnvironmentVariableEntry { Key = key, Value = value });
        }

        RunAsAdministratorCheckBox.IsChecked = sourceItem.RunAsAdministrator;
        HotkeyInput.SetGesture(sourceItem.Hotkey);
        UpdateTargetType();
        UpdateInitial();
        UpdateEnvironmentEmptyState();
        SourceInitialized += HandleSourceInitialized;
        Loaded += HandleLoaded;
        _ = RefreshIconPreviewAsync();
    }

    /// <summary>
    /// 应用窗口 DWM 外观。
    /// </summary>
    /// <param name="sender">当前窗口。</param>
    /// <param name="eventArgs">事件参数。</param>
    private void HandleSourceInitialized(object? sender, EventArgs eventArgs) =>
        themeService.ApplyWindowBackdrop(this, theme, material);

    /// <summary>
    /// 窗口显示后聚焦最合适的输入框。
    /// </summary>
    /// <param name="sender">当前窗口。</param>
    /// <param name="eventArgs">事件参数。</param>
    private void HandleLoaded(object sender, RoutedEventArgs eventArgs)
    {
        if (NameTextBox.Text.Length == 0)
        {
            NameTextBox.Focus();
        }
        else
        {
            TargetTextBox.Focus();
        }
    }

    /// <summary>
    /// 选择程序、快捷方式或普通文件作为启动目标。
    /// </summary>
    /// <param name="sender">浏览按钮。</param>
    /// <param name="eventArgs">事件参数。</param>
    private void BrowseTarget_Click(object sender, RoutedEventArgs eventArgs)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择启动目标",
            Filter = "常用目标|*.exe;*.com;*.lnk;*.url;*.bat;*.cmd|所有文件|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        TargetTextBox.Text = dialog.FileName;
        if (string.IsNullOrWhiteSpace(NameTextBox.Text))
        {
            NameTextBox.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
        }

        _ = RefreshIconPreviewAsync();
    }

    /// <summary>
    /// 选择 PNG、JPEG 或 ICO 文件作为自定义图标。
    /// </summary>
    /// <param name="sender">修改图标按钮。</param>
    /// <param name="eventArgs">事件参数。</param>
    private void BrowseIcon_Click(object sender, RoutedEventArgs eventArgs)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择自定义图标",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.ico|所有文件|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            customIconPath = dialog.FileName;
            _ = RefreshIconPreviewAsync();
        }
    }

    /// <summary>
    /// 清除自定义图标并恢复目标的关联图标。
    /// </summary>
    /// <param name="sender">默认图标按钮。</param>
    /// <param name="eventArgs">事件参数。</param>
    private void ClearIcon_Click(object sender, RoutedEventArgs eventArgs)
    {
        customIconPath = string.Empty;
        _ = RefreshIconPreviewAsync();
    }

    /// <summary>
    /// 根据目标文本切换可用的程序专属选项。
    /// </summary>
    /// <param name="sender">目标输入框。</param>
    /// <param name="eventArgs">文本变化事件参数。</param>
    private void TargetTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs eventArgs) =>
        UpdateTargetType();

    /// <summary>
    /// 目标输入完成后刷新图标预览。
    /// </summary>
    /// <param name="sender">目标输入框。</param>
    /// <param name="eventArgs">焦点变化事件参数。</param>
    private void TargetTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs eventArgs) =>
        _ = RefreshIconPreviewAsync();

    /// <summary>
    /// 名称变化时更新无图标状态的首字母。
    /// </summary>
    /// <param name="sender">名称输入框。</param>
    /// <param name="eventArgs">文本变化事件参数。</param>
    private void NameTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs eventArgs) =>
        UpdateInitial();

    /// <summary>
    /// 在环境变量列表末尾添加一行空白键值。
    /// </summary>
    /// <param name="sender">添加变量按钮。</param>
    /// <param name="eventArgs">事件参数。</param>
    private void AddEnvironmentVariable_Click(object sender, RoutedEventArgs eventArgs)
    {
        environmentVariables.Add(new EnvironmentVariableEntry());
        UpdateEnvironmentEmptyState();
    }

    /// <summary>
    /// 删除按钮所对应的环境变量行。
    /// </summary>
    /// <param name="sender">携带环境变量行参数的删除按钮。</param>
    /// <param name="eventArgs">事件参数。</param>
    private void DeleteEnvironmentVariable_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is System.Windows.Controls.Button
            {
                CommandParameter: EnvironmentVariableEntry entry
            })
        {
            environmentVariables.Remove(entry);
            UpdateEnvironmentEmptyState();
        }
    }

    /// <summary>
    /// 校验输入、检测快捷键冲突并生成编辑结果。
    /// </summary>
    /// <param name="sender">保存按钮。</param>
    /// <param name="eventArgs">事件参数。</param>
    private void Confirm_Click(object sender, RoutedEventArgs eventArgs)
    {
        ValidationText.Text = string.Empty;
        var name = NameTextBox.Text.Trim();
        var target = TargetTextBox.Text.Trim();
        if (name.Length == 0 || target.Length == 0)
        {
            ValidationText.Text = "名称和启动目标不能为空。";
            return;
        }

        var gesture = HotkeyInput.Gesture;
        if (!gesture.IsEmpty && !gesture.IsValid())
        {
            ValidationText.Text = "全局快捷键必须至少包含一个修饰键。";
            HotkeyInput.Focus();
            return;
        }

        Guid? excludedId = isNew ? null : sourceItem.Id;
        if (!gesture.IsEmpty && !hotkeyService.IsAvailable(gesture, excludedId))
        {
            ValidationText.Text = "该快捷键已被其他项目或系统程序占用。";
            HotkeyInput.Focus();
            return;
        }

        var type = TargetClassifier.Classify(target);
        var normalizedEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (type == LauncherTargetType.Application
            && !TryBuildEnvironmentVariables(normalizedEnvironment))
        {
            return;
        }

        ResultItem = new LauncherItem
        {
            Id = sourceItem.Id,
            Name = name,
            Target = target,
            TargetType = type,
            Arguments = type == LauncherTargetType.Application ? ArgumentsTextBox.Text.Trim() : string.Empty,
            EnvironmentVariables = normalizedEnvironment,
            CustomIconPath = customIconPath,
            RunAsAdministrator = type == LauncherTargetType.Application
                && RunAsAdministratorCheckBox.IsChecked == true,
            Hotkey = gesture,
            SortOrder = sourceItem.SortOrder
        };
        DialogResult = true;
    }

    /// <summary>
    /// 校验编辑行并填充不区分大小写的 Windows 环境变量字典。
    /// </summary>
    /// <param name="result">接收规范化键值的目标字典。</param>
    /// <returns>所有变量有效且没有重复名称时返回 <see langword="true"/>。</returns>
    private bool TryBuildEnvironmentVariables(Dictionary<string, string> result)
    {
        result.Clear();
        foreach (var entry in environmentVariables)
        {
            var key = entry.Key.Trim();
            if (key.Length == 0 || key.Contains('=') || key.Contains('\0'))
            {
                ValidationText.Text = "环境变量名称不能为空，也不能包含等号或空字符。";
                return false;
            }

            if (entry.Value.Contains('\0'))
            {
                ValidationText.Text = $"环境变量“{key}”的值不能包含空字符。";
                return false;
            }

            if (!result.TryAdd(key, entry.Value))
            {
                ValidationText.Text = $"环境变量“{key}”重复；变量名不区分大小写。";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 根据当前行数切换环境变量空状态提示。
    /// </summary>
    private void UpdateEnvironmentEmptyState()
    {
        if (EnvironmentEmptyText is not null)
        {
            EnvironmentEmptyText.Visibility = environmentVariables.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    /// <summary>
    /// 取消本次编辑并关闭窗口。
    /// </summary>
    /// <param name="sender">取消控件。</param>
    /// <param name="eventArgs">事件参数。</param>
    private void Cancel_Click(object sender, RoutedEventArgs eventArgs) => DialogResult = false;

    /// <summary>
    /// 根据目标识别结果刷新类型提示和程序选项状态。
    /// </summary>
    private void UpdateTargetType()
    {
        if (TypeText is null || ProgramOptionsPanel is null)
        {
            return;
        }

        var type = TargetClassifier.Classify(TargetTextBox?.Text);
        TypeText.Text = type switch
        {
            LauncherTargetType.Application => "程序",
            LauncherTargetType.Url => "网址",
            _ => "文件或快捷方式"
        };
        ProgramOptionsPanel.IsEnabled = type == LauncherTargetType.Application;
    }

    /// <summary>
    /// 更新无图标时显示的项目首字符。
    /// </summary>
    private void UpdateInitial()
    {
        if (InitialText is null)
        {
            return;
        }

        var name = NameTextBox?.Text.Trim();
        InitialText.Text = string.IsNullOrWhiteSpace(name) ? "?" : name[..1].ToUpperInvariant();
    }

    /// <summary>
    /// 根据当前输入异步提取并显示图标预览。
    /// </summary>
    /// <returns>图标加载完成时结束的任务。</returns>
    private async Task RefreshIconPreviewAsync()
    {
        if (IconPreview is null || TargetTextBox is null)
        {
            return;
        }

        var target = TargetTextBox.Text.Trim();
        var previewItem = new LauncherItem
        {
            Name = NameTextBox.Text,
            Target = target,
            TargetType = TargetClassifier.Classify(target),
            CustomIconPath = customIconPath
        };
        try
        {
            IconPreview.Source = await iconCache.GetIconAsync(previewItem);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            IconPreview.Source = null;
        }
    }
}
