using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nexo.Support;

public sealed record ChangeRequest(string Title, string Details, bool KeepNetwork = false, string? LicenseUrl = null);
public sealed class WindowsTools(Store store, Account actor, Func<ChangeRequest, Task<bool>> confirm, IProgress<string> progress, Action<string>? inform = null) : IAgentTools
{
    internal static readonly TimeSpan ElevatedTimeout = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan NvidiaInstallerTimeout = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan NvidiaSupervisionTimeout = NvidiaInstallerTimeout + TimeSpan.FromMinutes(5);

    private readonly SupportService support = new(store);
    private readonly Dictionary<int, string> observedInterfaces = [];
    private readonly Dictionary<int, ObservedAdapter> observedAdapters = [];
    private readonly Dictionary<int, JsonElement> observedNetworkDetails = [];
    private readonly Dictionary<string, JsonElement> observedServiceDetails = new(StringComparer.OrdinalIgnoreCase);
    // Quando cada estado de serviço foi lido (#88): o pedido de aprovação só mostra uma leitura recente.
    private readonly Dictionary<string, DateTimeOffset> observedServiceAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, JsonElement> observedPrinterDetails = new(StringComparer.OrdinalIgnoreCase);
    // Última leitura dos eventos de cada serviço (#58), já na frase que vai para o pedido de aprovação do reinício.
    private readonly Dictionary<string, (DateTimeOffset At, string Note)> observedServiceEvents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, bool> configurableInterfaces = [];
    private readonly HashSet<int> physicalInterfaces = [];
    private readonly Dictionary<string, (string Topic, Device Device)> observedDevices = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> observedPrinters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Title, string HardwareId, int Revision)> observedUpdates = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? observedNetworkAt;
    private DateTimeOffset? observedDevicesAt;
    private DateTimeOffset? observedPrintersAt;
    private DateTimeOffset? observedUpdatesAt;
    private OfficialDriverSource.DriverOffer? observedNvidiaDriver;
    private DateTimeOffset? observedNvidiaDriverAt;
    private bool? lastApproval;
    // Se alguma aprovação já foi concedida nesta execução (#73). Não volta a false: no set_dns/set_ipv4 a 2ª pergunta ("Manter a
    // configuração?") vem DEPOIS de a mudança ser aplicada, e recusá-la não desfaz o que já foi feito.
    private bool changeAuthorized;
    private InterventionRecord? currentIntervention;
    // Executa o script de eventos do serviço; os testes trocam o PowerShell por uma saída conhecida.
    internal Func<string, CancellationToken, Task<string>> ServiceEventsReader { get; init; } = (script, token) => SupportService.Powershell(script, token);
    // Relógio da validade da leitura de eventos (5 minutos); os testes avançam o tempo.
    internal Func<DateTimeOffset> ServiceEventsClock { get; init; } = () => DateTimeOffset.UtcNow;
    // Consulta da rede, aplicação da mudança de rede e início do processo elevado; os testes trocam por versões sem Windows.
    internal Func<string, CancellationToken, Task<string>> NetworkReader { get; init; } = (script, token) => SupportService.Powershell(script, token);
    internal Func<NetworkRequest, Func<ChangeRequest, Task<bool>>, CancellationToken, Task<string>>? NetworkApplier { get; init; }
    internal Func<string, Process> ElevatedStarter { get; init; } = StartElevated;
    // Leitura das impressoras e do diagnóstico de dispositivos; os testes trocam pelo resultado do PowerShell (#84).
    internal Func<string, CancellationToken, Task<string>> PrinterReader { get; init; } = (script, token) => SupportService.Powershell(script, token);
    internal Func<string, CancellationToken, Task<string>> DeviceReader { get; init; } = (script, token) => SupportService.Powershell(script, token, 75);
    private static readonly IReadOnlyDictionary<string, JsonElement> toolSchemas = ToolCatalog.ForScope(false)
        .Select(tool => JsonSerializer.SerializeToElement(tool))
        .ToDictionary(tool => tool.GetProperty("function").GetProperty("name").GetString()!, tool => tool.GetProperty("function").GetProperty("parameters").Clone(), StringComparer.Ordinal);
    // O PowerShell fecha a string de aspas simples também com ‘ ’ ‚ ‛ (aspas tipográficas), não só com '. Cada uma é
    // dobrada, como a ', para nomes vindos do Windows (impressora, caminho do perfil) continuarem texto literal.
    public static string Quote(string value)
    {
        var quoted = new StringBuilder(value.Length + 2).Append('\'');
        foreach (var character in value)
        {
            quoted.Append(character);
            if (character is '\'' or '\u2018' or '\u2019' or '\u201A' or '\u201B') quoted.Append(character);
        }
        return quoted.Append('\'').ToString();
    }
    public static string Value(JsonElement args, string key) => args.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : throw new InvalidOperationException("Argumento de texto ausente: " + key);
    public static int Number(JsonElement args, string key) => args.TryGetProperty(key, out var value) && value.TryGetInt32(out var number) ? number : throw new InvalidOperationException("Número inteiro ausente: " + key);
    public static string Ipv4(string text)
    {
        if (!Regex.IsMatch(text, @"^\d{1,3}(\.\d{1,3}){3}$") || !IPAddress.TryParse(text, out var address) || address.AddressFamily != AddressFamily.InterNetwork) throw new InvalidOperationException("IPv4 inválido: " + text);
        var bytes = address.GetAddressBytes();
        if (bytes[0] is 0 or 127 || bytes[0] >= 224 || text == "255.255.255.255") throw new InvalidOperationException("Use um endereço IPv4 unicast válido.");
        return address.ToString();
    }
    public static void ValidateStatic(string address, int prefix, string gateway)
    {
        address = Ipv4(address);
        if (prefix is < 1 or > 30) throw new InvalidOperationException("O prefixo deve estar entre 1 e 30.");
        static uint Integer(string value) { var b = IPAddress.Parse(value).GetAddressBytes(); return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3]; }
        var ip = Integer(address); var mask = uint.MaxValue << (32 - prefix);
        if ((ip & ~mask) == 0 || (ip & ~mask) == ~mask) throw new InvalidOperationException("O IP não pode ser o endereço da rede ou de broadcast.");
        if (gateway.Length > 0)
        {
            var gw = Integer(Ipv4(gateway));
            if (gw == ip || (gw & mask) != (ip & mask) || (gw & ~mask) == 0 || (gw & ~mask) == ~mask) throw new InvalidOperationException("Gateway inválido: deve ser outro host na mesma sub-rede.");
        }
    }
    public static string NetworkQuery => NetworkAdapterScripts.Read("NetworkInventory.ps1");
    private static bool Fresh(DateTimeOffset? observedAt) => observedAt is { } at && DateTimeOffset.UtcNow - at <= TimeSpan.FromMinutes(5);
    private const string DriverSearchScript = """
        $session=New-Object -ComObject Microsoft.Update.Session
        $searcher=$session.CreateUpdateSearcher()
        $searcher.Online=$true
        $searcher.ServerSelection=0
        $search=$searcher.Search("IsInstalled=0 and IsHidden=0 and Type='Driver'")
        """;
    internal static string DeviceClasses(string topic) => topic switch
    {
        "camera" => "'Camera','Image'", "audio" => "'AudioEndpoint','MEDIA'", "bluetooth" => "'Bluetooth'", "printer" => "'Printer','PrintQueue'",
        _ => throw new InvalidOperationException("Categoria de dispositivo não permitida.")
    };
    internal static string HardwareFilter(string category) => category switch
    {
        "problem" => "[int]$_.ConfigManagerErrorCode -ne 0",
        "display" => "$_.PNPClass -eq 'Display'",
        "network" => "$_.PNPClass -eq 'Net'",
        "storage" => "$_.PNPClass -in 'DiskDrive','HDC','SCSIAdapter'",
        "usb" => "$_.PNPClass -in 'USB','HIDClass'",
        "camera" => "$_.PNPClass -in 'Camera','Image'",
        "audio" => "$_.PNPClass -in 'AudioEndpoint','MEDIA'",
        "bluetooth" => "$_.PNPClass -eq 'Bluetooth'",
        "printer" => "$_.PNPClass -in 'Printer','PrintQueue'",
        _ => throw new InvalidOperationException("Categoria de hardware não permitida.")
    };
    // Letra de unidade → disco que a contém (#93). Na validação da 11fa078, o T: era um VHDX montado e o modelo o atribuiu
    // ao "USB DISK 2.0", porque o inventário listava os discos sem as letras. MSFT_Partition dá a letra e o número do disco;
    // MSFT_Disk dá nome, barramento e, para VHD/VHDX, o arquivo (Location). Roda no mesmo processo do inventário, só na
    // categoria storage, e cada consulta ao provedor do Storage tem prazo próprio de 10 s: um provedor travado (RAID, USB)
    // não segura o inventário até os 90 s dele (@qa). Uma falha ou o prazo esgotado deixam só a ligação indisponível.
    internal const int VolumesTimeoutSeconds = 10;
    internal const string StorageVolumesScript = "$volumes=@(); $volumesError=''; $volumesClock=[Diagnostics.Stopwatch]::StartNew(); " +
        "try{ $disks=@{}; @(Get-CimInstance -Namespace 'root/Microsoft/Windows/Storage' -ClassName 'MSFT_Disk' -OperationTimeoutSec 10 -ErrorAction Stop) | ForEach-Object {$disks[[int]$_.Number]=$_}; " +
        "$volumes=@(Get-CimInstance -Namespace 'root/Microsoft/Windows/Storage' -ClassName 'MSFT_Partition' -OperationTimeoutSec 10 -ErrorAction Stop | Where-Object {[string]$_.DriveLetter -match '^[A-Za-z]$'} | Sort-Object {[string]$_.DriveLetter} | Select-Object -First 26 | ForEach-Object {$k=$disks[[int]$_.DiskNumber]; " +
        "[pscustomobject]@{Letter=([string]$_.DriveLetter+':');DiskNumber=[int]$_.DiskNumber;Disk=[string]$k.FriendlyName;BusType=[string]$k.BusType;Location=[string]$k.Location}}) }" +
        "catch{ $volumes=@(); $volumesError=if($volumesClock.Elapsed.TotalSeconds -ge 9.5 -or [string]$_.Exception.Message -match 'timed? ?out|OperationTimeout|tempo limite|0x80338029'){'prazo esgotado (10 s)'}else{[string]$_.Exception.Message} }; ";
    internal const string VolumesNote = "Volumes liga cada letra de unidade ao disco que a contém. Disco virtual é um arquivo VHD/VHDX montado, não um dispositivo: não atribua a letra dele a um disco da lista Devices. Não associe a um disco uma letra que não esteja em Volumes.";

    // Resultado do inventário de armazenamento com a ligação letra → disco já interpretada: o modelo recebe o tipo do disco
    // (virtual, USB, interno) em vez de ter de deduzi-lo do nome.
    internal static string StorageInventoryResult(JsonElement root)
    {
        static string Field(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        var error = Field(root, "VolumesError");
        var volumes = (root.TryGetProperty("Volumes", out var listed) && listed.ValueKind == JsonValueKind.Array ? listed.EnumerateArray() : Enumerable.Empty<JsonElement>())
            .Where(item => Regex.IsMatch(Field(item, "Letter"), "^[A-Za-z]:$")).Take(26).Select(item =>
            {
                var bus = BusName(Field(item, "BusType")); var location = Field(item, "Location");
                var virtualDisk = bus.Equals("File Backed Virtual", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(location, @"\.(?:vhdx?|avhdx?)\s*$", RegexOptions.IgnoreCase);
                return new
                {
                    Letter = Field(item, "Letter").ToUpperInvariant(),
                    Disk = InvestigationEvidence.Clean(Field(item, "Disk")),
                    BusType = bus,
                    Kind = virtualDisk ? "disco virtual (VHD/VHDX montado de arquivo)" : DiskKind(bus),
                    File = virtualDisk ? InvestigationEvidence.Clean(location) : null
                };
            }).ToArray();
        return Ok(new
        {
            Category = "storage",
            Total = root.TryGetProperty("Total", out var total) ? total.Clone() : (JsonElement?)null,
            Truncated = root.TryGetProperty("Truncated", out var truncated) ? truncated.Clone() : (JsonElement?)null,
            Devices = root.GetProperty("Devices").Clone(),
            Volumes = volumes,
            VolumesNote = error.Length > 0 || volumes.Length == 0
                ? "O Windows não informou a ligação entre letras de unidade e discos" + (error.Length > 0 ? " (" + InvestigationEvidence.Clean(error) + ")" : "") + ". Não associe uma letra a um disco pelo nome, pelo tipo ou pelo tamanho."
                : VolumesNote
        });
    }
    // Barramento vazio ou Unknown não vira "disco interno" (@qa); SD, MMC, iSCSI... aparecem pelo nome.
    internal static string DiskKind(string bus) => bus.ToUpperInvariant() switch
    {
        "USB" => "disco USB (removível)",
        "" or "UNKNOWN" => "tipo não identificado",
        "NVME" or "SATA" or "SAS" or "ATA" or "ATAPI" or "SCSI" or "RAID" or "SCM" or "UFS" or "STORAGE SPACES" => "disco interno",
        _ => "disco " + bus
    };
    // MSFT_Disk.BusType pode chegar como número quando a formatação do módulo Storage não se aplica.
    internal static string BusName(string bus) => bus switch
    {
        "0" => "Unknown", "1" => "SCSI", "2" => "ATAPI", "3" => "ATA", "4" => "1394", "5" => "SSA", "6" => "Fibre Channel", "7" => "USB", "8" => "RAID", "9" => "iSCSI",
        "10" => "SAS", "11" => "SATA", "12" => "SD", "13" => "MMC", "14" => "Virtual", "15" => "File Backed Virtual", "16" => "Storage Spaces", "17" => "NVMe", "18" => "SCM", "19" => "UFS",
        _ => InvestigationEvidence.Clean(bus)
    };
    private static string HardwareTopic(string category) => category is "camera" or "audio" or "bluetooth" or "printer" or "network" ? category : "hardware";

    // A consulta de impressoras confere antes o estado do Spooler (#84). Parado ou em outro estado que não Running, o script
    // devolve só esse estado, num objeto; em execução, a lista de sempre, num array.
    internal const string PrintersScript = "$spooler=Get-Service -Name Spooler -ErrorAction Stop; if([string]$spooler.Status -ne 'Running'){ ConvertTo-Json -Compress -InputObject ([pscustomobject]@{Spooler=[string]$spooler.Status;StartType=[string]$spooler.StartType}) } else { ConvertTo-Json -Depth 5 -Compress -InputObject @(Get-Printer | ForEach-Object { $p=$_; [pscustomobject]@{Name=$p.Name;Status=[string]$p.PrinterStatus;Driver=$p.DriverName;Port=$p.PortName;Jobs=@(Get-PrintJob -PrinterName $p.Name -ErrorAction SilentlyContinue | Select-Object ID,JobStatus)} }) }";

    // O estado do Spooler quando o script confirmou que ele não está em execução; null para a lista de impressoras.
    // Um objeto sem o estado, ou dizendo Running, é formato inesperado: a consulta falha como antes, sem diagnóstico inventado.
    internal static (string Status, string StartType)? SpoolerNotRunning(string output)
    {
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("Spooler", out var status) || status.ValueKind != JsonValueKind.String || status.GetString() is not { Length: > 0 } state
            || string.Equals(state, "Running", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A consulta de impressoras retornou um formato inesperado.");
        var start = root.TryGetProperty("StartType", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() ?? "" : "";
        return (state, start);
    }

    internal static string SpoolerStoppedResult(string status, string startType) => Ok(new
    {
        SpoolerRunning = false, SpoolerStatus = status, SpoolerStartType = startType, Printers = Array.Empty<object>(),
        note = (string.Equals(status, "Stopped", StringComparison.OrdinalIgnoreCase)
            ? "O serviço de impressão (Spooler) está parado."
            : "O serviço de impressão (Spooler) não está em execução (estado " + status + ").")
            + " Sem ele o Windows não lista impressoras nem filas e nada imprime; este é o achado da consulta, não uma falha dela."
            + (string.Equals(startType, "Disabled", StringComparison.OrdinalIgnoreCase)
                ? " O serviço está desabilitado: verifique a política da organização antes de mudar o modo de inicialização."
                : " Reiniciar o Spooler (restart_service) depende da aprovação do usuário.")
    });

    // Corte por caractere visível (text element), não por unidade UTF-16: não parte par substituto, emoji nem acento combinado.
    internal static string Limited(string text, int limit)
    {
        var info = new System.Globalization.StringInfo(text);
        return info.LengthInTextElements > limit ? info.SubstringByTextElements(0, limit) : text;
    }

    // Estado do serviço antes do reinício, para a janela de aprovação (#88): só a leitura recente do executor (get_printers com o
    // Spooler parado ou get_services), nunca os argumentos do modelo. Sem leitura, a janela fica como antes.
    internal string? ObservedServiceState(string service)
    {
        if (!observedServiceAt.TryGetValue(service, out var at) || ServiceEventsClock() - at > TimeSpan.FromMinutes(5) || !observedServiceDetails.TryGetValue(service, out var detail)
            || detail.ValueKind != JsonValueKind.Object || !detail.TryGetProperty("Status", out var status) || status.ValueKind != JsonValueKind.String) return null;
        var start = detail.TryGetProperty("StartType", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() ?? "" : "";
        return ServiceStateText(status.GetString() ?? "", start);
    }

    private void RememberRestarted(string service, string result)
    {
        observedServiceDetails.Remove(service); observedServiceAt.Remove(service);
        try
        {
            using var document = JsonDocument.Parse(result);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True
                && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("Status", out var status) && status.ValueKind == JsonValueKind.String)
            {
                observedServiceDetails[service] = JsonSerializer.SerializeToElement(new { Name = service, Status = status.GetString() });
                observedServiceAt[service] = ServiceEventsClock();
            }
        }
        catch (JsonException) { /* Sem resultado legível, a leitura anterior já saiu. */ }
    }

    internal static string? ServiceStateText(string status, string startType)
    {
        static string Part(string value) => Limited(InvestigationEvidence.Clean(value).Trim(), 30);
        var state = status.ToLowerInvariant() switch
        {
            "stopped" => "parado", "running" => "em execução", "paused" => "pausado", "startpending" => "iniciando",
            "stoppending" => "parando", "pausepending" => "pausando", "continuepending" => "retomando", _ => Part(status)
        };
        if (state.Length == 0) return null;
        var start = startType.ToLowerInvariant() switch
        {
            "automatic" => "automático", "manual" => "manual", "disabled" => "desabilitado", "" => "", _ => Part(startType)
        };
        return "Estado atual: " + state + (start.Length > 0 ? " (início " + start + ")" : "") + ".";
    }

    // Texto da janela de aprovação de enable_device/restart_device (#84): o driver aparece quando a consulta do turno trouxe
    // o INF, a versão ou o fornecedor desse ID. Tudo vem da observação do executor, nada dos argumentos do modelo.
    internal static string DeviceChangeDetails(string action, Device device)
    {
        // O nome do dispositivo também vem do Windows (do descritor USB, por exemplo): sem quebra de linha, não imita a linha do driver.
        static string Part(string value, int limit = 80) => Limited(InvestigationEvidence.Clean(value).Trim(), limit);
        var parts = new[] { Part(device.InfName), Part(device.DriverVersion) is { Length: > 0 } version ? "versão " + version : "", Part(device.DriverProvider) }
            .Where(part => part.Length > 0).ToArray();
        return (action == "restart_device" ? "Reiniciar " : "Habilitar ") + Part(device.Name, 160) + "."
            + (parts.Length > 0 ? "\nDriver: " + string.Join(" · ", parts) + "." : "")
            + "\nO dispositivo poderá ficar temporariamente indisponível.";
    }

    // The schema sent to Ollama is advisory. Enforce it again at the executor boundary.
    // A recusa volta ao modelo como resultado da ferramenta: ela diz o que é aceito, para que a próxima chamada seja corrigida
    // em vez de repetida (#83). As regras são as mesmas do esquema anunciado; nada passa a ser aceito por isso.
    internal static void ValidateArguments(string name, JsonElement args)
    {
        if (!toolSchemas.TryGetValue(name, out var schema) || name is "finish_support" or "ask_user") throw new InvalidOperationException("Ferramenta não permitida: " + InvestigationEvidence.Quoted(name, 40));
        if (args.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Os argumentos de " + name + " devem ser um objeto JSON. " + Accepted(name, schema));
        var properties = schema.GetProperty("properties");
        var required = schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()!).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var argument in args.EnumerateObject())
        {
            if (!seen.Add(argument.Name)) throw new InvalidOperationException("Argumento repetido em " + name + ": " + InvestigationEvidence.Quoted(argument.Name, 40) + ". Envie cada argumento uma vez.");
            if (!properties.TryGetProperty(argument.Name, out var rule)) throw new InvalidOperationException("Argumento inesperado em " + name + ": " + InvestigationEvidence.Quoted(argument.Name, 40) + ". " + Accepted(name, schema));
            ValidateArgument(name, argument.Name, argument.Value, rule, !required.Contains(argument.Name));
        }
        foreach (var missing in required.Where(r => !seen.Contains(r)))
            throw new InvalidOperationException("Argumento obrigatório ausente em " + name + ": " + missing + ". " + Accepted(name, schema));
    }

    // Serviços que get_services consulta. A mesma lista vai no script e na recusa de argumento (#83).
    internal static readonly string[] ObservedServices = ["Audiosrv", "AudioEndpointBuilder", "Spooler", "bthserv", "WlanSvc"];

    // O que a ferramenta aceita, no texto que volta ao modelo depois de uma recusa.
    private static string Accepted(string tool, JsonElement schema)
    {
        var properties = schema.GetProperty("properties").EnumerateObject().ToArray();
        if (properties.Length == 0) return tool + " não recebe argumentos; chame com {}" + (tool == "get_services" ? ". Ela já traz " + string.Join(", ", ObservedServices[..^1]) + " e " + ObservedServices[^1] + "." : ".");
        var required = schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()!).ToHashSet(StringComparer.Ordinal);
        return tool + " aceita: " + string.Join("; ", properties.Select(p => p.Name + " (" + (required.Contains(p.Name) ? "obrigatório" : "opcional") + ", " + Kind(p.Value) + ")")) + ".";
    }

    private static string Kind(JsonElement rule) => rule.GetProperty("type").GetString() switch
    {
        "string" when rule.TryGetProperty("enum", out var choices) => "um de: " + string.Join(", ", choices.EnumerateArray().Select(c => c.GetString())),
        "string" => "texto",
        "integer" => "número inteiro",
        "array" => "lista" + (rule.TryGetProperty("maxItems", out var max) ? " de até " + max.GetInt32() + " itens" : "") + " de " + Kind(rule.GetProperty("items")),
        var other => other ?? "?"
    };

    private static void ValidateArgument(string tool, string name, JsonElement value, JsonElement rule, bool optional)
    {
        var where = " em " + tool + ": " + name + ". ";
        switch (rule.GetProperty("type").GetString())
        {
            case "string":
                if (value.ValueKind != JsonValueKind.String) throw new InvalidOperationException("Argumento de texto inválido" + where + "Envie " + Kind(rule) + ", entre aspas.");
                var text = value.GetString()!;
                if (text.Length > 1024 || text.Any(char.IsControl)) throw new InvalidOperationException("Argumento de texto inválido" + where + "Use até 1024 caracteres, sem caracteres de controle.");
                if (rule.TryGetProperty("enum", out var choices) && !choices.EnumerateArray().Any(choice => choice.GetString() == text))
                    throw new InvalidOperationException("Valor não permitido para " + name + " em " + tool + ": " + InvestigationEvidence.Quoted(text, 40) + ". Valores aceitos: "
                        + string.Join(", ", choices.EnumerateArray().Select(c => c.GetString())) + (optional ? "; ou omita " + name + "." : "."));
                break;
            case "integer":
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out _)) throw new InvalidOperationException("Número inteiro inválido" + where + "Envie um número inteiro, sem aspas.");
                break;
            case "array":
                if (value.ValueKind != JsonValueKind.Array || rule.TryGetProperty("maxItems", out var max) && value.GetArrayLength() > max.GetInt32()) throw new InvalidOperationException("Lista inválida" + where + "Envie " + Kind(rule) + ".");
                foreach (var item in value.EnumerateArray()) ValidateArgument(tool, name, item, rule.GetProperty("items"), false);
                break;
            default: throw new InvalidOperationException("Esquema de argumento não permitido" + where);
        }
    }

    public async Task<string> Execute(string name, JsonElement args, CancellationToken token)
    {
        if (!ToolCatalog.IsMutation(name)) return await ExecuteCore(name, args, token);
        lastApproval = null;
        changeAuthorized = false;
        currentIntervention = new InterventionRecord
        {
            Action = name, Target = args.ValueKind == JsonValueKind.Object ? args.GetRawText() : "{}",
            Risk = name.Contains("driver") || name is "set_dns" or "set_ipv4" ? "high" : "medium",
            Before = CaptureObservedBefore(args), CommandTemplate = name
        };
        MoveIntervention("proposed", "Ação proposta; ainda não autorizada nem executada.");
        string? output = null;
        try { return output = await ExecuteCore(name, args, token); }
        // Antes da aprovação nada foi alterado: validação, consulta prévia ou o próprio pedido de aprovação falharam (#73).
        catch (Exception ex) when (!changeAuthorized) { ex.Data[TurnFacts.NotStartedKey] = true; throw; }
        finally
        {
            var state = ResultState(output);
            var pendingId = currentIntervention?.PendingOperation;
            try
            {
                if (state != "unknown" && !string.IsNullOrEmpty(pendingId)) currentIntervention!.PendingOperation = "";
                currentIntervention!.After = output ?? JsonSerializer.Serialize(new { status = "unavailable", reason = "A execução terminou sem resultado retornado." });
                currentIntervention.Summary = InterventionSummary(state, currentIntervention.PendingOperation != "");
                MoveIntervention(state, currentIntervention.Summary);
                if (state != "unknown" && !string.IsNullOrEmpty(pendingId)) PendingOperations.DeleteResult(store.Root, pendingId);
            }
            catch (Exception ex) { try { store.Log(actor, "Falha ao registrar intervenção operacional: " + ex.Message); } catch { /* Preserve the original action result. */ } }
            finally
            {
                if (!string.IsNullOrEmpty(pendingId)) PendingOperations.Release(pendingId);
                currentIntervention = null;
            }
        }
    }

    internal static string ResultState(string? output)
    {
        if (output == null) return "unknown";
        try
        {
            using var result = JsonDocument.Parse(output);
            var root = result.RootElement;
            if (root.TryGetProperty("state", out var reported) && reported.ValueKind == JsonValueKind.String && reported.GetString() == "unknown")
                return "unknown";
            if (root.TryGetProperty("denied", out var denied) && denied.ValueKind == JsonValueKind.True)
                return "denied";
            return root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True ? "reported" : "failed";
        }
        catch (JsonException)
        {
            return "unknown";
        }
    }

    internal static string InterventionSummary(string state, bool pending)
    {
        if (state == "reported") return "Executor relatou conclusão; funcionamento depende de verificação.";
        if (state == "unknown" && pending) return "A operação pode continuar em segundo plano; o Nexo conferirá o resultado quando o Windows concluir.";
        return "Resultado não confirmado ou recusado.";
    }

    private string CaptureObservedBefore(JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object)
        {
            if (args.TryGetProperty("device_id", out var device) && device.ValueKind == JsonValueKind.String
                && observedDevices.TryGetValue(device.GetString() ?? "", out var found))
                return JsonSerializer.Serialize(new { status = "observed", source = "device_inventory", device = found.Device });
            if (args.TryGetProperty("interface_index", out var index) && index.TryGetInt32(out var number)
                && observedNetworkDetails.TryGetValue(number, out var adapter))
                return JsonSerializer.Serialize(new { status = "observed", source = "network_inventory", adapter });
            if (args.TryGetProperty("service", out var service) && service.ValueKind == JsonValueKind.String
                && observedServiceDetails.TryGetValue(service.GetString() ?? "", out var serviceDetail))
                return JsonSerializer.Serialize(new { status = "observed", source = "service_inventory", service = serviceDetail });
            if (args.TryGetProperty("printer_name", out var printer) && printer.ValueKind == JsonValueKind.String
                && observedPrinterDetails.TryGetValue(printer.GetString() ?? "", out var printerDetail))
                return JsonSerializer.Serialize(new { status = "observed", source = "printer_inventory", printer = printerDetail });
        }
        return JsonSerializer.Serialize(new { status = "unavailable", reason = "Nenhuma leitura anterior estruturada para este alvo foi registrada pelo executor." });
    }

    private void MoveIntervention(string state, string details)
    {
        if (currentIntervention == null) return;
        if (currentIntervention.Transitions.LastOrDefault()?.State != state)
            currentIntervention.Transitions.Add(new InterventionTransition(DateTimeOffset.UtcNow, state, details));
        currentIntervention.State = state;
        currentIntervention.Authorized = lastApproval == true;
        using var operational = new OperationalStore(store.Root);
        operational.SaveIntervention(currentIntervention);
    }

    private async Task<string> ExecuteCore(string name, JsonElement args, CancellationToken token)
    {
        store.Require(actor, a => a.Diagnose);
        ValidateArguments(name, args);
        token.ThrowIfCancellationRequested();
        switch (name)
        {
            case "get_network":
                observedInterfaces.Clear(); observedAdapters.Clear(); observedNetworkDetails.Clear(); configurableInterfaces.Clear(); physicalInterfaces.Clear();
                observedNetworkAt = null;
                var network = await NetworkReader(NetworkQuery, token);
                using (var document = JsonDocument.Parse(network))
                {
                    foreach (var adapter in document.RootElement.GetProperty("Adapters").EnumerateArray())
                    {
                        var observed = new ObservedAdapter(adapter.GetProperty("Index").GetInt32(), adapter.GetProperty("Guid").GetString()!, adapter.GetProperty("Name").GetString()!, adapter.GetProperty("Status").GetString()!, adapter.GetProperty("Disabled").GetBoolean());
                        observedInterfaces[observed.Index] = observed.Name; observedAdapters[observed.Index] = observed;
                        observedNetworkDetails[observed.Index] = adapter.Clone();
                        configurableInterfaces[observed.Index] = NetworkAssessment.CanConfigure(adapter);
                        if (adapter.GetProperty("Hardware").GetBoolean() && !adapter.GetProperty("Virtual").GetBoolean() && adapter.GetProperty("InterfaceType").GetInt32() == 6) physicalInterfaces.Add(observed.Index);
                    }
                    observedNetworkAt = DateTimeOffset.UtcNow;
                    return Ok(NetworkAssessment.Enrich(document.RootElement));
                }
            case "enable_network_adapter":
                var adapterIndex = Number(args, "interface_index");
                if (!Fresh(observedNetworkAt) || !observedAdapters.TryGetValue(adapterIndex, out var target)) throw new InvalidOperationException("Consulte get_network nesta etapa e use o índice exato da interface desativada.");
                if (!target.Disabled) return Ok(new { changed = false, note = "A interface não foi detectada como desativada; nenhum ajuste feito. Disconnected indica falta de link, não desativação." });
                var enableScript = NetworkAdapterScripts.Enable(target);
                if (!await Approve(new ChangeRequest("Ativar interface de rede", "Habilitar “" + target.Name + "” (índice " + target.Index + ").\nEstado observado: " + target.Status + ".\nA ação também permite reativar vEthernet/Hyper-V. Nenhum IP, DNS ou switch virtual será reconfigurado."), token)) return Denied();
                var activation = await Elevated(enableScript, token);
                using (var activationResult = JsonDocument.Parse(activation))
                {
                    if (!activationResult.RootElement.GetProperty("success").GetBoolean()) return activation;
                    progress.Report("Verificando a configuração da interface após a ativação…");
                    try
                    {
                        using var after = JsonDocument.Parse(await SupportService.Powershell(NetworkQuery, CancellationToken.None));
                        return Ok(new { Activation = activationResult.RootElement.Clone(), NetworkAfter = NetworkAssessment.Enrich(after.RootElement), InternetVerified = false });
                    }
                    catch (Exception ex) { return Ok(new { Activation = activationResult.RootElement.Clone(), VerificationWarning = ex.Message, InternetVerified = false }); }
                }
            case "test_connection":
                return await TestConnection(Value(args, "host"), Number(args, "port"), token);
            case "probe_network_interfaces":
                using (var current = JsonDocument.Parse(await Execute("get_network", JsonSerializer.SerializeToElement(new { }), token)))
                    return Ok(await InterfaceProbe.Run(current.RootElement.GetProperty("data"), token));
            case "get_boot_status":
            case "prepare_restart":
                var boot = await SupportService.Powershell("[pscustomobject]@{BootId=(Get-CimInstance Win32_OperatingSystem -ErrorAction Stop).LastBootUpTime.ToUniversalTime().ToString('o')} | ConvertTo-Json -Compress", token);
                using (var bootDoc = JsonDocument.Parse(boot)) return Ok(bootDoc.RootElement.Clone());
            case "wait_for_reconnect":
                var kind = Value(args, "kind");
                string watchScript; string identity; string displayName;
                if (kind == "network")
                {
                    var watchIndex = Number(args, "interface_index");
                    if (!Fresh(observedNetworkAt) || !observedAdapters.TryGetValue(watchIndex, out var watched) || !physicalInterfaces.Contains(watchIndex) || watched.Disabled) throw new InvalidOperationException("Consulte a rede e selecione uma interface física habilitada. vEthernet não tem cabo próprio para retirar.");
                    identity = watched.Guid; displayName = watched.Name;
                    watchScript = "$a=@(Get-NetAdapter -ErrorAction Stop | Where-Object {[guid]$_.InterfaceGuid -eq [guid]" + Quote(identity) + "}); if($a -and [string]$a[0].Status -eq 'Disabled'){throw 'Interface foi desabilitada; isso não comprova retirada do cabo'}; [pscustomobject]@{Connected=([bool]($a -and [string]$a[0].Status -eq 'Up'))} | ConvertTo-Json -Compress";
                }
                else if (kind == "device")
                {
                    identity = Value(args, "device_id");
                    if (!Fresh(observedDevicesAt) || !observedDevices.TryGetValue(identity, out var watched) || !(identity.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) || identity.StartsWith("BTH", StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Consulte e identifique um periférico externo USB/Bluetooth antes de acompanhar a reconexão. Não peça para desmontar dispositivos internos.");
                    displayName = watched.Device.Name;
                    watchScript = "$d=@(Get-PnpDevice -PresentOnly -ErrorAction Stop | Where-Object {$_.InstanceId -eq " + Quote(identity) + "}); [pscustomobject]@{Connected=($d.Count -gt 0)} | ConvertTo-Json -Compress";
                }
                else throw new InvalidOperationException("Escolha kind=network ou device.");
                var watchResult = await PhysicalObservation.Wait(async ct =>
                {
                    using var snapshot = JsonDocument.Parse(await SupportService.Powershell(watchScript, ct, 15));
                    return new PhysicalState(snapshot.RootElement.GetProperty("Connected").GetBoolean(), identity);
                }, message => { var text = displayName + ": " + message; progress.Report(text); inform?.Invoke(text); }, token, TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(2));
                return Ok(watchResult);
            case "diagnose_device":
                var topic = Value(args, "topic");
                observedNvidiaDriver = null; observedNvidiaDriverAt = null;
                observedDevices.Clear();
                observedDevicesAt = null;
                if (topic == "system") return Ok((await support.Diagnose(actor, topic, progress, token)).Summary);
                var classes = DeviceClasses(topic);
                var diagnosis = DriverStoreInventory.ParseDeviceDiagnosis(await DeviceReader(DriverStoreInventory.DeviceDiagnosisScript(classes), token));
                var devices = diagnosis.Devices;
                foreach (var device in devices.Where(d => !string.IsNullOrWhiteSpace(d.Id))) observedDevices[device.Id] = (topic, device);
                var extra = topic == "camera" ? await SupportService.Powershell("Get-ItemProperty -LiteralPath 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\CapabilityAccessManager\\ConsentStore\\webcam' -ErrorAction SilentlyContinue | Select-Object Value | ConvertTo-Json -Compress", token) : "";
                observedDevicesAt = DateTimeOffset.UtcNow;
                return DeviceDiagnosisResult(diagnosis, extra);
            case "get_hardware_inventory":
                var category = Value(args, "category");
                observedNvidiaDriver = null; observedNvidiaDriverAt = null;
                var filter = HardwareFilter(category);
                observedDevices.Clear();
                observedDevicesAt = null;
                var inventoryScript = "$drivers=@{}; @(Get-CimInstance Win32_PnPSignedDriver -ErrorAction Stop) | ForEach-Object {if($_.DeviceID){$drivers[[string]$_.DeviceID]=$_}}; " +
                    "$items=@(Get-CimInstance Win32_PnPEntity -ErrorAction Stop | Where-Object {$_.Present -eq $true -and (" + filter + ")} | Sort-Object Name); " +
                    "$selected=@($items | Select-Object -First 80 | ForEach-Object {$d=$drivers[[string]$_.PNPDeviceID]; [pscustomobject]@{Name=[string]$_.Name;Id=[string]$_.PNPDeviceID;Code=[int]$_.ConfigManagerErrorCode;Status=[string]$_.Status;Class=[string]$_.PNPClass;Manufacturer=[string]$_.Manufacturer;HardwareIds=@($_.HardwareID | Where-Object {$_});CompatibleIds=@($_.CompatibleID | Where-Object {$_});DriverProvider=[string]$d.DriverProviderName;DriverVersion=[string]$d.DriverVersion}}); " +
                    (category == "storage" ? StorageVolumesScript : "") +
                    "[pscustomobject]@{Category=" + Quote(category) + ";Total=$items.Count;Truncated=($items.Count -gt 80);Devices=$selected" + (category == "storage" ? ";Volumes=$volumes;VolumesError=$volumesError" : "") + "} | ConvertTo-Json -Depth 6 -Compress";
                using (var inventory = JsonDocument.Parse(await SupportService.Powershell(inventoryScript, token, 90)))
                {
                    var listed = inventory.RootElement.GetProperty("Devices");
                    var foundDevices = JsonSerializer.Deserialize<List<Device>>(listed.GetRawText()) ?? [];
                    foreach (var device in foundDevices.Where(d => !string.IsNullOrWhiteSpace(d.Id))) observedDevices[device.Id] = (HardwareTopic(category), device);
                    observedDevicesAt = DateTimeOffset.UtcNow;
                    return category == "storage" ? StorageInventoryResult(inventory.RootElement) : Ok(inventory.RootElement.Clone());
                }
            case "get_printers":
                observedPrinters.Clear(); observedPrinterDetails.Clear();
                observedPrintersAt = null;
                var printers = await PrinterReader(PrintersScript, token);
                if (SpoolerNotRunning(printers) is { } spooler)
                {
                    // Sem o Spooler o Windows não lista impressoras: o estado do serviço é o resultado da consulta (#84).
                    observedServiceDetails["Spooler"] = JsonSerializer.SerializeToElement(new { Name = "Spooler", spooler.Status, spooler.StartType });
                    observedServiceAt["Spooler"] = ServiceEventsClock();
                    return SpoolerStoppedResult(spooler.Status, spooler.StartType);
                }
                // A lista só vem com o Spooler em execução: uma leitura anterior dele parado deixou de valer.
                observedServiceDetails.Remove("Spooler"); observedServiceAt.Remove("Spooler");
                using (var printerDoc = JsonDocument.Parse(printers)) foreach (var entry in printerDoc.RootElement.EnumerateArray())
                    if (entry.TryGetProperty("Name", out var printerName) && printerName.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(printerName.GetString()))
                    { observedPrinters.Add(printerName.GetString()!); observedPrinterDetails[printerName.GetString()!] = entry.Clone(); }
                observedPrintersAt = DateTimeOffset.UtcNow;
                return Ok(printers);
            case "get_services":
                var serviceRaw = await SupportService.Powershell("ConvertTo-Json -Compress -InputObject @(Get-Service -Name " + string.Join(",", ObservedServices) + " | Select-Object Name,@{N='Status';E={[string]$_.Status}},@{N='StartType';E={[string]$_.StartType}})", token);
                observedServiceDetails.Clear(); observedServiceAt.Clear();
                using (var serviceDoc = JsonDocument.Parse(serviceRaw)) foreach (var entry in serviceDoc.RootElement.EnumerateArray())
                    if (entry.TryGetProperty("Name", out var serviceName) && serviceName.ValueKind == JsonValueKind.String)
                    { observedServiceDetails[serviceName.GetString() ?? ""] = entry.Clone(); observedServiceAt[serviceName.GetString() ?? ""] = ServiceEventsClock(); }
                return Ok(serviceRaw);
            case "get_recent_errors":
                if (args.TryGetProperty("service", out _)) return await ReadServiceEvents(Value(args, "service"), token);
                return Ok(await SupportService.Powershell("$log=& \"$env:WINDIR\\System32\\wevtutil.exe\" qe System '/q:*[System[(Level=2) and TimeCreated[timediff(@SystemTime) <= 86400000]]]' /c:12 /rd:true /f:text; if($LASTEXITCODE -ne 0){throw 'Não foi possível ler os eventos do sistema'}; $text=$log -join \"`n\"; if($text.Length -gt 7000){$text.Substring(0,7000)}else{$text}", token));
            case "get_processes":
                return Ok(ProcessesForModel(await SupportService.Powershell("ConvertTo-Json -Compress -InputObject @(Get-Process | Sort-Object WorkingSet64 -Descending | Select-Object -First 12 Name,Id,@{N='MemoryMB';E={[math]::Round($_.WorkingSet64/1MB)}},Responding)", token)));
            case "get_health_report":
                using (var operational = new OperationalStore(store.Root))
                {
                    HealthThresholds limits;
                    try { limits = HealthSettings.Load(store.Root).Thresholds(); } catch { limits = new HealthThresholds(); }
                    return Ok(HealthReportPresentation.FromStore(operational, limits));
                }
            case "get_driver_store":
                var observedId = Value(args, "device_id");
                if (!Fresh(observedDevicesAt) || !observedDevices.ContainsKey(observedId)) throw new InvalidOperationException("Consulte get_hardware_inventory e escolha um ID exato antes de consultar o INF.");
                var package = await DriverStoreInventory.Read(observedId, token);
                return Ok(package == null ? new { available = false, note = "O Windows não retornou INF instalado para este dispositivo. O histórico de pacotes não foi confirmado." } : (object)new { available = true, package, note = "Este é o pacote associado ao dispositivo observado, não um catálogo completo de drivers." });
            case "set_dns":
            case "set_ipv4":
                var index = Number(args, "interface_index");
                if (!Fresh(observedNetworkAt) || !observedInterfaces.ContainsKey(index)) throw new InvalidOperationException("Consulte get_network nesta etapa antes de configurar a interface.");
                if (!configurableInterfaces.GetValueOrDefault(index)) throw new InvalidOperationException("Esta interface é gerenciada pelo Windows ou por VPN; reconfiguração de IP/DNS não permitida.");
                var request = NetworkChange.Parse(name, args) with { ExpectedGuid = observedAdapters[index].Guid };
                if (!await Approve(new ChangeRequest("Alterar configuração de rede", observedInterfaces[index] + "\n" + request.Description + "\nA conexão pode ser interrompida. O estado anterior será salvo e restaurado se você não confirmar a nova configuração."), token)) return Denied();
                var change = NetworkApplier is { } apply ? await apply(request, next => Approve(next, token), token) : await NetworkChange.Apply(request, store, next => Approve(next, token), progress, token);
                using (var changeResult = JsonDocument.Parse(change))
                {
                    if (!changeResult.RootElement.GetProperty("success").GetBoolean()) return change;
                    progress.Report("Conferindo IP, DHCP, gateway e DNS após a alteração…");
                    try
                    {
                        using var afterChange = JsonDocument.Parse(await Execute("get_network", JsonSerializer.SerializeToElement(new { }), CancellationToken.None));
                        return Ok(new { Change = changeResult.RootElement.Clone(), NetworkAfter = afterChange.RootElement.GetProperty("data").Clone(), InternetVerified = false });
                    }
                    catch (Exception ex) { return Ok(new { Change = changeResult.RootElement.Clone(), VerificationWarning = ex.Message, InternetVerified = false }); }
                }
            case "flush_dns":
                if (!await Approve(new ChangeRequest("Limpar cache DNS", "Limpar as resoluções de nomes em cache neste computador."), token)) return Denied();
                return Ok(await support.Repair(actor, "dns", null, token));
            case "enable_camera":
            case "enable_device":
            case "restart_device":
                var id = Value(args, "device_id");
                if (!Fresh(observedDevicesAt) || !observedDevices.TryGetValue(id, out var known)) throw new InvalidOperationException("Consulte o dispositivo nesta etapa antes de alterá-lo.");
                ValidateDeviceTarget(name, known.Topic, known.Device);
                if (!await Approve(new ChangeRequest("Ajustar dispositivo", DeviceChangeDetails(name, known.Device)), token)) return Denied();
                var allowedClasses = name == "enable_camera" ? "'Camera','Image'" : "'Camera','Image','AudioEndpoint','MEDIA','Bluetooth','Printer','PrintQueue'";
                var codeCheck = name == "restart_device" ? "" : "if([int]$d[0].ConfigManagerErrorCode -ne 22){throw 'Dispositivo não está mais desabilitado; faça novo diagnóstico'}; ";
                var deviceScript = "$d=@(Get-CimInstance Win32_PnPEntity | Where-Object {$_.PNPDeviceID -eq " + Quote(id) + " -and $_.PNPClass -in " + allowedClasses + "}); if($d.Count -ne 1){throw 'Dispositivo permitido não encontrado ou ambíguo'}; " + codeCheck + "& \"$env:WINDIR\\System32\\pnputil.exe\" " + (name == "restart_device" ? "/restart-device " : "/enable-device ") + Quote(id) + " | Out-Null; if($LASTEXITCODE -notin 0,3010){throw 'Falha do PnPUtil'}; Start-Sleep -Seconds 2; $after=@(Get-CimInstance Win32_PnPEntity | Where-Object {$_.PNPDeviceID -eq " + Quote(id) + "}); if($after.Count -ne 1 -or [int]$after[0].ConfigManagerErrorCode -ne 0){throw 'Windows ainda reporta erro no dispositivo; confirme se precisa reiniciar'}; $after[0] | Select-Object Name,ConfigManagerErrorCode,Status";
                return await Elevated(deviceScript, token);
            case "allow_camera":
                if (!await Approve(new ChangeRequest("Permitir câmera", "Permitir acesso à câmera neste perfil do Windows. Isso amplia a permissão de câmera para aplicativos; políticas da organização continuam valendo."), token)) return Denied();
                var privacy = await SupportService.Powershell("$p='HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\CapabilityAccessManager\\ConsentStore\\webcam'; $before=(Get-ItemProperty -LiteralPath $p -ErrorAction SilentlyContinue).Value; if(-not (Test-Path -LiteralPath $p)){New-Item -Path $p -Force | Out-Null}; Set-ItemProperty -LiteralPath $p -Name Value -Value Allow; [pscustomobject]@{Before=$before;After=(Get-ItemProperty -LiteralPath $p).Value} | ConvertTo-Json -Compress", token);
                return Ok(privacy);
            case "restart_service":
                var service = Value(args, "service");
                if (service is not ("Audiosrv" or "Spooler" or "bthserv" or "WlanSvc")) throw new InvalidOperationException("Serviço não permitido.");
                // O pedido cita os eventos do serviço (#58). Sem leitura recente, o executor consulta agora; se a consulta falhar,
                // o reinício continua disponível e o pedido diz que os eventos não foram lidos.
                if (!observedServiceEvents.TryGetValue(service, out var serviceEvents) || ServiceEventsClock() - serviceEvents.At > TimeSpan.FromMinutes(5))
                {
                    try { await ReadServiceEvents(service, token); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { /* A falha fica registrada como consulta indisponível. */ }
                    serviceEvents = observedServiceEvents[service];
                }
                if (!await Approve(new ChangeRequest("Reiniciar serviço", ServiceEvents.RestartDetails(service, serviceEvents.Note, ObservedServiceState(service))), token)) return Denied();
                string restarted;
                // Depois do reinício, a leitura de antes deixa de valer (#88): um 2º pedido não pode mostrar "parado" velho. Com o
                // resultado do Windows, ela vira o estado informado; sem ele, sai. Se o processo elevado nem iniciou, nada mudou.
                try { restarted = await Elevated("$s=Get-Service -Name " + Quote(service) + "; if($s.StartType -eq 'Disabled'){throw 'Serviço desabilitado. Verifique a política da organização antes de mudar o modo de inicialização'}; Restart-Service -Name " + Quote(service) + " -ErrorAction Stop; $s=Get-Service -Name " + Quote(service) + "; $s.WaitForStatus('Running',[timespan]::FromSeconds(20)); [pscustomobject]@{Service=$s.Name;Status=[string]$s.Status}", token); }
                catch (Exception ex) when (!ex.Data.Contains(TurnFacts.NotStartedKey)) { observedServiceDetails.Remove(service); observedServiceAt.Remove(service); throw; }
                RememberRestarted(service, restarted);
                return restarted;
            case "clear_print_queue":
                var printer = Value(args, "printer_name");
                if (!Fresh(observedPrintersAt) || !observedPrinters.Contains(printer)) throw new InvalidOperationException("Consulte get_printers e escolha uma impressora encontrada nesta etapa.");
                var queue = await SupportService.Powershell("$p=Get-Printer | Where-Object {$_.Name -eq " + Quote(printer) + "}; if(-not $p){throw 'Impressora não encontrada'}; @(Get-PrintJob -PrinterName $p.Name).Count", token);
                if (!await Approve(new ChangeRequest("Cancelar trabalhos de impressão", "Remover " + queue.Trim() + " trabalho(s) da fila “" + printer + "”. Eles precisarão ser enviados novamente."), token)) return Denied();
                return await Elevated("$p=Get-Printer | Where-Object {$_.Name -eq " + Quote(printer) + "}; if(-not $p){throw 'Impressora não encontrada'}; Get-PrintJob -PrinterName $p.Name | Remove-PrintJob; [pscustomobject]@{Printer=$p.Name;RemainingJobs=@(Get-PrintJob -PrinterName $p.Name).Count}", token);
            case "scan_driver_updates":
                if (!Fresh(observedDevicesAt) || observedDevices.Count == 0) throw new InvalidOperationException("Identifique o hardware com get_hardware_inventory ou diagnose_device antes de buscar drivers.");
                observedUpdates.Clear();
                observedUpdatesAt = null;
                progress.Report("Buscando online na fonte de atualizações configurada no Windows; isso pode levar alguns minutos…");
                var updates = await SupportService.Powershell(DriverSearchScript + "\nConvertTo-Json -Compress -InputObject @($search.Updates | ForEach-Object { [pscustomobject]@{Id=$_.Identity.UpdateID;Revision=[int]$_.Identity.RevisionNumber;Title=[string]$_.Title;HardwareID=[string]$_.DriverHardwareID;Manufacturer=[string]$_.DriverManufacturer;Provider=[string]$_.DriverProvider;Downloaded=[bool]$_.IsDownloaded} })", token, 180);
                var matchingUpdates = new List<object>();
                using (var doc = JsonDocument.Parse(updates)) foreach (var update in doc.RootElement.EnumerateArray())
                {
                    var updateIdFromScan = update.GetProperty("Id").GetString() ?? "";
                    if (!Guid.TryParse(updateIdFromScan, out _)) continue;
                    var hardwareId = update.GetProperty("HardwareID").GetString() ?? "";
                    Device matched;
                    try { matched = MatchDriverTarget(hardwareId, observedDevices.Values.Select(item => item.Device)); }
                    catch (InvalidOperationException) { continue; }
                    var title = update.GetProperty("Title").GetString() ?? "";
                    var revision = update.GetProperty("Revision").GetInt32();
                    observedUpdates[updateIdFromScan] = (title, hardwareId, revision);
                    matchingUpdates.Add(new { Id = updateIdFromScan, Revision = revision, Title = title, HardwareID = hardwareId,
                        Manufacturer = update.GetProperty("Manufacturer").GetString() ?? "", Provider = update.GetProperty("Provider").GetString() ?? "",
                        Target = matched.Name, Downloaded = update.GetProperty("Downloaded").GetBoolean(), Source = "Fonte de atualizações configurada no Windows" });
                }
                observedUpdatesAt = DateTimeOffset.UtcNow;
                return Ok(JsonSerializer.Serialize(matchingUpdates));
            case "find_official_nvidia_driver":
                if (!Fresh(observedDevicesAt) || observedDevices.Count != 1 || !OfficialDriverSource.IsNvidiaDisplay(observedDevices.Values.Single().Device))
                    throw new InvalidOperationException("Identifique uma única placa de vídeo NVIDIA presente antes da consulta oficial.");
                observedNvidiaDriver = null;
                observedNvidiaDriverAt = null;
                progress.Report("Consultando o catálogo oficial da NVIDIA para o modelo e Windows detectados…");
                var candidate = await OfficialDriverSource.FindNvidiaDriver(observedDevices.Values.Single().Device, token);
                observedNvidiaDriver = candidate;
                observedNvidiaDriverAt = DateTimeOffset.UtcNow;
                return Ok(candidate == null ? new { Available = false, Source = "NVIDIA" } : (object)new
                {
                    Available = true, DriverId = candidate.Id, Version = candidate.Version, Target = candidate.DeviceName,
                    InstalledVersion = candidate.InstalledVersion, Source = "NVIDIA", DownloadUrl = candidate.Url.ToString()
                });
            case "install_official_nvidia_driver":
                var vendorId = Value(args, "driver_id");
                if (!Fresh(observedNvidiaDriverAt) || observedNvidiaDriver == null || observedNvidiaDriver.Id != vendorId
                    || !Fresh(observedDevicesAt) || observedDevices.Count != 1 || !OfficialDriverSource.IsNvidiaDisplay(observedDevices.Values.Single().Device)
                    || observedDevices.Values.Single().Device.Id.Length == 0)
                    throw new InvalidOperationException("Consulte novamente o hardware e a oferta oficial antes de instalar.");
                var official = observedNvidiaDriver;
                var nvidiaDevice = observedDevices.Values.Single().Device;
                if (!string.Equals(nvidiaDevice.Name, official.DeviceName, StringComparison.OrdinalIgnoreCase)
                    || nvidiaDevice.DriverVersion != official.InstalledVersion)
                    throw new InvalidOperationException("O dispositivo mudou desde a consulta; instalação bloqueada.");
                var freshDetails = await OfficialDriverSource.GetNvidiaDetails(vendorId, token);
                var checkedOffer = OfficialDriverSource.ParseNvidiaDriverDetails(freshDetails, vendorId, nvidiaDevice.Name, nvidiaDevice.DriverVersion,
                    OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000));
                if (checkedOffer == null || checkedOffer.Url != official.Url || checkedOffer.Version != official.Version)
                    throw new InvalidOperationException("A oferta oficial mudou desde a consulta; instalação bloqueada.");
                var hdmiAudio = await DetectNvidiaHdmiAudio(token);
                if (!await Approve(new ChangeRequest("Instalar driver NVIDIA " + official.Version,
                    NvidiaApprovalDetails(nvidiaDevice.Name, nvidiaDevice.DriverVersion, official.Version, official.Url, hdmiAudio),
                    LicenseUrl: OfficialDriverSource.NvidiaLicense), token)) return Denied();
                var vendorRecovery = await PrepareDriverRecovery(token);
                if (vendorRecovery != null) return vendorRecovery;
                token.ThrowIfCancellationRequested();
                progress.Report("Baixando o driver completo da NVIDIA e validando a assinatura digital…");
                var driverFile = await OfficialDriverSource.DownloadNvidiaDriver(official, token);
                token.ThrowIfCancellationRequested();
                progress.Report($"Solicitando permissão do Windows e instalando o driver NVIDIA. O Nexo acompanha por {NvidiaSupervisionTimeout.TotalMinutes:F0} minutos e depois mantém o estado incerto até receber o resultado…");
                return VerifyDriverInstallationResult(await Elevated(NvidiaDriverInstallationScript(driverFile, official, nvidiaDevice.Id, nvidiaDevice.DriverVersion), NvidiaSupervisionTimeout, token));
            case "download_nvidia_app":
                if (!Fresh(observedDevicesAt) || observedDevices.Count != 1 || !OfficialDriverSource.IsNvidiaDisplay(observedDevices.Values.Single().Device))
                    throw new InvalidOperationException("O aplicativo NVIDIA só pode ser baixado após identificar uma única placa NVIDIA PCI presente.");
                if (!await Approve(new ChangeRequest("Baixar aplicativo oficial da NVIDIA", "O Windows Update não ofereceu um driver. Baixar o instalador do NVIDIA App a partir de nvidia.com/us.download.nvidia.com para escolher o driver da placa detectada. O Nexo verificará a assinatura digital antes de disponibilizar o arquivo. Esta etapa ainda não instala um driver."), token)) return Denied();
                progress.Report("Baixando do domínio oficial da NVIDIA e verificando a assinatura digital…");
                var vendorDownload = await OfficialDriverSource.DownloadNvidiaApp(token);
                return Ok(new { FileName = vendorDownload.Path, Source = vendorDownload.Url.ToString(), Signature = "Valid", Installed = false });
            case "install_driver_update":
                var updateId = Value(args, "update_id");
                if (!Fresh(observedUpdatesAt) || !observedUpdates.TryGetValue(updateId, out var selectedUpdate) || !Guid.TryParse(updateId, out _)) throw new InvalidOperationException("Consulte os drivers disponíveis e use um Id retornado nesta etapa.");
                if (!Fresh(observedDevicesAt)) throw new InvalidOperationException("Faça um novo diagnóstico do dispositivo antes de instalar o driver.");
                var driverTarget = MatchDriverTarget(selectedUpdate.HardwareId, observedDevices.Values.Select(item => item.Device));
                if (!await Approve(new ChangeRequest("Instalar driver pela fonte do Windows", "Baixar e instalar: " + selectedUpdate.Title + " (" + updateId + ", revisão " + selectedUpdate.Revision + ") para “" + driverTarget.Name + "”. Origem: fonte de atualizações configurada no Windows. Pode interromper o dispositivo e exigir reinicialização. A instalação inclui aceitar os termos do fornecedor. O Nexo não reiniciará o PC automaticamente. Antes da instalação, o Nexo tentará criar e confirmar um ponto de restauração; se indisponível, a instalação será interrompida."), token)) return Denied();
                var updateRecovery = await PrepareDriverRecovery(token);
                if (updateRecovery != null) return updateRecovery;
                return VerifyDriverInstallationResult(await Elevated(DriverInstallationScript(updateId, selectedUpdate.Title, selectedUpdate.HardwareId, selectedUpdate.Revision, driverTarget.Id), token));
            default: throw new InvalidOperationException("Ferramenta não permitida: " + name);
        }
    }
    internal static void ValidateDeviceTarget(string action, string topic, Device device)
    {
        if (action is not ("enable_camera" or "enable_device" or "restart_device") || string.IsNullOrWhiteSpace(device.Id) || action == "enable_camera" && topic != "camera" || topic is not ("camera" or "audio" or "bluetooth" or "printer"))
            throw new InvalidOperationException("O dispositivo consultado não pertence à categoria permitida para esta ação.");
        if (action != "restart_device" && device.Code != 22) throw new InvalidOperationException("O dispositivo não foi detectado como desabilitado.");
    }
    internal static Device MatchDriverTarget(string hardwareId, IEnumerable<Device> devices)
    {
        if (string.IsNullOrWhiteSpace(hardwareId)) throw new InvalidOperationException("O Windows Update não informou o ID de hardware; avalie este driver manualmente.");
        var matches = devices.Where(device => (device.HardwareIds ?? []).Concat(device.CompatibleIds ?? [])
            .Any(id => string.Equals(id, hardwareId, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("O driver não corresponde a um único dispositivo diagnosticado. Consulte o dispositivo e avalie a atualização manualmente.");
        return matches[0];
    }
    internal static string NvidiaDriverInstallationScript(string path, OfficialDriverSource.DriverOffer offer, string deviceId, string beforeVersion) => $$"""
        $targetId={{Quote(deviceId)}}
        $installer={{Quote(path)}}
        $expectedVersion={{Quote(beforeVersion)}}
        $before=@(Get-CimInstance Win32_PnPEntity -ErrorAction Stop | Where-Object {$_.PNPDeviceID -eq $targetId -and $_.Present -eq $true})
        if($before.Count -ne 1 -or $before[0].PNPClass -ne 'Display'){throw 'A placa NVIDIA não foi encontrada de forma única antes da instalação'}
        $signed=@(Get-CimInstance Win32_PnPSignedDriver -ErrorAction Stop | Where-Object {$_.DeviceID -eq $targetId})
        if($signed.Count -ne 1 -or [string]$signed[0].DriverVersion -ne $expectedVersion){throw 'A versão do driver mudou antes da instalação; operação bloqueada'}
        $signature=Get-AuthenticodeSignature -LiteralPath $installer
        if($signature.Status -ne 'Valid' -or [string]$signature.SignerCertificate.Subject -notmatch 'NVIDIA'){throw 'A assinatura do instalador NVIDIA não foi validada'}
        try{
            $audioBefore=@(Get-CimInstance Win32_PnPEntity -ErrorAction Stop | Where-Object {[string]$_.PNPDeviceID -like {{Quote(NvidiaHdmiAudioPattern)}} -and $_.Present -eq $true})
        }catch{throw ('Não foi possível consultar o áudio HDMI da placa antes da instalação. O instalador NVIDIA não foi iniciado e nenhum driver foi alterado. Detalhe: '+$_.Exception.Message)}
        $audioIds=@($audioBefore | ForEach-Object {[string]$_.PNPDeviceID})
        $audioBeforeCodes=@{}
        foreach($device in $audioBefore){$audioBeforeCodes[[string]$device.PNPDeviceID]=$(if($null -eq $device.ConfigManagerErrorCode){$null}else{[int]$device.ConfigManagerErrorCode})}
        $arguments=if($audioIds.Count -gt 0){ {{PowerShellArray(NvidiaInstallerArguments(hdmiAudio: true))}} }else{ {{PowerShellArray(NvidiaInstallerArguments(hdmiAudio: false))}} }
        $process=Start-Process -FilePath $installer -ArgumentList $arguments -PassThru -ErrorAction Stop
        if(-not $process.WaitForExit({{(long)NvidiaInstallerTimeout.TotalMilliseconds}})){$process.WaitForExit()}
        $exitCode=if($null -eq $process.ExitCode){$null}else{[int]$process.ExitCode}
        Start-Sleep -Seconds 3
        $after=@(Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue | Where-Object {$_.PNPDeviceID -eq $targetId -and $_.Present -eq $true})
        $afterDriver=@(Get-CimInstance Win32_PnPSignedDriver -ErrorAction SilentlyContinue | Where-Object {$_.DeviceID -eq $targetId})
        $afterVersion=if($afterDriver.Count -eq 1){[string]$afterDriver[0].DriverVersion}else{''}
        $audioAfterCodes=@{}
        $audioVanished=@(); $audioFailed=@(); $audioUnknown=@()
        $audioWorking=@($audioIds | Where-Object {$audioBeforeCodes[$_] -eq 0})
        if($audioIds.Count -gt 0){
            for($attempt=1; $attempt -le {{NvidiaAudioReadings}}; $attempt++){
                $devicesNow=@(Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue)
                foreach($id in $audioIds){
                    $match=@($devicesNow | Where-Object {$_.Present -eq $true -and [string]$_.PNPDeviceID -eq $id})
                    $audioAfterCodes[$id]=if($devicesNow.Count -eq 0 -or ($match.Count -eq 1 -and $null -eq $match[0].ConfigManagerErrorCode)){$null}elseif($match.Count -ne 1){-1}else{[int]$match[0].ConfigManagerErrorCode}
                }
                $audioVanished=@($audioWorking | Where-Object {$audioAfterCodes[$_] -eq -1})
                $audioUnknown=@($audioWorking | Where-Object {$null -eq $audioAfterCodes[$_]})
                $audioFailed=@($audioWorking | Where-Object {$null -ne $audioAfterCodes[$_] -and $audioAfterCodes[$_] -notin @(0,-1)})
                if(($audioVanished.Count+$audioFailed.Count+$audioUnknown.Count) -eq 0 -or $attempt -ge {{NvidiaAudioReadings}}){break}
                Start-Sleep -Seconds {{NvidiaAudioReadingIntervalSeconds}}
            }
        }
        $audioPreexisting=@($audioIds | Where-Object {$audioBeforeCodes[$_] -ne 0})
        $audioStatus=if($audioIds.Count -eq 0){'absent'}elseif($audioVanished.Count){'vanished'}elseif($audioFailed.Count){'failed'}elseif($audioUnknown.Count){'unknown'}elseif($audioPreexisting.Count){'preexisting'}else{'preserved'}
        $audioKey=if($audioIds.Count -gt 0){@($audioVanished+$audioFailed+$audioUnknown+$audioPreexisting+$audioIds)[0]}else{''}
        $audioBeforeCode=if($audioKey){$audioBeforeCodes[$audioKey]}else{$null}
        $audioAfterCode=if($audioKey){$audioAfterCodes[$audioKey]}else{$null}
        $audioPreserved=if($audioIds.Count -eq 0){$null}else{$audioStatus -eq 'preserved'}
        $audioDriverVersion=''
        if($audioKey){$audioDriver=@(Get-CimInstance Win32_PnPSignedDriver -ErrorAction SilentlyContinue | Where-Object {[string]$_.DeviceID -eq $audioKey}); if($audioDriver.Count -eq 1){$audioDriverVersion=[string]$audioDriver[0].DriverVersion} }
        [pscustomobject]@{Driver='NVIDIA '+{{Quote(offer.Version)}};OfferedVersion={{Quote(offer.Version)}};Target=[string]$before[0].Name;BeforeVersion=$expectedVersion;AfterVersion=$afterVersion;AfterDriverCount=$afterDriver.Count;DeviceCodeAfter=$(if($after.Count -eq 1 -and $null -ne $after[0].ConfigManagerErrorCode){[int]$after[0].ConfigManagerErrorCode}else{-1});RebootRequired=($exitCode -eq 1);Source='NVIDIA';InstallerExitCode=$exitCode;HdmiAudioBefore=($audioIds.Count -gt 0);HdmiAudioStatus=$audioStatus;HdmiAudioBeforeCode=$audioBeforeCode;HdmiAudioAfterCode=$audioAfterCode;HdmiAudioPreserved=$audioPreserved;HdmiAudioDriverVersion=$audioDriverVersion}
        """;
    // Instalar só Display.Driver faz o setup da NVIDIA remover o nvhda.inf (áudio HDMI/DisplayPort da placa).
    // Os IDs vêm do cache do instalador: HDAudio.nvi (name="HDAudio.Driver") e setup.cfg (Display.Driver).
    internal const string NvidiaHdmiAudioPattern = "HDAUDIO\\FUNC_01&VEN_10DE&*";
    // Depois dos 3 s iniciais, até 5 leituras do áudio com 2 s entre elas (no máximo 8 s a mais), sem espera após a última.
    internal const int NvidiaAudioReadings = 5;
    internal const int NvidiaAudioReadingIntervalSeconds = 2;
    internal static string[] NvidiaInstallerArguments(bool hdmiAudio) => hdmiAudio
        ? ["-s", "-n", "Display.Driver", "HDAudio.Driver"]
        : ["-s", "-n", "Display.Driver"];
    internal static string PowerShellArray(IEnumerable<string> values) => "@(" + string.Join(",", values.Select(Quote)) + ")";
    // Consulta só de leitura, sem elevação, para avisar no pedido de autorização. O script elevado decide de novo na hora.
    internal static readonly string NvidiaHdmiAudioQuery = "ConvertTo-Json -Compress -InputObject @(Get-CimInstance Win32_PnPEntity -ErrorAction Stop | Where-Object { [string]$_.PNPDeviceID -like "
        + Quote(NvidiaHdmiAudioPattern) + " -and $_.Present -eq $true } | ForEach-Object { [string]$_.PNPDeviceID })";
    internal static bool? ParseNvidiaHdmiAudioQuery(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            return document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.GetArrayLength() > 0 : null;
        }
        catch (JsonException) { return null; }
    }
    internal static Task<bool?> DetectNvidiaHdmiAudio(CancellationToken token) =>
        DetectNvidiaHdmiAudio((script, cancellation) => SupportService.Powershell(script, cancellation), token);
    // O executor é parâmetro para os testes; em produção é a consulta PowerShell sem elevação.
    internal static async Task<bool?> DetectNvidiaHdmiAudio(Func<string, CancellationToken, Task<string>> powershell, CancellationToken token)
    {
        try { return ParseNvidiaHdmiAudioQuery(await powershell(NvidiaHdmiAudioQuery, token)); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }
    internal static string NvidiaApprovalDetails(string deviceName, string currentVersion, string newVersion, Uri source, bool? hdmiAudio) =>
        "Placa: " + deviceName + "\nVersão atual: " + currentVersion + "\nNova versão: " + newVersion + "\nFonte: " + source
        + hdmiAudio switch
        {
            true => "\nO áudio HDMI/DisplayPort da placa (som pelo monitor ou TV) também será reinstalado. Se você usa o som pelo monitor ou TV, talvez seja preciso testá-lo depois.",
            null => "\nSe o computador tiver áudio HDMI/DisplayPort da placa (som pelo monitor ou TV), ele também será reinstalado. Se você usa o som pelo monitor ou TV, talvez seja preciso testá-lo depois.",
            false => ""
        }
        + "\nO download tem cerca de 1 GB. A tela pode piscar; talvez seja necessário reiniciar."
        + "\nO Nexo não reiniciará o computador. Leia e aceite a licença da NVIDIA para autorizar o download e a instalação. Antes de instalar, o Nexo tentará criar e confirmar um ponto de restauração; se indisponível, a instalação será interrompida.";
    // Vale só para quem usa o som pelo monitor ou TV: um monitor sem alto-falante não indica defeito.
    private const string SoundTestSteps = " Se você usa o som pelo monitor ou TV (HDMI/DisplayPort), toque um áudio e confira se ele sai. Se não sair:"
        + " 1) confira se a saída escolhida no Windows é a do monitor ou TV; 2) feche e abra de novo o aplicativo que toca o som;"
        + " 3) desligue e ligue o monitor ou TV, ou reconecte o cabo HDMI/DisplayPort; 4) reinicie o computador. Se ainda assim não sair, encaminhe à TI.";
    // O Windows só informa o estado do dispositivo de áudio; quem confirma se o som sai é o usuário.
    // Por isso o áudio sem erro nunca é declarado confirmado e vem com o pedido de teste.
    // Tipos: "warning" quando o áudio que funcionava piorou; "audioTest" quando é preciso testar o som; "audioInfo" quando nada mudou.
    internal static (string Kind, string Message)? HdmiAudioReport(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("HdmiAudioBefore", out var before) || before.ValueKind != JsonValueKind.True)
            return null;
        int? Code(string key) => data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
        string Text(string key) => data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        string Describe(int? code) => code is { } known ? "código " + known : "sem código informado";
        var beforeCode = Code("HdmiAudioBeforeCode");
        var afterCode = Code("HdmiAudioAfterCode");
        var version = Text("HdmiAudioDriverVersion");
        var reboot = data.TryGetProperty("RebootRequired", out var restart) && restart.ValueKind == JsonValueKind.True;
        const string device = "dispositivo de áudio HDMI/DisplayPort da placa NVIDIA";
        var guidance = (reboot ? " O Windows pediu reinicialização: reinicie e, se usa o som pelo monitor ou TV, teste de novo." : "")
            + " Se continuar sem som, confira “Controladores de som, vídeo e jogos” no Gerenciador de Dispositivos ou encaminhe à TI.";
        return Text("HdmiAudioStatus") switch
        {
            "vanished" => ("warning", "Atenção: o " + device + " funcionava antes da instalação e o Windows não o encontra mais. O som pela placa parou: a saída HDMI/DisplayPort para monitor ou TV ficou sem áudio." + guidance),
            "failed" => ("warning", "Atenção: o " + device + " estava sem erro antes da instalação e agora o Windows informa erro (" + Describe(afterCode) + "). O som pela placa parou: a saída HDMI/DisplayPort para monitor ou TV ficou sem áudio." + guidance),
            "preexisting" when afterCode == 0 => ("audioTest", "O " + device + " já estava desativado ou com erro antes da instalação (" + Describe(beforeCode) + ") e agora está sem erro"
                + (version.Length > 0 ? " (driver " + version + ")" : "") + "." + SoundTestSteps),
            "preexisting" => ("audioInfo", "O " + device + " já estava desativado ou com erro antes da instalação (" + Describe(beforeCode) + ") e continua assim ("
                + (afterCode == -1 ? "não aparece mais no Windows" : Describe(afterCode)) + "). Esse estado não foi causado por esta instalação."),
            "preserved" when afterCode == 0 => ("audioTest", "O " + device + " está sem erro no Windows"
                + (version.Length > 0 ? " (driver " + version + ")" : " (versão do driver não informada)") + ". O Windows só informa o estado do dispositivo; quem confirma se o som sai é você." + SoundTestSteps),
            _ => ("audioTest", "O Windows não informou o estado do " + device + " depois da instalação, então o Nexo não sabe se o áudio pela placa continua funcionando." + SoundTestSteps)
        };
    }
    private static string DriverInstallationScript(string updateId, string title, string hardwareId, int revision, string deviceId) => DriverSearchScript + "\n" + $$"""
        $targetId={{Quote(deviceId)}}
        $hardwareId={{Quote(hardwareId)}}
        $before=@(Get-CimInstance Win32_PnPEntity -ErrorAction Stop | Where-Object {$_.PNPDeviceID -eq $targetId -and $_.Present -eq $true})
        if($before.Count -ne 1 -or -not (@($before[0].HardwareID)+@($before[0].CompatibleID) -contains $hardwareId)){throw 'Identidade do hardware mudou; instalação bloqueada'}
        $beforeDriver=@(Get-CimInstance Win32_PnPSignedDriver -ErrorAction SilentlyContinue | Where-Object {$_.DeviceID -eq $targetId} | Select-Object -First 1)
        $matches=@($search.Updates | Where-Object {$_.Identity.UpdateID -eq {{Quote(updateId)}} -and [int]$_.Identity.RevisionNumber -eq {{revision}} -and [string]$_.Title -eq {{Quote(title)}} -and [string]$_.DriverHardwareID -eq $hardwareId})
        if($matches.Count -ne 1){throw 'Driver consultado não está mais disponível com a mesma identidade e revisão'}
        $update=$matches[0]
        if(-not $update.EulaAccepted){$update.AcceptEula()}
        $collection=New-Object -ComObject Microsoft.Update.UpdateColl
        [void]$collection.Add($update)
        $downloader=$session.CreateUpdateDownloader()
        $downloader.Updates=$collection
        $download=$downloader.Download()
        if([int]$download.ResultCode -ne 2){throw ('Download não concluído: '+[int]$download.ResultCode)}
        $installer=$session.CreateUpdateInstaller()
        $installer.Updates=$collection
        $result=$installer.Install()
        if([int]$result.ResultCode -ne 2 -or [int]$result.GetUpdateResult(0).ResultCode -ne 2){throw ('Instalação não concluída: '+[int]$result.ResultCode)}
        $after=@(Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue | Where-Object {$_.PNPDeviceID -eq $targetId -and $_.Present -eq $true})
        $afterDriver=@(Get-CimInstance Win32_PnPSignedDriver -ErrorAction SilentlyContinue | Where-Object {$_.DeviceID -eq $targetId} | Select-Object -First 1)
        $beforeVersion=[string]$beforeDriver[0].DriverVersion
        $afterVersion=[string]$afterDriver[0].DriverVersion
        $verification=if([bool]$result.RebootRequired){'pending_restart'}elseif($after.Count -eq 1 -and [int]$after[0].ConfigManagerErrorCode -eq 0 -and $afterVersion -and $afterVersion -ne $beforeVersion){'version_changed'}else{'unconfirmed'}
        [pscustomobject]@{Driver=[string]$update.Title;Target=[string]$before[0].Name;BeforeVersion=$beforeVersion;AfterVersion=$afterVersion;DeviceCodeAfter=$(if($after.Count -eq 1){[int]$after[0].ConfigManagerErrorCode}else{-1});RebootRequired=[bool]$result.RebootRequired;Verification=$verification;Source='Fonte de atualizações configurada no Windows'}
        """;
    // Resultado da instalação NVIDIA, sempre comparado com a oferta (#13). O script só mede; a decisão é tomada aqui.
    //   expected_version_confirmed: placa presente, Code 0, e a versão do Windows corresponde à oferta pela regra de InstalledMarketingVersion
    //                               (32.0.16.2114 ↔ 621.14). Conta como instalada, ainda com teste funcional pendente.
    //   unexpected_version:         a placa está com Code 0 ou 14 e a versão mudou, mas para outra que não a oferecida. A oferta não é dada como instalada.
    //   unchanged:                  a placa está com Code 0 e continua na versão de antes.
    //   unconfirmed:                a placa sumiu, ficou com erro, o Windows não informou uma única versão ou o instalador não informou o código de saída.
    //   pending_restart:            o instalador saiu com 1 (reinício pedido), com a placa em Code 0 ou 14, e a versão é conferida depois de
    //                               reiniciar; ou a placa ficou em Code 14 (CM_PROB_NEED_RESTART) já na versão oferecida. Code 14 com outra
    //                               versão é unexpected_version, e sem mudança de versão é unconfirmed.
    // O código de saída nunca decide sozinho: o setup 621.14 saiu com 0x80070005 com a versão nova instalada (ensaio de 07/11/2026).
    internal static bool MatchesOfferedNvidiaVersion(string offeredVersion, string windowsVersion) =>
        Regex.IsMatch(offeredVersion, @"^\d{3}\.\d{2}$") && Version.TryParse(offeredVersion, out var offered)
        && OfficialDriverSource.InstalledMarketingVersion(windowsVersion) == offered;
    internal const int DeviceNeedsRestart = 14;
    internal static string ClassifyNvidiaInstallation(string offeredVersion, string beforeVersion, string afterVersion, int deviceCodeAfter, int? installerExitCode, bool rebootRequired)
    {
        var reported = !string.IsNullOrWhiteSpace(afterVersion);
        var changed = reported && !string.Equals(afterVersion.Trim(), beforeVersion.Trim(), StringComparison.OrdinalIgnoreCase);
        // Reinício pedido pelo instalador só conta com a placa sem erro ou só esperando reinício; placa sumida ou com erro não vira sucesso.
        if (installerExitCode == 1 || rebootRequired) return deviceCodeAfter is 0 or DeviceNeedsRestart ? "pending_restart" : "unconfirmed";
        if (installerExitCode == null) return "unconfirmed";
        if (deviceCodeAfter == DeviceNeedsRestart && changed)
            return MatchesOfferedNvidiaVersion(offeredVersion, afterVersion.Trim()) ? "pending_restart" : "unexpected_version";
        if (deviceCodeAfter != 0 || !reported) return "unconfirmed";
        if (!changed) return "unchanged";
        return MatchesOfferedNvidiaVersion(offeredVersion, afterVersion.Trim()) ? "expected_version_confirmed" : "unexpected_version";
    }
    internal static string InstallerExitCodeText(int code) => "código " + code + " (0x" + unchecked((uint)code).ToString("X8") + ")";
    private static string VerifyNvidiaInstallation(JsonElement data, string offered)
    {
        string Text(string key) => data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        int? Number(string key) => data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
        var exitCode = Number("InstallerExitCode");
        var deviceCode = Number("DeviceCodeAfter") ?? -1;
        var before = Text("BeforeVersion");
        var after = Text("AfterVersion");
        var reboot = data.TryGetProperty("RebootRequired", out var restart) && restart.ValueKind == JsonValueKind.True;
        var verification = ClassifyNvidiaInstallation(offered, before, after, deviceCode, exitCode, reboot);
        var classified = System.Text.Json.Nodes.JsonNode.Parse(data.GetRawText())!.AsObject();
        classified["Verification"] = verification;
        // Quem pediu o reinício: o instalador (código 1) ou o Windows (placa em Code 14).
        if (verification == "pending_restart") classified["RestartRequestedBy"] = exitCode == 1 || reboot ? "installer" : "windows";
        var audio = HdmiAudioReport(data);
        var unusualExit = exitCode is { } code && code is not (0 or 1) ? InstallerExitCodeText(code) : null;
        if (verification is "expected_version_confirmed" or "pending_restart")
        {
            var result = new System.Text.Json.Nodes.JsonObject { ["success"] = true };
            // A versão já aparece na frase principal do resultado; o aviso só explica o código.
            if (unusualExit != null)
                result["installerWarning"] = verification == "expected_version_confirmed"
                    ? "Aviso: o instalador NVIDIA terminou com " + unusualExit + ", mas o Windows confirma a versão oferecida, com a placa sem erro. O código fica registrado para a TI."
                    : "Aviso: o instalador NVIDIA terminou com " + unusualExit + ". Depois de reiniciar, confira se a versão do driver é a oferecida. O código fica registrado para a TI.";
            if (audio is { } report) result[report.Kind] = report.Message;
            result["data"] = classified;
            return result.ToJsonString();
        }
        var installed = OfficialDriverSource.InstalledMarketingVersion(after.Trim());
        var reason = verification switch
        {
            "unexpected_version" => "O Windows agora informa a versão " + after + (installed is { } other ? " (driver " + other.Major + "." + other.Minor.ToString("D2") + ")" : "")
                + ", que não corresponde ao driver " + offered + " oferecido" + (deviceCode == DeviceNeedsRestart ? ", e também pede reinício da placa (código 14)" : "")
                + ". O Nexo não considera a versão oferecida instalada.",
            "unchanged" => "O Windows continua informando a versão anterior " + after + "; o driver " + offered + " oferecido não foi confirmado no dispositivo.",
            _ => (exitCode == 1 ? "O instalador NVIDIA pediu reinicialização, mas, depois" : "Depois") + " da instalação, "
                + (exitCode == null ? "o instalador não informou o código de saída"
                    : deviceCode == -1 ? "o Windows não encontrou a placa NVIDIA de forma única ou não informou o estado dela"
                    : deviceCode == DeviceNeedsRestart ? "o Windows pede reinício da placa NVIDIA (código 14), mas não informou uma versão nova do driver"
                    : deviceCode != 0 ? "a placa NVIDIA está com erro no Windows (código " + deviceCode + ")"
                    : Number("AfterDriverCount") > 1 ? "o Windows informou mais de um driver para a placa"
                    : "o Windows não informou a versão do driver da placa")
                + ", então o driver " + offered + " oferecido não foi confirmado."
        };
        return JsonSerializer.Serialize(new
        {
            success = false,
            installationReported = true,
            error = (unusualExit != null ? "O instalador NVIDIA terminou com " + unusualExit + ". " : "") + reason
                + " Confira o Gerenciador de Dispositivos antes de considerar a correção concluída." + (audio is { } note ? " " + note.Message : ""),
            data = classified
        });
    }
    internal static string VerifyDriverInstallationResult(string output)
    {
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        if (!root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object) return output;
        if (data.TryGetProperty("OfferedVersion", out var offeredVersion) && offeredVersion.ValueKind == JsonValueKind.String)
            return VerifyNvidiaInstallation(data, offeredVersion.GetString() ?? "");
        // result.json gravado por um Nexo anterior ao #13: a instalação NVIDIA só comparava com a versão anterior.
        if (data.TryGetProperty("Source", out var source) && source.ValueKind == JsonValueKind.String && source.GetString() == "NVIDIA"
            && data.TryGetProperty("Verification", out var legacy) && legacy.ValueKind == JsonValueKind.String && legacy.GetString() == "version_changed")
        {
            var unverified = System.Text.Json.Nodes.JsonNode.Parse(data.GetRawText())!.AsObject();
            unverified["Verification"] = "unconfirmed";
            return JsonSerializer.Serialize(new
            {
                success = false,
                installationReported = true,
                error = "Este resultado veio de uma versão anterior do Nexo, que não comparava a versão instalada com o driver NVIDIA oferecido. Confira no Gerenciador de Dispositivos se a versão do driver de vídeo é a oferecida antes de considerar a correção concluída."
                    + (HdmiAudioReport(data) is { } legacyAudio ? " " + legacyAudio.Message : ""),
                data = unverified
            });
        }
        // Fonte do Windows: a oferta (IWindowsDriverUpdate) não tem campo de versão do driver, e o scan_driver_updates guarda só
        // ID, revisão, título, ID de hardware, fabricante e provedor. Sem versão oferecida, o script continua comparando com a
        // versão anterior (version_changed).
        if (!data.TryGetProperty("Verification", out var verification) || verification.ValueKind != JsonValueKind.String) return output;
        var audio = HdmiAudioReport(data);
        if (verification.GetString() != "unconfirmed")
            return audio is not { } report ? output
                : JsonSerializer.Serialize(new Dictionary<string, object> { ["success"] = true, [report.Kind] = report.Message, ["data"] = data.Clone() });
        return JsonSerializer.Serialize(new
        {
            success = false,
            installationReported = true,
            error = "O Windows relatou a instalação, mas ainda não foi possível confirmar a nova versão do driver no dispositivo. Confira o Gerenciador de Dispositivos antes de considerar a correção concluída."
                + (audio is { } note ? " " + note.Message : ""),
            data = data.Clone()
        });
    }
    private async Task<bool> Approve(ChangeRequest request, CancellationToken token)
    {
        store.Require(actor, a => a.Repair);
        token.ThrowIfCancellationRequested();
        try
        {
            var result = await confirm(request);
            lastApproval = result;
            changeAuthorized |= result;
            token.ThrowIfCancellationRequested();
            store.Require(actor, a => a.Repair);
            store.Log(actor, (result ? "Autorizado: " : "Recusado: ") + request.Title);
            MoveIntervention(result ? "authorized" : "denied", request.Title);
            if (result) MoveIntervention("executing", "Autorização registrada; execução pode começar.");
            return result;
        }
        catch (OperationCanceledException)
        {
            store.Log(actor, "Cancelado: " + request.Title);
            throw;
        }
    }
    private async Task<string?> PrepareDriverRecovery(CancellationToken token)
    {
        progress.Report("Criando e confirmando um ponto de restauração antes do driver…");
        var result = await RestorePointProtection.Prepare(ElevatedPreparation, token);
        if (!result.Ready)
        {
            MoveIntervention("protection_unavailable", result.Reason);
            return JsonSerializer.Serialize(new { success = false, recoveryProtectionUnavailable = true, error = "A proteção de restauração não foi confirmada; o driver não foi instalado. " + result.Reason });
        }
        currentIntervention!.RecoveryPointSequence = result.SequenceNumber;
        MoveIntervention("protected", "Ponto de restauração " + result.SequenceNumber + " confirmado antes da instalação.");
        return null;
    }
    private async Task<string> ReadServiceEvents(string service, CancellationToken token)
    {
        if (!ServiceEvents.IsAllowed(service)) throw new InvalidOperationException("Serviço não permitido.");
        try
        {
            var result = ServiceEvents.Result(ServiceEvents.Parse(await ServiceEventsReader(ServiceEvents.Script(service), token), service), ServiceEventsClock());
            observedServiceEvents[service] = (ServiceEventsClock(), ServiceEvents.Note(result, service));
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            observedServiceEvents[service] = (ServiceEventsClock(), ServiceEvents.Unavailable(service));
            throw new InvalidOperationException(ServiceEvents.Unavailable(service) + " " + (ex is JsonException ? "O Windows devolveu um resultado em formato inesperado." : ex.Message), ex);
        }
    }
    private static string Denied() => JsonSerializer.Serialize(new { success = false, denied = true, message = "Ação recusada pelo usuário. Não executar nem repetir." });
    public static string Ok(object data) => JsonSerializer.Serialize(new { success = true, data });
    public static string DeviceDiagnosisResult(DeviceDiagnosis diagnosis, string privacy) => Ok(new { count = diagnosis.Devices.Count, devices = diagnosis.Devices, drivers = DriverStoreInventory.FaultyDrivers(diagnosis), privacy, note = diagnosis.Devices.Count == 0 ? "Nenhum dispositivo desta categoria foi encontrado. Não existe dispositivo identificado para habilitar. Pode estar ausente, desconectado ou sem reconhecimento; não afirme que está desabilitado sem evidência." : "Code 22 = desabilitado; Code 0 = sem erro de PnP, não comprova funcionamento no aplicativo. Em drivers, cada dispositivo com erro traz o INF, a versão e o fornecedor ligados ao ID exato; \"sem driver instalado\" significa que o Windows não tem driver para esse ID." });

    // O nome de um executável é escolhido por quem o criou: é dado não confiável, e o filtro só limita o que chega ao modelo.
    public static string ProcessesForModel(string json)
    {
        System.Text.Json.Nodes.JsonArray? processes;
        try { processes = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonArray; }
        catch (JsonException ex) { throw new InvalidDataException("A lista de processos veio em formato inesperado.", ex); }
        if (processes == null || processes.Any(process => process is not System.Text.Json.Nodes.JsonObject))
            throw new InvalidDataException("A lista de processos veio em formato inesperado.");
        foreach (var process in processes.Cast<System.Text.Json.Nodes.JsonObject>())
        {
            var name = process["Name"] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
            var safe = new string(name.Take(40).Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray());
            process["Name"] = safe.Length == 0 ? "processo" : safe;
        }
        return processes.ToJsonString();
    }
    private static async Task<string> TestConnection(string host, int port, CancellationToken token)
    {
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown || host.Length > 253 || port is < 1 or > 65535) throw new InvalidOperationException("Informe hostname/IP e porta válidos.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        string[] addresses;
        try { addresses = (await Dns.GetHostAddressesAsync(host, deadline.Token)).Select(a => a.ToString()).ToArray(); }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException) { token.ThrowIfCancellationRequested(); return Ok(new { host, dnsResolved = false, tcpConnected = false, error = ex.Message }); }
        using var tcp = new TcpClient(); var clock = Stopwatch.StartNew();
        try { await tcp.ConnectAsync(host, port, deadline.Token); return Ok(new { host, port, addresses, dnsResolved = true, tcpConnected = true, elapsedMs = clock.ElapsedMilliseconds }); }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException) { token.ThrowIfCancellationRequested(); return Ok(new { host, port, addresses, dnsResolved = true, tcpConnected = false, error = ex.Message }); }
    }
    // O mesmo invólucro do processo elevado é usado pelo teste que executa o script com cmdlets falsos.
    internal static string ElevatedWrapper(string script, string output) =>
        "$ErrorActionPreference='Stop'; try { $data=& { " + script + " }; $result=@{success=$true;data=$data} } catch { $result=@{success=$false;error=$_.Exception.Message} }; $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath " + Quote(output) + " -Encoding UTF8";
    private Task<string> Elevated(string script, CancellationToken token) => Elevated(script, ElevatedTimeout, token);

    private Task<string> ElevatedPreparation(string script, CancellationToken token) => Elevated(script, ElevatedTimeout, token, tracksResult: false);

    private async Task<string> Elevated(string script, TimeSpan timeout, CancellationToken token, bool tracksResult = true)
    {
        token.ThrowIfCancellationRequested();
        var operationId = Guid.NewGuid().ToString("N");
        var dir = PendingOperations.Folder(store.Root, operationId); Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "result.json");
        var wrapped = ElevatedWrapper(script, output);
        var finished = false;
        var started = false;
        var registered = false;
        try
        {
            token.ThrowIfCancellationRequested();
            if (tracksResult && currentIntervention != null)
            {
                using var operational = new OperationalStore(store.Root);
                PendingOperations.Register(operational, currentIntervention, operationId, DateTimeOffset.UtcNow);
                registered = true;
            }
            Process process;
            // O processo elevado não iniciou (UAC cancelado, Windows recusou): o script não rodou e nada foi alterado (#73).
            try { EnsureElevatedFits(wrapped); process = ElevatedStarter(wrapped); }
            catch (Exception ex) when (ex is not OperationCanceledException) { ex.Data[TurnFacts.NotStartedKey] = true; throw; }
            using var elevated = process;
            started = true;
            var state = await OperationSupervisor.Wait(async () => { await process.WaitForExitAsync(); return process.ExitCode; }, timeout, token);
            finished = process.HasExited;
            if (state == OperationState.Unknown) return JsonSerializer.Serialize(new { success = false, state = "unknown", error = "O processo elevado não confirmou o término no prazo ou a espera foi cancelada. A ação pode continuar em segundo plano; confira o estado antes de repetir." });
            if (state == OperationState.Failed || !File.Exists(output)) return JsonSerializer.Serialize(new { success = false, state = "unknown", error = "O processo elevado não retornou resultado válido. Estado não confirmado." });
            return await File.ReadAllTextAsync(output, CancellationToken.None);
        }
        finally
        {
            if (registered && !started)
            {
                if (currentIntervention != null) currentIntervention.PendingOperation = "";
                PendingOperations.Release(operationId);
            }
            try { if (!started || finished && !registered) { if (File.Exists(output)) File.Delete(output); Directory.Delete(dir); } }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    internal static Process StartElevated(string script) => StartElevated(ElevatedArguments(script));

    /// <summary>Limite da linha de comando do Windows (CreateProcess), contando o caminho do powershell.exe.</summary>
    internal const int CommandLineLimit = 32767;
    internal static string ElevatedShell => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>Argumentos do powershell.exe elevado: o script vai inteiro, em UTF-16 e Base64, em -EncodedCommand.</summary>
    internal static string[] ElevatedArguments(string script) => ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))];

    /// <summary>Tamanho da linha de comando que o Windows recebe: caminho entre aspas e argumentos separados por espaço.</summary>
    internal static int CommandLength(IReadOnlyList<string> arguments) => ElevatedShell.Length + 2 + arguments.Sum(argument => argument.Length + 1);

    // Um script maior que a linha de comando do Windows não chega a iniciar: falha clara, antes do UAC, sem alterar nada.
    internal static void EnsureElevatedFits(string script) => EnsureFits(ElevatedArguments(script));
    private static void EnsureFits(IReadOnlyList<string> arguments)
    {
        var length = CommandLength(arguments);
        if (length > CommandLineLimit) throw new InvalidOperationException($"O comando elevado tem {length} caracteres e passa do limite de {CommandLineLimit} da linha de comando do Windows. A operação não foi iniciada e nada foi alterado.");
    }

    internal static Process StartElevated(IReadOnlyList<string> arguments)
    {
        EnsureFits(arguments);
        var start = new ProcessStartInfo(ElevatedShell) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("O Windows não iniciou a operação elevada.");
    }
}
