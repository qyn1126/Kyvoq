using Kyvoq.App.Services;

namespace Kyvoq.App.Tests.Services;

/// <summary>
/// 验证主窗口围绕鼠标居中并限制在目标显示器工作区内。
/// </summary>
public sealed class CursorWindowPositionerTests
{
    /// <summary>
    /// 验证窗口在空间充足时以鼠标为中心，在靠近边缘或尺寸过大时保持可操作区域可见。
    /// </summary>
    /// <param name="cursorX">鼠标横坐标。</param>
    /// <param name="cursorY">鼠标纵坐标。</param>
    /// <param name="workLeft">工作区左边界。</param>
    /// <param name="workTop">工作区上边界。</param>
    /// <param name="workRight">工作区右边界。</param>
    /// <param name="workBottom">工作区下边界。</param>
    /// <param name="windowWidth">窗口宽度。</param>
    /// <param name="windowHeight">窗口高度。</param>
    /// <param name="expectedX">期望的窗口左侧坐标。</param>
    /// <param name="expectedY">期望的窗口顶部坐标。</param>
    [Theory]
    [InlineData(500, 400, 0, 0, 1000, 800, 200, 100, 400, 350)]
    [InlineData(10, 10, 0, 0, 1000, 800, 200, 100, 0, 0)]
    [InlineData(990, 790, 0, 0, 1000, 800, 200, 100, 800, 700)]
    [InlineData(-960, 540, -1920, 0, 0, 1080, 400, 300, -1160, 390)]
    [InlineData(-1900, 20, -1920, 0, 0, 1080, 400, 300, -1920, 0)]
    [InlineData(500, 400, 0, 0, 1000, 800, 1200, 900, 0, 0)]
    public void CalculateCenteredPosition_ShouldCenterOrClampWindowInsideWorkArea(
        int cursorX,
        int cursorY,
        int workLeft,
        int workTop,
        int workRight,
        int workBottom,
        int windowWidth,
        int windowHeight,
        int expectedX,
        int expectedY)
    {
        var result = CursorWindowPositioner.CalculateCenteredPosition(
            new ScreenPoint(cursorX, cursorY),
            new ScreenRectangle(workLeft, workTop, workRight, workBottom),
            windowWidth,
            windowHeight);

        Assert.Equal(new ScreenPoint(expectedX, expectedY), result);
    }
}
