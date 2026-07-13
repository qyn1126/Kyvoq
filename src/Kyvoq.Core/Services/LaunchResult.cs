namespace Kyvoq.Core.Services;

/// <summary>
/// 表示一次启动请求的结果。
/// </summary>
public sealed record LaunchResult(bool IsSuccessful, string ErrorMessage)
{
    /// <summary>
    /// 创建成功结果。
    /// </summary>
    /// <returns>成功结果。</returns>
    public static LaunchResult Success() => new(true, string.Empty);

    /// <summary>
    /// 创建包含错误消息的失败结果。
    /// </summary>
    /// <param name="message">适合展示给用户的错误消息。</param>
    /// <returns>失败结果。</returns>
    public static LaunchResult Failure(string message) => new(false, message);
}
