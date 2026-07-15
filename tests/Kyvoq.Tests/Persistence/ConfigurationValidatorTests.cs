using Kyvoq.Core.Models;
using Kyvoq.Core.Persistence;

namespace Kyvoq.Tests.Persistence;

/// <summary>
/// 验证导入配置的版本、标识和排序规范化。
/// </summary>
public sealed class ConfigurationValidatorTests
{
    /// <summary>
    /// 验证空分组集合会自动补充可用的默认分组。
    /// </summary>
    [Fact]
    public void ValidateAndNormalize_ShouldAddDefaultGroupWhenEmpty()
    {
        var configuration = new LauncherConfiguration();

        ConfigurationValidator.ValidateAndNormalize(configuration);

        Assert.Equal("常用", Assert.Single(configuration.Groups).Name);
    }

    /// <summary>
    /// 验证重复标识会被替换且分组、项目排序号连续重建。
    /// </summary>
    [Fact]
    public void ValidateAndNormalize_ShouldRepairDuplicateIdsAndOrders()
    {
        var duplicateId = Guid.NewGuid();
        var firstItem = new LauncherItem
        {
            Id = duplicateId,
            Name = "B",
            Target = @"C:\B.exe",
            SortOrder = 8
        };
        var secondItem = new LauncherItem
        {
            Id = duplicateId,
            Name = "A",
            Target = @"C:\A.exe",
            SortOrder = 2
        };
        var group = new LauncherGroup
        {
            Id = duplicateId,
            Name = "工具",
            SortOrder = 5,
            Items = [firstItem, secondItem]
        };
        var configuration = new LauncherConfiguration { Groups = [group] };

        ConfigurationValidator.ValidateAndNormalize(configuration);

        var allIds = configuration.Groups
            .SelectMany(candidate => candidate.Items.Select(item => item.Id).Prepend(candidate.Id))
            .ToArray();
        Assert.Equal(allIds.Length, allIds.Distinct().Count());
        Assert.Equal([0, 1], configuration.Groups[0].Items.Select(item => item.SortOrder));
        Assert.Equal("A", configuration.Groups[0].Items[0].Name);
    }

    /// <summary>
    /// 验证来自未来版本的配置会被明确拒绝。
    /// </summary>
    [Fact]
    public void ValidateAndNormalize_ShouldRejectFutureSchemaVersion()
    {
        var configuration = LauncherConfiguration.CreateDefault();
        configuration.SchemaVersion = LauncherConfiguration.CurrentSchemaVersion + 1;

        Assert.Throws<InvalidDataException>(() =>
            ConfigurationValidator.ValidateAndNormalize(configuration));
    }

    /// <summary>
    /// 验证配置加载时会根据目标重新识别类型，避免信任过期字段。
    /// </summary>
    [Fact]
    public void ValidateAndNormalize_ShouldRefreshTargetType()
    {
        var configuration = LauncherConfiguration.CreateDefault();
        configuration.Groups[0].Items.Add(new LauncherItem
        {
            Name = "网站",
            Target = "https://example.com",
            TargetType = LauncherTargetType.Application
        });

        ConfigurationValidator.ValidateAndNormalize(configuration);

        Assert.Equal(LauncherTargetType.Url, configuration.Groups[0].Items[0].TargetType);
    }

    /// <summary>
    /// 验证无效主界面快捷键和无修饰键的项目快捷键会被安全修复。
    /// </summary>
    [Fact]
    public void ValidateAndNormalize_ShouldRepairInvalidHotkeys()
    {
        var configuration = LauncherConfiguration.CreateDefault();
        configuration.Settings.MainWindowHotkey = HotkeyGesture.Empty();
        configuration.Groups[0].Items.Add(new LauncherItem
        {
            Name = "无效快捷键",
            Target = @"C:\Invalid.exe",
            Hotkey = new HotkeyGesture { VirtualKey = 0x41 }
        });

        ConfigurationValidator.ValidateAndNormalize(configuration);

        Assert.Equal(HotkeyGesture.CreateDefaultMainWindow(), configuration.Settings.MainWindowHotkey);
        Assert.True(configuration.Groups[0].Items[0].Hotkey.IsEmpty);
    }

    /// <summary>
    /// 验证导入配置中的异常窗口尺寸会被限制到受支持范围。
    /// </summary>
    [Fact]
    public void ValidateAndNormalize_ShouldClampWindowDimensions()
    {
        var configuration = LauncherConfiguration.CreateDefault();
        configuration.Settings.WindowWidth = 10;
        configuration.Settings.WindowHeight = 20000;

        ConfigurationValidator.ValidateAndNormalize(configuration);

        Assert.Equal(100, configuration.Settings.WindowWidth);
        Assert.Equal(4320, configuration.Settings.WindowHeight);
    }

    /// <summary>
    /// 验证第一版默认窗口尺寸升级后会变为新的紧凑尺寸。
    /// </summary>
    [Fact]
    public void ValidateAndNormalize_ShouldMigrateLegacyDefaultWindowDimensions()
    {
        var configuration = LauncherConfiguration.CreateDefault();
        configuration.SchemaVersion = 1;
        configuration.Settings.WindowWidth = 1180;
        configuration.Settings.WindowHeight = 760;

        ConfigurationValidator.ValidateAndNormalize(configuration);

        Assert.Equal(920, configuration.Settings.WindowWidth);
        Assert.Equal(620, configuration.Settings.WindowHeight);
        Assert.Equal(LauncherConfiguration.CurrentSchemaVersion, configuration.SchemaVersion);
    }

    /// <summary>
    /// 验证第一版配置中用户自行调整的窗口尺寸在升级时保持不变。
    /// </summary>
    [Fact]
    public void ValidateAndNormalize_ShouldPreserveLegacyCustomWindowDimensions()
    {
        var configuration = LauncherConfiguration.CreateDefault();
        configuration.SchemaVersion = 1;
        configuration.Settings.WindowWidth = 1000;
        configuration.Settings.WindowHeight = 700;

        ConfigurationValidator.ValidateAndNormalize(configuration);

        Assert.Equal(1000, configuration.Settings.WindowWidth);
        Assert.Equal(700, configuration.Settings.WindowHeight);
    }

    /// <summary>
    /// 验证未定义的主题枚举会回退为跟随系统。
    /// </summary>
    [Fact]
    public void ValidateAndNormalize_ShouldRepairUnknownTheme()
    {
        var configuration = LauncherConfiguration.CreateDefault();
        configuration.Settings.Theme = (AppTheme)99;

        ConfigurationValidator.ValidateAndNormalize(configuration);

        Assert.Equal(AppTheme.System, configuration.Settings.Theme);
    }

    /// <summary>
    /// 验证未定义的窗口材质会回退到默认云母材质。
    /// </summary>
    [Fact]
    public void ValidateAndNormalize_ShouldRepairUnknownWindowMaterial()
    {
        var configuration = LauncherConfiguration.CreateDefault();
        configuration.Settings.WindowMaterial = (WindowMaterial)99;

        ConfigurationValidator.ValidateAndNormalize(configuration);

        Assert.Equal(WindowMaterial.Mica, configuration.Settings.WindowMaterial);
    }

    /// <summary>
    /// 验证未定义的强调色模式及透明自定义颜色会回退到安全值。
    /// </summary>
    [Fact]
    public void ValidateAndNormalize_ShouldRepairInvalidAccentSettings()
    {
        var configuration = LauncherConfiguration.CreateDefault();
        configuration.Settings.AccentMode = (AccentMode)99;
        configuration.Settings.CustomAccentArgb = 0;

        ConfigurationValidator.ValidateAndNormalize(configuration);

        Assert.Equal(AccentMode.System, configuration.Settings.AccentMode);
        Assert.Equal(0xFF7C5CFCu, configuration.Settings.CustomAccentArgb);
    }

    /// <summary>
    /// 验证环境变量名称按 Windows 规则校验且忽略大小写检测重复。
    /// </summary>
    [Fact]
    public void ValidateAndNormalize_ShouldRejectInvalidEnvironmentVariableNames()
    {
        var configuration = LauncherConfiguration.CreateDefault();
        configuration.Groups[0].Items.Add(new LauncherItem
        {
            Name = "工具",
            Target = @"C:\Tools\Tool.exe",
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["KYVOQ_MODE"] = "one",
                ["kyvoq_mode"] = "two"
            }
        });

        Assert.Throws<InvalidDataException>(() =>
            ConfigurationValidator.ValidateAndNormalize(configuration));
    }
}
