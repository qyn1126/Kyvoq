namespace Kyvoq.Core.Persistence;

/// <summary>
/// 表示配置文件加载后的状态。
/// </summary>
public enum ConfigurationLoadState
{
    Loaded,
    CreatedDefault,
    RecoveredFromBackup,
    ResetAfterCorruption
}
