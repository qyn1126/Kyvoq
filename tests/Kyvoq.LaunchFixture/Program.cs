namespace Kyvoq.LaunchFixture;

/// <summary>
/// 为启动服务集成测试记录真实进程参数、程序目录和环境变量。
/// </summary>
public static class Program
{
    /// <summary>
    /// 将当前目录、命令行参数和启动环境中的测试及代理变量写入结果文件。
    /// </summary>
    /// <param name="args">第一个参数为输出路径，其余参数为待验证内容。</param>
    /// <returns>参数有效并写入成功时返回 0。</returns>
    public static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            return 2;
        }

        File.WriteAllLines(
            args[0],
            [
                Environment.CurrentDirectory,
                string.Join('|', args.Skip(1)),
                Environment.GetEnvironmentVariable("KYVOQ_TEST_VALUE") ?? string.Empty,
                Environment.GetEnvironmentVariable("HTTP_PROXY") ?? string.Empty,
                Environment.GetEnvironmentVariable("HTTPS_PROXY") ?? string.Empty,
                Environment.GetEnvironmentVariable("ALL_PROXY") ?? string.Empty
            ]);
        return 0;
    }
}
