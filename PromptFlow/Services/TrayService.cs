using System.Drawing;
using System.IO;
using Forms = System.Windows.Forms;

namespace PromptFlow.Services;

public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Icon _applicationIcon;
    public event EventHandler? OpenRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler? ToggleMonitorRequested;
    public bool MonitorEnabled { get => _monitorEnabled; set { _monitorEnabled=value; if(_toggleItem is not null) _toggleItem.Text=value?"暂停监听":"恢复监听"; } }
    private bool _monitorEnabled = true;
    private Forms.ToolStripMenuItem? _toggleItem;
    public TrayService()
    {
        _applicationIcon = LoadApplicationIcon();
        _icon = new Forms.NotifyIcon { Icon = _applicationIcon, Visible = true, Text = "PromptFlow AI 剪贴板" };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开剪贴板", null, (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty));
        _toggleItem = new Forms.ToolStripMenuItem("暂停监听", null, (_, _) => ToggleMonitorRequested?.Invoke(this, EventArgs.Empty)); menu.Items.Add(_toggleItem);
        menu.Items.Add("设置", null, (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));
        _icon.ContextMenuStrip = menu;
    }
    private static Icon LoadApplicationIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "PromptFlow.ico");
            using var icon = new Icon(path);
            return (Icon)icon.Clone();
        }
        catch
        {
            return (Icon)SystemIcons.Application.Clone();
        }
    }
    public void Dispose(){_icon.Visible=false;_icon.Dispose();_applicationIcon.Dispose();}
}
