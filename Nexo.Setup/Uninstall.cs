using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Nexo.Setup;

public sealed record UninstallTargets(string AppRoot, string CacheRoot, string DesktopShortcut, string StartMenuShortcut);

public static class Uninstaller
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    public static UninstallTargets CurrentUserTargets()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new UninstallTargets(
            Path.Combine(local, "Programs", "Nexo"),
            Path.Combine(local, "Nexo", "InstallerCache"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Nexo.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Start Menu", "Programs", "Nexo.lnk"));
    }

    public static void RemoveInstallation(UninstallTargets targets, InstallAudit audit)
    {
        var appRoot = Path.GetFullPath(targets.AppRoot).TrimEnd(Path.DirectorySeparatorChar);
        var cacheRoot = Path.GetFullPath(targets.CacheRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (!Path.GetFileName(appRoot).Equals("Nexo", StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(cacheRoot).Equals("InstallerCache", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(Path.Combine(appRoot, "Nexo.exe"))
            || !File.Exists(Path.Combine(appRoot, "NexoUninstall.exe")))
            throw new InvalidDataException("A pasta não contém uma instalação completa do Nexo.");
        audit.Record("uninstall", "started", new { appRoot, cacheRoot });
        DeleteTree(appRoot);
        audit.Record("application", "removed", new { path = appRoot });
        foreach (var suffix in new[] { ".new", ".previous" })
        {
            var staged = appRoot + suffix;
            if (Directory.Exists(staged))
            {
                DeleteTree(staged);
                audit.Record("application_stage", "removed", new { path = staged });
            }
        }
        RemoveOwnedShortcut(targets.DesktopShortcut, appRoot, audit, "shortcut_desktop");
        RemoveOwnedShortcut(targets.StartMenuShortcut, appRoot, audit, "shortcut_start_menu");
        if (Directory.Exists(cacheRoot))
        {
            DeleteTree(cacheRoot);
            audit.Record("installer_cache", "removed", new { path = cacheRoot });
        }
        else audit.Record("installer_cache", "absent");
        audit.Record("uninstall", "completed", new { preserved = new[] { "Ollama", "qwen3:8b", "InstallerLogs" } });
    }

    private static void RemoveOwnedShortcut(string shortcutPath, string appRoot, InstallAudit audit, string eventName)
    {
        if (!File.Exists(shortcutPath)) { audit.Record(eventName, "absent"); return; }
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new IOException("Atalhos do Windows indisponíveis.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        var target = (string)shortcut.TargetPath;
        if (string.IsNullOrWhiteSpace(target) || !Path.IsPathFullyQualified(target)
            || !Path.GetFullPath(target).Equals(Path.Combine(appRoot, "Nexo.exe"), StringComparison.OrdinalIgnoreCase))
        {
            audit.Record(eventName, "preserved_other_target");
            return;
        }
        File.Delete(shortcutPath);
        audit.Record(eventName, "removed", new { path = shortcutPath });
    }

    private static void DeleteTree(string root)
    {
        if (!Directory.Exists(root)) return;
        if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Pasta de instalação redirecionada; remoção cancelada.");
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(entry);
                else DeleteTree(entry);
            }
            else File.Delete(entry);
        }
        Directory.Delete(root);
    }

    public static void StartWorker()
    {
        var source = Environment.ProcessPath ?? throw new IOException("Desinstalador indisponível.");
        if (!Path.GetFileName(source).Equals("NexoUninstall.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Abra NexoUninstall.exe dentro da pasta do Nexo.");
        var targets = CurrentUserTargets();
        if (!Path.GetFullPath(source).Equals(Path.Combine(targets.AppRoot, "NexoUninstall.exe"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Desinstalador fora da pasta do aplicativo.");
        if (Process.GetProcessesByName("Nexo").Any(process => !process.HasExited))
            throw new IOException("Feche o Nexo antes de desinstalar.");
        var workerDirectory = Path.Combine(Path.GetTempPath(), "Nexo-Uninstall");
        Directory.CreateDirectory(workerDirectory);
        var token = Guid.NewGuid().ToString("N");
        var workerPath = Path.Combine(workerDirectory, "NexoUninstall-" + token + ".exe");
        var tokenPath = Path.Combine(workerDirectory, token + ".token");
        File.Copy(source, workerPath);
        File.WriteAllText(tokenPath, token);
        try
        {
            var start = new ProcessStartInfo(workerPath) { UseShellExecute = false };
            start.ArgumentList.Add("--uninstall-worker");
            start.ArgumentList.Add(token);
            start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _ = Process.Start(start) ?? throw new IOException("Não foi possível iniciar a limpeza.");
        }
        catch
        {
            File.Delete(tokenPath);
            File.Delete(workerPath);
            throw;
        }
    }

    public static async Task<int> RunWorkerAsync(string[] args)
    {
        if (args.Length != 3 || args[0] != "--uninstall-worker" || !Regex.IsMatch(args[1], "^[0-9a-f]{32}$")
            || !int.TryParse(args[2], out var parentPid)) return 2;
        var workerDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Nexo-Uninstall"));
        var expected = Path.Combine(workerDirectory, "NexoUninstall-" + args[1] + ".exe");
        if (!Path.GetFullPath(Environment.ProcessPath ?? "").Equals(expected, StringComparison.OrdinalIgnoreCase)) return 2;
        var tokenPath = Path.Combine(workerDirectory, args[1] + ".token");
        if (!File.Exists(tokenPath) || File.ReadAllText(tokenPath) != args[1]) return 2;
        File.Delete(tokenPath);
        using var audit = new InstallAudit(mode: "uninstall");
        try
        {
            try
            {
                using var parent = Process.GetProcessById(parentPid);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await parent.WaitForExitAsync(timeout.Token);
            }
            catch (ArgumentException) { }
            audit.Record("uninstall", "launcher_exited");
            RemoveInstallation(CurrentUserTargets(), audit);
            audit.Complete("success", new { preserved = new[] { "Ollama", "qwen3:8b", "InstallerLogs" } });
            MessageBoxW(IntPtr.Zero, "Nexo e seus atalhos foram removidos. Ollama, o modelo e os logs locais foram preservados.\n\nLog: " + audit.Path,
                "Nexo desinstalado", 0x40);
            return 0;
        }
        catch (Exception ex)
        {
            audit.Record("error", "failure", new { type = ex.GetType().Name, message = ex.Message });
            audit.Complete("failure", new { errorType = ex.GetType().Name });
            MessageBoxW(IntPtr.Zero, "A desinstalação não foi concluída: " + ex.Message + "\n\nLog: " + audit.Path,
                "Nexo", 0x10);
            return 1;
        }
    }
}

public sealed class UninstallWindow : Window
{
    public int ExitCode { get; private set; } = 1;

    public UninstallWindow()
    {
        Title = "Desinstalar Nexo";
        Width = 500;
        Height = 310;
        MinWidth = MaxWidth = Width;
        MinHeight = MaxHeight = Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.White;
        var panel = new StackPanel { Margin = new Thickness(28) };
        panel.Children.Add(new TextBlock { Text = "Desinstalar o Nexo?", FontSize = 22, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = "Serão removidos o aplicativo, os atalhos e o cache de instalação deste usuário. O Ollama, o modelo qwen3:8b e os logs serão preservados.",
            FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 18, 0, 0)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Logs: " + InstallAudit.DefaultDirectory,
            FontSize = 11, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0)
        });
        var status = new TextBlock { Foreground = Brushes.DarkRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 26, 0, 0) };
        var cancel = new Button { Content = "Cancelar", Width = 100, Height = 34, Margin = new Thickness(0, 0, 10, 0) };
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(cancel);
        var remove = new Button { Content = "Desinstalar", Width = 110, Height = 34 };
        remove.Click += (_, _) =>
        {
            try { Uninstaller.StartWorker(); ExitCode = 0; Close(); }
            catch (Exception ex) { status.Text = ex.Message; }
        };
        buttons.Children.Add(remove);
        panel.Children.Add(buttons);
        Content = panel;
    }
}
