using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace Kyvoq.App.Tests.Controls;

/// <summary>
/// 验证图标网格在真实 WPF 布局中只实现视口附近的容器。
/// </summary>
public sealed class VirtualizingWrapPanelTests
{
    /// <summary>
    /// 验证绑定两千个项目后实际创建的视觉容器数量远小于总项目数。
    /// </summary>
    [Fact]
    public void Layout_ShouldRealizeOnlyVisibleItems()
    {
        Exception? failure = null;
        var realizedCount = 0;
        var realizedAfterScroll = 0;
        var distantItemRealized = false;
        var thread = new Thread(() =>
        {
            try
            {
                var panelFactory = new FrameworkElementFactory(typeof(VirtualizingWrapPanel));
                panelFactory.SetValue(VirtualizingWrapPanel.ItemSizeProperty, new Size(120, 120));
                var listBox = new ListBox
                {
                    Width = 620,
                    Height = 430,
                    ItemsSource = Enumerable.Range(0, 2000),
                    ItemsPanel = new ItemsPanelTemplate(panelFactory)
                };
                ScrollViewer.SetCanContentScroll(listBox, true);
                VirtualizingPanel.SetIsVirtualizing(listBox, true);
                VirtualizingPanel.SetVirtualizationMode(listBox, VirtualizationMode.Recycling);
                var window = new Window
                {
                    Width = 640,
                    Height = 460,
                    Left = -10000,
                    Top = -10000,
                    Opacity = 0,
                    WindowStyle = WindowStyle.None,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    Content = listBox
                };
                window.Show();
                window.UpdateLayout();
                var panel = FindVisualChild<VirtualizingWrapPanel>(listBox)
                    ?? throw new InvalidOperationException("未创建虚拟化换行面板。");
                realizedCount = VisualTreeHelper.GetChildrenCount(panel);
                listBox.ScrollIntoView(1500);
                window.UpdateLayout();
                realizedAfterScroll = VisualTreeHelper.GetChildrenCount(panel);
                distantItemRealized = listBox.ItemContainerGenerator.ContainerFromIndex(1500) is not null;
                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));

        Assert.Null(failure);
        Assert.InRange(realizedCount, 1, 100);
        Assert.InRange(realizedAfterScroll, 1, 100);
        Assert.True(distantItemRealized);
    }

    /// <summary>
    /// 递归查找指定类型的首个 WPF 视觉子元素。
    /// </summary>
    /// <typeparam name="T">需要查找的视觉元素类型。</typeparam>
    /// <param name="parent">搜索起点。</param>
    /// <returns>找到的视觉元素；不存在时返回空。</returns>
    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            var nested = FindVisualChild<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}
