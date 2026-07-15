using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Kyvoq.App.Services;

/// <summary>
/// 表示虚拟屏幕坐标中的一个点。
/// </summary>
internal readonly record struct ScreenPoint(int X, int Y);

/// <summary>
/// 使用鼠标所在显示器的工作区定位主窗口。
/// </summary>
internal static class CursorWindowPositioner
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint PositionFlags = SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder;

    /// <summary>
    /// 将窗口中心尽量对准鼠标，并把窗口限制在鼠标所在显示器的工作区内。
    /// </summary>
    /// <param name="window">需要定位的 WPF 窗口。</param>
    /// <returns>成功取得屏幕信息并完成定位时返回 <see langword="true"/>。</returns>
    internal static bool TryPositionNearCursor(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var windowHandle = new WindowInteropHelper(window).EnsureHandle();
        if (windowHandle == IntPtr.Zero || !GetCursorPos(out var cursorPosition))
        {
            return false;
        }

        var targetMonitor = MonitorFromPoint(cursorPosition, MonitorDefaultToNearest);
        if (targetMonitor == IntPtr.Zero)
        {
            return false;
        }

        var monitorInfo = new NativeMonitorInfo
        {
            Size = (uint)Marshal.SizeOf<NativeMonitorInfo>()
        };
        if (!GetMonitorInfo(targetMonitor, ref monitorInfo)
            || !GetWindowRect(windowHandle, out var windowBounds))
        {
            return false;
        }

        var workArea = monitorInfo.WorkArea.ToScreenRectangle();
        var windowWidth = windowBounds.Right - windowBounds.Left;
        var windowHeight = windowBounds.Bottom - windowBounds.Top;
        if (workArea.Right <= workArea.Left
            || workArea.Bottom <= workArea.Top
            || windowWidth <= 0
            || windowHeight <= 0)
        {
            return false;
        }

        var currentMonitor = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        if (currentMonitor != targetMonitor)
        {
            var workAreaCenter = new ScreenPoint(
                workArea.Left + ((workArea.Right - workArea.Left) / 2),
                workArea.Top + ((workArea.Bottom - workArea.Top) / 2));
            var stagingPosition = CalculateCenteredPosition(
                workAreaCenter,
                workArea,
                windowWidth,
                windowHeight);
            if (!SetWindowPos(
                    windowHandle,
                    IntPtr.Zero,
                    stagingPosition.X,
                    stagingPosition.Y,
                    0,
                    0,
                    PositionFlags)
                || !GetWindowRect(windowHandle, out windowBounds))
            {
                return false;
            }

            windowWidth = windowBounds.Right - windowBounds.Left;
            windowHeight = windowBounds.Bottom - windowBounds.Top;
            if (windowWidth <= 0 || windowHeight <= 0)
            {
                return false;
            }
        }

        var position = CalculateCenteredPosition(
            new ScreenPoint(cursorPosition.X, cursorPosition.Y),
            workArea,
            windowWidth,
            windowHeight);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        return SetWindowPos(
            windowHandle,
            IntPtr.Zero,
            position.X,
            position.Y,
            0,
            0,
            PositionFlags);
    }

    /// <summary>
    /// 计算以指定点为中心并限制在工作区内的窗口左上角坐标。
    /// </summary>
    /// <param name="center">期望的窗口中心坐标。</param>
    /// <param name="workArea">目标显示器工作区。</param>
    /// <param name="windowWidth">窗口像素宽度。</param>
    /// <param name="windowHeight">窗口像素高度。</param>
    /// <returns>窗口最终左上角的虚拟屏幕坐标。</returns>
    /// <exception cref="ArgumentException">工作区没有有效宽度或高度。</exception>
    /// <exception cref="ArgumentOutOfRangeException">窗口宽度或高度不是正数。</exception>
    internal static ScreenPoint CalculateCenteredPosition(
        ScreenPoint center,
        ScreenRectangle workArea,
        int windowWidth,
        int windowHeight)
    {
        var workAreaWidth = workArea.Right - workArea.Left;
        var workAreaHeight = workArea.Bottom - workArea.Top;
        if (workAreaWidth <= 0 || workAreaHeight <= 0)
        {
            throw new ArgumentException("显示器工作区必须具有正数宽度和高度。", nameof(workArea));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowHeight);

        var desiredLeft = (long)center.X - (windowWidth / 2L);
        var desiredTop = (long)center.Y - (windowHeight / 2L);
        var left = windowWidth >= workAreaWidth
            ? workArea.Left
            : (int)Math.Clamp(desiredLeft, workArea.Left, (long)workArea.Right - windowWidth);
        var top = windowHeight >= workAreaHeight
            ? workArea.Top
            : (int)Math.Clamp(desiredTop, workArea.Top, (long)workArea.Bottom - windowHeight);
        return new ScreenPoint(left, top);
    }

    /// <summary>
    /// 获取当前鼠标在虚拟屏幕中的坐标。
    /// </summary>
    /// <param name="point">接收鼠标坐标。</param>
    /// <returns>调用成功时返回 <see langword="true"/>。</returns>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    /// <summary>
    /// 获取包含或最接近指定点的显示器句柄。
    /// </summary>
    /// <param name="point">虚拟屏幕坐标。</param>
    /// <param name="flags">找不到直接包含该点的显示器时所用回退策略。</param>
    /// <returns>显示器句柄；失败时为空句柄。</returns>
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    /// <summary>
    /// 获取包含或最接近指定窗口的显示器句柄。
    /// </summary>
    /// <param name="windowHandle">窗口句柄。</param>
    /// <param name="flags">找不到直接包含该窗口的显示器时所用回退策略。</param>
    /// <returns>显示器句柄；失败时为空句柄。</returns>
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    /// <summary>
    /// 获取显示器完整区域和排除任务栏后的工作区域。
    /// </summary>
    /// <param name="monitorHandle">显示器句柄。</param>
    /// <param name="monitorInfo">接收显示器信息。</param>
    /// <returns>调用成功时返回 <see langword="true"/>。</returns>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(
        IntPtr monitorHandle,
        ref NativeMonitorInfo monitorInfo);

    /// <summary>
    /// 获取窗口当前的虚拟屏幕边界。
    /// </summary>
    /// <param name="windowHandle">窗口句柄。</param>
    /// <param name="rectangle">接收窗口边界。</param>
    /// <returns>调用成功时返回 <see langword="true"/>。</returns>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(
        IntPtr windowHandle,
        out NativeRectangle rectangle);

    /// <summary>
    /// 在不改变大小、激活状态和层级的前提下移动窗口。
    /// </summary>
    /// <param name="windowHandle">窗口句柄。</param>
    /// <param name="insertAfter">Z 序参照窗口；当前调用不会使用。</param>
    /// <param name="x">目标左侧坐标。</param>
    /// <param name="y">目标顶部坐标。</param>
    /// <param name="width">目标宽度；当前调用不会使用。</param>
    /// <param name="height">目标高度；当前调用不会使用。</param>
    /// <param name="flags">窗口定位标志。</param>
    /// <returns>调用成功时返回 <see langword="true"/>。</returns>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    /// <summary>
    /// 对应 Win32 POINT 结构。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    /// <summary>
    /// 对应 Win32 RECT 结构。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        /// <summary>
        /// 转换为应用内部使用的虚拟屏幕矩形。
        /// </summary>
        /// <returns>具有相同边界的屏幕矩形。</returns>
        public readonly ScreenRectangle ToScreenRectangle() => new(Left, Top, Right, Bottom);
    }

    /// <summary>
    /// 对应 Win32 MONITORINFO 结构。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitorInfo
    {
        public uint Size;
        public NativeRectangle Monitor;
        public NativeRectangle WorkArea;
        public uint Flags;
    }
}
