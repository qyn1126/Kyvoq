using Kyvoq.Core.Models;
using Kyvoq.Core.Services;

namespace Kyvoq.Tests.Services;

/// <summary>
/// 验证启动目标类型识别规则。
/// </summary>
public sealed class TargetClassifierTests
{
    /// <summary>
    /// 验证 HTTP 与 HTTPS 地址被识别为网址。
    /// </summary>
    /// <param name="target">待识别网址。</param>
    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://localhost:8080/path")]
    public void Classify_ShouldRecognizeWebUrls(string target)
    {
        Assert.Equal(LauncherTargetType.Url, TargetClassifier.Classify(target));
    }

    /// <summary>
    /// 验证可执行扩展名忽略大小写并识别为程序。
    /// </summary>
    [Fact]
    public void Classify_ShouldRecognizeExecutableCaseInsensitively()
    {
        Assert.Equal(LauncherTargetType.Application, TargetClassifier.Classify(@"C:\Tools\APP.EXE"));
    }

    /// <summary>
    /// 验证快捷方式和普通文档交由 Windows 文件关联打开。
    /// </summary>
    /// <param name="target">待识别文件路径。</param>
    [Theory]
    [InlineData(@"C:\Tools\App.lnk")]
    [InlineData(@"C:\Docs\note.pdf")]
    public void Classify_ShouldTreatShortcutsAndDocumentsAsFiles(string target)
    {
        Assert.Equal(LauncherTargetType.File, TargetClassifier.Classify(target));
    }
}
