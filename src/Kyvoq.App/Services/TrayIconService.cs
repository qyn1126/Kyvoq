using System.Drawing;
using System.Windows.Forms;

namespace Kyvoq.App.Services;

/// <summary>
/// 提供常驻通知区域图标和应用生命周期菜单。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon notifyIcon;
    private readonly ToolStripMenuItem hotkeysMenuItem;
    private readonly Icon? ownedIcon;
    private bool disposed;

    public event EventHandler? ShowRequested;

    public event EventHandler? ToggleHotkeysRequested;

    public event EventHandler? ExitRequested;

    /// <summary>
    /// 创建并显示 Kyvoq 通知区域图标。
    /// </summary>
    /// <param name="itemHotkeysEnabled">项目快捷键是否处于启用状态。</param>
    public TrayIconService(bool itemHotkeysEnabled)
    {
        ownedIcon = BrandIconFactory.CreateIcon();

        var menu = new ContextMenuStrip();
        var showMenuItem = new ToolStripMenuItem("显示 Kyvoq");
        showMenuItem.Click += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
        hotkeysMenuItem = new ToolStripMenuItem();
        hotkeysMenuItem.Click += (_, _) => ToggleHotkeysRequested?.Invoke(this, EventArgs.Empty);
        var exitMenuItem = new ToolStripMenuItem("退出");
        exitMenuItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(showMenuItem);
        menu.Items.Add(hotkeysMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitMenuItem);

        notifyIcon = new NotifyIcon
        {
            Icon = ownedIcon ?? SystemIcons.Application,
            Text = "Kyvoq 快速启动器",
            ContextMenuStrip = menu,
            Visible = true
        };
        notifyIcon.MouseClick += HandleMouseClick;
        SetItemHotkeysEnabled(itemHotkeysEnabled);
    }

    /// <summary>
    /// 更新托盘菜单中项目快捷键开关的文字。
    /// </summary>
    /// <param name="enabled">项目快捷键是否启用。</param>
    public void SetItemHotkeysEnabled(bool enabled)
    {
        hotkeysMenuItem.Text = enabled ? "暂停项目快捷键" : "启用项目快捷键";
    }

    /// <summary>
    /// 隐藏并释放通知区域图标。
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        notifyIcon.Visible = false;
        notifyIcon.MouseClick -= HandleMouseClick;
        notifyIcon.ContextMenuStrip?.Dispose();
        notifyIcon.Dispose();
        ownedIcon?.Dispose();
    }

    /// <summary>
    /// 处理托盘图标鼠标点击，左键直接显示主窗口。
    /// </summary>
    /// <param name="sender">通知图标。</param>
    /// <param name="eventArgs">鼠标事件参数。</param>
    private void HandleMouseClick(object? sender, MouseEventArgs eventArgs)
    {
        if (eventArgs.Button == MouseButtons.Left)
        {
            ShowRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
