using System.Threading;
using System.Windows;
using System.IO;

namespace PromptFlow;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            try
            {
                var logPath = Path.Combine(Path.GetTempPath(), "promptflow-error.log");
                File.AppendAllText(logPath, $"[{DateTime.Now:O}]\n{args.Exception}\n\n");
                System.Windows.MessageBox.Show($"操作失败，程序仍在运行。\n{args.Exception.Message}", "PromptFlow", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
        };
        _mutex = new Mutex(true, "PromptFlow.SingleInstance", out var created);
        if (!created) { Shutdown(); return; }
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e) { _mutex?.Dispose(); base.OnExit(e); }
}
