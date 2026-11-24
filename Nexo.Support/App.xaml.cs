using System.IO;
using System.Windows;

namespace Nexo.Support;
public partial class App : Application
{
    private Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        // #142: o leitor de sensores elevado não abre janela, não usa o mutex nem carrega dados do usuário; lê, grava e termina.
        if (e.Args.Length > 0 && e.Args[0] == ElevatedSensorReader.Argument)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(ElevatedSensorReader.Run(e.Args));
            return;
        }
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var preview = e.Args.Contains("--screenshot") || e.Args.Contains("--tray-smoke");
        instance = new Mutex(true, "Local\\Nexo.Support." + Environment.UserName + (preview ? ".Preview" : ""), out var first);
        if (!first) { MessageBox.Show("O Nexo já está aberto nesta sessão."); Shutdown(); return; }
        try
        {
            var store = new Store(preview ? Path.Combine(AppContext.BaseDirectory, ".preview-data") : null, directAccess: true);
            // #143: a prévia aceita "--theme glacial" para capturar o tema claro sem gravar a preferência.
            var themeArgument = Array.IndexOf(e.Args, "--theme");
            ThemeManager.Apply(preview && themeArgument >= 0 && themeArgument + 1 < e.Args.Length ? e.Args[themeArgument + 1] : store.Data.Theme);
            var window = new MainWindow(store);
            var workArea = SystemParameters.WorkArea;
            window.Width = Math.Min(window.Width, Math.Max(window.MinWidth, workArea.Width));
            window.Height = Math.Min(window.Height, Math.Max(window.MinHeight, workArea.Height));
            MainWindow = window;
            window.Show();
            if (e.Args.Contains("--screenshot")) window.CapturePreview();
            if (e.Args.Contains("--tray-smoke")) window.VerifyTraySmoke();
        }
        catch (Exception ex) { MessageBox.Show("Não foi possível iniciar o Nexo. Os dados existentes foram preservados.\n" + ex.Message); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
