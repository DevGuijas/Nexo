using System.IO;
using System.Windows;

namespace Nexo.Support;
public partial class App : Application
{
    private Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        instance = new Mutex(true, "Local\\Nexo.Support." + Environment.UserName + (e.Args.Contains("--screenshot") ? ".Preview" : ""), out var first);
        if (!first) { MessageBox.Show("O Nexo já está aberto nesta sessão."); Shutdown(); return; }
        try
        {
            var store = new Store(e.Args.Contains("--screenshot") ? Path.Combine(AppContext.BaseDirectory, ".preview-data") : null);
            var window = new MainWindow(store);
            MainWindow = window;
            window.Show();
            if (e.Args.Contains("--screenshot")) window.CapturePreview();
        }
        catch (Exception ex) { MessageBox.Show("Não foi possível iniciar o Nexo. Os dados existentes foram preservados.\n" + ex.Message); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
