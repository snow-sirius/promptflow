using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
using PromptFlow.Models;
using System.IO;

namespace PromptFlow.Services;

public sealed class SettingsService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string _settingsPath;
    public AppSettings Current { get; private set; }

    public SettingsService()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlow");
        Directory.CreateDirectory(root);
        _settingsPath = Path.Combine(root, "settings.json");
        Current = Load();
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath));
                if (loaded is not null)
                {
                    if (string.IsNullOrWhiteSpace(loaded.DataDirectory)) loaded.DataDirectory = GetDefaultDataDirectory();
                    loaded.ShortcutFolderSlots ??= [null, null, null];
                    while (loaded.ShortcutFolderSlots.Count < 3) loaded.ShortcutFolderSlots.Add(null);
                    if (loaded.ShortcutFolderSlots.Count > 3) loaded.ShortcutFolderSlots = loaded.ShortcutFolderSlots.Take(3).ToList();
                    return loaded;
                }
            }
        }
        catch { }
        return new AppSettings { DataDirectory = GetDefaultDataDirectory() };
    }

    public void Save(AppSettings settings)
    {
        Current = settings;
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        SetAutoStart(settings.StartWithWindows);
    }

    public static string GetDefaultDataDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlow", "data");

    public void SetAutoStart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (key is null) return;
        if (enabled)
        {
            var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(exe)) key.SetValue("PromptFlow", $"\"{exe}\" --background");
        }
        else key.DeleteValue("PromptFlow", false);
    }
}
