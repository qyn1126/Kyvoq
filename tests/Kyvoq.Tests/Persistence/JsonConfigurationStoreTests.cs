using System.Text.Json;
using Kyvoq.Core.Models;
using Kyvoq.Core.Persistence;

namespace Kyvoq.Tests.Persistence;

/// <summary>
/// 验证 JSON 配置存储的保存、导入导出与损坏恢复行为。
/// </summary>
public sealed class JsonConfigurationStoreTests : IDisposable
{
    private readonly string temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "Kyvoq.Tests",
        Guid.NewGuid().ToString("N"));

    /// <summary>
    /// 验证配置写入不再为每个异步写操作启用直写磁盘选项。
    /// </summary>
    [Fact]
    public void ConfigurationWriteOptions_ShouldNotUseWriteThrough()
    {
        Assert.False(JsonConfigurationStore.ConfigurationWriteOptions.HasFlag(FileOptions.WriteThrough));
    }

    /// <summary>
    /// 删除每个测试创建的临时配置目录。
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(temporaryDirectory))
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    /// <summary>
    /// 验证首次加载返回默认配置，保存后可以完整读回。
    /// </summary>
    [Fact]
    public async Task LoadAndSaveAsync_ShouldRoundTripConfiguration()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var store = new JsonConfigurationStore(temporaryDirectory);
        var initial = await store.LoadAsync(cancellationToken);
        initial.Configuration.Groups[0].Items.Add(new LauncherItem
        {
            Name = "记事本",
            Target = @"C:\Windows\notepad.exe"
        });

        await store.SaveAsync(initial.Configuration, cancellationToken);
        var reloaded = await store.LoadAsync(cancellationToken);

        Assert.Equal(ConfigurationLoadState.CreatedDefault, initial.State);
        Assert.Equal(ConfigurationLoadState.Loaded, reloaded.State);
        Assert.Equal("记事本", Assert.Single(reloaded.Configuration.Groups[0].Items).Name);
    }

    /// <summary>
    /// 验证检查主配置后文件被外部删除时，保存会回退到覆盖移动并留下完整配置。
    /// </summary>
    [Fact]
    public async Task SaveAsync_ShouldRecoverWhenPrimaryDisappearsBeforeReplace()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(temporaryDirectory);
        var configurationPath = Path.Combine(temporaryDirectory, "config.json");
        await File.WriteAllTextAsync(configurationPath, "{}", cancellationToken);
        using var store = new JsonConfigurationStore(
            temporaryDirectory,
            (sourcePath, destinationPath, backupPath, ignoreMetadataErrors) =>
            {
                File.Delete(destinationPath);
                File.Replace(sourcePath, destinationPath, backupPath, ignoreMetadataErrors);
            });
        var configuration = LauncherConfiguration.CreateDefault();
        configuration.Groups[0].Name = "竞态恢复";

        await store.SaveAsync(configuration, cancellationToken);
        var result = await store.LoadAsync(cancellationToken);

        Assert.Equal(ConfigurationLoadState.Loaded, result.State);
        Assert.Equal("竞态恢复", result.Configuration.Groups[0].Name);
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory, "*.tmp"));
    }

    /// <summary>
    /// 验证主配置损坏时自动读取最近一次原子替换留下的备份。
    /// </summary>
    [Fact]
    public async Task LoadAsync_ShouldRecoverFromBackupWhenPrimaryIsCorrupt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var store = new JsonConfigurationStore(temporaryDirectory);
        var configuration = LauncherConfiguration.CreateDefault();
        configuration.Groups[0].Name = "第一个版本";
        await store.SaveAsync(configuration, cancellationToken);
        configuration.Groups[0].Name = "第二个版本";
        await store.SaveAsync(configuration, cancellationToken);
        await File.WriteAllTextAsync(store.ConfigurationPath, "{ invalid json", cancellationToken);

        var result = await store.LoadAsync(cancellationToken);

        Assert.Equal(ConfigurationLoadState.RecoveredFromBackup, result.State);
        Assert.Equal("第一个版本", result.Configuration.Groups[0].Name);
        Assert.NotEmpty(result.Message);
    }

    /// <summary>
    /// 验证主配置和备份均损坏时载入空白配置但不删除原文件。
    /// </summary>
    [Fact]
    public async Task LoadAsync_ShouldResetWithoutDeletingCorruptFiles()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(temporaryDirectory);
        using var store = new JsonConfigurationStore(temporaryDirectory);
        await File.WriteAllTextAsync(store.ConfigurationPath, "broken", cancellationToken);
        await File.WriteAllTextAsync(store.BackupPath, "also broken", cancellationToken);

        var result = await store.LoadAsync(cancellationToken);

        Assert.Equal(ConfigurationLoadState.ResetAfterCorruption, result.State);
        Assert.Equal("常用", Assert.Single(result.Configuration.Groups).Name);
        Assert.True(File.Exists(store.ConfigurationPath));
        Assert.True(File.Exists(store.BackupPath));
    }

    /// <summary>
    /// 验证导出文件可以重新导入并保持项目参数和环境变量。
    /// </summary>
    [Fact]
    public async Task ExportAndImportAsync_ShouldPreserveArguments()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var store = new JsonConfigurationStore(temporaryDirectory);
        var configuration = LauncherConfiguration.CreateDefault();
        configuration.Groups[0].Items.Add(new LauncherItem
        {
            Name = "工具",
            Target = @"C:\Tools\Tool.exe",
            Arguments = "--mode fast",
            EnvironmentVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["KYVOQ_MODE"] = "fast"
            }
        });
        var exportPath = Path.Combine(temporaryDirectory, "export.json");

        await store.ExportAsync(configuration, exportPath, cancellationToken);
        var imported = await store.ImportAsync(exportPath, cancellationToken);
        var exportedJson = await File.ReadAllTextAsync(exportPath, cancellationToken);

        var importedItem = Assert.Single(imported.Groups[0].Items);
        Assert.Equal("--mode fast", importedItem.Arguments);
        Assert.Equal("fast", importedItem.EnvironmentVariables["kyvoq_mode"]);
        Assert.Contains("\"TargetType\": \"Application\"", exportedJson);
        Assert.Contains("\"EnvironmentVariables\"", exportedJson);
    }

    /// <summary>
    /// 验证导入比当前应用更新的配置版本时抛出明确错误。
    /// </summary>
    [Fact]
    public async Task ImportAsync_ShouldRejectFutureSchemaVersion()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(temporaryDirectory);
        using var store = new JsonConfigurationStore(temporaryDirectory);
        var path = Path.Combine(temporaryDirectory, "future.json");
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(new
            {
                SchemaVersion = LauncherConfiguration.CurrentSchemaVersion + 1,
                Groups = Array.Empty<object>(),
                Settings = new { }
            }),
            cancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportAsync(path, cancellationToken));
    }

    /// <summary>
    /// 验证第一版配置加载后使用新的主界面快捷键并迁移旧默认窗口尺寸。
    /// </summary>
    [Fact]
    public async Task LoadAsync_ShouldMigrateVersionOneSettings()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(temporaryDirectory);
        using var store = new JsonConfigurationStore(temporaryDirectory);
        await File.WriteAllTextAsync(
            store.ConfigurationPath,
            """
            {
              "SchemaVersion": 1,
              "Groups": [
                {
                  "Name": "常用",
                  "SortOrder": 0,
                  "Items": []
                }
              ],
              "Settings": {
                "SummonHotkey": { "Modifiers": "Control, Alt", "VirtualKey": 32 },
                "WindowWidth": 1180,
                "WindowHeight": 760
              }
            }
            """,
            cancellationToken);

        var result = await store.LoadAsync(cancellationToken);

        Assert.Equal(LauncherConfiguration.CurrentSchemaVersion, result.Configuration.SchemaVersion);
        Assert.Equal("Alt+Space", result.Configuration.Settings.MainWindowHotkey.ToString());
        Assert.Equal(920, result.Configuration.Settings.WindowWidth);
        Assert.Equal(620, result.Configuration.Settings.WindowHeight);
    }

    /// <summary>
    /// 验证第二版配置升级后忽略已停用的工作目录并初始化环境变量。
    /// </summary>
    [Fact]
    public async Task LoadAsync_ShouldMigrateVersionTwoItemSettings()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(temporaryDirectory);
        using var store = new JsonConfigurationStore(temporaryDirectory);
        await File.WriteAllTextAsync(
            store.ConfigurationPath,
            """
            {
              "SchemaVersion": 2,
              "Groups": [{
                "Name": "常用",
                "Items": [{
                  "Name": "工具",
                  "Target": "C:\\Tools\\Tool.exe",
                  "WorkingDirectory": "C:\\Legacy"
                }]
              }],
              "Settings": {}
            }
            """,
            cancellationToken);

        var result = await store.LoadAsync(cancellationToken);
        var item = Assert.Single(Assert.Single(result.Configuration.Groups).Items);

        Assert.Equal(3, result.Configuration.SchemaVersion);
        Assert.Empty(item.EnvironmentVariables);
    }
}
