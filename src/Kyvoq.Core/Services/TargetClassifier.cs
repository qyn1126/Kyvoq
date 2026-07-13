using Kyvoq.Core.Models;

namespace Kyvoq.Core.Services;

/// <summary>
/// 负责根据目标文本识别启动类型。
/// </summary>
public static class TargetClassifier
{
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe",
        ".com",
        ".bat",
        ".cmd"
    };

    /// <summary>
    /// 根据目标文本判断其属于程序、文件还是网址。
    /// </summary>
    /// <param name="target">启动目标。</param>
    /// <returns>识别出的目标类型。</returns>
    public static LauncherTargetType Classify(string? target)
    {
        var value = target?.Trim() ?? string.Empty;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https")
        {
            return LauncherTargetType.Url;
        }

        return ExecutableExtensions.Contains(Path.GetExtension(value))
            ? LauncherTargetType.Application
            : LauncherTargetType.File;
    }
}
