using System.Text.Json.Serialization;
using Kyvoq.Core.Models;

namespace Kyvoq.Core.Persistence;

/// <summary>
/// 为启动配置提供编译期生成的 JSON 元数据，减少首次序列化开销。
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(LauncherConfiguration))]
internal sealed partial class KyvoqJsonSerializerContext : JsonSerializerContext;
