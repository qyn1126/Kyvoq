using Microsoft.Win32;

namespace Kyvoq.App.Services;

/// <summary>
/// 管理当前用户的 Windows 登录启动项。
/// </summary>
public sealed class WindowsStartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Kyvoq";

    /// <summary>
    /// 根据设置创建或删除当前用户的登录启动项。
    /// </summary>
    /// <param name="enabled">是否启用开机启动。</param>
    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (enabled)
        {
            var executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("无法获取 Kyvoq 可执行文件路径。");
            key.SetValue(ValueName, $"\"{executable}\" --background", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
