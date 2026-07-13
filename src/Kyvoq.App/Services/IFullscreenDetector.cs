namespace Kyvoq.App.Services;

/// <summary>
/// 定义当前前台窗口是否应抑制 Kyvoq 快捷键的检测能力。
/// </summary>
internal interface IFullscreenDetector
{
    /// <summary>
    /// 判断当前前台窗口是否处于需要抑制快捷键的全屏状态。
    /// </summary>
    /// <returns>应抑制快捷键时返回 <see langword="true"/>。</returns>
    bool IsForegroundFullscreen();
}
