using Kyvoq.Core.Models;
using Kyvoq.Core.Services;

namespace Kyvoq.Core.Persistence;

/// <summary>
/// 负责校验并规范化持久化配置。
/// </summary>
public static class ConfigurationValidator
{
    /// <summary>
    /// 校验配置版本、标识和必要字段，并重新生成稳定排序号。
    /// </summary>
    /// <param name="configuration">需要校验的配置。</param>
    /// <returns>完成规范化的同一配置实例。</returns>
    /// <exception cref="InvalidDataException">配置版本过新或包含无效字段时抛出。</exception>
    public static LauncherConfiguration ValidateAndNormalize(LauncherConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var sourceSchemaVersion = configuration.SchemaVersion;
        if (configuration.SchemaVersion > LauncherConfiguration.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"配置版本 {configuration.SchemaVersion} 高于当前支持的版本 {LauncherConfiguration.CurrentSchemaVersion}。");
        }

        configuration.Settings ??= new AppSettings();
        if (sourceSchemaVersion < 2
            && Math.Abs(configuration.Settings.WindowWidth - 1180) < 0.5
            && Math.Abs(configuration.Settings.WindowHeight - 760) < 0.5)
        {
            configuration.Settings.WindowWidth = 920;
            configuration.Settings.WindowHeight = 620;
        }

        configuration.SchemaVersion = LauncherConfiguration.CurrentSchemaVersion;
        if (!Enum.IsDefined(configuration.Settings.Theme))
        {
            configuration.Settings.Theme = AppTheme.System;
        }

        if (!Enum.IsDefined(configuration.Settings.AccentMode))
        {
            configuration.Settings.AccentMode = AccentMode.System;
        }

        if (configuration.Settings.CustomAccentArgb == 0)
        {
            configuration.Settings.CustomAccentArgb = 0xFF7C5CFC;
        }
        else
        {
            configuration.Settings.CustomAccentArgb |= 0xFF000000;
        }

        configuration.Settings.MainWindowHotkey ??= HotkeyGesture.CreateDefaultMainWindow();
        if (!configuration.Settings.MainWindowHotkey.IsValid())
        {
            configuration.Settings.MainWindowHotkey = HotkeyGesture.CreateDefaultMainWindow();
        }

        configuration.Settings.WindowWidth = double.IsFinite(configuration.Settings.WindowWidth)
            ? Math.Clamp(configuration.Settings.WindowWidth, 100, 7680)
            : 920;
        configuration.Settings.WindowHeight = double.IsFinite(configuration.Settings.WindowHeight)
            ? Math.Clamp(configuration.Settings.WindowHeight, 100, 4320)
            : 620;
        if (configuration.Settings.WindowLeft is double left && !double.IsFinite(left))
        {
            configuration.Settings.WindowLeft = null;
        }

        if (configuration.Settings.WindowTop is double top && !double.IsFinite(top))
        {
            configuration.Settings.WindowTop = null;
        }

        configuration.Groups ??= [];

        if (configuration.Groups.Count == 0)
        {
            configuration.Groups.Add(new LauncherGroup { Name = "常用" });
        }

        var ids = new HashSet<Guid>();
        foreach (var group in configuration.Groups.OrderBy(group => group.SortOrder).ThenBy(group => group.Name))
        {
            if (group.Id == Guid.Empty || !ids.Add(group.Id))
            {
                group.Id = CreateUniqueId(ids);
            }

            group.Name = RequireText(group.Name, "分组名称");
            group.Items ??= [];
            foreach (var item in group.Items.OrderBy(item => item.SortOrder).ThenBy(item => item.Name))
            {
                if (item.Id == Guid.Empty || !ids.Add(item.Id))
                {
                    item.Id = CreateUniqueId(ids);
                }

                item.Name = RequireText(item.Name, "项目名称");
                item.Target = RequireText(item.Target, $"项目“{item.Name}”的目标");
                item.Arguments ??= string.Empty;
                item.EnvironmentVariables = NormalizeEnvironmentVariables(
                    item.EnvironmentVariables,
                    item.Name);
                item.CustomIconPath ??= string.Empty;
                item.Hotkey ??= HotkeyGesture.Empty();
                if (!item.Hotkey.IsEmpty && !item.Hotkey.IsValid())
                {
                    item.Hotkey = HotkeyGesture.Empty();
                }

                item.TargetType = TargetClassifier.Classify(item.Target);
                if (item.TargetType != LauncherTargetType.Application)
                {
                    item.Arguments = string.Empty;
                    item.EnvironmentVariables.Clear();
                    item.RunAsAdministrator = false;
                }
            }

            group.Items = group.Items.OrderBy(item => item.SortOrder).ThenBy(item => item.Name).ToList();
            Reindex(group.Items, static (item, order) => item.SortOrder = order);
        }

        configuration.Groups = configuration.Groups
            .OrderBy(group => group.SortOrder)
            .ThenBy(group => group.Name)
            .ToList();
        Reindex(configuration.Groups, static (group, order) => group.SortOrder = order);
        return configuration;
    }

    /// <summary>
    /// 校验并重建 Windows 环境变量字典，确保名称有效且不区分大小写唯一。
    /// </summary>
    /// <param name="variables">待规范化环境变量。</param>
    /// <param name="itemName">用于错误信息的项目名称。</param>
    /// <returns>使用 Windows 名称比较规则的新字典。</returns>
    /// <exception cref="InvalidDataException">变量名重复或包含非法字符时抛出。</exception>
    private static Dictionary<string, string> NormalizeEnvironmentVariables(
        Dictionary<string, string>? variables,
        string itemName)
    {
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in variables ?? [])
        {
            var normalizedName = name?.Trim() ?? string.Empty;
            if (normalizedName.Length == 0
                || normalizedName.Contains('=')
                || normalizedName.Contains('\0'))
            {
                throw new InvalidDataException($"项目“{itemName}”包含无效环境变量名称。");
            }

            if ((value ?? string.Empty).Contains('\0'))
            {
                throw new InvalidDataException($"项目“{itemName}”的环境变量“{normalizedName}”包含空字符。");
            }

            if (!normalized.TryAdd(normalizedName, value ?? string.Empty))
            {
                throw new InvalidDataException($"项目“{itemName}”包含重复环境变量“{normalizedName}”。");
            }
        }

        return normalized;
    }

    /// <summary>
    /// 创建当前集合中不存在的新标识。
    /// </summary>
    /// <param name="ids">已经使用的标识集合。</param>
    /// <returns>新生成且已经加入集合的标识。</returns>
    private static Guid CreateUniqueId(HashSet<Guid> ids)
    {
        Guid id;
        do
        {
            id = Guid.NewGuid();
        }
        while (!ids.Add(id));

        return id;
    }

    /// <summary>
    /// 校验必要文本并去除首尾空白。
    /// </summary>
    /// <param name="value">待校验的文本。</param>
    /// <param name="fieldName">错误消息中的字段名称。</param>
    /// <returns>规范化后的文本。</returns>
    private static string RequireText(string? value, string fieldName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            throw new InvalidDataException($"{fieldName}不能为空。");
        }

        return normalized;
    }

    /// <summary>
    /// 依照当前集合顺序重新生成连续排序号。
    /// </summary>
    /// <typeparam name="T">集合元素类型。</typeparam>
    /// <param name="items">需要重排的集合。</param>
    /// <param name="setOrder">写入排序号的操作。</param>
    private static void Reindex<T>(IReadOnlyList<T> items, Action<T, int> setOrder)
    {
        for (var index = 0; index < items.Count; index++)
        {
            setOrder(items[index], index);
        }
    }
}
