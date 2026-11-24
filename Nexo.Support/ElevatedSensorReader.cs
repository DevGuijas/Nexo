using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nexo.Support;

public sealed record ElevatedTemperature(double Celsius, string Sensor, string Source, DateTimeOffset Time);
public sealed record ElevatedReadResult(string State, string Message, ElevatedTemperature? Reading = null);
/// <summary>State: "canceled" (UAC), "timeout" ou "exited" (com o código de saída).</summary>
public sealed record ElevatedRun(string State, int? ExitCode = null);

public interface IElevatedSensorSystem
{
    Task<ElevatedRun> Run(string output, TimeSpan timeout, CancellationToken token);
    /// <summary>Motivo para não confiar no arquivo e na cadeia de pastas protegidas, ou null.</summary>
    string? Untrusted(string file);
    string ReadText(string file);
}

/// <summary>
/// Leitor de sensores elevado (#142): "Nexo.exe --read-sensors &lt;arquivo&gt;" lê só a LibreHardwareMonitor (CPU), grava um JSON
/// em %ProgramData%\NexoSupport\sensors e termina, sem janela, sem dados do usuário e sem outros argumentos. A pasta é criada
/// e conferida pelo próprio processo elevado, com o descritor da cópia protegida da rede e do debloat (dono Administradores,
/// herança desligada, SYSTEM e Administradores com controle total, Usuários só leitura). O app sem admin pede um UAC por
/// clique, espera até 20 s e só aceita o JSON da pasta conferida, do pedido atual e com valor plausível.
/// </summary>
public static class ElevatedSensorReader
{
    public const string Argument = "--read-sensors";
    public const int TimeoutSeconds = 20;
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);
    public const string Source = HardwareSensors.TemperatureSource + ", processo separado com administrador";
    public static string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NexoSupport", "sensors");
    private static readonly Regex FileName = new(@"^reading-[0-9a-f]{32}\.json\z", RegexOptions.CultureInvariant);
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public static string NewOutputPath(string root) => Path.Combine(root, "reading-" + Guid.NewGuid().ToString("N") + ".json");

    /// <summary>Só "--read-sensors &lt;arquivo&gt;", com o arquivo direto na pasta protegida e no nome que o Nexo gera. Devolve o motivo da recusa ou null.</summary>
    public static string? ValidateArguments(IReadOnlyList<string> args, string root, out string output)
    {
        output = "";
        if (args.Count != 2 || !string.Equals(args[0], Argument, StringComparison.Ordinal)) return "O modo de leitura aceita só " + Argument + " e o arquivo de saída.";
        var path = args[1];
        if (path.Length is 0 or > 260 || !Path.IsPathFullyQualified(path)) return "Arquivo de saída inválido.";
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return "Arquivo de saída inválido."; }
        // Nada de "..", barras trocadas ou prefixos: o caminho dado já tem de ser o normalizado.
        if (!string.Equals(full, path, StringComparison.Ordinal)) return "Arquivo de saída fora da forma esperada.";
        var parent = Path.GetDirectoryName(full);
        if (parent == null || !string.Equals(parent, Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase))
            return "O arquivo de saída precisa ficar na pasta protegida " + root + ".";
        if (!FileName.IsMatch(Path.GetFileName(full))) return "Nome do arquivo de saída fora do formato do Nexo.";
        output = full;
        return null;
    }

    /// <summary>JSON gravado pelo modo elevado; o Time é o da leitura.</summary>
    public static string Document(TemperatureReading reading, DateTimeOffset time) => JsonSerializer.Serialize(new
    {
        Format = 1,
        Time = time.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
        Temperature = new { reading.State, reading.Celsius, reading.Sensor, Source, reading.Detail }
    });

    /// <summary>
    /// Lê o JSON do modo elevado. Vale só com estado "read", valor entre 0 e 125 °C, hora depois do pedido (com 1 min de folga
    /// de relógio) e com menos de 5 min. Fora disso devolve null e o motivo; nunca um valor aproximado.
    /// </summary>
    public static ElevatedTemperature? Parse(string text, DateTimeOffset notBefore, DateTimeOffset now, out string detail)
    {
        detail = "O resultado da leitura com administrador é inválido e foi ignorado.";
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Format", out var format) || format.ValueKind != JsonValueKind.Number || format.GetInt32() != 1) return null;
            if (!root.TryGetProperty("Time", out var timeValue) || timeValue.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(timeValue.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time)) return null;
            if (!root.TryGetProperty("Temperature", out var temperature) || temperature.ValueKind != JsonValueKind.Object) return null;
            var state = Text(temperature, "State");
            if (state != "read")
            {
                var reason = Text(temperature, "Detail");
                detail = "A leitura com administrador não obteve temperatura válida" + (reason.Length > 0 ? ": " + reason.TrimEnd('.') : "") + ".";
                return null;
            }
            if (!temperature.TryGetProperty("Celsius", out var celsius) || celsius.ValueKind != JsonValueKind.Number) return null;
            var value = celsius.GetDouble();
            if (!double.IsFinite(value) || value <= 0 || value > HardwareSensors.MaxPlausibleCelsius) return null;
            var sensor = Text(temperature, "Sensor");
            if (sensor.Length == 0) return null;
            if (time < notBefore - ClockSkew || time > now + ClockSkew || now - time > MaxAge)
            {
                detail = "O resultado da leitura com administrador não é deste pedido ou tem mais de 5 minutos; foi ignorado.";
                return null;
            }
            return new ElevatedTemperature(Math.Round(value, 1), sensor, Source, time);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    private static string Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? (value.GetString() is { } text && text.Length <= 200 ? text : "") : "";

    /// <summary>Nome do sensor sem o identificador entre parênteses: "Core (Tctl/Tdie)".</summary>
    public static string SensorName(string sensor)
    {
        var at = sensor.LastIndexOf(" (/", StringComparison.Ordinal);
        return at > 0 ? sensor[..at] : sensor;
    }

    public static string Describe(ElevatedTemperature reading) =>
        reading.Celsius.ToString("F1", PtBr) + " °C · " + SensorName(reading.Sensor) + " · leitura com administrador às " + reading.Time.ToLocalTime().ToString("HH:mm", PtBr);

    private static ElevatedTemperature? last;
    /// <summary>Guarda a última leitura elevada válida para o relatório de saúde, que a usa só enquanto tiver menos de 5 min.</summary>
    public static void Remember(ElevatedTemperature reading) => Volatile.Write(ref last, reading);
    public static void Forget() => Volatile.Write(ref last, null);
    public static ElevatedTemperature? Fresh(DateTimeOffset now) => Volatile.Read(ref last) is { } reading && now - reading.Time <= MaxAge && reading.Time <= now + ClockSkew ? reading : null;

    /// <summary>Um UAC por chamada; nada fica em laço. Cancelado, tempo esgotado ou JSON inválido viram estado, nunca valor.</summary>
    public static async Task<ElevatedReadResult> ReadAsync(IElevatedSensorSystem system, string root, Func<DateTimeOffset> clock, CancellationToken token)
    {
        var output = NewOutputPath(root);
        var started = clock();
        var run = await system.Run(output, TimeSpan.FromSeconds(TimeoutSeconds), token);
        if (run.State == "canceled") return new("canceled", "A autorização de administrador foi cancelada. Nenhuma temperatura foi lida.");
        if (run.State != "exited") return new("timeout", $"A leitura com administrador não terminou em {TimeoutSeconds} s. Nenhuma temperatura foi mostrada.");
        if (run.ExitCode != 0) return new("failed", ExitMessage(run.ExitCode));
        if (system.Untrusted(output) is { } reason) return new("untrusted", "O resultado não está protegido como o Nexo exige e foi ignorado: " + reason);
        string text;
        try { text = system.ReadText(output); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new("invalid", "Não foi possível ler o resultado da leitura com administrador: " + ex.Message); }
        var reading = Parse(text, started, clock(), out var detail);
        if (reading == null) return new("invalid", detail);
        Remember(reading);
        return new("read", Describe(reading), reading);
    }

    public static string ExitMessage(int? code) => code switch
    {
        2 => "O modo de leitura recusou o pedido (argumentos ou caminho fora da pasta protegida).",
        3 => "O modo de leitura não estava com administrador e não leu nada.",
        4 => "A pasta protegida %ProgramData%\\NexoSupport\\sensors não passou na conferência; nada foi lido.",
        5 => "O modo de leitura não conseguiu gravar o resultado na pasta protegida.",
        _ => $"O modo de leitura terminou com o código {code?.ToString(CultureInfo.InvariantCulture) ?? "desconhecido"}; nenhuma temperatura foi mostrada."
    };

    /// <summary>Entrada do modo elevado. Códigos: 0 gravado, 2 argumentos recusados, 3 sem admin, 4 pasta não confiável, 5 falha ao gravar.</summary>
    public static int Run(string[] args)
    {
        if (ValidateArguments(args, Root, out var output) != null) return 2;
        if (!OperatingSystem.IsWindows() || !IsAdministrator()) return 3;
        try { if (PrepareProtectedFolder(Root) != null) return 4; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SystemException) { return 4; }
        var snapshot = new HealthSnapshot();
        var time = DateTimeOffset.UtcNow;
        try
        {
            // Fora do contexto do Dispatcher: a espera não pode depender da thread que está esperando.
            var json = Task.Run(() => EmbeddedCpuTemperature.ReadAsync(CancellationToken.None)).GetAwaiter().GetResult();
            time = DateTimeOffset.UtcNow;
            using var document = JsonDocument.Parse(json);
            HardwareSensors.ReadTemperature(document.RootElement, snapshot, time);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            HardwareSensors.TemperatureUnavailable(snapshot, time, ex is TimeoutException ? $"O sensor não respondeu em {HardwareSensors.TemperatureTimeoutSeconds} s." : "A leitura falhou: " + ex.Message);
        }
        try
        {
            RemoveStale(Root, output);
            return WriteProtected(output, Document(snapshot.CpuTemperature!, time)) == null ? 0 : 5;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SystemException) { return 5; }
    }

    [SupportedOSPlatform("windows")]
    internal static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static readonly (string Sid, FileSystemRights Rights)[] Entries =
        [("S-1-5-18", FileSystemRights.FullControl), ("S-1-5-32-544", FileSystemRights.FullControl), ("S-1-5-32-545", FileSystemRights.ReadAndExecute)];

    /// <summary>NexoSupport e sensors: pasta nova nasce com o descritor; pasta existente nunca é adotada sem passar na conferência.</summary>
    [SupportedOSPlatform("windows")]
    internal static string? PrepareProtectedFolder(string root)
    {
        foreach (var folder in new[] { Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(root))!, root })
        {
            if (!Directory.Exists(folder) && !File.Exists(folder))
            {
                var security = new DirectorySecurity();
                security.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
                security.SetAccessRuleProtection(true, false);
                foreach (var (sid, rights) in Entries)
                    security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                security.CreateDirectory(folder);
            }
            if (NetworkRecovery.UntrustedItem(new DirectoryInfo(folder)) is { } reason) return reason;
        }
        return null;
    }

    /// <summary>Arquivo criado já com o descritor (CreateNew num temporário da pasta protegida), trocado na mesma pasta e conferido.</summary>
    [SupportedOSPlatform("windows")]
    internal static string? WriteProtected(string path, string text)
    {
        var temporary = path + ".tmp";
        if (File.Exists(temporary)) File.Delete(temporary);
        var security = new FileSecurity();
        security.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
        security.SetAccessRuleProtection(true, false);
        foreach (var (sid, rights) in Entries) security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), rights, AccessControlType.Allow));
        using (var stream = new FileInfo(temporary).Create(FileMode.CreateNew, FileSystemRights.Write | FileSystemRights.ReadData | FileSystemRights.Synchronize, FileShare.None, 4096, FileOptions.None, security))
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
        }
        File.Move(temporary, path, true);
        return NetworkRecovery.UntrustedItem(new FileInfo(path));
    }

    // Só os arquivos com o nome do Nexo, direto na pasta e que não são link; nada recursivo com elevação.
    private static void RemoveStale(string root, string keep)
    {
        foreach (var file in Directory.EnumerateFiles(root, "reading-*.json*"))
        {
            var name = Path.GetFileName(file);
            if (string.Equals(file, keep, StringComparison.OrdinalIgnoreCase) || !(FileName.IsMatch(name) || name.EndsWith(".json.tmp", StringComparison.Ordinal) && FileName.IsMatch(name[..^4]))) continue;
            if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) continue;
            File.Delete(file);
        }
    }
}

/// <summary>O Windows de verdade: o próprio Nexo.exe com ShellExecute "runas" e a conferência da cópia protegida da rede.</summary>
public sealed class WindowsElevatedSensorSystem : IElevatedSensorSystem
{
    public async Task<ElevatedRun> Run(string output, TimeSpan timeout, CancellationToken token)
    {
        var executable = Environment.ProcessPath ?? throw new IOException("Caminho do Nexo indisponível.");
        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = ElevatedSensorReader.Argument + " \"" + output + "\""
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return new("canceled"); }
        if (process == null) throw new IOException("O Windows não iniciou a leitura com administrador.");
        using (process)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(timeout);
            try { await process.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new("timeout"); }
            return new("exited", process.ExitCode);
        }
    }

    public string? Untrusted(string file)
    {
        if (!OperatingSystem.IsWindows()) return "disponível só no Windows";
        try
        {
            var directory = Path.GetDirectoryName(file)!;
            var items = new FileSystemInfo[] { new DirectoryInfo(Path.GetDirectoryName(directory)!), new DirectoryInfo(directory), new FileInfo(file) };
            foreach (var item in items)
            {
                if (!item.Exists) return item.FullName + ": não existe";
                if (NetworkRecovery.UntrustedItem(item) is { } reason) return reason;
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SystemException) { return file + ": não foi possível ler a ACL (" + ex.Message + ")"; }
    }

    public string ReadText(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 16_384) throw new IOException("Resultado maior que o limite.");
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        return reader.ReadToEnd();
    }
}
