using Kyvoq.Core.Models;

namespace Kyvoq.Tests.Models;

/// <summary>
/// 验证异步持久化使用的完整配置快照。
/// </summary>
public sealed class LauncherConfigurationTests
{
    /// <summary>
    /// 验证修改配置副本不会反向改变原设置、分组或项目。
    /// </summary>
    [Fact]
    public void Clone_ShouldCreateIndependentObjectGraph()
    {
        var configuration = LauncherConfiguration.CreateDefault();
        configuration.Groups[0].Items.Add(new LauncherItem
        {
            Name = "原项目",
            Target = @"C:\Original.exe",
            EnvironmentVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["KYVOQ_PROFILE"] = "original"
            }
        });

        var clone = configuration.Clone();
        clone.Settings.Theme = AppTheme.Dark;
        clone.Settings.WindowMaterial = WindowMaterial.Acrylic;
        clone.Settings.AccentMode = AccentMode.Custom;
        clone.Settings.CustomAccentArgb = 0xFF336699;
        clone.Groups[0].Name = "新分组";
        clone.Groups[0].Items[0].Name = "新项目";
        clone.Groups[0].Items[0].EnvironmentVariables["KYVOQ_PROFILE"] = "changed";

        Assert.Equal(AppTheme.System, configuration.Settings.Theme);
        Assert.Equal(WindowMaterial.Mica, configuration.Settings.WindowMaterial);
        Assert.Equal(AccentMode.System, configuration.Settings.AccentMode);
        Assert.Equal("常用", configuration.Groups[0].Name);
        Assert.Equal("原项目", configuration.Groups[0].Items[0].Name);
        Assert.Equal("original", configuration.Groups[0].Items[0].EnvironmentVariables["kyvoq_profile"]);
    }
}
