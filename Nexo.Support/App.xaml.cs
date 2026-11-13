using System.IO;
using System.Windows;

namespace Nexo.Support;
public partial class App : Application
{
    private Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var preview = e.Args.Contains("--screenshot") || e.Args.Contains("--tray-smoke");
        instance = new Mutex(true, "Local\\Nexo.Support." + Environment.UserName + (preview ? ".Preview" : ""), out var first);
        if (!first) { MessageBox.Show("O Nexo já está aberto nesta sessão."); Shutdown(); return; }
        try
        {
            var store = new Store(preview ? Path.Combine(AppContext.BaseDirectory, ".preview-data") : null, directAccess: true);
            var window = new MainWindow(store);
            MainWindow = window;
            window.Show();
            if (e.Args.Contains("--screenshot")) window.CapturePreview();
            if (e.Args.Contains("--tray-smoke")) window.VerifyTraySmoke();
        }
        catch (Exception ex) { MessageBox.Show("Não foi possível iniciar o Nexo. Os dados existentes foram preservados.\n" + ex.Message); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
