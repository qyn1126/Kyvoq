using Kyvoq.Core.Models;

namespace Kyvoq.App.Services;

/// <summary>
/// 定义全局快捷键注册和冲突检测能力。
/// </summary>
public interface IGlobalHotkeyService : IDisposable
{
    event EventHandler<Guid>? Invoked;

    /// <summary>
    /// 差异应用主界面快捷键和全部启用的项目快捷键。
    /// </summary>
    /// <param name="settings">当前应用设置。</param>
    /// <param name="items">全部启动项目。</param>
    /// <returns>注册结果及冲突项目。</returns>
    HotkeyRegistrationReport Apply(AppSettings settings, IEnumerable<LauncherItem> items);

    /// <summary>
    /// 检测快捷键是否未被其他程序或其他项目占用。
    /// </summary>
    /// <param name="gesture">待检测快捷键。</param>
    /// <param name="excludedActionId">编辑现有绑定时排除的动作标识。</param>
    /// <returns>可以使用时返回 <see langword="true"/>。</returns>
    bool IsAvailable(HotkeyGesture gesture, Guid? excludedActionId = null);
}
