using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Kyvoq.Core.Models;
using Kyvoq.Core.Services;

namespace Kyvoq.App.Services;

/// <summary>
/// 通过 Win32 RegisterHotKey 管理主窗口生命周期内的全局快捷键。
/// </summary>
public sealed class GlobalHotkeyService : IGlobalHotkeyService
{
    public static readonly Guid MainWindowActionId = Guid.Empty;
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private const int FirstRegistrationId = 0x5000;
    private const int TemporaryRegistrationId = 0x6FFF;
    private readonly IntPtr windowHandle;
    private readonly HwndSource source;
    private readonly IFullscreenDetector fullscreenDetector;
    private readonly Func<IntPtr, int, uint, uint, bool> registerHotKey;
    private readonly Func<IntPtr, int, bool> unregisterHotKey;
    private readonly Dictionary<int, Guid> actionsByRegistration = [];
    private readonly Dictionary<Guid, int> registrationsByAction = [];
    private readonly Dictionary<Guid, HotkeyGesture> configuredGestures = [];
    private readonly Dictionary<Guid, HotkeyGesture> registeredGestures = [];
    private readonly Queue<int> reusableRegistrationIds = [];
    private int nextRegistrationId = FirstRegistrationId;
    private bool disposed;

    public event EventHandler<Guid>? Invoked;

    /// <summary>
    /// 为指定 WPF 窗口创建全局快捷键服务。
    /// </summary>
    /// <param name="window">用于接收 WM_HOTKEY 消息的长期窗口。</param>
    public GlobalHotkeyService(Window window)
        : this(
            window,
            new WindowsFullscreenDetector(),
            RegisterHotKey,
            UnregisterHotKey)
    {
    }

    /// <summary>
    /// 使用可替换的全屏检测器创建全局快捷键服务。
    /// </summary>
    /// <param name="window">用于接收 WM_HOTKEY 消息的长期窗口。</param>
    /// <param name="fullscreenDetector">决定是否忽略快捷键动作的检测器。</param>
    internal GlobalHotkeyService(Window window, IFullscreenDetector fullscreenDetector)
        : this(window, fullscreenDetector, RegisterHotKey, UnregisterHotKey)
    {
    }

    /// <summary>
    /// 使用可替换的全屏检测器及 Win32 注册函数创建可测试服务。
    /// </summary>
    /// <param name="window">用于接收 WM_HOTKEY 消息的长期窗口。</param>
    /// <param name="fullscreenDetector">决定是否忽略快捷键动作的检测器。</param>
    /// <param name="registerHotKey">注册单个全局快捷键的函数。</param>
    /// <param name="unregisterHotKey">注销单个全局快捷键的函数。</param>
    internal GlobalHotkeyService(
        Window window,
        IFullscreenDetector fullscreenDetector,
        Func<IntPtr, int, uint, uint, bool> registerHotKey,
        Func<IntPtr, int, bool> unregisterHotKey)
    {
        ArgumentNullException.ThrowIfNull(window);
        this.fullscreenDetector = fullscreenDetector
            ?? throw new ArgumentNullException(nameof(fullscreenDetector));
        this.registerHotKey = registerHotKey ?? throw new ArgumentNullException(nameof(registerHotKey));
        this.unregisterHotKey = unregisterHotKey ?? throw new ArgumentNullException(nameof(unregisterHotKey));
        windowHandle = new WindowInteropHelper(window).EnsureHandle();
        source = HwndSource.FromHwnd(windowHandle)
            ?? throw new InvalidOperationException("无法创建全局快捷键消息源。");
        source.AddHook(WindowProcedure);
    }

    /// <summary>
    /// 差异更新主界面快捷键和所有没有冲突的项目快捷键。
    /// </summary>
    /// <param name="settings">当前应用设置。</param>
    /// <param name="items">全部启动项目。</param>
    /// <returns>主界面快捷键状态及冲突项目集合。</returns>
    public HotkeyRegistrationReport Apply(AppSettings settings, IEnumerable<LauncherItem> items)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(items);

        var itemArray = items.ToArray();
        var updatedConfiguredGestures = new Dictionary<Guid, HotkeyGesture>
        {
            [MainWindowActionId] = settings.MainWindowHotkey
        };
        foreach (var item in itemArray.Where(item => item.Hotkey.IsValid()))
        {
            updatedConfiguredGestures[item.Id] = item.Hotkey;
        }

        var conflicts = new HashSet<Guid>(
            HotkeyConflictDetector.FindItemConflicts(settings.MainWindowHotkey, itemArray));
        var desiredRegistrations = new List<KeyValuePair<Guid, HotkeyGesture>>
        {
            new(MainWindowActionId, settings.MainWindowHotkey)
        };
        if (settings.ItemHotkeysEnabled)
        {
            foreach (var item in itemArray.Where(item => item.Hotkey.IsValid()))
            {
                if (conflicts.Contains(item.Id))
                {
                    continue;
                }

                desiredRegistrations.Add(new KeyValuePair<Guid, HotkeyGesture>(item.Id, item.Hotkey));
            }
        }

        var desiredByAction = desiredRegistrations.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var (actionId, registeredGesture) in registeredGestures.ToArray())
        {
            if (!desiredByAction.TryGetValue(actionId, out var desiredGesture)
                || desiredGesture != registeredGesture)
            {
                UnregisterAction(actionId);
            }
        }

        configuredGestures.Clear();
        foreach (var (actionId, gesture) in updatedConfiguredGestures)
        {
            configuredGestures[actionId] = gesture;
        }

        foreach (var actionId in registrationsByAction.Keys
                     .Where(actionId => !updatedConfiguredGestures.ContainsKey(actionId))
                     .ToArray())
        {
            ReleaseRegistrationId(actionId);
        }

        var mainWindowRegistered = false;
        foreach (var (actionId, gesture) in desiredRegistrations)
        {
            var registered = (registeredGestures.TryGetValue(actionId, out var existingGesture)
                              && existingGesture == gesture)
                || TryRegister(actionId, gesture);
            if (actionId == MainWindowActionId)
            {
                mainWindowRegistered = registered;
            }
            else if (!registered)
            {
                conflicts.Add(actionId);
            }
        }

        return new HotkeyRegistrationReport(mainWindowRegistered, conflicts);
    }

    /// <summary>
    /// 同时检查配置内绑定和其他进程占用情况。
    /// </summary>
    /// <param name="gesture">待检测的快捷键。</param>
    /// <param name="excludedActionId">编辑现有动作时排除的标识。</param>
    /// <returns>快捷键可安全使用时返回 <see langword="true"/>。</returns>
    public bool IsAvailable(HotkeyGesture gesture, Guid? excludedActionId = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(gesture);
        if (!gesture.IsValid())
        {
            return gesture.IsEmpty;
        }

        if (configuredGestures.Any(pair => pair.Value == gesture && pair.Key != excludedActionId))
        {
            return false;
        }

        if (excludedActionId is Guid ownActionId
            && configuredGestures.TryGetValue(ownActionId, out var ownGesture)
            && ownGesture == gesture
            && registeredGestures.ContainsKey(ownActionId))
        {
            return true;
        }

        var registered = registerHotKey(
            windowHandle,
            TemporaryRegistrationId,
            (uint)gesture.Modifiers | ModNoRepeat,
            gesture.VirtualKey);
        if (registered)
        {
            unregisterHotKey(windowHandle, TemporaryRegistrationId);
        }

        return registered;
    }

    /// <summary>
    /// 注销所有快捷键并移除窗口消息钩子。
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        UnregisterAll();
        configuredGestures.Clear();
        source.RemoveHook(WindowProcedure);
    }

    /// <summary>
    /// 尝试注册单个动作快捷键并记录其映射。
    /// </summary>
    /// <param name="actionId">应用内部动作标识。</param>
    /// <param name="gesture">需要注册的组合键。</param>
    /// <returns>成功注册时返回 <see langword="true"/>。</returns>
    private bool TryRegister(Guid actionId, HotkeyGesture gesture)
    {
        if (!gesture.IsValid())
        {
            return false;
        }

        var registrationId = GetOrCreateRegistrationId(actionId);
        if (!registerHotKey(
                windowHandle,
                registrationId,
                (uint)gesture.Modifiers | ModNoRepeat,
                gesture.VirtualKey))
        {
            return false;
        }

        actionsByRegistration[registrationId] = actionId;
        registeredGestures[actionId] = gesture;
        return true;
    }

    /// <summary>
    /// 获取动作现有的 Win32 注册标识，或分配一个新的稳定标识。
    /// </summary>
    /// <param name="actionId">应用内部动作标识。</param>
    /// <returns>当前窗口内唯一的注册标识。</returns>
    private int GetOrCreateRegistrationId(Guid actionId)
    {
        if (registrationsByAction.TryGetValue(actionId, out var registrationId))
        {
            return registrationId;
        }

        if (!reusableRegistrationIds.TryDequeue(out registrationId))
        {
            registrationId = nextRegistrationId++;
            if (registrationId == TemporaryRegistrationId)
            {
                registrationId = nextRegistrationId++;
            }
        }

        registrationsByAction[actionId] = registrationId;
        return registrationId;
    }

    /// <summary>
    /// 注销指定动作当前成功注册的快捷键。
    /// </summary>
    /// <param name="actionId">需要注销的动作标识。</param>
    private void UnregisterAction(Guid actionId)
    {
        if (!registrationsByAction.TryGetValue(actionId, out var registrationId)
            || !registeredGestures.Remove(actionId))
        {
            return;
        }

        unregisterHotKey(windowHandle, registrationId);
        actionsByRegistration.Remove(registrationId);
    }

    /// <summary>
    /// 释放已从配置删除的动作注册标识以供后续动作复用。
    /// </summary>
    /// <param name="actionId">已经不再配置快捷键的动作标识。</param>
    private void ReleaseRegistrationId(Guid actionId)
    {
        UnregisterAction(actionId);
        if (registrationsByAction.Remove(actionId, out var registrationId))
        {
            reusableRegistrationIds.Enqueue(registrationId);
        }
    }

    /// <summary>
    /// 注销当前服务注册的全部 Win32 快捷键。
    /// </summary>
    private void UnregisterAll()
    {
        foreach (var registrationId in actionsByRegistration.Keys)
        {
            unregisterHotKey(windowHandle, registrationId);
        }

        actionsByRegistration.Clear();
        registrationsByAction.Clear();
        registeredGestures.Clear();
        reusableRegistrationIds.Clear();
    }

    /// <summary>
    /// 处理窗口收到的全局快捷键消息。
    /// </summary>
    /// <param name="handle">窗口句柄。</param>
    /// <param name="message">Win32 消息编号。</param>
    /// <param name="wordParameter">包含注册标识的参数。</param>
    /// <param name="longParameter">包含组合键信息的参数。</param>
    /// <param name="handled">是否已经处理消息。</param>
    /// <returns>消息处理结果。</returns>
    private IntPtr WindowProcedure(
        IntPtr handle,
        int message,
        IntPtr wordParameter,
        IntPtr longParameter,
        ref bool handled)
    {
        if (message == WmHotkey
            && actionsByRegistration.TryGetValue(wordParameter.ToInt32(), out var actionId))
        {
            handled = true;
            if (!fullscreenDetector.IsForegroundFullscreen())
            {
                Invoked?.Invoke(this, actionId);
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 调用 Win32 API 注册进程外全局快捷键。
    /// </summary>
    /// <param name="windowHandle">接收快捷键消息的窗口句柄。</param>
    /// <param name="id">当前窗口内唯一的注册标识。</param>
    /// <param name="modifiers">Win32 修饰键标志。</param>
    /// <param name="virtualKey">Windows 虚拟键代码。</param>
    /// <returns>注册成功时返回 <see langword="true"/>。</returns>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

    /// <summary>
    /// 调用 Win32 API 注销当前窗口的全局快捷键。
    /// </summary>
    /// <param name="windowHandle">原接收快捷键消息的窗口句柄。</param>
    /// <param name="id">需要注销的注册标识。</param>
    /// <returns>注销成功时返回 <see langword="true"/>。</returns>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);

}
