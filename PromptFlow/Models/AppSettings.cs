namespace PromptFlow.Models;

public sealed record AppSettings
{
    public bool StartWithWindows { get; set; }
    public string Hotkey { get; set; } = "Ctrl+XButton1";
    public string DataDirectory { get; set; } = string.Empty;
    public int MaxHistoryItems { get; set; } = 500;
    public bool MonitorEnabled { get; set; } = true;
    public int MaxImageBytes { get; set; } = 10 * 1024 * 1024;
}
