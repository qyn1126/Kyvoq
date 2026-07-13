using System.Text.Json;
using Kyvoq.Core.Models;

namespace Kyvoq.Core.Persistence;

/// <summary>
/// 使用版本化 JSON 文件持久化启动器配置。
/// </summary>
public sealed class JsonConfigurationStore : IConfigurationStore, IDisposable
{
    private const string ConfigurationFileName = "config.json";
    internal const FileOptions ConfigurationWriteOptions = FileOptions.Asynchronous;
    private readonly SemaphoreSlim ioLock = new(1, 1);
    private readonly KyvoqJsonSerializerContext serializerContext;
    private readonly Action<string, string, string?, bool> fileReplacer;
    private bool disposed;

    public string DataDirectory { get; }

    public string ConfigurationPath => Path.Combine(DataDirectory, ConfigurationFileName);

    public string BackupPath => $"{ConfigurationPath}.bak";

    /// <summary>
    /// 使用指定数据目录创建 JSON 配置存储。
    /// </summary>
    /// <param name="dataDirectory">配置和备份所在目录。</param>
    public JsonConfigurationStore(string dataDirectory)
        : this(dataDirectory, File.Replace)
    {
    }

    /// <summary>
    /// 使用可替换的文件替换操作创建可测试的 JSON 配置存储。
    /// </summary>
    /// <param name="dataDirectory">配置和备份所在目录。</param>
    /// <param name="fileReplacer">将临时文件原子替换为主配置的操作。</param>
    internal JsonConfigurationStore(
        string dataDirectory,
        Action<string, string, string?, bool> fileReplacer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        this.fileReplacer = fileReplacer ?? throw new ArgumentNullException(nameof(fileReplacer));
        DataDirectory = Path.GetFullPath(dataDirectory);
        serializerContext = new KyvoqJsonSerializerContext(new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
    }

    /// <summary>
    /// 加载主配置，并在读取失败时依次尝试备份和默认配置。
    /// </summary>
    /// <param name="cancellationToken">用于取消读取的令牌。</param>
    /// <returns>配置内容和恢复状态。</returns>
    public async Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(DataDirectory);
            if (!File.Exists(ConfigurationPath))
            {
                return new ConfigurationLoadResult(
                    LauncherConfiguration.CreateDefault(),
                    ConfigurationLoadState.CreatedDefault,
                    string.Empty);
            }

            try
            {
                var configuration = await ReadAndValidateAsync(ConfigurationPath, cancellationToken).ConfigureAwait(false);
                return new ConfigurationLoadResult(configuration, ConfigurationLoadState.Loaded, string.Empty);
            }
            catch (Exception exception) when (IsRecoverableReadError(exception))
            {
                if (File.Exists(BackupPath))
                {
                    try
                    {
                        var backup = await ReadAndValidateAsync(BackupPath, cancellationToken).ConfigureAwait(false);
                        return new ConfigurationLoadResult(
                            backup,
                            ConfigurationLoadState.RecoveredFromBackup,
                            "主配置文件无法读取，已使用最近一次有效备份恢复。");
                    }
                    catch (Exception backupException) when (IsRecoverableReadError(backupException))
                    {
                    }
                }

                return new ConfigurationLoadResult(
                    LauncherConfiguration.CreateDefault(),
                    ConfigurationLoadState.ResetAfterCorruption,
                    "配置文件和备份均无法读取，已载入空白配置；原文件仍保留在数据目录中。");
            }
        }
        finally
        {
            ioLock.Release();
        }
    }

    /// <summary>
    /// 校验配置后使用临时文件和原子替换写入磁盘。
    /// </summary>
    /// <param name="configuration">需要保存的配置。</param>
    /// <param name="cancellationToken">用于取消写入的令牌。</param>
    public async Task SaveAsync(
        LauncherConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(configuration);
        await ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(DataDirectory);
            ConfigurationValidator.ValidateAndNormalize(configuration);
            var temporaryPath = $"{ConfigurationPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await WriteJsonAsync(configuration, temporaryPath, cancellationToken).ConfigureAwait(false);
                if (File.Exists(ConfigurationPath))
                {
                    try
                    {
                        fileReplacer(temporaryPath, ConfigurationPath, BackupPath, true);
                    }
                    catch (FileNotFoundException) when (!File.Exists(ConfigurationPath))
                    {
                        File.Move(temporaryPath, ConfigurationPath, overwrite: true);
                    }
                }
                else
                {
                    File.Move(temporaryPath, ConfigurationPath, overwrite: true);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            ioLock.Release();
        }
    }

    /// <summary>
    /// 将完整配置写入用户指定的导出文件。
    /// </summary>
    /// <param name="configuration">需要导出的配置。</param>
    /// <param name="destinationPath">导出文件路径。</param>
    /// <param name="cancellationToken">用于取消写入的令牌。</param>
    public async Task ExportAsync(
        LauncherConfiguration configuration,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ConfigurationValidator.ValidateAndNormalize(configuration);
        var fullPath = Path.GetFullPath(destinationPath);
        var parent = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        await WriteJsonAsync(configuration, fullPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 从用户选择的 JSON 文件读取并校验配置。
    /// </summary>
    /// <param name="sourcePath">导入文件路径。</param>
    /// <param name="cancellationToken">用于取消读取的令牌。</param>
    /// <returns>完成规范化的配置。</returns>
    public Task<LauncherConfiguration> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        return ReadAndValidateAsync(Path.GetFullPath(sourcePath), cancellationToken);
    }

    /// <summary>
    /// 释放配置存储使用的同步资源。
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        ioLock.Dispose();
    }

    /// <summary>
    /// 从 JSON 文件反序列化并校验配置。
    /// </summary>
    /// <param name="path">JSON 文件路径。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>完成校验的配置。</returns>
    private async Task<LauncherConfiguration> ReadAndValidateAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var configuration = await JsonSerializer.DeserializeAsync(
                stream,
                serializerContext.LauncherConfiguration,
                cancellationToken)
            .ConfigureAwait(false);
        if (configuration is null)
        {
            throw new InvalidDataException("配置文件没有包含有效内容。");
        }

        return ConfigurationValidator.ValidateAndNormalize(configuration);
    }

    /// <summary>
    /// 将配置写入文件并强制刷新到磁盘。
    /// </summary>
    /// <param name="configuration">需要写入的配置。</param>
    /// <param name="path">目标文件路径。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    private async Task WriteJsonAsync(
        LauncherConfiguration configuration,
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            ConfigurationWriteOptions);
        await JsonSerializer.SerializeAsync(
                stream,
                configuration,
                serializerContext.LauncherConfiguration,
                cancellationToken)
            .ConfigureAwait(false);
        stream.Flush(true);
    }

    /// <summary>
    /// 判断异常是否属于可通过备份恢复的配置读取错误。
    /// </summary>
    /// <param name="exception">读取配置时捕获的异常。</param>
    /// <returns>适合尝试备份恢复时返回 <see langword="true"/>。</returns>
    private static bool IsRecoverableReadError(Exception exception) =>
        exception is JsonException or InvalidDataException or IOException or UnauthorizedAccessException;
}
