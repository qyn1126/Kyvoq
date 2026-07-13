namespace Kyvoq.App.Services;

/// <summary>
/// 表示一批全局快捷键的注册结果。
/// </summary>
public sealed record HotkeyRegistrationReport(bool MainWindowRegistered, IReadOnlySet<Guid> ConflictingItemIds);
