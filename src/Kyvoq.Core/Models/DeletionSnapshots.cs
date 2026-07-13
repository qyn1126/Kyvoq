namespace Kyvoq.Core.Models;

/// <summary>
/// 保存已删除项目的原始位置和完整数据。
/// </summary>
/// <param name="GroupId">项目原来所属的分组标识。</param>
/// <param name="ItemIndex">项目在分组中的原始索引。</param>
/// <param name="Item">被删除的项目。</param>
public sealed record DeletedItemSnapshot(Guid GroupId, int ItemIndex, LauncherItem Item);

/// <summary>
/// 保存已删除分组的原始位置和完整数据。
/// </summary>
/// <param name="GroupIndex">分组在配置中的原始索引。</param>
/// <param name="Group">被删除的分组及其项目。</param>
public sealed record DeletedGroupSnapshot(int GroupIndex, LauncherGroup Group);
