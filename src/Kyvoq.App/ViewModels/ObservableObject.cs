using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Kyvoq.App.ViewModels;

/// <summary>
/// 为视图模型提供属性变更通知。
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 在属性值实际变化后写入新值并发送通知。
    /// </summary>
    /// <typeparam name="T">属性值类型。</typeparam>
    /// <param name="field">属性对应的字段。</param>
    /// <param name="value">准备写入的新值。</param>
    /// <param name="propertyName">调用方属性名称。</param>
    /// <returns>发生变化时返回 <see langword="true"/>。</returns>
    protected bool SetProperty<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>
    /// 发送指定属性的变更通知。
    /// </summary>
    /// <param name="propertyName">发生变化的属性名称。</param>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
