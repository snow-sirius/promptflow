using System.Threading;
using System.Windows;

namespace PromptFlow;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "PromptFlow.SingleInstance", out var created);
        if (!created) { Shutdown(); return; }
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e) { _mutex?.Dispose(); base.OnExit(e); }
}
