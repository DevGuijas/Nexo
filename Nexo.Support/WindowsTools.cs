using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nexo.Support;

public sealed record ChangeRequest(string Title, string Details, bool KeepNetwork = false);
public sealed class WindowsTools(Store store, Account actor, Func<ChangeRequest, Task<bool>> confirm, IProgress<string> progress, Action<string>? inform = null) : IAgentTools
{
    private readonly SupportService support = new(store);
    private readonly Dictionary<int, string> observedInterfaces = [];
    private readonly Dictionary<int, ObservedAdapter> observedAdapters = [];
    private readonly Dictionary<int, bool> configurableInterfaces = [];
    private readonly HashSet<int> physicalInterfaces = [];
    private readonly Dictionary<string, (string Topic, Device Device)> observedDevices = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> observedPrinters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Title, string HardwareId, int Revision)> observedUpdates = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? observedNetworkAt;
    private DateTimeOffset? observedDevicesAt;
    private DateTimeOffset? observedPrintersAt;
    private DateTimeOffset? observedUpdatesAt;
    private static readonly IReadOnlyDictionary<string, JsonElement> toolSchemas = ToolCatalog.ForScope(false)
        .Select(tool => JsonSerializer.SerializeToElement(tool))
        .ToDictionary(tool => tool.GetProperty("function").GetProperty("name").GetString()!, tool => tool.GetProperty("function").GetProperty("parameters").Clone(), StringComparer.Ordinal);
    public static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
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
    private static string HardwareFilter(string category) => category switch
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
    private static string HardwareTopic(string category) => category is "camera" or "audio" or "bluetooth" or "printer" or "network" ? category : "hardware";

    // The schema sent to Ollama is advisory. Enforce it again at the executor boundary.
    internal static void ValidateArguments(string name, JsonElement args)
    {
        if (!toolSchemas.TryGetValue(name, out var schema) || name is "finish_support" or "ask_user") throw new InvalidOperationException("Ferramenta não permitida: " + name);
        if (args.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Os argumentos da ferramenta devem ser um objeto JSON.");
        var properties = schema.GetProperty("properties");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var argument in args.EnumerateObject())
        {
            if (!seen.Add(argument.Name) || !properties.TryGetProperty(argument.Name, out var rule)) throw new InvalidOperationException("Argumento inesperado ou repetido: " + argument.Name);
            ValidateArgument(argument.Name, argument.Value, rule);
        }
        foreach (var required in schema.GetProperty("required").EnumerateArray())
            if (!seen.Contains(required.GetString()!) && !(name == "set_dns" && required.GetString() == "servers" && args.TryGetProperty("mode", out var mode) && mode.ValueKind == JsonValueKind.String && mode.GetString() == "automatic"))
                throw new InvalidOperationException("Argumento obrigatório ausente: " + required.GetString());
    }

    private static void ValidateArgument(string name, JsonElement value, JsonElement rule)
    {
        switch (rule.GetProperty("type").GetString())
        {
            case "string":
                if (value.ValueKind != JsonValueKind.String) throw new InvalidOperationException("Argumento de texto inválido: " + name);
                var text = value.GetString()!;
                if (text.Length > 1024 || text.Any(char.IsControl)) throw new InvalidOperationException("Argumento de texto inválido: " + name);
                if (rule.TryGetProperty("enum", out var choices) && !choices.EnumerateArray().Any(choice => choice.GetString() == text)) throw new InvalidOperationException("Valor não permitido: " + name);
                break;
            case "integer":
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out _)) throw new InvalidOperationException("Número inteiro inválido: " + name);
                break;
            case "array":
                if (value.ValueKind != JsonValueKind.Array || rule.TryGetProperty("maxItems", out var max) && value.GetArrayLength() > max.GetInt32()) throw new InvalidOperationException("Lista inválida: " + name);
                foreach (var item in value.EnumerateArray()) ValidateArgument(name, item, rule.GetProperty("items"));
                break;
            default: throw new InvalidOperationException("Esquema de argumento não permitido: " + name);
        }
    }

    public async Task<string> Execute(string name, JsonElement args, CancellationToken token)
    {
        store.Require(actor, a => a.Diagnose);
        ValidateArguments(name, args);
        token.ThrowIfCancellationRequested();
        switch (name)
        {
            case "get_network":
                observedInterfaces.Clear(); observedAdapters.Clear(); configurableInterfaces.Clear(); physicalInterfaces.Clear();
                observedNetworkAt = null;
                var network = await SupportService.Powershell(NetworkQuery, token);
                using (var document = JsonDocument.Parse(network))
                {
                    foreach (var adapter in document.RootElement.GetProperty("Adapters").EnumerateArray())
                    {
                        var observed = new ObservedAdapter(adapter.GetProperty("Index").GetInt32(), adapter.GetProperty("Guid").GetString()!, adapter.GetProperty("Name").GetString()!, adapter.GetProperty("Status").GetString()!, adapter.GetProperty("Disabled").GetBoolean());
                        observedInterfaces[observed.Index] = observed.Name; observedAdapters[observed.Index] = observed;
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
                observedDevices.Clear();
                observedDevicesAt = null;
                if (topic == "system") return Ok((await support.Diagnose(actor, topic, progress, token)).Summary);
                var classes = topic switch { "camera" => "'Camera','Image'", "audio" => "'AudioEndpoint','MEDIA'", "bluetooth" => "'Bluetooth'", "printer" => "'Printer','PrintQueue'", _ => throw new InvalidOperationException("Categoria de dispositivo não permitida.") };
                var json = await SupportService.Powershell("ConvertTo-Json -Depth 5 -Compress -InputObject @(Get-CimInstance Win32_PnPEntity | Where-Object { $_.PNPClass -in " + classes + " } | ForEach-Object { [pscustomobject]@{ Name=$_.Name; Id=$_.PNPDeviceID; Code=[int]$_.ConfigManagerErrorCode; Status=$_.Status; HardwareIds=@($_.HardwareID); CompatibleIds=@($_.CompatibleID) } })", token);
                var devices = JsonSerializer.Deserialize<List<Device>>(json) ?? [];
                foreach (var device in devices.Where(d => !string.IsNullOrWhiteSpace(d.Id))) observedDevices[device.Id] = (topic, device);
                var extra = topic == "camera" ? await SupportService.Powershell("Get-ItemProperty -LiteralPath 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\CapabilityAccessManager\\ConsentStore\\webcam' -ErrorAction SilentlyContinue | Select-Object Value | ConvertTo-Json -Compress", token) : "";
                observedDevicesAt = DateTimeOffset.UtcNow;
                return Ok(new { count = devices.Count, devices, privacy = extra, note = devices.Count == 0 ? "Nenhum dispositivo desta categoria foi encontrado. Não existe dispositivo identificado para habilitar. Pode estar ausente, desconectado ou sem reconhecimento; não afirme que está desabilitado sem evidência." : "Code 22 = desabilitado; Code 0 = sem erro de PnP, não comprova funcionamento no aplicativo." });
            case "get_hardware_inventory":
                var category = Value(args, "category");
                var filter = HardwareFilter(category);
                observedDevices.Clear();
                observedDevicesAt = null;
                var inventoryScript = "$drivers=@{}; @(Get-CimInstance Win32_PnPSignedDriver -ErrorAction Stop) | ForEach-Object {if($_.DeviceID){$drivers[[string]$_.DeviceID]=$_}}; " +
                    "$items=@(Get-CimInstance Win32_PnPEntity -ErrorAction Stop | Where-Object {$_.Present -eq $true -and (" + filter + ")} | Sort-Object Name); " +
                    "$selected=@($items | Select-Object -First 80 | ForEach-Object {$d=$drivers[[string]$_.PNPDeviceID]; [pscustomobject]@{Name=[string]$_.Name;Id=[string]$_.PNPDeviceID;Code=[int]$_.ConfigManagerErrorCode;Status=[string]$_.Status;Class=[string]$_.PNPClass;Manufacturer=[string]$_.Manufacturer;HardwareIds=@($_.HardwareID | Where-Object {$_});CompatibleIds=@($_.CompatibleID | Where-Object {$_});DriverProvider=[string]$d.DriverProviderName;DriverVersion=[string]$d.DriverVersion}}); " +
                    "[pscustomobject]@{Category=" + Quote(category) + ";Total=$items.Count;Truncated=($items.Count -gt 80);Devices=$selected} | ConvertTo-Json -Depth 6 -Compress";
                using (var inventory = JsonDocument.Parse(await SupportService.Powershell(inventoryScript, token, 90)))
                {
                    var listed = inventory.RootElement.GetProperty("Devices");
                    var foundDevices = JsonSerializer.Deserialize<List<Device>>(listed.GetRawText()) ?? [];
                    foreach (var device in foundDevices.Where(d => !string.IsNullOrWhiteSpace(d.Id))) observedDevices[device.Id] = (HardwareTopic(category), device);
                    observedDevicesAt = DateTimeOffset.UtcNow;
                    return Ok(inventory.RootElement.Clone());
                }
            case "get_printers":
                observedPrinters.Clear();
                observedPrintersAt = null;
                var printers = await SupportService.Powershell("ConvertTo-Json -Depth 5 -Compress -InputObject @(Get-Printer | ForEach-Object { $p=$_; [pscustomobject]@{Name=$p.Name;Status=[string]$p.PrinterStatus;Driver=$p.DriverName;Port=$p.PortName;Jobs=@(Get-PrintJob -PrinterName $p.Name -ErrorAction SilentlyContinue | Select-Object ID,JobStatus)} })", token);
                using (var printerDoc = JsonDocument.Parse(printers)) foreach (var entry in printerDoc.RootElement.EnumerateArray())
                    if (entry.TryGetProperty("Name", out var printerName) && printerName.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(printerName.GetString())) observedPrinters.Add(printerName.GetString()!);
                observedPrintersAt = DateTimeOffset.UtcNow;
                return Ok(printers);
            case "get_services":
                return Ok(await SupportService.Powershell("ConvertTo-Json -Compress -InputObject @(Get-Service -Name Audiosrv,AudioEndpointBuilder,Spooler,bthserv,WlanSvc | Select-Object Name,@{N='Status';E={[string]$_.Status}},@{N='StartType';E={[string]$_.StartType}})", token));
            case "get_recent_errors":
                return Ok(await SupportService.Powershell("$log=& \"$env:WINDIR\\System32\\wevtutil.exe\" qe System '/q:*[System[(Level=2) and TimeCreated[timediff(@SystemTime) <= 86400000]]]' /c:12 /rd:true /f:text; if($LASTEXITCODE -ne 0){throw 'Não foi possível ler os eventos do sistema'}; $text=$log -join \"`n\"; if($text.Length -gt 7000){$text.Substring(0,7000)}else{$text}", token));
            case "get_processes":
                return Ok(await SupportService.Powershell("ConvertTo-Json -Compress -InputObject @(Get-Process | Sort-Object WorkingSet64 -Descending | Select-Object -First 12 Name,Id,@{N='MemoryMB';E={[math]::Round($_.WorkingSet64/1MB)}},Responding)", token));
            case "set_dns":
            case "set_ipv4":
                var index = Number(args, "interface_index");
                if (!Fresh(observedNetworkAt) || !observedInterfaces.ContainsKey(index)) throw new InvalidOperationException("Consulte get_network nesta etapa antes de configurar a interface.");
                if (!configurableInterfaces.GetValueOrDefault(index)) throw new InvalidOperationException("Esta interface é gerenciada pelo Windows ou por VPN; reconfiguração de IP/DNS não permitida.");
                var request = NetworkChange.Parse(name, args) with { ExpectedGuid = observedAdapters[index].Guid };
                if (!await Approve(new ChangeRequest("Alterar configuração de rede", observedInterfaces[index] + "\n" + request.Description + "\nA conexão pode ser interrompida. O estado anterior será salvo e restaurado se você não confirmar a nova configuração."), token)) return Denied();
                var change = await NetworkChange.Apply(request, store, next => Approve(next, token), progress, token);
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
                if (!await Approve(new ChangeRequest("Ajustar dispositivo", (name == "restart_device" ? "Reiniciar " : "Habilitar ") + known.Device.Name + ".\nO dispositivo poderá ficar temporariamente indisponível."), token)) return Denied();
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
                if (!await Approve(new ChangeRequest("Reiniciar serviço", "Reiniciar " + service + ". Áudio, impressão, Bluetooth ou Wi-Fi poderão ser interrompidos conforme o serviço escolhido."), token)) return Denied();
                return await Elevated("$s=Get-Service -Name " + Quote(service) + "; if($s.StartType -eq 'Disabled'){throw 'Serviço desabilitado. Verifique a política da organização antes de mudar o modo de inicialização'}; Restart-Service -Name " + Quote(service) + " -ErrorAction Stop; $s=Get-Service -Name " + Quote(service) + "; $s.WaitForStatus('Running',[timespan]::FromSeconds(20)); [pscustomobject]@{Service=$s.Name;Status=[string]$s.Status}", token);
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
                if (!await Approve(new ChangeRequest("Instalar driver pela fonte do Windows", "Baixar e instalar: " + selectedUpdate.Title + " (" + updateId + ", revisão " + selectedUpdate.Revision + ") para “" + driverTarget.Name + "”. Origem: fonte de atualizações configurada no Windows. Pode interromper o dispositivo e exigir reinicialização. A instalação inclui aceitar os termos do fornecedor. O Nexo não reiniciará o PC automaticamente."), token)) return Denied();
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
    internal static string VerifyDriverInstallationResult(string output)
    {
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        if (!root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("Verification", out var verification)
            || verification.ValueKind != JsonValueKind.String) return output;
        if (verification.GetString() != "unconfirmed") return output;
        return JsonSerializer.Serialize(new
        {
            success = false,
            installationReported = true,
            error = "O Windows relatou a instalação, mas ainda não foi possível confirmar a nova versão do driver no dispositivo. Confira o Gerenciador de Dispositivos antes de considerar a correção concluída.",
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
            token.ThrowIfCancellationRequested();
            store.Require(actor, a => a.Repair);
            store.Log(actor, (result ? "Autorizado: " : "Recusado: ") + request.Title);
            return result;
        }
        catch (OperationCanceledException)
        {
            store.Log(actor, "Cancelado: " + request.Title);
            throw;
        }
    }
    private static string Denied() => JsonSerializer.Serialize(new { success = false, denied = true, message = "Ação recusada pelo usuário. Não executar nem repetir." });
    public static string Ok(object data) => JsonSerializer.Serialize(new { success = true, data });
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
    private async Task<string> Elevated(string script, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var dir = Path.Combine(store.Root, "operations", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "result.json");
        var wrapped = "$ErrorActionPreference='Stop'; try { $data=& { " + script + " }; $result=@{success=$true;data=$data} } catch { $result=@{success=$false;error=$_.Exception.Message} }; $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath " + Quote(output) + " -Encoding UTF8";
        try
        {
            token.ThrowIfCancellationRequested();
            using var process = StartElevated(wrapped);
            // Do not kill an in-flight device/driver operation on UI cancellation; report its actual final state.
            await process.WaitForExitAsync();
            if (!File.Exists(output)) return JsonSerializer.Serialize(new { success = false, error = "O processo elevado não retornou resultado. Estado não confirmado." });
            return await File.ReadAllTextAsync(output, CancellationToken.None);
        }
        finally
        {
            try { if (File.Exists(output)) File.Delete(output); Directory.Delete(dir); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    internal static Process StartElevated(string script)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("O Windows não iniciou a operação elevada.");
    }
}
