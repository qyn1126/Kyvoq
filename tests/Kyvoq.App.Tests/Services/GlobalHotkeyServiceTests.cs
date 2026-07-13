using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Kyvoq.App.Services;
using Kyvoq.Core.Models;

namespace Kyvoq.App.Tests.Services;

/// <summary>
/// 验证真实窗口句柄上的全局快捷键注册生命周期。
/// </summary>
public sealed class GlobalHotkeyServiceTests
{
    private const int WmHotkey = 0x0312;
    private const int FirstRegistrationId = 0x5000;

    /// <summary>
    /// 验证主界面快捷键可注册，且重复项目快捷键在调用 Win32 前已全部标记为冲突。
    /// </summary>
    [Fact]
    public void Apply_ShouldReportEveryDuplicatedItem()
    {
        HotkeyRegistrationReport? report = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new Window();
                using var service = new GlobalHotkeyService(window);
                var gesture = new HotkeyGesture
                {
                    Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift,
                    VirtualKey = 0x7B
                };
                var first = new LauncherItem { Name = "A", Target = @"C:\A.exe", Hotkey = gesture };
                var second = new LauncherItem { Name = "B", Target = @"C:\B.exe", Hotkey = gesture with { } };
                report = service.Apply(
                    new AppSettings
                    {
                        MainWindowHotkey = new HotkeyGesture
                        {
                            Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift,
                            VirtualKey = 0x7A
                        }
                    },
                    [first, second]);
                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));

        Assert.Null(failure);
        Assert.NotNull(report);
        Assert.True(report.MainWindowRegistered);
        Assert.Equal(2, report.ConflictingItemIds.Count);
    }

    /// <summary>
    /// 验证全屏期间快捷键消息被静默忽略，离开全屏后立即恢复触发。
    /// </summary>
    [Fact]
    public void WindowMessage_ShouldRespectFullscreenDetector()
    {
        Exception? failure = null;
        var invokedCount = 0;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new Window();
                var detector = new StubFullscreenDetector { IsFullscreen = true };
                using var service = new GlobalHotkeyService(window, detector);
                var report = service.Apply(
                    new AppSettings
                    {
                        MainWindowHotkey = new HotkeyGesture
                        {
                            Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift,
                            VirtualKey = 0x79
                        }
                    },
                    []);
                Assert.True(report.MainWindowRegistered);
                service.Invoked += (_, _) => invokedCount++;
                var handle = new WindowInteropHelper(window).Handle;

                _ = SendMessage(handle, WmHotkey, new IntPtr(FirstRegistrationId), IntPtr.Zero);
                detector.IsFullscreen = false;
                _ = SendMessage(handle, WmHotkey, new IntPtr(FirstRegistrationId), IntPtr.Zero);
                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));

        Assert.Null(failure);
        Assert.Equal(1, invokedCount);
    }

    /// <summary>
    /// 验证重复应用相同热键不调用 Win32，单项变化时仅更新对应注册。
    /// </summary>
    [Fact]
    public void Apply_ShouldOnlyRegisterChangedBindings()
    {
        Exception? failure = null;
        (int Registrations, int Unregistrations)? counts = null;
        var thread = new Thread(() =>
        {
            try
            {
                var registrations = 0;
                var unregistrations = 0;
                var window = new Window();
                using var service = new GlobalHotkeyService(
                    window,
                    new StubFullscreenDetector(),
                    (_, _, _, _) =>
                    {
                        registrations++;
                        return true;
                    },
                    (_, _) =>
                    {
                        unregistrations++;
                        return true;
                    });
                var settings = new AppSettings
                {
                    MainWindowHotkey = new HotkeyGesture
                    {
                        Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt,
                        VirtualKey = 0x70
                    }
                };
                var item = new LauncherItem
                {
                    Name = "A",
                    Target = @"C:\A.exe",
                    Hotkey = new HotkeyGesture
                    {
                        Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt,
                        VirtualKey = 0x71
                    }
                };

                service.Apply(settings, [item]);
                service.Apply(settings, [item]);
                item.Hotkey = new HotkeyGesture
                {
                    Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt,
                    VirtualKey = 0x72
                };
                service.Apply(settings, [item]);
                counts = (registrations, unregistrations);
                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));

        Assert.Null(failure);
        Assert.Equal((3, 1), counts);
    }

    /// <summary>
    /// 提供可切换结果的全屏检测测试替身。
    /// </summary>
    private sealed class StubFullscreenDetector : IFullscreenDetector
    {
        public bool IsFullscreen { get; set; }

        /// <summary>
        /// 返回测试指定的前台全屏状态。
        /// </summary>
        /// <returns>当前测试状态。</returns>
        public bool IsForegroundFullscreen() => IsFullscreen;
    }

    /// <summary>
    /// 向窗口同步发送 Win32 消息。
    /// </summary>
    /// <param name="windowHandle">目标窗口句柄。</param>
    /// <param name="message">消息编号。</param>
    /// <param name="wordParameter">消息字参数。</param>
    /// <param name="longParameter">消息长参数。</param>
    /// <returns>窗口过程返回值。</returns>
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(
        IntPtr windowHandle,
        int message,
        IntPtr wordParameter,
        IntPtr longParameter);
}
