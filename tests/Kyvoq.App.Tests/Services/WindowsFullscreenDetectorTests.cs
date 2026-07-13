using System.Text;
using Microsoft.Win32.SafeHandles;
using Kyvoq.App.Services;

namespace Kyvoq.App.Tests.Services;

/// <summary>
/// 验证独占全屏、无边框全屏和浏览器排除规则。
/// </summary>
public sealed class WindowsFullscreenDetectorTests
{
    /// <summary>
    /// 验证 Windows 报告独占全屏时会暂停快捷键。
    /// </summary>
    [Fact]
    public void IsForegroundFullscreen_ShouldDetectExclusiveFullscreen()
    {
        var detector = CreateDetector(CreateSnapshot(), isExclusiveFullscreen: true);

        Assert.True(detector.IsForegroundFullscreen());
    }

    /// <summary>
    /// 验证覆盖完整显示器且边界误差不超过两像素的无边框窗口会被识别。
    /// </summary>
    [Theory]
    [InlineData(0, 0, 1920, 1080)]
    [InlineData(-2, 1, 1918, 1082)]
    [InlineData(-1920, 0, 0, 1080, -1920, 0, 0, 1080)]
    public void IsForegroundFullscreen_ShouldDetectBorderlessMonitorCoverage(
        int windowLeft,
        int windowTop,
        int windowRight,
        int windowBottom,
        int monitorLeft = 0,
        int monitorTop = 0,
        int monitorRight = 1920,
        int monitorBottom = 1080)
    {
        var snapshot = CreateSnapshot(
            windowBounds: new ScreenRectangle(windowLeft, windowTop, windowRight, windowBottom),
            monitorBounds: new ScreenRectangle(monitorLeft, monitorTop, monitorRight, monitorBottom));
        var detector = CreateDetector(snapshot);

        Assert.True(detector.IsForegroundFullscreen());
    }

    /// <summary>
    /// 验证窗口化程序和普通带边框最大化窗口不会被识别为无边框全屏。
    /// </summary>
    [Fact]
    public void IsForegroundFullscreen_ShouldIgnoreWindowedAndFramedMaximizedWindows()
    {
        var windowedDetector = CreateDetector(CreateSnapshot(
            windowBounds: new ScreenRectangle(100, 100, 1500, 900)));
        var maximizedDetector = CreateDetector(CreateSnapshot(isFramedMaximized: true));

        Assert.False(windowedDetector.IsForegroundFullscreen());
        Assert.False(maximizedDetector.IsForegroundFullscreen());
    }

    /// <summary>
    /// 验证国际主流浏览器即使独占或覆盖完整显示器也不会暂停快捷键。
    /// </summary>
    [Theory]
    [InlineData("msedge.exe")]
    [InlineData("chrome.exe")]
    [InlineData("firefox.exe")]
    [InlineData("brave.exe")]
    [InlineData("opera.exe")]
    [InlineData("vivaldi.exe")]
    public void IsForegroundFullscreen_ShouldExcludeSupportedBrowsers(string executableName)
    {
        var detector = CreateDetector(
            CreateSnapshot(executablePath: $@"C:\Browsers\{executableName}"),
            isExclusiveFullscreen: true);

        Assert.False(detector.IsForegroundFullscreen());
    }

    /// <summary>
    /// 验证不可交互、系统或 Kyvoq 自身窗口不会触发全屏抑制。
    /// </summary>
    [Theory]
    [InlineData(false, false, false, false, false)]
    [InlineData(true, true, false, false, false)]
    [InlineData(true, false, true, false, false)]
    [InlineData(true, false, false, true, false)]
    [InlineData(true, false, false, false, true)]
    public void IsForegroundFullscreen_ShouldIgnoreIneligibleWindows(
        bool isVisible,
        bool isMinimized,
        bool isCloaked,
        bool isSystemWindow,
        bool isCurrentProcess)
    {
        var detector = CreateDetector(CreateSnapshot(
            isVisible: isVisible,
            isMinimized: isMinimized,
            isCloaked: isCloaked,
            isSystemWindow: isSystemWindow,
            isCurrentProcess: isCurrentProcess),
            isExclusiveFullscreen: true);

        Assert.False(detector.IsForegroundFullscreen());
    }

    /// <summary>
    /// 验证原生检测异常时采用放行策略，避免快捷键永久失效。
    /// </summary>
    [Fact]
    public void IsForegroundFullscreen_ShouldFailOpenWhenDetectionThrows()
    {
        var detector = new WindowsFullscreenDetector(
            () => throw new InvalidOperationException("native failure"),
            () => true);

        Assert.False(detector.IsForegroundFullscreen());
    }

    /// <summary>
    /// 验证进程路径查询从小缓冲区开始，并仅在失败后按需扩大容量。
    /// </summary>
    [Fact]
    public void QueryExecutablePath_ShouldStartSmallAndGrowAfterFailure()
    {
        var capacities = new List<int>();
        var expectedPath = new string('a', 1500);
        using var processHandle = new SafeProcessHandle(new IntPtr(1), ownsHandle: false);
        WindowsFullscreenDetector.ProcessImageNameQuery query = (
            SafeProcessHandle handle,
            uint flags,
            StringBuilder buffer,
            ref uint pathLength) =>
        {
            capacities.Add(buffer.Capacity);
            if (capacities.Count == 1)
            {
                return false;
            }

            buffer.Append(expectedPath);
            pathLength = (uint)expectedPath.Length;
            return true;
        };

        var result = WindowsFullscreenDetector.QueryExecutablePath(
            processHandle,
            query,
            () => 122);

        Assert.Equal(expectedPath, result);
        Assert.Equal([1024, 2048], capacities);
    }

    /// <summary>
    /// 验证非缓冲区不足错误不会继续分配更大的进程路径缓冲区。
    /// </summary>
    [Fact]
    public void QueryExecutablePath_ShouldStopAfterNonBufferFailure()
    {
        var attempts = 0;
        using var processHandle = new SafeProcessHandle(new IntPtr(1), ownsHandle: false);
        WindowsFullscreenDetector.ProcessImageNameQuery query = (
            SafeProcessHandle handle,
            uint flags,
            StringBuilder buffer,
            ref uint pathLength) =>
        {
            attempts++;
            return false;
        };

        var result = WindowsFullscreenDetector.QueryExecutablePath(
            processHandle,
            query,
            () => 5);

        Assert.Null(result);
        Assert.Equal(1, attempts);
    }

    /// <summary>
    /// 创建使用固定窗口快照和独占状态的检测器。
    /// </summary>
    /// <param name="snapshot">待返回的前台窗口快照。</param>
    /// <param name="isExclusiveFullscreen">是否模拟独占全屏。</param>
    /// <returns>可重复执行的测试检测器。</returns>
    private static WindowsFullscreenDetector CreateDetector(
        FullscreenWindowSnapshot snapshot,
        bool isExclusiveFullscreen = false) =>
        new(() => snapshot, () => isExclusiveFullscreen);

    /// <summary>
    /// 创建默认覆盖主显示器的有效前台窗口快照。
    /// </summary>
    /// <param name="executablePath">前台进程路径。</param>
    /// <param name="isVisible">窗口是否可见。</param>
    /// <param name="isMinimized">窗口是否最小化。</param>
    /// <param name="isCloaked">窗口是否被 DWM 隐藏。</param>
    /// <param name="isSystemWindow">是否为桌面或 Shell 窗口。</param>
    /// <param name="isCurrentProcess">是否属于 Kyvoq。</param>
    /// <param name="isFramedMaximized">是否为普通带边框最大化窗口。</param>
    /// <param name="windowBounds">窗口屏幕边界。</param>
    /// <param name="monitorBounds">显示器屏幕边界。</param>
    /// <returns>用于检测规则测试的快照。</returns>
    private static FullscreenWindowSnapshot CreateSnapshot(
        string? executablePath = @"C:\Games\Game.exe",
        bool isVisible = true,
        bool isMinimized = false,
        bool isCloaked = false,
        bool isSystemWindow = false,
        bool isCurrentProcess = false,
        bool isFramedMaximized = false,
        ScreenRectangle? windowBounds = null,
        ScreenRectangle? monitorBounds = null) =>
        new(
            executablePath,
            isVisible,
            isMinimized,
            isCloaked,
            isSystemWindow,
            isCurrentProcess,
            isFramedMaximized,
            windowBounds ?? new ScreenRectangle(0, 0, 1920, 1080),
            monitorBounds ?? new ScreenRectangle(0, 0, 1920, 1080));
}
