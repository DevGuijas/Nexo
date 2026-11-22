using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
namespace Nexo.Support;

/// <summary>
/// Implementação real da #127. O inventário e a remoção de apps rodam no Windows PowerShell 5.1 sem elevação (o módulo Appx
/// não é confiável no PowerShell 7); o ponto de restauração, os serviços, as tarefas e o Recall vão pelo executor elevado
/// da otimização, que grava o resultado numa pasta protegida. O estado anterior desses itens fica em
/// %ProgramData%\NexoSupport\debloat, gravado e lido só pelo executor elevado.
/// </summary>
public sealed class WindowsDebloatSystem : IDebloatSystem
{
    public int Build => Environment.OSVersion.Version.Build;
    public IDebloatRegistry Registry { get; } = new CurrentUserRegistry();
    public Task<string> RunUserScriptAsync(string script, int timeoutSeconds, CancellationToken token) => SupportService.Powershell(script, token, timeoutSeconds);
    public Task<string> RunElevatedAsync(string script, CancellationToken token) => OptimizationDiskExecutor.RunAsync(DebloatScripts.ForElevation(script), token);
    // A mesma conferência da cópia protegida da rede: NexoSupport, debloat, a pasta da aplicação e o undo.json.
    public DebloatProtectedUndo ReadProtectedUndo(string runId) =>
        DebloatProtectedStore.Read(DebloatProtectedStore.Root, runId, (directory, file) => NetworkRecovery.UntrustedReasonOnWindows(directory, [file]));

    private sealed class CurrentUserRegistry : IDebloatRegistry
    {
        public DebloatRegistryState Read(string key, string name)
        {
            using var handle = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key, false);
            var value = handle?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (handle == null || value == null) return DebloatRegistryState.Absent;
            return handle.GetValueKind(name) switch
            {
                RegistryValueKind.DWord => new(true, DebloatValueKind.DWord, Convert.ToInt32(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)),
                RegistryValueKind.String => new(true, DebloatValueKind.String, (string)value),
                RegistryValueKind.Binary => new(true, DebloatValueKind.Binary, BitConverter.ToString((byte[])value)),
                var other => throw new InvalidOperationException($"{name} tem o tipo {other}, que o Nexo não restaura; nada foi alterado."),
            };
        }

        public void Write(string key, string name, DebloatValueKind kind, string value)
        {
            using var handle = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(key, true);
            switch (kind)
            {
                case DebloatValueKind.DWord: handle.SetValue(name, int.Parse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture), RegistryValueKind.DWord); break;
                case DebloatValueKind.String: handle.SetValue(name, value, RegistryValueKind.String); break;
                case DebloatValueKind.Binary: handle.SetValue(name, value.Length == 0 ? [] : value.Split('-').Select(b => byte.Parse(b, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray(), RegistryValueKind.Binary); break;
            }
        }

        public void Delete(string key, string name)
        {
            using var handle = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key, true);
            handle?.DeleteValue(name, false);
        }

        // Avisa o Windows e o Explorer para aplicarem aparência, barra de tarefas e extensões sem reiniciar.
        public void NotifySettingsChanged()
        {
            SendMessageTimeout(new IntPtr(0xffff), 0x001A, UIntPtr.Zero, "WindowMetrics", 0x0002, 5000, out _);
            SendMessageTimeout(new IntPtr(0xffff), 0x001A, UIntPtr.Zero, "TraySettings", 0x0002, 5000, out _);
            SendMessageTimeout(new IntPtr(0xffff), 0x001A, UIntPtr.Zero, null, 0x0002, 5000, out _);
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string? lParam, uint flags, uint timeout, out UIntPtr result);
        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
    }
}
