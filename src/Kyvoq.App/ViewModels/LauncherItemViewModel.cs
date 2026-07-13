using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using Kyvoq.App.Services;
using Kyvoq.Core.Models;

namespace Kyvoq.App.ViewModels;

/// <summary>
/// 为启动项目提供界面状态和异步图标。
/// </summary>
public sealed class LauncherItemViewModel : ObservableObject
{
    private ImageSource? icon;
    private bool hasHotkeyConflict;
    private readonly IconCacheService iconCache;
    private int iconLoadStarted;

    public LauncherItem Model { get; }

    public Guid Id => Model.Id;

    public string Name => Model.Name;

    public string DisplayName { get; }

    public string Target => Model.Target;

    public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name[..1].ToUpperInvariant();

    public string HotkeyText => Model.Hotkey.ToString();

    public ImageSource? Icon
    {
        get
        {
            if (Interlocked.Exchange(ref iconLoadStarted, 1) == 0)
            {
                _ = LoadIconAsync();
            }

            return icon;
        }
        private set => SetProperty(ref icon, value);
    }

    public bool HasHotkeyConflict
    {
        get => hasHotkeyConflict;
        set => SetProperty(ref hasHotkeyConflict, value);
    }

    /// <summary>
    /// 创建启动项目视图模型；图标会在界面首次读取时延迟加载。
    /// </summary>
    /// <param name="model">底层启动项目。</param>
    /// <param name="iconCache">图标缓存服务。</param>
    public LauncherItemViewModel(LauncherItem model, IconCacheService iconCache)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        this.iconCache = iconCache ?? throw new ArgumentNullException(nameof(iconCache));
        DisplayName = CreateDisplayName(Model.Name);
    }

    /// <summary>
    /// 在常见分隔符、驼峰边界和字母数字边界添加零宽空格，为名称提供优先软换行位置。
    /// </summary>
    /// <param name="name">保持视觉内容不变的原始项目名称。</param>
    /// <returns>仅增加 Unicode 零宽空格的显示名称。</returns>
    internal static string CreateDisplayName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var runes = name.EnumerateRunes().ToArray();
        if (runes.Length < 2)
        {
            return name;
        }

        const char zeroWidthSpace = '\u200B';
        var displayName = new StringBuilder(name.Length + 8);
        for (var index = 0; index < runes.Length; index++)
        {
            var current = runes[index];
            if (index > 0)
            {
                var previous = runes[index - 1];
                var next = index + 1 < runes.Length ? runes[index + 1] : (Rune?)null;
                var camelCaseBoundary = Rune.IsUpper(current)
                    && (Rune.IsLower(previous)
                        || next is Rune nextRune
                        && Rune.IsUpper(previous)
                        && Rune.IsLower(nextRune));
                var letterDigitBoundary = Rune.IsLetter(previous) && Rune.IsDigit(current)
                    || Rune.IsDigit(previous) && Rune.IsLetter(current);
                if ((camelCaseBoundary || letterDigitBoundary)
                    && previous.Value != zeroWidthSpace)
                {
                    displayName.Append(zeroWidthSpace);
                }
            }

            displayName.Append(current.ToString());
            if (IsSoftWrapSeparator(current)
                && index + 1 < runes.Length
                && runes[index + 1].Value != zeroWidthSpace
                && !Rune.IsWhiteSpace(runes[index + 1]))
            {
                displayName.Append(zeroWidthSpace);
            }
        }

        return displayName.ToString();
    }

    /// <summary>
    /// 判断字符是否属于文件名和标识符中常见的可见分隔符。
    /// </summary>
    /// <param name="value">待判断的 Unicode 字符。</param>
    /// <returns>适合在其后提供软换行机会时返回 <see langword="true"/>。</returns>
    private static bool IsSoftWrapSeparator(Rune value) => value.Value is
        '_' or '-' or '.' or '/' or '\\' or ':';

    /// <summary>
    /// 从缓存异步加载项目图标。
    /// </summary>
    /// <returns>表示图标加载过程的任务。</returns>
    private async Task LoadIconAsync()
    {
        try
        {
            Icon = await iconCache.GetIconAsync(Model);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ExternalException)
        {
            Icon = null;
        }
    }
}
