using System.Windows;
using System.Windows.Input;
using Forms = System.Windows.Forms;
using PromptFlow.Models;
using PromptFlow.Services;

namespace PromptFlow;

public sealed class SettingsSaveEventArgs : EventArgs
{
    public AppSettings Settings { get; }
    public IReadOnlyList<string> Exclusions { get; }
    public SettingsSaveEventArgs(AppSettings settings, IReadOnlyList<string> exclusions) { Settings = settings; Exclusions = exclusions; }
}

public partial class SettingsWindow : Window
{
    private readonly AppSettings _initial;
    private readonly Func<int> _clearHistory;
    public event EventHandler<SettingsSaveEventArgs>? SaveRequested;

    public SettingsWindow(AppSettings settings, IEnumerable<string> exclusions, Func<int> clearHistory)
    {
        InitializeComponent();
        _initial = settings;
        _clearHistory = clearHistory;
        AutoStartCheck.IsChecked = settings.StartWithWindows;
        MonitorCheck.IsChecked = settings.MonitorEnabled;
        HotkeyBox.Text = settings.Hotkey;
        MaxHistoryBox.Text = settings.MaxHistoryItems.ToString();
        DataPathBox.Text = settings.DataDirectory;
        ExclusionsBox.Text = string.Join(Environment.NewLine, exclusions);
    }

    private void HotkeyBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) { e.Handled = true; return; }
        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None) { e.Handled = true; return; }
        HotkeyBox.Text = FormatHotkey(modifiers, FormatKey(key));
        e.Handled = true;
    }
    private void HotkeyBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not (MouseButton.XButton1 or MouseButton.XButton2)) return;
        var modifiers = Keyboard.Modifiers;
        if (modifiers != ModifierKeys.None) HotkeyBox.Text = FormatHotkey(modifiers, e.ChangedButton == MouseButton.XButton1 ? "XButton1" : "XButton2");
        e.Handled = true;
    }
    private static string FormatHotkey(ModifierKeys modifiers, string key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key); return string.Join("+", parts);
    }
    private static string FormatKey(Key key)
    {
        if (key >= Key.A && key <= Key.Z) return key.ToString();
        if (key >= Key.D0 && key <= Key.D9) return ((int)key - (int)Key.D0).ToString();
        if (key >= Key.NumPad0 && key <= Key.NumPad9) return ((int)key - (int)Key.NumPad0).ToString();
        if (key >= Key.F1 && key <= Key.F24) return key.ToString();
        return key switch { Key.Return => "Enter", Key.Escape => "Escape", Key.Space => "Space", Key.Tab => "Tab", Key.Back => "Back", Key.Delete => "Delete", Key.Insert => "Insert", Key.Home => "Home", Key.End => "End", Key.PageUp => "PageUp", Key.PageDown => "PageDown", Key.Up => "Up", Key.Down => "Down", Key.Left => "Left", Key.Right => "Right", _ => key.ToString() };
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void ChoosePath_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { SelectedPath = DataPathBox.Text };
        if (dialog.ShowDialog() == Forms.DialogResult.OK) DataPathBox.Text = dialog.SelectedPath;
    }
    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        AutoStartCheck.IsChecked = false; MonitorCheck.IsChecked = true; HotkeyBox.Text = "Ctrl+XButton1";
        MaxHistoryBox.Text = "500"; DataPathBox.Text = SettingsService.GetDefaultDataDirectory(); ExclusionsBox.Clear();
    }
    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show(this, "确定清空未收藏的历史记录？", "PromptFlow", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            var deletedCount = _clearHistory();
            System.Windows.MessageBox.Show(this, deletedCount == 0 ? "没有可清空的未收藏历史。" : $"已清空 {deletedCount} 条未收藏历史。", "PromptFlow", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(MaxHistoryBox.Text, out var max) || max < 1) max = 500;
        var hotkey = string.IsNullOrWhiteSpace(HotkeyBox.Text) ? _initial.Hotkey : HotkeyBox.Text.Trim();
        var settings = _initial with { StartWithWindows = AutoStartCheck.IsChecked == true, MonitorEnabled = MonitorCheck.IsChecked == true, Hotkey = hotkey, MaxHistoryItems = max, DataDirectory = DataPathBox.Text.Trim() };
        var exclusions = ExclusionsBox.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        SaveRequested?.Invoke(this, new SettingsSaveEventArgs(settings, exclusions)); Close();
    }
}
