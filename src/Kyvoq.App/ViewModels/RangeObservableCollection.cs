using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Kyvoq.App.ViewModels;

/// <summary>
/// 支持以单次 Reset 通知替换全部元素，避免筛选时产生大量逐项布局。
/// </summary>
/// <typeparam name="T">集合元素类型。</typeparam>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>
    /// 使用新序列替换全部元素，并只发送一次集合重置通知。
    /// </summary>
    /// <param name="items">新的完整元素序列。</param>
    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
