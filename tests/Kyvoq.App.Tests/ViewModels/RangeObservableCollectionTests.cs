using System.Collections.Specialized;
using Kyvoq.App.ViewModels;

namespace Kyvoq.App.Tests.ViewModels;

/// <summary>
/// 验证批量可观察集合不会为每个筛选结果触发布局通知。
/// </summary>
public sealed class RangeObservableCollectionTests
{
    /// <summary>
    /// 验证替换大量元素时只发送一次 Reset 集合通知。
    /// </summary>
    [Fact]
    public void ReplaceAll_ShouldRaiseSingleResetNotification()
    {
        var collection = new RangeObservableCollection<int>();
        var notifications = new List<NotifyCollectionChangedEventArgs>();
        collection.CollectionChanged += (_, eventArgs) => notifications.Add(eventArgs);

        collection.ReplaceAll(Enumerable.Range(0, 2000));

        Assert.Equal(2000, collection.Count);
        var notification = Assert.Single(notifications);
        Assert.Equal(NotifyCollectionChangedAction.Reset, notification.Action);
    }
}
