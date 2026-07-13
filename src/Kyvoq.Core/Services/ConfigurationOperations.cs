using Kyvoq.Core.Models;

namespace Kyvoq.Core.Services;

/// <summary>
/// 封装会同时影响多个分组或排序号的配置操作。
/// </summary>
public static class ConfigurationOperations
{
    /// <summary>
    /// 将项目移动到目标分组的指定位置。
    /// </summary>
    /// <param name="configuration">需要修改的配置。</param>
    /// <param name="itemId">待移动项目标识。</param>
    /// <param name="targetGroupId">目标分组标识。</param>
    /// <param name="targetIndex">目标索引；省略时追加到末尾。</param>
    /// <exception cref="KeyNotFoundException">找不到项目或目标分组时抛出。</exception>
    public static void MoveItem(
        LauncherConfiguration configuration,
        Guid itemId,
        Guid targetGroupId,
        int? targetIndex = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var sourceGroup = configuration.Groups.FirstOrDefault(group => group.Items.Any(item => item.Id == itemId))
            ?? throw new KeyNotFoundException("找不到需要移动的启动项目。");
        var targetGroup = configuration.Groups.FirstOrDefault(group => group.Id == targetGroupId)
            ?? throw new KeyNotFoundException("找不到目标分组。");
        var item = sourceGroup.Items.First(candidate => candidate.Id == itemId);
        sourceGroup.Items.Remove(item);

        var insertIndex = targetIndex ?? targetGroup.Items.Count;
        insertIndex = Math.Clamp(insertIndex, 0, targetGroup.Items.Count);
        targetGroup.Items.Insert(insertIndex, item);
        ReindexItems(sourceGroup);
        if (!ReferenceEquals(sourceGroup, targetGroup))
        {
            ReindexItems(targetGroup);
        }
    }

    /// <summary>
    /// 在同一分组中把项目移动到另一个项目之前。
    /// </summary>
    /// <param name="group">需要重排的分组。</param>
    /// <param name="itemId">待移动项目标识。</param>
    /// <param name="beforeItemId">作为插入位置的项目标识；为空时移动到末尾。</param>
    /// <exception cref="KeyNotFoundException">找不到待移动项目或定位项目时抛出。</exception>
    public static void ReorderItem(LauncherGroup group, Guid itemId, Guid? beforeItemId)
    {
        ArgumentNullException.ThrowIfNull(group);
        var item = group.Items.FirstOrDefault(candidate => candidate.Id == itemId)
            ?? throw new KeyNotFoundException("找不到需要排序的启动项目。");
        if (beforeItemId == itemId)
        {
            return;
        }

        group.Items.Remove(item);
        if (beforeItemId is null)
        {
            group.Items.Add(item);
        }
        else
        {
            var index = group.Items.FindIndex(candidate => candidate.Id == beforeItemId.Value);
            if (index < 0)
            {
                throw new KeyNotFoundException("找不到用于定位的启动项目。");
            }

            group.Items.Insert(index, item);
        }

        ReindexItems(group);
    }

    /// <summary>
    /// 调整分组在侧边栏中的位置。
    /// </summary>
    /// <param name="configuration">需要修改的配置。</param>
    /// <param name="groupId">待移动分组标识。</param>
    /// <param name="offset">相对移动距离，通常为 -1 或 1。</param>
    /// <returns>实际发生移动时返回 <see langword="true"/>。</returns>
    public static bool MoveGroup(LauncherConfiguration configuration, Guid groupId, int offset)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var index = configuration.Groups.FindIndex(group => group.Id == groupId);
        if (index < 0)
        {
            throw new KeyNotFoundException("找不到需要排序的分组。");
        }

        var targetIndex = Math.Clamp(index + offset, 0, configuration.Groups.Count - 1);
        if (targetIndex == index)
        {
            return false;
        }

        var group = configuration.Groups[index];
        configuration.Groups.RemoveAt(index);
        configuration.Groups.Insert(targetIndex, group);
        ReindexGroups(configuration);
        return true;
    }

    /// <summary>
    /// 删除指定项目并返回可用于撤销的快照。
    /// </summary>
    /// <param name="configuration">需要修改的配置。</param>
    /// <param name="itemId">待删除项目标识。</param>
    /// <returns>包含原分组、索引和项目数据的快照。</returns>
    /// <exception cref="KeyNotFoundException">找不到项目时抛出。</exception>
    public static DeletedItemSnapshot RemoveItem(LauncherConfiguration configuration, Guid itemId)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var group = configuration.Groups.FirstOrDefault(candidate => candidate.Items.Any(item => item.Id == itemId))
            ?? throw new KeyNotFoundException("找不到需要删除的启动项目。");
        var itemIndex = group.Items.FindIndex(item => item.Id == itemId);
        var item = group.Items[itemIndex];
        group.Items.RemoveAt(itemIndex);
        ReindexItems(group);
        return new DeletedItemSnapshot(group.Id, itemIndex, item);
    }

    /// <summary>
    /// 根据删除快照把项目恢复到原分组和原位置。
    /// </summary>
    /// <param name="configuration">需要修改的配置。</param>
    /// <param name="snapshot">项目删除快照。</param>
    /// <exception cref="KeyNotFoundException">原分组不存在时抛出。</exception>
    /// <exception cref="InvalidOperationException">项目标识已经存在时抛出。</exception>
    public static void RestoreItem(LauncherConfiguration configuration, DeletedItemSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (configuration.Groups.SelectMany(group => group.Items).Any(item => item.Id == snapshot.Item.Id))
        {
            throw new InvalidOperationException("无法恢复已经存在的启动项目。");
        }

        var group = configuration.Groups.FirstOrDefault(candidate => candidate.Id == snapshot.GroupId)
            ?? throw new KeyNotFoundException("找不到项目原来所属的分组。");
        group.Items.Insert(Math.Clamp(snapshot.ItemIndex, 0, group.Items.Count), snapshot.Item);
        ReindexItems(group);
    }

    /// <summary>
    /// 删除指定分组及其项目并返回可用于撤销的快照。
    /// </summary>
    /// <param name="configuration">需要修改的配置。</param>
    /// <param name="groupId">待删除分组标识。</param>
    /// <returns>包含原索引和完整分组数据的快照。</returns>
    /// <exception cref="InvalidOperationException">尝试删除唯一分组时抛出。</exception>
    /// <exception cref="KeyNotFoundException">找不到分组时抛出。</exception>
    public static DeletedGroupSnapshot RemoveGroup(LauncherConfiguration configuration, Guid groupId)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Groups.Count <= 1)
        {
            throw new InvalidOperationException("至少需要保留一个分组。");
        }

        var groupIndex = configuration.Groups.FindIndex(group => group.Id == groupId);
        if (groupIndex < 0)
        {
            throw new KeyNotFoundException("找不到需要删除的分组。");
        }

        var group = configuration.Groups[groupIndex];
        configuration.Groups.RemoveAt(groupIndex);
        ReindexGroups(configuration);
        return new DeletedGroupSnapshot(groupIndex, group);
    }

    /// <summary>
    /// 根据删除快照把分组及其项目恢复到原位置。
    /// </summary>
    /// <param name="configuration">需要修改的配置。</param>
    /// <param name="snapshot">分组删除快照。</param>
    /// <exception cref="InvalidOperationException">分组或项目标识已经存在时抛出。</exception>
    public static void RestoreGroup(LauncherConfiguration configuration, DeletedGroupSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(snapshot);
        var existingIds = configuration.Groups
            .SelectMany(group => group.Items.Select(item => item.Id).Prepend(group.Id))
            .ToHashSet();
        if (existingIds.Contains(snapshot.Group.Id)
            || snapshot.Group.Items.Any(item => existingIds.Contains(item.Id)))
        {
            throw new InvalidOperationException("无法恢复标识已经存在的分组或项目。");
        }

        configuration.Groups.Insert(
            Math.Clamp(snapshot.GroupIndex, 0, configuration.Groups.Count),
            snapshot.Group);
        ReindexGroups(configuration);
        ReindexItems(snapshot.Group);
    }

    /// <summary>
    /// 重新生成分组内项目的连续排序号。
    /// </summary>
    /// <param name="group">需要重排的分组。</param>
    private static void ReindexItems(LauncherGroup group)
    {
        for (var index = 0; index < group.Items.Count; index++)
        {
            group.Items[index].SortOrder = index;
        }
    }

    /// <summary>
    /// 重新生成配置中分组的连续排序号。
    /// </summary>
    /// <param name="configuration">需要重排的配置。</param>
    private static void ReindexGroups(LauncherConfiguration configuration)
    {
        for (var index = 0; index < configuration.Groups.Count; index++)
        {
            configuration.Groups[index].SortOrder = index;
        }
    }
}
