using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Kyvoq.App.Services;

/// <summary>
/// 表示使用虚拟屏幕坐标描述的矩形。
/// </summary>
internal readonly record struct ScreenRectangle(int Left, int Top, int Right, int Bottom);

/// <summary>
/// 表示一次前台窗口检测需要的稳定属性快照。
/// </summary>
internal sealed record FullscreenWindowSnapshot(
    string? ExecutablePath,
    bool IsVisible,
    bool IsMinimized,
    bool IsCloaked,
    bool IsSystemWindow,
    bool IsCurrentProcess,
    bool IsFramedMaximized,
    ScreenRectangle? WindowBounds,
    ScreenRectangle? MonitorBounds);

/// <summary>
/// 使用 Windows 前台窗口、DWM 和通知状态检测独占或无边框全屏。
/// </summary>
internal sealed class WindowsFullscreenDetector : IFullscreenDetector
{
    private const int InitialExecutablePathCapacity = 1024;
    private const int MaximumExecutablePathCapacity = 32768;
    private const int ErrorInsufficientBuffer = 122;
    private const int DwmExtendedFrameBounds = 9;
    private const int DwmCloaked = 14;
    private const int GwlStyle = -16;
    private const long WsCaption = 0x00C00000L;
    private const long WsThickFrame = 0x00040000L;
    private const uint MonitorDefaultToNearest = 2;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int BoundaryTolerance = 2;
    private static readonly HashSet<string> ExcludedBrowserExecutables = new(
        [
            "msedge.exe",
            "chrome.exe",
            "firefox.exe",
            "brave.exe",
            "opera.exe",
            "vivaldi.exe"
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly Func<FullscreenWindowSnapshot?> snapshotProvider;
    private readonly Func<bool> exclusiveFullscreenProvider;

    /// <summary>
    /// 定义可替换的进程映像路径查询调用。
    /// </summary>
    /// <param name="processHandle">目标进程句柄。</param>
    /// <param name="flags">路径格式标志。</param>
    /// <param name="executablePath">接收路径文本的缓冲区。</param>
    /// <param name="pathLength">输入缓冲区容量并接收实际长度。</param>
    /// <returns>查询成功时返回 <see langword="true"/>。</returns>
    internal delegate bool ProcessImageNameQuery(
        SafeProcessHandle processHandle,
        uint flags,
        StringBuilder executablePath,
        ref uint pathLength);

    /// <summary>
    /// 创建使用真实 Windows API 的全屏检测器。
    /// </summary>
    public WindowsFullscreenDetector()
        : this(CaptureForegroundWindow, DetectExclusiveFullscreen)
    {
    }

    /// <summary>
    /// 创建使用可替换窗口快照和独占状态来源的检测器。
    /// </summary>
    /// <param name="snapshotProvider">获取前台窗口快照的函数。</param>
    /// <param name="exclusiveFullscreenProvider">获取独占全屏状态的函数。</param>
    internal WindowsFullscreenDetector(
        Func<FullscreenWindowSnapshot?> snapshotProvider,
        Func<bool> exclusiveFullscreenProvider)
    {
        this.snapshotProvider = snapshotProvider ?? throw new ArgumentNullException(nameof(snapshotProvider));
        this.exclusiveFullscreenProvider = exclusiveFullscreenProvider
            ?? throw new ArgumentNullException(nameof(exclusiveFullscreenProvider));
    }

    /// <summary>
    /// 判断当前前台窗口是否处于独占或无边框全屏，并排除主流浏览器。
    /// </summary>
    /// <returns>应抑制 Kyvoq 快捷键时返回 <see langword="true"/>。</returns>
    public bool IsForegroundFullscreen()
    {
        try
        {
            var snapshot = snapshotProvider();
            if (snapshot is null
                || !snapshot.IsVisible
                || snapshot.IsMinimized
                || snapshot.IsCloaked
                || snapshot.IsSystemWindow
                || snapshot.IsCurrentProcess
                || IsExcludedBrowser(snapshot.ExecutablePath))
            {
                return false;
            }

            if (exclusiveFullscreenProvider())
            {
                return true;
            }

            return !snapshot.IsFramedMaximized
                && snapshot.WindowBounds is ScreenRectangle windowBounds
                && snapshot.MonitorBounds is ScreenRectangle monitorBounds
                && MatchesMonitorBounds(windowBounds, monitorBounds);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 获取当前前台窗口的可测试属性快照。
    /// </summary>
    /// <returns>存在前台窗口时返回快照，否则返回 <see langword="null"/>。</returns>
    private static FullscreenWindowSnapshot? CaptureForegroundWindow()
    {
        var windowHandle = GetForegroundWindow();
        if (windowHandle == IntPtr.Zero)
        {
            return null;
        }

        _ = GetWindowThreadProcessId(windowHandle, out var processId);
        var executablePath = TryGetExecutablePath(processId);
        var isSystemWindow = windowHandle == GetDesktopWindow() || windowHandle == GetShellWindow();
        return new FullscreenWindowSnapshot(
            executablePath,
            IsWindowVisible(windowHandle),
            IsIconic(windowHandle),
            IsWindowCloaked(windowHandle),
            isSystemWindow,
            processId == (uint)Environment.ProcessId,
            IsFramedMaximizedWindow(windowHandle),
            TryGetWindowBounds(windowHandle),
            TryGetMonitorBounds(windowHandle));
    }

    /// <summary>
    /// 查询 Windows 是否正在运行独占 Direct3D 全屏程序。
    /// </summary>
    /// <returns>系统明确报告独占 Direct3D 全屏时返回 <see langword="true"/>。</returns>
    private static bool DetectExclusiveFullscreen() =>
        SHQueryUserNotificationState(out var state) >= 0
        && state == QueryUserNotificationState.RunningDirect3DFullscreen;

    /// <summary>
    /// 判断可执行文件是否属于无需抑制快捷键的主流浏览器。
    /// </summary>
    /// <param name="executablePath">前台进程路径。</param>
    /// <returns>匹配浏览器进程名时返回 <see langword="true"/>。</returns>
    private static bool IsExcludedBrowser(string? executablePath) =>
        !string.IsNullOrWhiteSpace(executablePath)
        && ExcludedBrowserExecutables.Contains(Path.GetFileName(executablePath));

    /// <summary>
    /// 判断窗口边界是否在容差范围内与显示器完整边界一致。
    /// </summary>
    /// <param name="windowBounds">窗口边界。</param>
    /// <param name="monitorBounds">显示器边界。</param>
    /// <returns>四条边均匹配时返回 <see langword="true"/>。</returns>
    private static bool MatchesMonitorBounds(
        ScreenRectangle windowBounds,
        ScreenRectangle monitorBounds) =>
        Math.Abs(windowBounds.Left - monitorBounds.Left) <= BoundaryTolerance
        && Math.Abs(windowBounds.Top - monitorBounds.Top) <= BoundaryTolerance
        && Math.Abs(windowBounds.Right - monitorBounds.Right) <= BoundaryTolerance
        && Math.Abs(windowBounds.Bottom - monitorBounds.Bottom) <= BoundaryTolerance;

    /// <summary>
    /// 尝试获取指定进程的完整可执行文件路径。
    /// </summary>
    /// <param name="processId">目标进程标识。</param>
    /// <returns>成功时返回完整路径，否则返回 <see langword="null"/>。</returns>
    private static string? TryGetExecutablePath(uint processId)
    {
        if (processId == 0)
        {
            return null;
        }

        using var processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (processHandle.IsInvalid)
        {
            return null;
        }

        return QueryExecutablePath(
            processHandle,
            QueryFullProcessImageName,
            Marshal.GetLastPInvokeError);
    }

    /// <summary>
    /// 使用小缓冲区查询进程路径，并在查询失败时逐步扩大到 Windows 最大路径容量。
    /// </summary>
    /// <param name="processHandle">目标进程句柄。</param>
    /// <param name="query">实际执行路径查询的函数。</param>
    /// <param name="getLastError">查询失败后获取 Win32 错误码的函数。</param>
    /// <returns>成功时返回完整路径，否则返回 <see langword="null"/>。</returns>
    internal static string? QueryExecutablePath(
        SafeProcessHandle processHandle,
        ProcessImageNameQuery query,
        Func<int> getLastError)
    {
        ArgumentNullException.ThrowIfNull(processHandle);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(getLastError);
        var capacity = InitialExecutablePathCapacity;
        while (true)
        {
            var path = new StringBuilder(capacity);
            var pathLength = (uint)capacity;
            if (query(processHandle, 0, path, ref pathLength))
            {
                return path.ToString();
            }

            if (getLastError() != ErrorInsufficientBuffer
                || capacity >= MaximumExecutablePathCapacity)
            {
                return null;
            }

            capacity = Math.Min(capacity * 2, MaximumExecutablePathCapacity);
        }
    }

    /// <summary>
    /// 判断窗口是否被桌面窗口管理器隐藏。
    /// </summary>
    /// <param name="windowHandle">目标窗口句柄。</param>
    /// <returns>窗口被 cloaked 时返回 <see langword="true"/>。</returns>
    private static bool IsWindowCloaked(IntPtr windowHandle) =>
        DwmGetWindowAttributeInt(
            windowHandle,
            DwmCloaked,
            out var cloaked,
            sizeof(int)) >= 0
        && cloaked != 0;

    /// <summary>
    /// 判断窗口是否为普通带标题栏或可调整边框的最大化窗口。
    /// </summary>
    /// <param name="windowHandle">目标窗口句柄。</param>
    /// <returns>普通带边框最大化时返回 <see langword="true"/>。</returns>
    private static bool IsFramedMaximizedWindow(IntPtr windowHandle)
    {
        if (!IsZoomed(windowHandle))
        {
            return false;
        }

        var style = GetWindowLongPtr(windowHandle, GwlStyle).ToInt64();
        return (style & WsCaption) == WsCaption || (style & WsThickFrame) != 0;
    }

    /// <summary>
    /// 尝试读取窗口的 DWM 实际边界，并在失败时回退到普通窗口边界。
    /// </summary>
    /// <param name="windowHandle">目标窗口句柄。</param>
    /// <returns>成功时返回屏幕边界，否则返回 <see langword="null"/>。</returns>
    private static ScreenRectangle? TryGetWindowBounds(IntPtr windowHandle)
    {
        if (DwmGetWindowAttributeRectangle(
                windowHandle,
                DwmExtendedFrameBounds,
                out var rectangle,
                Marshal.SizeOf<NativeRectangle>()) < 0
            && !GetWindowRect(windowHandle, out rectangle))
        {
            return null;
        }

        return rectangle.ToScreenRectangle();
    }

    /// <summary>
    /// 尝试获取窗口主要所在显示器的完整屏幕边界。
    /// </summary>
    /// <param name="windowHandle">目标窗口句柄。</param>
    /// <returns>成功时返回显示器边界，否则返回 <see langword="null"/>。</returns>
    private static ScreenRectangle? TryGetMonitorBounds(IntPtr windowHandle)
    {
        var monitorHandle = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        if (monitorHandle == IntPtr.Zero)
        {
            return null;
        }

        var monitorInfo = new NativeMonitorInfo
        {
            Size = (uint)Marshal.SizeOf<NativeMonitorInfo>()
        };
        return GetMonitorInfo(monitorHandle, ref monitorInfo)
            ? monitorInfo.Monitor.ToScreenRectangle()
            : null;
    }

    /// <summary>
    /// 表示 Windows 用户通知状态中的相关枚举值。
    /// </summary>
    private enum QueryUserNotificationState
    {
        RunningDirect3DFullscreen = 3
    }

    /// <summary>
    /// 表示 Win32 RECT 结构。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        /// <summary>
        /// 转换为托管屏幕矩形。
        /// </summary>
        /// <returns>包含相同坐标的矩形。</returns>
        public readonly ScreenRectangle ToScreenRectangle() => new(Left, Top, Right, Bottom);
    }

    /// <summary>
    /// 表示 Win32 MONITORINFO 结构。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitorInfo
    {
        public uint Size;
        public NativeRectangle Monitor;
        public NativeRectangle WorkArea;
        public uint Flags;
    }

    /// <summary>
    /// 获取当前接收键盘输入的前台窗口。
    /// </summary>
    /// <returns>前台窗口句柄。</returns>
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>
    /// 获取桌面窗口句柄。
    /// </summary>
    /// <returns>桌面窗口句柄。</returns>
    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    /// <summary>
    /// 获取 Shell 桌面窗口句柄。
    /// </summary>
    /// <returns>Shell 窗口句柄。</returns>
    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    /// <summary>
    /// 判断窗口当前是否可见。
    /// </summary>
    /// <param name="windowHandle">目标窗口句柄。</param>
    /// <returns>可见时返回 <see langword="true"/>。</returns>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    /// <summary>
    /// 判断窗口当前是否最小化。
    /// </summary>
    /// <param name="windowHandle">目标窗口句柄。</param>
    /// <returns>最小化时返回 <see langword="true"/>。</returns>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr windowHandle);

    /// <summary>
    /// 判断窗口当前是否最大化。
    /// </summary>
    /// <param name="windowHandle">目标窗口句柄。</param>
    /// <returns>最大化时返回 <see langword="true"/>。</returns>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr windowHandle);

    /// <summary>
    /// 获取窗口所属进程标识。
    /// </summary>
    /// <param name="windowHandle">目标窗口句柄。</param>
    /// <param name="processId">接收进程标识。</param>
    /// <returns>创建窗口的线程标识。</returns>
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    /// <summary>
    /// 获取窗口的扩展样式值。
    /// </summary>
    /// <param name="windowHandle">目标窗口句柄。</param>
    /// <param name="index">待读取属性索引。</param>
    /// <returns>窗口属性值。</returns>
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr windowHandle, int index);

    /// <summary>
    /// 获取普通窗口屏幕边界。
    /// </summary>
    /// <param name="windowHandle">目标窗口句柄。</param>
    /// <param name="rectangle">接收窗口边界。</param>
    /// <returns>成功时返回 <see langword="true"/>。</returns>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRectangle rectangle);

    /// <summary>
    /// 获取与窗口相交面积最大的显示器。
    /// </summary>
    /// <param name="windowHandle">目标窗口句柄。</param>
    /// <param name="flags">窗口不在显示器内时的回退规则。</param>
    /// <returns>显示器句柄。</returns>
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    /// <summary>
    /// 获取显示器完整区域和工作区域。
    /// </summary>
    /// <param name="monitorHandle">目标显示器句柄。</param>
    /// <param name="monitorInfo">接收显示器信息。</param>
    /// <returns>成功时返回 <see langword="true"/>。</returns>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(
        IntPtr monitorHandle,
        ref NativeMonitorInfo monitorInfo);

    /// <summary>
    /// 打开仅用于查询路径的进程句柄。
    /// </summary>
    /// <param name="desiredAccess">请求的访问权限。</param>
    /// <param name="inheritHandle">子进程是否继承句柄。</param>
    /// <param name="processId">目标进程标识。</param>
    /// <returns>需要释放的安全进程句柄。</returns>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    /// <summary>
    /// 查询进程的完整映像文件路径。
    /// </summary>
    /// <param name="processHandle">目标进程句柄。</param>
    /// <param name="flags">路径格式标志。</param>
    /// <param name="executablePath">接收路径文本。</param>
    /// <param name="pathLength">输入容量并接收实际长度。</param>
    /// <returns>成功时返回 <see langword="true"/>。</returns>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle processHandle,
        uint flags,
        StringBuilder executablePath,
        ref uint pathLength);

    /// <summary>
    /// 获取窗口的整数 DWM 属性。
    /// </summary>
    /// <param name="windowHandle">目标窗口句柄。</param>
    /// <param name="attribute">DWM 属性编号。</param>
    /// <param name="attributeValue">接收属性值。</param>
    /// <param name="attributeSize">属性缓冲区大小。</param>
    /// <returns>HRESULT 状态码。</returns>
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeInt(
        IntPtr windowHandle,
        int attribute,
        out int attributeValue,
        int attributeSize);

    /// <summary>
    /// 获取窗口的矩形 DWM 属性。
    /// </summary>
    /// <param name="windowHandle">目标窗口句柄。</param>
    /// <param name="attribute">DWM 属性编号。</param>
    /// <param name="attributeValue">接收矩形值。</param>
    /// <param name="attributeSize">属性缓冲区大小。</param>
    /// <returns>HRESULT 状态码。</returns>
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeRectangle(
        IntPtr windowHandle,
        int attribute,
        out NativeRectangle attributeValue,
        int attributeSize);

    /// <summary>
    /// 查询当前用户是否处于独占 Direct3D 全屏状态。
    /// </summary>
    /// <param name="state">接收通知状态。</param>
    /// <returns>HRESULT 状态码。</returns>
    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out QueryUserNotificationState state);
}
