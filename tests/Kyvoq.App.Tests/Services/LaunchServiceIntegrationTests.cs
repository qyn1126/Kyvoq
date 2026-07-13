using System.IO;
using Kyvoq.Core.Models;
using Kyvoq.Core.Services;

namespace Kyvoq.App.Tests.Services;

/// <summary>
/// 验证启动服务跨越真实 Windows 进程边界后的参数、程序目录和环境变量行为。
/// </summary>
public sealed class LaunchServiceIntegrationTests : IDisposable
{
    private readonly string temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "Kyvoq.App.Tests",
        Guid.NewGuid().ToString("N"));

    /// <summary>
    /// 删除测试进程产生的临时结果目录。
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(temporaryDirectory))
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    /// <summary>
    /// 验证包含空格的参数、程序目录和环境变量会被真实子进程完整接收。
    /// </summary>
    [Fact]
    public async Task LaunchAsync_ShouldPassArgumentsEnvironmentAndTargetDirectoryToRealProcess()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(temporaryDirectory);
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Kyvoq.LaunchFixture.exe");
        var outputPath = Path.Combine(temporaryDirectory, "launch-result.txt");
        Assert.True(File.Exists(fixturePath), $"找不到启动测试夹具：{fixturePath}");
        var item = new LauncherItem
        {
            Name = "启动夹具",
            Target = fixturePath,
            Arguments = $"\"{outputPath}\" \"first value\" second",
            EnvironmentVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["KYVOQ_TEST_VALUE"] = "environment value"
            }
        };

        var result = await new LaunchService().LaunchAsync(item, cancellationToken);
        await WaitForReadableFileAsync(outputPath, cancellationToken);
        var lines = await File.ReadAllLinesAsync(outputPath, cancellationToken);

        Assert.True(result.IsSuccessful, result.ErrorMessage);
        Assert.Equal(Path.GetDirectoryName(fixturePath), lines[0]);
        Assert.Equal("first value|second", lines[1]);
        Assert.Equal("environment value", lines[2]);
    }

    /// <summary>
    /// 在限制时间内等待测试子进程完成结果文件写入并释放文件句柄。
    /// </summary>
    /// <param name="path">期望出现的结果文件。</param>
    /// <param name="cancellationToken">测试取消令牌。</param>
    /// <returns>文件可稳定读取时完成的任务。</returns>
    private static async Task WaitForReadableFileAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return;
            }
            catch (IOException)
            {
            }

            await Task.Delay(25, cancellationToken);
        }

        Assert.Fail("测试子进程没有在预期时间内完成结果文件写入。");
    }
}
