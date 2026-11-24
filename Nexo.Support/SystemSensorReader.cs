using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nexo.Support;

/// <summary>
/// Temperatura vista pelo app a partir do live.json do leitor do sistema. State: "off" (leitor não instalado), "waiting" (instalado,
/// ainda sem leitura desde a inicialização), "live" (fresca, com valor), "error" (fresca, sem valor), "stale" (sem resposta) ou
/// "ignored" (arquivo fora da proteção ou inválido).
/// </summary>
public sealed record LiveTemperature(string State, string Message, double? Celsius = null, string Sensor = "", DateTimeOffset? Time = null, string Version = "")
{
    public bool Valid => State == "live" && Celsius.HasValue;
}

/// <summary>State: "done", "canceled" (UAC recusado) ou "failed".</summary>
public sealed record SensorReaderResult(string State, string Message);

public interface ILiveSensorSystem
{
    bool Installed();
    /// <summary>Motivo para não confiar no arquivo e nas pastas protegidas acima dele, ou null.</summary>
    string? Untrusted(string file);
    /// <summary>Conteúdo do arquivo, ou null quando ele não existe.</summary>
    string? ReadText(string file, int maxBytes);
    DateTimeOffset BootTime();
    /// <summary>Quando a cópia do leitor foi gravada (ativação ou atualização), ou null sem ela.</summary>
    DateTimeOffset? InstalledTime();
}

/// <summary>
/// Leitor de sensores do sistema (#142, opção 2): uma cópia mínima do Nexo em %ProgramFiles%\Nexo\Sensors, rodando como SYSTEM
/// pela tarefa agendada "Nexo Sensores" desde a inicialização. Ela lê só a temperatura da CPU pela LibreHardwareMonitor a cada 2 s
/// e grava, de forma atômica, %ProgramData%\NexoSupport\sensors\live.json (pasta protegida). O app sem admin lê esse arquivo depois
/// de conferir dono e ACL, e só usa a leitura com menos de 10 s. A instalação, a atualização e a remoção são só por clique, com um
/// UAC cada, pelo executor elevado supervisionado (PowerShell com a fixação do PSModulePath, #131). O modo nunca roda a partir da
/// pasta do usuário: fora da cópia protegida, ele sai com código 2 antes de ler qualquer coisa.
/// </summary>
public static class SystemSensorReader
{
    public const string LoopArgument = "--sensor-loop";
    public const string TaskName = "Nexo Sensores";
    public const string Executable = "Nexo.exe";
    public const string ExtractFolder = "extract";
    public const string SourceLabel = "leitor de sensores do sistema";
    public const string ReportSource = "leitor de sensores do sistema (LibreHardwareMonitor como SYSTEM, cópia protegida em Arquivos de Programas, ao vivo)";
    public const string FolderLabel = @"%ProgramFiles%\Nexo\Sensors";
    public const string LiveLabel = @"%ProgramData%\NexoSupport\sensors\live.json";
    public const int MaxBytes = 4096;
    public const string WaitingText = "aguardando o leitor…";
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(5);
    // Logo depois de ligar o PC, o live.json ainda é o de antes do desligamento: por 2 min isso é "aguardando", não "sem resposta".
    public static readonly TimeSpan BootGrace = TimeSpan.FromMinutes(2);
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly Regex CopyName = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}\z", RegexOptions.CultureInvariant);
    private static readonly Regex VersionFormat = new(@"^\d{1,4}\.\d{1,4}\.\d{1,6}\z", RegexOptions.CultureInvariant);

    public static string InstallFolder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Nexo", "Sensors");
    public static string LiveFile { get; } = Path.Combine(ElevatedSensorReader.Root, "live.json");

    // ---------- textos ----------
    public const string ActivateLabel = "Ativar temperatura ao vivo";
    public const string DeactivateLabel = "Desativar temperatura ao vivo";
    public const string UpdateLabel = "Atualizar leitor";
    public const string ConfirmationTitle = "Ativar temperatura ao vivo";
    public static readonly string ConfirmationDetails = string.Join('\n',
        "A temperatura da CPU passa a aparecer ao vivo no Nexo, sem abrir o Nexo como administrador.",
        "",
        "O que acontece ao autorizar:",
        "1. Um leitor mínimo de sensores (os arquivos do Nexo necessários só para a leitura) é copiado para uma pasta protegida em Arquivos de Programas, " + FolderLabel + ". Cada arquivo é conferido por SHA-256 contra a origem.",
        "2. Ele roda como tarefa do sistema (SYSTEM), \"" + TaskName + "\", desde a inicialização do Windows. Lê só a temperatura da CPU a cada 2 s e grava só esse valor em " + LiveLabel + ". Não abre janela nem lê dados seus.",
        "3. O Windows vai pedir autorização de administrador (UAC) uma vez. Depois disso, inclusive ao reiniciar o PC, não há novo pedido.",
        "",
        "Para desativar: Saúde do PC > \"" + DeactivateLabel + "\" (pede o UAC uma vez), que remove a tarefa e a pasta.");
    public const string DeactivateTitle = "Desativar temperatura ao vivo";
    public static readonly string DeactivateDetails = string.Join('\n',
        "Remove a tarefa do sistema \"" + TaskName + "\", a pasta " + FolderLabel + " e o arquivo da última leitura (" + LiveLabel + ").",
        "O Windows vai pedir autorização de administrador (UAC) uma vez.",
        "O PawnIO continua instalado; para removê-lo, use " + PawnIoSetup.Uninstall + ".");
    public const string UpdateTitle = "Atualizar leitor de sensores";

    /// <summary>
    /// O "Atualizar leitor" aparece com versão diferente ou, na mesma versão, com outra compilação: o ProductVersion do Nexo.exe
    /// (a versão informativa, que o SDK do .NET 8 completa com "+commit" num checkout git) diferente da do Nexo aberto. Assim, uma
    /// cópia antiga da mesma versão (a da tarefa com cmd.exe, #142 P9) também pode ser trocada. Sem o "+commit" nas duas, vale só a
    /// versão, como antes.
    /// </summary>
    public static bool Outdated(string installedVersion, string? installedBuild, string currentVersion, string? currentBuild) =>
        installedVersion != currentVersion || (!string.IsNullOrWhiteSpace(installedBuild) && !string.IsNullOrWhiteSpace(currentBuild) && !string.Equals(installedBuild.Trim(), currentBuild.Trim(), StringComparison.Ordinal));
    public static string UpdateDetails(string installed) => string.Join('\n',
        "Troca a cópia do leitor em " + FolderLabel + " (versão " + installed + ") pela deste Nexo (" + AppVersion.Current + "), conferindo o SHA-256 de cada arquivo, e reinicia a tarefa \"" + TaskName + "\".",
        "O Windows vai pedir autorização de administrador (UAC) uma vez.");

    // Defeito da validação de 23/11: com o PawnIO instalado, a leitura sem admin dá zero e a tela mostrava "descartada" em laranja.
    public const string AdminNeededTitle = "A temperatura precisa de permissão de administrador";
    public const string AdminNeededDetail = "O PawnIO está instalado, mas o Nexo roda sem administrador e o Windows não libera o sensor para ele. Ative a temperatura ao vivo ou leia uma vez com administrador.";
    public const string AdminNeededShort = "precisa de permissão de administrador. Ative a temperatura ao vivo na Saúde do PC.";
    public const string ReportAdminNeeded = "o PawnIO está instalado, mas o Nexo sem administrador não tem acesso ao sensor, o que não indica defeito no driver. A temperatura ao vivo e a leitura com administrador são ativadas pelo usuário na Saúde do PC";

    /// <summary>PawnIO instalado e o sensor só devolvendo zero no processo sem admin: falta de permissão, não leitura descartada.</summary>
    public static bool NeedsAdministrator(TemperatureReading? reading, bool pawnIoInstalled) =>
        pawnIoInstalled && reading is { State: "invalid" } && reading.Detail == HardwareSensors.ZeroDetail;

    // ---------- modo --sensor-loop ----------

    /// <summary>
    /// Só "--sensor-loop", sem mais nada, e só a partir de %ProgramFiles%\Nexo\Sensors\Nexo.exe. No executável single-file, nenhum
    /// arquivo pode ter sido extraído: extraído, ele iria para o %TEMP% do SYSTEM (C:\Windows\Temp\.net), onde um usuário comum
    /// consegue criar pastas e plantar DLLs. A extração aparece na lista de pastas de busca nativa que o host entrega ao runtime
    /// (NATIVE_DLL_SEARCH_DIRECTORIES): sem extração, ela só tem a pasta do executável; com extração, ganha a pasta extraída. O .NET 8
    /// não publica outra propriedade com esse caminho (não há BUNDLE_EXTRACTION_PATH). Fora do single-file (pasta do build), a lista
    /// não é conferida. Devolve o motivo da recusa ou null.
    /// </summary>
    public static string? ValidateLoop(IReadOnlyList<string> args, string? processPath, string installFolder, bool singleFile, string? nativeSearchDirectories)
    {
        if (args.Count != 1 || !string.Equals(args[0], LoopArgument, StringComparison.Ordinal)) return "O leitor de sensores aceita só " + LoopArgument + ", sem outros argumentos.";
        if (string.IsNullOrEmpty(installFolder) || !Path.IsPathFullyQualified(installFolder)) return "Pasta protegida do leitor indisponível.";
        if (string.IsNullOrEmpty(processPath) || !Path.IsPathFullyQualified(processPath)) return "Caminho do executável indisponível.";
        string folder, full;
        try { folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installFolder)); full = Path.GetFullPath(processPath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return "Caminho do executável inválido."; }
        var expected = Path.Combine(folder, Executable);
        if (!string.Equals(full, processPath, StringComparison.Ordinal) || !string.Equals(full, expected, StringComparison.OrdinalIgnoreCase))
            return "O leitor de sensores só roda a partir de " + expected + "; nunca da pasta do usuário.";
        if (singleFile && Extracted(nativeSearchDirectories, folder) is { } extracted)
            return "O leitor de sensores não roda com arquivos extraídos do single-file (" + extracted + "); só com as DLLs nativas ao lado do Nexo.exe em " + folder + ".";
        return null;
    }

    /// <summary>Primeira pasta de busca nativa que não é a pasta protegida, ou o motivo de a lista faltar; null quando só há ela.</summary>
    private static string? Extracted(string? nativeSearchDirectories, string folder)
    {
        var entries = (nativeSearchDirectories ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Length == 0) return "lista de pastas nativas do runtime indisponível";
        foreach (var entry in entries)
        {
            string full;
            try { full = Path.IsPathFullyQualified(entry) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry)) : ""; }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { full = ""; }
            if (!string.Equals(full, folder, StringComparison.OrdinalIgnoreCase)) return entry;
        }
        return null;
    }

    public static bool SingleFile => string.IsNullOrEmpty(typeof(SystemSensorReader).Assembly.Location);
    public const string NativeSearchProperty = "NATIVE_DLL_SEARCH_DIRECTORIES";

    /// <summary>
    /// Códigos: 2 recusado (argumentos, caminho ou arquivos extraídos do single-file), 3 não é SYSTEM, 4 pasta não confiável,
    /// 5 a busca de DLLs não foi restringida (SensorLoopEntry), 6 o sensor não abriu; 0 outra instância já roda. Chamado só pelo
    /// SensorLoopEntry, depois do SetDefaultDllDirectories.
    /// </summary>
    public static int RunLoop(string[] args) =>
        RunLoop(args, Environment.ProcessPath, InstallFolder, SingleFile, AppContext.GetData(NativeSearchProperty) as string);

    internal static int RunLoop(string[] args, string? processPath, string installFolder, bool singleFile, string? nativeSearchDirectories)
    {
        if (ValidateLoop(args, processPath, installFolder, singleFile, nativeSearchDirectories) != null) return 2;
        if (!OperatingSystem.IsWindows() || !IsSystem()) return 3;
        if (UntrustedInstall(installFolder) != null) return 4;
        using var single = new Mutex(true, @"Global\Nexo.Sensors", out var first);
        if (!first) return 0;
        try { if (ElevatedSensorReader.PrepareProtectedFolder(ElevatedSensorReader.Root) != null) return 4; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SystemException) { return 4; }
        try { using var self = Process.GetCurrentProcess(); self.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
        return Loop();
    }

    [SupportedOSPlatform("windows")]
    private static bool IsSystem()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true;
    }

    [SupportedOSPlatform("windows")]
    internal static string? UntrustedInstall(string installFolder)
    {
        try
        {
            var items = new FileSystemInfo[] { new DirectoryInfo(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(installFolder))!), new DirectoryInfo(installFolder), new FileInfo(Path.Combine(installFolder, Executable)) };
            foreach (var item in items)
            {
                if (!item.Exists) return item.FullName + ": não existe";
                if (NetworkRecovery.UntrustedItem(item) is { } reason) return reason;
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SystemException) { return installFolder + ": não foi possível ler a ACL (" + ex.Message + ")"; }
    }

    // A sessão da LibreHardwareMonitor abre uma vez e é lida a cada 2 s; o arquivo é sempre o mesmo (substituído), nunca cresce.
    [SupportedOSPlatform("windows")]
    private static int Loop()
    {
        var version = AppVersion.Current;
        var pid = Environment.ProcessId;
        ICpuSensorSession? session = null;
        try
        {
            while (true)
            {
                var time = DateTimeOffset.UtcNow;
                TemperatureReading reading;
                if (session == null && !PawnIoDriver.Installed())
                    reading = new("no_source", Source: HardwareSensors.TemperatureSource, Time: time, Detail: "O driver PawnIO não está instalado.");
                else
                {
                    try
                    {
                        if (session == null)
                        {
                            var created = EmbeddedCpuTemperature.CreateSession();
                            try { created.Open(); }
                            catch (Exception ex) when (ex is not OutOfMemoryException)
                            {
                                // LHM 0.9.6 não reabre com segurança no mesmo processo: grava o erro e sai; a tarefa reinicia o leitor.
                                try { created.Dispose(); } catch (Exception dispose) when (dispose is not OutOfMemoryException) { }
                                Write(LiveDocument(new("error", Source: HardwareSensors.TemperatureSource, Time: time, Detail: "O sensor não abriu: " + Short(ex.Message)), time, version, pid));
                                return 6;
                            }
                            session = created;
                        }
                        var snapshot = new HealthSnapshot();
                        using var document = JsonDocument.Parse(EmbeddedCpuTemperature.SensorsJson(session.ReadSensors()));
                        HardwareSensors.ReadTemperature(document.RootElement, snapshot, time);
                        reading = snapshot.CpuTemperature ?? new("error", Source: HardwareSensors.TemperatureSource, Time: time, Detail: "Resposta sem dados de temperatura.");
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        reading = new("error", Source: HardwareSensors.TemperatureSource, Time: time, Detail: "A leitura falhou: " + Short(ex.Message));
                    }
                }
                Write(LiveDocument(reading, time, version, pid));
                Thread.Sleep(Interval);
            }
        }
        finally { session?.Dispose(); }
    }

    [SupportedOSPlatform("windows")]
    private static void Write(string text)
    {
        // Temporário criado já protegido na mesma pasta e trocado por Move: quem lê vê o arquivo antigo ou o novo, nunca metade.
        try { ElevatedSensorReader.WriteProtected(LiveFile, text); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SystemException) { }
    }

    private static string Short(string detail) => detail.Length <= 200 ? detail : detail[..200];

    /// <summary>live.json: valor (só com estado "read"), sensor, horário UTC, versão do leitor e pid.</summary>
    public static string LiveDocument(TemperatureReading reading, DateTimeOffset time, string version, int pid) => JsonSerializer.Serialize(new
    {
        Format = 1,
        Kind = "live",
        Time = time.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
        reading.State,
        Celsius = reading.State == "read" ? reading.Celsius : null,
        Sensor = reading.State == "read" ? reading.Sensor : "",
        Detail = Short(reading.Detail),
        Version = version,
        Pid = pid
    });

    // ---------- leitura pelo app ----------

    public static LiveTemperature Current() => Evaluate(new WindowsLiveSensorSystem(), DateTimeOffset.UtcNow);

    public static LiveTemperature Evaluate(ILiveSensorSystem system, DateTimeOffset now)
    {
        try
        {
            if (!system.Installed()) return new("off", "");
            var text = system.ReadText(LiveFile, MaxBytes);
            if (text == null)
            {
                // Sem nenhum live.json, "aguardando" vale só nos 2 min depois da inicialização ou da ativação; depois disso o leitor
                // não gravou nada (tarefa que sai com código 2, 3, 4 ou 6, ou que nem abre) e a tela diz que ele não respondeu (@qa).
                var boot = system.BootTime();
                var since = system.InstalledTime() is { } installed && installed > boot ? installed : boot;
                return now - since > BootGrace
                    ? new("stale", "leitor de sensores sem resposta desde " + since.ToLocalTime().ToString("HH:mm", PtBr) + ", sem nenhuma leitura gravada")
                    : new("waiting", WaitingText);
            }
            if (text.Length > MaxBytes) return Ignored("maior que o limite de " + MaxBytes + " bytes");
            if (system.Untrusted(LiveFile) is { } reason) return Ignored(reason);
            return Parse(text, now, system.BootTime());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Ignored("não foi possível ler: " + Short(ex.Message)); }
    }

    private static LiveTemperature Ignored(string reason) => new("ignored", "o arquivo do leitor de sensores foi ignorado (" + reason + ")");

    public static LiveTemperature Parse(string text, DateTimeOffset now, DateTimeOffset boot)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Format", out var format) || format.ValueKind != JsonValueKind.Number || !format.TryGetInt32(out var number) || number != 1
                || Text(root, "Kind") != "live") return Ignored("formato desconhecido");
            if (!DateTimeOffset.TryParse(Text(root, "Time"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time)) return Ignored("sem horário");
            if (!root.TryGetProperty("Pid", out var pid) || pid.ValueKind != JsonValueKind.Number || !pid.TryGetInt32(out var id) || id <= 0) return Ignored("sem pid");
            var version = Text(root, "Version");
            if (!VersionFormat.IsMatch(version)) return Ignored("versão inválida");
            if (time > now + ClockSkew) return Ignored("horário no futuro");
            if (now - time > FreshFor)
            {
                if (time < boot && now - boot <= BootGrace) return new("waiting", WaitingText, Version: version);
                return new("stale", "leitor de sensores sem resposta desde " + time.ToLocalTime().ToString("HH:mm", PtBr), Time: time, Version: version);
            }
            var state = Text(root, "State");
            if (state != "read")
            {
                var detail = Text(root, "Detail").TrimEnd('.');
                return new("error", "o leitor de sensores do sistema não obteve temperatura válida" + (detail.Length > 0 ? ": " + detail : "") , Time: time, Version: version);
            }
            if (!root.TryGetProperty("Celsius", out var celsius) || celsius.ValueKind != JsonValueKind.Number) return Ignored("sem valor");
            var value = celsius.GetDouble();
            if (!double.IsFinite(value) || value <= 0 || value > HardwareSensors.MaxPlausibleCelsius) return Ignored("valor fora da faixa");
            var sensor = Text(root, "Sensor");
            if (sensor.Length == 0) return Ignored("sem sensor");
            var reading = new LiveTemperature("live", "", Math.Round(value, 1), sensor, time, version);
            return reading with { Message = Describe(reading) };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return Ignored("conteúdo inválido"); }
    }

    private static string Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? (value.GetString() is { } text && text.Length <= 200 ? text : "") : "";

    public static string Describe(LiveTemperature reading) =>
        reading.Celsius!.Value.ToString("F1", PtBr) + " °C · " + ElevatedSensorReader.SensorName(reading.Sensor) + " · ao vivo, " + SourceLabel + ", às "
        + reading.Time!.Value.ToLocalTime().ToString("HH:mm:ss", PtBr);

    /// <summary>Texto e tom do estado do leitor na tela; null quando o leitor não está instalado.</summary>
    public static (string Text, string Tone)? Line(LiveTemperature live) => live.State switch
    {
        "live" => ("Temperatura da CPU: " + live.Message, "Success"),
        "waiting" => ("Temperatura: " + WaitingText, "Neutral"),
        "stale" or "error" or "ignored" => ("Temperatura da CPU: " + live.Message, "Warning"),
        _ => null
    };

    /// <summary>
    /// Linha da temperatura no "Estado do computador" da Início e na Saúde do PC. Com o leitor instalado, vale o live.json lido na
    /// montagem da tela (sem esperar o monitor nem o primeiro tique); sem ele, a leitura do monitor, com o texto neutro quando falta
    /// permissão. Null quando não há nada a mostrar.
    /// </summary>
    public static (string Text, string Tone)? TemperatureText(HealthSnapshot? latest, LiveTemperature live, bool pawnIoInstalled)
    {
        if (Line(live) is { } line) return line;
        if (latest == null) return null;
        if (NeedsAdministrator(latest.CpuTemperature, pawnIoInstalled)) return ("Temperatura da CPU: " + AdminNeededShort, "Neutral");
        return ("Temperatura da CPU: " + HealthReportPresentation.DescribeTemperature(latest), latest.CpuTemperature?.State is "error" or "invalid" ? "Warning" : "Neutral");
    }

    // ---------- instalação, atualização e remoção (elevadas) ----------

    /// <summary>
    /// Arquivos copiados: no Nexo publicado (single-file), o Nexo.exe e as .dll nativas do primeiro nível; na pasta do build, os .exe
    /// e .dll do primeiro nível mais o Nexo.runtimeconfig.json e o Nexo.deps.json. Subpastas (runtimes, ThirdParty) não vão: o modo
    /// de leitura não as usa. O single-file publicado sem IncludeNativeLibrariesForSelfExtract (o comando do README) deixa as nativas
    /// do WPF (wpfgfx_cor3.dll, PresentationNative_cor3.dll…) ao lado do .exe; sem elas na pasta protegida, o Nexo.exe como SYSTEM
    /// as procuraria pela ordem de busca de DLL do Windows, que chega às pastas do PATH (@qa).
    /// </summary>
    public static IReadOnlyList<string> FilesToCopy(bool singleFile, IEnumerable<string> names)
    {
        if (singleFile)
            return [Executable, .. names.Where(name => CopyName.IsMatch(name) && !name.Contains("..", StringComparison.Ordinal) && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Take(400)];
        var list = names.Where(name => CopyName.IsMatch(name) && !name.Contains("..", StringComparison.Ordinal)
                && (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Nexo.runtimeconfig.json", StringComparison.OrdinalIgnoreCase) || name.Equals("Nexo.deps.json", StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (!list.Contains(Executable, StringComparer.OrdinalIgnoreCase) || !list.Contains("Nexo.dll", StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("A pasta do Nexo em execução não tem os arquivos do leitor (Nexo.exe e Nexo.dll). Nada foi alterado.");
        if (list.Count > 400) throw new InvalidOperationException("A pasta do Nexo em execução tem arquivos demais para o leitor. Nada foi alterado.");
        return list;
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary>
    /// Script elevado de instalação e de atualização. Começa com a fixação do PSModulePath (#131) e usa as funções da cópia
    /// protegida da rede (ProtectedFolderScript): a pasta nasce com o descritor; uma pasta existente com ACL frouxa ou que seja link
    /// é recusada antes de qualquer alteração. Cada arquivo é aberto pelo processo elevado sem permitir escrita, o SHA-256 é lido
    /// desse mesmo handle, os bytes são copiados dele para um temporário já protegido, e o hash da cópia é conferido antes da troca.
    /// </summary>
    public static string InstallScript(string source, IReadOnlyList<string> files, string protectedFunctions)
    {
        if (files.Count == 0 || files.Any(name => !CopyName.IsMatch(name) || name.Contains("..", StringComparison.Ordinal))) throw new ArgumentException("Lista de arquivos inválida.", nameof(files));
        if (!Path.IsPathFullyQualified(source)) throw new ArgumentException("Pasta de origem inválida.", nameof(source));
        return PowerShellModulePath.Guard + "\n$ErrorActionPreference='Stop'\n" + protectedFunctions + Common
            + "$nxSource=" + Quote(Path.TrimEndingDirectorySeparator(source)) + "\n"
            + "$nxFiles=@(" + string.Join(",", files.Select(Quote)) + ")\n"
            + InstallBody;
    }

    /// <summary>Script elevado de remoção: para e remove a tarefa, apaga a pasta (sem seguir links) e o live.json.</summary>
    public static string RemoveScript(string protectedFunctions) =>
        PowerShellModulePath.Guard + "\n$ErrorActionPreference='Stop'\n" + protectedFunctions + Common + RemoveBody;

    private const string Common = """

        if(-not (New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Elevação necessária. Nada foi alterado.'}
        $nxBase=[Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
        if((Get-Item -LiteralPath $nxBase -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'A pasta Arquivos de Programas é um link. Nada foi alterado.'}
        $nxParent=Join-Path $nxBase 'Nexo'
        $nxTarget=Join-Path $nxParent 'Sensors'
        $nxExe=Join-Path $nxTarget 'Nexo.exe'
        $nxExtract=Join-Path $nxTarget 'extract'
        $nxTask='Nexo Sensores'
        $nxLive=Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'NexoSupport\sensors\live.json'
        function Test-NexoLink($path) { (Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint }
        # Todo Nexo.exe cujo executável é exatamente o da cópia protegida (o Windows não diferencia maiúsculas). A tarefa antiga
        # (cmd.exe /c ... Nexo.exe) deixava o Nexo.exe órfão quando parava: o Stop-ScheduledTask encerrava só o cmd.exe.
        function Get-NexoSensorProcess {
          @(Get-CimInstance -ClassName Win32_Process -Filter "Name='Nexo.exe'" -ErrorAction Stop | Where-Object { $_.ExecutablePath -and [string]::Equals([string]$_.ExecutablePath, $nxExe, [StringComparison]::OrdinalIgnoreCase) })
        }
        function Stop-NexoSensorLoop {
          if(Get-ScheduledTask -TaskName $nxTask -TaskPath '\' -ErrorAction SilentlyContinue){ Stop-ScheduledTask -TaskName $nxTask -TaskPath '\' -ErrorAction SilentlyContinue }
          for($i=0; $i -lt 20; $i++){
            $running=@(Get-NexoSensorProcess)
            if($running.Count -eq 0){ return }
            if($i % 4 -eq 0){ foreach($process in $running){ Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue } }
            Start-Sleep -Milliseconds 500
          }
          throw 'O leitor de sensores não terminou a tempo. Nada mais foi alterado.'
        }
        # Remoção sem seguir links: qualquer link dentro da pasta para tudo antes de apagar.
        function Assert-NexoTree($path) {
          foreach($child in @(Get-ChildItem -LiteralPath $path -Force)){
            if($child.Attributes -band [IO.FileAttributes]::ReparsePoint){ throw ('Há um link dentro da pasta do leitor (' + $child.FullName + '). Nada foi removido.') }
            if($child.PSIsContainer){ Assert-NexoTree $child.FullName }
          }
        }
        function Remove-NexoTree($path) {
          foreach($child in @(Get-ChildItem -LiteralPath $path -Force)){
            if($child.Attributes -band [IO.FileAttributes]::ReparsePoint){ throw ('Há um link dentro da pasta do leitor (' + $child.FullName + ').') }
            if($child.PSIsContainer){ Remove-NexoTree $child.FullName } else { Remove-Item -LiteralPath $child.FullName -Force }
          }
          Remove-Item -LiteralPath $path -Force
        }

        """;

    private const string InstallBody = """
        if(-not (Test-Path -LiteralPath $nxSource -PathType Container) -or (Test-NexoLink $nxSource)){throw 'A pasta do Nexo em execução não existe ou é um link. Nada foi alterado.'}
        $nxSourceFull=[IO.Path]::GetFullPath($nxSource).TrimEnd('\')
        if($nxSourceFull -ieq $nxTarget -or $nxSourceFull.StartsWith($nxTarget + '\', [StringComparison]::OrdinalIgnoreCase)){throw 'O Nexo em execução já é a cópia do leitor; abra o Nexo instalado normalmente. Nada foi alterado.'}
        # (a) Pastas protegidas: nascem com o descritor; existentes só passam se já estiverem protegidas e não forem link.
        Use-ProtectedDirectory $nxParent
        Use-ProtectedDirectory $nxTarget
        Assert-NexoTree $nxTarget
        Stop-NexoSensorLoop
        if(Test-Path -LiteralPath $nxExtract){ Remove-NexoTree $nxExtract }
        function New-NexoCopyStream($path) {
          $security=New-ProtectedFileSecurity
          $rights=[System.Security.AccessControl.FileSystemRights]'Write, ReadData, Synchronize'
          if([IO.FileStream].GetConstructor([type[]]@([string],[IO.FileMode],[System.Security.AccessControl.FileSystemRights],[IO.FileShare],[int],[IO.FileOptions],[System.Security.AccessControl.FileSecurity]))){
            return New-Object System.IO.FileStream($path,[IO.FileMode]::CreateNew,$rights,[IO.FileShare]::None,81920,[IO.FileOptions]::None,$security)
          }
          [System.IO.FileSystemAclExtensions]::Create((New-Object System.IO.FileInfo($path)),[IO.FileMode]::CreateNew,$rights,[IO.FileShare]::None,81920,[IO.FileOptions]::None,$security)
        }
        # (b) Cópia conferida: a origem fica aberta sem permitir escrita do hash até o fim da cópia.
        function Copy-NexoSensorFile($name) {
          if($name -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$' -or $name.Contains('..')){ throw ('Nome de arquivo recusado: ' + $name) }
          $from=Join-Path $nxSource $name
          $item=Get-Item -LiteralPath $from -Force
          if($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){ throw ('Origem recusada (pasta ou link): ' + $from) }
          $to=Join-Path $nxTarget $name
          $temporary=$to + '.tmp'
          if(Test-Path -LiteralPath $temporary){ Remove-Item -LiteralPath $temporary -Force }
          $sha=[Security.Cryptography.SHA256]::Create()
          $nxIn=New-Object System.IO.FileStream($from,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
          try {
            $expected=[BitConverter]::ToString($sha.ComputeHash($nxIn)).Replace('-','')
            [void]$nxIn.Seek(0,[IO.SeekOrigin]::Begin)
            $output=New-NexoCopyStream $temporary
            try { $nxIn.CopyTo($output) } finally { $output.Dispose() }
          } finally { $nxIn.Dispose() }
          $check=New-Object System.IO.FileStream($temporary,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
          try { $actual=[BitConverter]::ToString($sha.ComputeHash($check)).Replace('-','') } finally { $check.Dispose() }
          if($actual -ne $expected){ Remove-Item -LiteralPath $temporary -Force; throw ('O SHA-256 da cópia de ' + $name + ' não confere com a origem.') }
          Move-Item -LiteralPath $temporary -Destination $to -Force
          Assert-Trusted $to
          [pscustomobject]@{Name=$name;Sha256=$expected}
        }
        $nxCopied=@(foreach($name in $nxFiles){ Copy-NexoSensorFile $name })
        foreach($old in @(Get-ChildItem -LiteralPath $nxTarget -Force -File)){
          if($nxFiles -notcontains $old.Name -and -not ($old.Attributes -band [IO.FileAttributes]::ReparsePoint)){ Remove-Item -LiteralPath $old.FullName -Force }
        }
        # (c) Tarefa do sistema: SYSTEM, na inicialização, reinício em falha e sem limite de tempo. A ação é o próprio Nexo.exe da
        # pasta protegida, sem cmd.exe: parar a tarefa (Stop-ScheduledTask, schtasks /End) encerra o leitor, e o reinício em falha
        # vale para ele. A busca de DLLs (SetDefaultDllDirectories) e a recusa de arquivos extraídos do single-file ficam dentro do
        # --sensor-loop. O agendador expande % no caminho, e aspas mudariam a ação: um caminho com % ou aspas é recusado.
        if(($nxExe + $nxTarget) -match '[%"]'){throw 'O caminho do leitor tem % ou aspas e não pode ir para a tarefa. Nada foi registrado.'}
        $action=New-ScheduledTaskAction -Execute $nxExe -Argument '--sensor-loop' -WorkingDirectory $nxTarget
        $trigger=New-ScheduledTaskTrigger -AtStartup
        $principal=New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
        $settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew -Priority 7
        Register-ScheduledTask -TaskName $nxTask -TaskPath '\' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'Leitor de temperatura da CPU do Nexo (somente leitura). Para desativar: Nexo > Saúde do PC > Desativar temperatura ao vivo.' -Force | Out-Null
        # (d) Inicia já.
        Start-ScheduledTask -TaskName $nxTask -TaskPath '\'
        [pscustomobject]@{Target=$nxTarget;Task=$nxTask;Files=$nxCopied.Count;Sha256=@($nxCopied | ForEach-Object { $_.Name + ' ' + $_.Sha256 })}
        """;

    private const string RemoveBody = """
        if(Test-Path -LiteralPath $nxTarget){
          if(Test-NexoLink $nxTarget){ throw ('A pasta ' + $nxTarget + ' é um link e não foi removida. Nada foi alterado.') }
          Assert-NexoTree $nxTarget
        }
        Stop-NexoSensorLoop
        if(Get-ScheduledTask -TaskName $nxTask -TaskPath '\' -ErrorAction SilentlyContinue){ Unregister-ScheduledTask -TaskName $nxTask -TaskPath '\' -Confirm:$false }
        if(Test-Path -LiteralPath $nxTarget){ Remove-NexoTree $nxTarget }
        if((Test-Path -LiteralPath $nxParent) -and -not (Test-NexoLink $nxParent) -and @(Get-ChildItem -LiteralPath $nxParent -Force).Count -eq 0){ Remove-Item -LiteralPath $nxParent -Force }
        foreach($file in @($nxLive, ($nxLive + '.tmp'))){
          if((Test-Path -LiteralPath $file) -and -not (Test-NexoLink $file)){ Remove-Item -LiteralPath $file -Force }
        }
        [pscustomobject]@{Target=$nxTarget;Task=$nxTask;Removed=$true}
        """;

    /// <summary>Roda o script pelo executor elevado e traduz o resultado; UAC recusado e falha viram estado, nunca exceção.</summary>
    public static async Task<SensorReaderResult> ApplyAsync(Func<string, CancellationToken, Task<string>> runElevated, string action, string script, CancellationToken token)
    {
        string output;
        try { output = await runElevated(script, token); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return new("canceled", "A autorização de administrador foi recusada. Nada foi alterado."); }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException) { return new("failed", Failed(action, ex.Message)); }
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True)
                return new("done", action switch
                {
                    "remove" => "Temperatura ao vivo desativada. A tarefa \"" + TaskName + "\" e a pasta " + FolderLabel + " foram removidas.",
                    "update" => "Leitor de sensores atualizado para a versão " + AppVersion.Current + " e reiniciado.",
                    _ => "Temperatura ao vivo ativada. O leitor foi copiado para " + FolderLabel + " e a tarefa \"" + TaskName + "\" (SYSTEM) já está rodando. Para desativar: \"" + DeactivateLabel + "\"."
                });
            var error = root.TryGetProperty("error", out var detail) && detail.ValueKind == JsonValueKind.String ? detail.GetString() ?? "" : "";
            return new("failed", Failed(action, error.Length == 0 ? "resultado sem detalhe" : error));
        }
        catch (JsonException) { return new("failed", Failed(action, "resultado ilegível do executor elevado")); }
    }

    private static string Failed(string action, string detail) =>
        (action switch { "remove" => "Não foi possível desativar a temperatura ao vivo", "update" => "Não foi possível atualizar o leitor", _ => "Não foi possível ativar a temperatura ao vivo" })
        + ": " + Short(detail).TrimEnd('.') + ".";
}

/// <summary>O Windows de verdade: o Nexo.exe da cópia protegida, a conferência da pasta protegida e a leitura sem bloquear a troca.</summary>
public sealed class WindowsLiveSensorSystem : ILiveSensorSystem
{
    public bool Installed() => File.Exists(Path.Combine(SystemSensorReader.InstallFolder, SystemSensorReader.Executable));
    public string? Untrusted(string file) => new WindowsElevatedSensorSystem().Untrusted(file);
    public DateTimeOffset BootTime() => DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    // O Nexo.exe da cópia é gravado pelo script elevado (cópia nova, trocada por Move): a data dele é a da ativação ou da atualização.
    public DateTimeOffset? InstalledTime()
    {
        var file = Path.Combine(SystemSensorReader.InstallFolder, SystemSensorReader.Executable);
        try { return File.Exists(file) ? new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public string? ReadText(string file, int maxBytes)
    {
        try
        {
            // Leitura e exclusão compartilhadas: o leitor troca o arquivo por Move a cada 2 s, e o app não pode impedir a troca.
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > maxBytes) throw new IOException("arquivo maior que o limite de " + maxBytes + " bytes");
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            return reader.ReadToEnd();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    /// <summary>Versão do Nexo.exe instalado (x.y.z), ou null sem o leitor.</summary>
    public static string? InstalledVersion()
    {
        var file = Path.Combine(SystemSensorReader.InstallFolder, SystemSensorReader.Executable);
        try { return File.Exists(file) && Version.TryParse(FileVersionInfo.GetVersionInfo(file).FileVersion, out var version) ? AppVersion.Format(version) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException) { return null; }
    }

    /// <summary>Versão informativa (ProductVersion) do Nexo.exe instalado, ou null sem o leitor.</summary>
    public static string? InstalledBuild()
    {
        var file = Path.Combine(SystemSensorReader.InstallFolder, SystemSensorReader.Executable);
        try { return File.Exists(file) ? FileVersionInfo.GetVersionInfo(file).ProductVersion : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException) { return null; }
    }

    /// <summary>Versão informativa do Nexo aberto (a mesma que o SDK grava no ProductVersion do Nexo.exe).</summary>
    public static string? CurrentBuild() =>
        typeof(SystemSensorReader).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;

    /// <summary>Arquivos do Nexo em execução para a cópia (pasta do app, só o primeiro nível).</summary>
    public static (string Source, IReadOnlyList<string> Files) Sources()
    {
        var source = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        return (source, SystemSensorReader.FilesToCopy(SystemSensorReader.SingleFile, Directory.EnumerateFiles(source).Select(Path.GetFileName).OfType<string>()));
    }

    /// <summary>O executor elevado supervisionado (o mesmo do debloat): UAC uma vez, PowerShell com a fixação do PSModulePath.</summary>
    public static Task<string> RunElevatedAsync(string script, CancellationToken token) => OptimizationDiskExecutor.RunAsync(NetworkRecovery.Compact(script), token);
}

/// <summary>
/// Entrada do modo --sensor-loop (#142, P9), chamada pelo Program.Main antes de qualquer tipo do WPF. A tarefa executa o Nexo.exe
/// direto, sem cmd.exe e sem PATH fixo na linha de comando: a primeira coisa do processo é restringir a busca de DLLs à pasta do
/// executável e ao System32 (SetDefaultDllDirectories), antes de carregar qualquer DLL nativa (WPF, LibreHardwareMonitor, PawnIO).
/// Sem campos estáticos, para não disparar o inicializador do SystemSensorReader antes da restrição.
/// </summary>
public static class SensorLoopEntry
{
    public static int Run(string[] args) => Run(args, NativeDllSearch.Restrict, SystemSensorReader.RunLoop);

    /// <summary>Código 5 quando a busca não foi restringida; senão, o código do loop.</summary>
    internal static int Run(string[] args, Func<bool> restrictDllSearch, Func<string[], int> loop) => restrictDllSearch() ? loop(args) : 5;
}

/// <summary>
/// Ordem de busca de DLLs do processo do leitor: só a pasta do executável (a pasta protegida) e o System32. Com isso, o PATH e a
/// pasta atual deixam de valer para LoadLibrary sem caminho e para as dependências das DLLs nativas carregadas depois.
/// </summary>
public static class NativeDllSearch
{
    public const uint LoadLibrarySearchApplicationDir = 0x00000200;
    public const uint LoadLibrarySearchSystem32 = 0x00000800;
    public const uint Flags = LoadLibrarySearchApplicationDir | LoadLibrarySearchSystem32;

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);

    /// <summary>True quando a ordem de busca ficou restrita; false fora do Windows ou se o kernel32 recusar.</summary>
    public static bool Restrict()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { return SetDefaultDllDirectories(Flags); }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { return false; }
    }
}
