using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Nexo.Support;

namespace Nexo.Checks;

internal static class HardwareSensorChecks
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 15, 0, 0, TimeSpan.Zero);

    private const string Fakes = """
        function Get-CimInstance {
            param($Namespace, $ClassName, $Filter, $ErrorAction)
            if ($Namespace -ne 'root/LibreHardwareMonitor' -or $ClassName -ne 'Sensor' -or $Filter -ne "SensorType='Temperature'") { throw 'consulta inesperada' }
            switch ($temperatureScenario) {
                'no-source' { throw 'HRESULT 0x8004100e,Microsoft.Management.Infrastructure.CimCmdlets.GetCimInstanceCommand Namespace inválido' }
                'error' { throw 'Acesso negado ao provedor WMI' }
                'intel' {
                    [pscustomobject]@{ Name='GPU Core'; Identifier='/gpu-nvidia/0/temperature/0'; Value=[single]88 }
                    [pscustomobject]@{ Name='CPU Core #1'; Identifier='/intelcpu/0/temperature/1'; Value=[single]61 }
                    [pscustomobject]@{ Name='CPU Package'; Identifier='/intelcpu/0/temperature/0'; Value=[single]55.5 }
                }
                'amd' {
                    [pscustomobject]@{ Name='CCD1 (Tdie)'; Identifier='/amdcpu/0/temperature/3'; Value=[single]58 }
                    [pscustomobject]@{ Name='Core (Tctl/Tdie)'; Identifier='/amdcpu/0/temperature/2'; Value=[single]63.2 }
                }
                'invalid' { [pscustomobject]@{ Name='CPU Package'; Identifier='/intelcpu/0/temperature/0'; Value=[single]0 } }
                'gpu-only' { [pscustomobject]@{ Name='GPU Core'; Identifier='/gpu-nvidia/0/temperature/0'; Value=[single]70 } }
                'many-cores' {
                    foreach ($i in 0..39) { [pscustomobject]@{ Name='Core #'+$i; Identifier='/amdcpu/0/temperature/'+$i; Value=[single]60 } }
                    [pscustomobject]@{ Name='Core (Tctl/Tdie)'; Identifier='/amdcpu/0/temperature/40'; Value=[single]64.5 }
                }
            }
        }
        function Get-PhysicalDisk {
            param($ErrorAction)
            function Disk($id, $health, $operational, $media='SSD') { [pscustomobject]@{ DeviceId=[string]$id; FriendlyName='Disco '+$id; MediaType=$media; BusType='NVMe'; HealthStatus=$health; OperationalStatus=$operational } }
            switch ($storageScenario) {
                'error' { throw 'Acesso negado ao Windows Storage' }
                'empty' { }
                'mixed' { Disk 0 'Healthy' 'OK'; Disk 1 'Warning' @('Predictive Failure','OK') 'HDD'; Disk 2 'Healthy' 'OK' }
                'numeric' { Disk 0 0 @(2); Disk 1 2 @(6) }
                'healthy-many' { foreach ($i in 0..19) { Disk $i 'Healthy' 'OK' } }
                'alert-beyond-limit' { foreach ($i in 0..19) { Disk $i 'Healthy' 'OK' }; Disk 20 'Unhealthy' 'Lost Communication' }
                'healthy' { Disk 0 'Healthy' 'OK'; Disk 1 'Healthy' 'OK' }
                'unknown-status' { Disk 0 'Healthy' 'Starting' }
                'hang' { Start-Sleep -Seconds 60 }
            }
        }
        """;

    public static void Run(Action<bool, string> check)
    {
        Parsing(check);
        Presentation(check);
        Independent(check);
        if (Shell() != null) ScriptRuns(check);
        if (Environment.GetEnvironmentVariable("NEXO_LIVE_SAMPLE") == "1") Live(check);
    }

    private static void Parsing(Action<bool, string> check)
    {
        var intel = Parse("""{"Time":"2026-11-08T15:00:00.0000000Z","Temperature":{"Available":true,"Error":"","Sensors":[{"Name":"CPU Core #1","Identifier":"/intelcpu/0/temperature/1","Value":61},{"Name":"CPU Package","Identifier":"/intelcpu/0/temperature/0","Value":55.54}]},"Storage":{"Error":"","Total":1,"Disks":[{"Device":"0","Name":"SSD","MediaType":"SSD","BusType":"NVMe","Health":"Healthy","Operational":"OK"}]}}""");
        check(intel.CpuTemperatureC == 55.5 && intel.CpuTemperature is { State: "read", Source: HardwareSensors.TemperatureSource } reading && reading.Sensor.StartsWith("CPU Package") && reading.Time == Now,
            "Sensores: temperatura vem do sensor de pacote com origem e horário, não do núcleo mais quente");
        check(intel.StorageHealth == "healthy" && intel.Storage!.Disks.Single().OperationalStatus.SequenceEqual(["OK"]) && intel.Warnings.Count == 0,
            "Sensores: disco com HealthStatus Healthy e OperationalStatus OK aparece sem alerta do Windows");

        var absent = Parse("""{"Temperature":{"Available":false,"Error":"HRESULT 0x8004100e,Microsoft.Management.Infrastructure.CimCmdlets.GetCimInstanceCommand Namespace inválido","Sensors":[]},"Storage":{"Error":"","Total":0,"Disks":[]}}""");
        check(absent.CpuTemperatureC == null && absent.CpuTemperature!.State == "no_source" && absent.CpuTemperature.Time == Now && absent.StorageHealth == "unavailable",
            "Sensores: sem Libre Hardware Monitor a temperatura fica indisponível, com o horário da tentativa");
        check(!absent.Warnings.Any(w => w.StartsWith("Temperatura")), "Sensores: ausência da fonte de temperatura não torna a coleta parcial");

        var invalid = Parse("""{"Temperature":{"Available":true,"Sensors":[{"Name":"CPU Package","Identifier":"/intelcpu/0/temperature/0","Value":0},{"Name":"Package","Identifier":"/amdcpu/0/temperature/0","Value":180}]},"Storage":{"Total":0}}""");
        check(invalid.CpuTemperatureC == null && invalid.CpuTemperature!.State == "invalid" && invalid.Warnings.Any(w => w.Contains("descartada")),
            "Sensores: valor zero ou acima de 125 °C é descartado e registrado como limitação");

        var noPackage = Parse("""{"Temperature":{"Available":true,"Sensors":{"Name":"CPU Core #1","Identifier":"/intelcpu/0/temperature/1","Value":70}},"Storage":{"Total":0}}""");
        check(noPackage.CpuTemperatureC == null && noPackage.CpuTemperature!.State == "no_sensor",
            "Sensores: só núcleos individuais não viram temperatura da CPU por suposição");

        var failed = Parse("""{"Temperature":{"Available":false,"Error":"Acesso negado"},"Storage":{"Error":"Acesso negado ao Windows Storage","Total":0,"Disks":[]}}""");
        check(failed.CpuTemperature!.State == "error" && failed.StorageHealth == "unavailable" && failed.Warnings.Count == 2 && failed.Warnings.Any(w => w.Contains("Acesso negado ao Windows Storage")),
            "Sensores: falha das fontes vira estado indisponível com o erro do Windows");

        var truncated = Parse("{\"Storage\":{\"Total\":30,\"Disks\":[" + string.Join(",", Enumerable.Range(0, 20).Select(i => $$"""{"Device":"{{i}}","Name":"D{{i}}","Health":"Healthy","Operational":["OK"]}""")) + "]}}");
        check(truncated.StorageHealth == "unknown" && truncated.Storage!.Disks.Count == HardwareSensors.MaxPhysicalDisks && truncated.Storage.Total == 30 && truncated.Warnings.Any(w => w.Contains("16 de 30")),
            "Sensores: lista de discos cortada não vira saudável por suposição");

        var hidden = Parse("{\"Storage\":{\"Total\":21,\"Disks\":[" + string.Join(",", Enumerable.Range(0, 20).Select(i => $$"""{"Device":"{{i:D2}}","Name":"D{{i}}","Health":"Healthy","Operational":"OK"}""")) + """,{"Device":"99","Name":"Falho","Health":"Unhealthy","Operational":"Lost Communication"}]}}""");
        check(hidden.StorageHealth == "unhealthy" && hidden.Storage!.Disks[0].Device == "99",
            "Sensores: disco com falha aparece primeiro e não fica escondido pelo limite da lista");

        var numeric = Parse("""{"Storage":{"Total":3,"Disks":[{"Device":"0","Health":0,"Operational":[2]},{"Device":"1","Health":"1","Operational":[5]},{"Device":"2","Health":"Healthy","Operational":"Predictive Failure"}]}}""");
        check(numeric.StorageHealth == "warning" && numeric.Storage!.Disks.Select(d => d.State).SequenceEqual(["warning", "warning", "healthy"])
            && numeric.Storage.Disks[0].OperationalStatus.SequenceEqual(["Predictive Failure"]) && numeric.Storage.Disks[2].Health == "Healthy",
            "Sensores: códigos numéricos do Windows são traduzidos e falha prevista conta como atenção mesmo com HealthStatus Healthy");

        var unknownStatus = Parse("""{"Storage":{"Total":1,"Disks":[{"Device":"0","Health":"Healthy","Operational":"Starting"}]}}""");
        check(unknownStatus.StorageHealth == "unknown", "Sensores: estado operacional fora de OK deixa o resumo não confirmado");

        var unavailable = new HealthSnapshot();
        HardwareSensors.Unavailable(unavailable, Now, "O Windows demorou mais que o esperado.");
        check(unavailable.CpuTemperatureC == null && unavailable.StorageHealth == "unavailable" && unavailable.Warnings.Count == 1 && unavailable.Storage!.Time == Now,
            "Sensores: consulta que falha por inteiro registra as duas fontes como indisponíveis");

        var json = JsonSerializer.Serialize(intel);
        var restored = JsonSerializer.Deserialize<HealthSnapshot>(json)!;
        var legacy = JsonSerializer.Deserialize<HealthSnapshot>("""{"CpuTemperatureC":null,"StorageHealth":"unavailable","CollectionStatus":"complete"}""")!;
        check(restored.CpuTemperature == intel.CpuTemperature && restored.Storage!.Disks.Single().Name == "SSD" && legacy.CpuTemperature == null && legacy.Storage == null,
            "Sensores: leituras sobrevivem à gravação e amostras antigas continuam legíveis");
    }

    private static void Presentation(Action<bool, string> check)
    {
        var read = Parse("""{"Time":"2026-11-08T15:00:00Z","Temperature":{"Available":true,"Sensors":[{"Name":"CPU Package","Identifier":"/intelcpu/0/temperature/0","Value":55}]},"Storage":{"Total":2,"Disks":[{"Device":"0","Name":"NVMe","MediaType":"SSD","BusType":"NVMe","Health":"Healthy","Operational":"OK"},{"Device":"1","Name":"HDD","MediaType":"HDD","BusType":"SATA","Health":"Warning","Operational":["Predictive Failure"]}]}}""");
        var temperature = HealthReportPresentation.DescribeTemperature(read, now: Now);
        var storage = HealthReportPresentation.DescribeStorage(read, now: Now);
        check(temperature.StartsWith("55 °C, sensor CPU Package") && temperature.Contains("Libre Hardware Monitor") && temperature.Contains("lida às"),
            "Sensores: painel mostra valor, sensor, origem e horário da temperatura");
        check(storage.StartsWith("atenção informada pelo Windows, 2 de 2 discos físicos") && storage.Contains("Get-PhysicalDisk") && storage.Contains("lido às")
            && HealthReportPresentation.DescribeDisk(read.Storage!.Disks[0]).StartsWith("Disco 1 HDD (HDD, SATA): atenção informada pelo Windows; HealthStatus Warning, OperationalStatus Predictive Failure"),
            "Sensores: painel mostra resumo, origem, horário e cada disco com o estado informado pelo Windows");
        check(!storage.Contains("SMART") && HealthReportPresentation.StorageScope.Contains("não é uma leitura SMART completa"),
            "Sensores: estado do Windows não é chamado de SMART completo");
        var day = Now.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var later = Now.AddDays(2);
        check(HealthReportPresentation.DescribeTemperature(read, now: later).Contains(", lida em " + day + " às ") && HealthReportPresentation.DescribeStorage(read, now: later).Contains(", lido em " + day + " às ")
            && !temperature.Contains(" em " + day) && !storage.Contains(" em " + day),
            "Sensores: leitura de outro dia mostra a data junto do horário; a de hoje, só o horário");
        check(HealthReportPresentation.DescribeDisk(read.Storage!.Disks[0], true).StartsWith("Disco 1 “HDD” (HDD, SATA)") && HealthReportPresentation.DescribeDisk(read.Storage.Disks[0]).StartsWith("Disco 1 HDD ("),
            "Sensores: no relatório ao modelo o nome do disco vai entre aspas; no painel, sem aspas");

        var absent = Parse("""{"Temperature":{"Available":false,"Error":"HRESULT 0x8004100e"},"Storage":{"Total":0,"Disks":[]}}""");
        check(HealthReportPresentation.DescribeTemperature(absent).StartsWith("indisponível, Libre Hardware Monitor não está em execução") && !HealthReportPresentation.DescribeTemperature(absent).Contains("°C")
            && HealthReportPresentation.DescribeStorage(absent).StartsWith("indisponível, O Windows não listou discos físicos"),
            "Sensores: indisponibilidade é exibida com o motivo, sem valor presumido");
        var legacy = new HealthSnapshot();
        check(HealthReportPresentation.DescribeTemperature(legacy) == "indisponível, leitura anterior à coleta de sensores" && HealthReportPresentation.DescribeStorage(legacy) == "indisponível, leitura anterior à coleta do estado dos discos",
            "Sensores: amostra anterior à coleta não inventa temperatura nem estado de disco");

        var report = DiagnosticEngine.BuildReport(absent, [], new EventBatch(), Now);
        var withDisks = DiagnosticEngine.BuildReport(read, [], new EventBatch(), Now);
        check(report.Limitations.Any(l => l.StartsWith("Sensor de temperatura da CPU indisponível. Libre Hardware Monitor")) && report.Limitations.Any(l => l.StartsWith("Estado dos discos físicos indisponível."))
            && !report.Limitations.Any(l => l.Contains("SMART do armazenamento")) && withDisks.Limitations.Contains(HealthReportPresentation.StorageScope) && !withDisks.Limitations.Any(l => l.StartsWith("Sensor de temperatura")),
            "Sensores: relatório de diagnóstico explica a indisponibilidade e o limite da leitura do Windows");

        var root = Path.Combine(Path.GetTempPath(), "NexoSensors-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new OperationalStore(root);
            read.Time = DateTimeOffset.UtcNow;
            store.SaveSnapshot(read);
            var text = HealthReportPresentation.FromStore(store);
            check(text.Contains("Temperatura da CPU: 55 °C, sensor CPU Package") && text.Contains("Discos físicos: atenção informada pelo Windows") && text.Contains("Disco 1 “HDD”")
                && text.Contains("não é uma leitura SMART completa"),
                "Sensores: relatório de saúde para o assistente traz temperatura, discos e o limite da fonte");
            var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            var scope = lines.IndexOf(HealthReportPresentation.PhysicalDiskScope);
            check(scope > 0 && scope == lines.FindLastIndex(l => l.StartsWith("Disco ")) + 1 && HealthReportPresentation.PhysicalDiskScope.Contains("não estão ligados às letras de unidade"),
                "Sensores: relatório avisa, logo depois dos discos físicos, que eles não estão ligados às letras de unidade (#93)");
            var noDisks = new HealthSnapshot { Time = DateTimeOffset.UtcNow.AddSeconds(1), Storage = new StorageReading("unavailable", HardwareSensors.StorageSource, DateTimeOffset.UtcNow, 0, [], "O Windows não listou discos físicos."), StorageHealth = "unavailable" };
            store.SaveSnapshot(noDisks);
            check(!HealthReportPresentation.FromStore(store).Contains(HealthReportPresentation.PhysicalDiskScope),
                "Sensores: sem disco físico listado, o aviso das letras de unidade não aparece");
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private const string TemperatureJson = """{"Time":"2026-11-08T15:00:00Z","Temperature":{"Available":true,"Sensors":[{"Name":"CPU Package","Identifier":"/intelcpu/0/temperature/0","Value":55}]}}""";
    private const string StorageJson = """{"Time":"2026-11-08T15:00:00Z","Storage":{"Total":1,"Disks":[{"Device":"0","Name":"NVMe","Health":"Healthy","Operational":"OK"}]}}""";

    // Temperatura e discos com prazo e falha próprios (#19): no PC há AMD-RAID, e um Get-PhysicalDisk travado não pode
    // apagar a temperatura. Roda sem PowerShell, com executores falsos.
    private static void Independent(Action<bool, string> check)
    {
        var timeouts = new Dictionary<string, int>();
        HealthSnapshot Collect(Func<string, Task<string>> temperature, Func<string, Task<string>> storage, CancellationToken token = default)
        {
            var snapshot = new HealthSnapshot { Time = Now };
            var task = HardwareSensors.Collect(snapshot, (script, timeout, _) =>
            {
                var isTemperature = script == HardwareSensors.TemperatureScript;
                timeouts[isTemperature ? "temperature" : "storage"] = timeout;
                return isTemperature ? temperature(script) : storage(script);
            }, token, () => Now);
            if (!task.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Collect não terminou: as consultas não foram iniciadas juntas.");
            return snapshot;
        }

        var stuckDisks = Collect(_ => Task.FromResult(TemperatureJson), _ => Task.FromException<string>(new TimeoutException("O Windows demorou mais que o esperado. Tente novamente.")));
        check(stuckDisks.CpuTemperatureC == 55 && stuckDisks.CpuTemperature!.State == "read" && stuckDisks.StorageHealth == "unavailable"
            && stuckDisks.Storage!.Detail == $"O Windows não informou os discos físicos em {HardwareSensors.StorageTimeoutSeconds} s." && stuckDisks.Storage.Time == Now
            && stuckDisks.Warnings.SequenceEqual(["Estado dos discos: " + stuckDisks.Storage.Detail]),
            "Sensores independentes: disco travado fica indisponível pelo prazo e a temperatura lida continua no registro");
        check(timeouts["temperature"] == HardwareSensors.TemperatureTimeoutSeconds && timeouts["storage"] == HardwareSensors.StorageTimeoutSeconds
            && HardwareSensors.TemperatureTimeoutSeconds + HardwareSensors.StorageTimeoutSeconds > 0 && HardwareSensors.StorageTimeoutSeconds <= 20,
            "Sensores independentes: cada consulta recebe o próprio prazo");

        var brokenWmi = Collect(_ => Task.FromException<string>(new InvalidOperationException("O Windows não concluiu a operação. WMI travou")), _ => Task.FromResult(StorageJson));
        check(brokenWmi.CpuTemperature!.State == "error" && brokenWmi.CpuTemperature.Detail.Contains("WMI travou") && brokenWmi.CpuTemperatureC == null
            && brokenWmi.StorageHealth == "healthy" && brokenWmi.Warnings.Count == 1,
            "Sensores independentes: falha da temperatura não apaga o estado dos discos");

        var stuckTemperature = Collect(_ => Task.FromException<string>(new TimeoutException("x")), _ => Task.FromResult("não é json"));
        check(stuckTemperature.CpuTemperature!.Detail == $"O Libre Hardware Monitor não respondeu em {HardwareSensors.TemperatureTimeoutSeconds} s." && stuckTemperature.StorageHealth == "unavailable"
            && stuckTemperature.Storage!.Detail.StartsWith("A consulta dos discos físicos falhou:") && stuckTemperature.Warnings.Count == 2,
            "Sensores independentes: prazo da temperatura e resposta inválida dos discos são registrados cada um com o seu motivo");

        // As duas começam antes de qualquer uma terminar: a temperatura só responde depois que a consulta dos discos começou.
        var storageStarted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var parallel = Collect(async _ => { await storageStarted.Task; return TemperatureJson; }, _ => { storageStarted.TrySetResult(""); return Task.FromResult(StorageJson); });
        check(parallel.CpuTemperatureC == 55 && parallel.StorageHealth == "healthy",
            "Sensores independentes: temperatura e discos são consultados ao mesmo tempo, não um depois do outro");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var propagated = false;
        try { Collect(_ => Task.FromCanceled<string>(cancelled.Token), _ => Task.FromResult(StorageJson), cancelled.Token); }
        catch (AggregateException ex) when (ex.InnerException is OperationCanceledException) { propagated = true; }
        check(propagated, "Sensores independentes: cancelamento pedido continua interrompendo a coleta, sem virar leitura indisponível");
    }

    private static void ScriptRuns(Action<bool, string> check)
    {
        var intel = Run("intel", "mixed");
        check(intel.CpuTemperatureC == 55.5 && intel.CpuTemperature!.Sensor == "CPU Package (/intelcpu/0/temperature/0)" && intel.StorageHealth == "warning"
            && intel.Storage!.Disks.Select(d => d.Device).SequenceEqual(["1", "0", "2"]) && intel.Storage.Disks[0].OperationalStatus.SequenceEqual(["Predictive Failure", "OK"]),
            "Script de sensores executado: filtra sensores fora da CPU, escolhe o pacote e põe o disco em alerta primeiro");
        var amd = Run("amd", "healthy-many");
        check(amd.CpuTemperatureC == 63.2 && amd.CpuTemperature!.Sensor.StartsWith("Core (Tctl/Tdie)") && amd.StorageHealth == "unknown" && amd.Storage!.Total == 20 && amd.Storage.Disks.Count == 16,
            "Script de sensores executado: AMD usa Tctl/Tdie e a lista cortada fica não confirmada");
        var absent = Run("no-source", "alert-beyond-limit");
        check(absent.CpuTemperature!.State == "no_source" && absent.StorageHealth == "unhealthy" && absent.Storage!.Disks[0].Device == "20" && absent.Storage.Total == 21,
            "Script de sensores executado: sem Libre Hardware Monitor fica indisponível e o disco com falha além do limite é mantido");
        var invalid = Run("invalid", "error");
        check(invalid.CpuTemperature!.State == "invalid" && invalid.StorageHealth == "unavailable" && invalid.Storage!.Detail.Contains("Acesso negado ao Windows Storage"),
            "Script de sensores executado: valor inválido é descartado e erro do Windows Storage vira indisponível");
        var failed = Run("error", "numeric");
        check(failed.CpuTemperature!.State == "error" && failed.CpuTemperature.Detail.Contains("Acesso negado ao provedor WMI") && failed.StorageHealth == "unhealthy"
            && failed.Storage!.Disks.Select(d => (d.Device, d.Health)).SequenceEqual([("1", "Unhealthy"), ("0", "Healthy")]),
            "Script de sensores executado: erro do WMI é registrado e códigos numéricos de disco são ordenados e traduzidos");
        var gpuOnly = Run("gpu-only", "empty");
        check(gpuOnly.CpuTemperature!.State == "no_sensor" && gpuOnly.StorageHealth == "unavailable",
            "Script de sensores executado: sensor só de GPU não vira temperatura da CPU e lista vazia fica indisponível");
        var manyCores = Run("many-cores", "healthy");
        check(manyCores.CpuTemperatureC == 64.5 && manyCores.CpuTemperature!.Sensor == "Core (Tctl/Tdie) (/amdcpu/0/temperature/40)",
            "Script de sensores executado: com mais de 32 sensores de núcleo, o de pacote ou die continua lido (#19)");
        // Get-PhysicalDisk preso de verdade: o processo dos discos é encerrado pelo prazo (aqui encurtado) e a temperatura fica.
        var watch = Stopwatch.StartNew();
        var hung = Run("intel", "hang", 4);
        check(hung.CpuTemperatureC == 55.5 && hung.StorageHealth == "unavailable" && hung.Storage!.Detail.Contains($"em {HardwareSensors.StorageTimeoutSeconds} s") && watch.Elapsed < TimeSpan.FromSeconds(30),
            "Script de sensores executado: Get-PhysicalDisk travado é encerrado pelo prazo sem apagar a temperatura");
    }

    private static void Live(Action<bool, string> check)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var live = new HealthSnapshot();
        var watch = Stopwatch.StartNew();
        HardwareSensors.Collect(live, (script, seconds, token) => SupportService.Powershell(script, token, seconds), timeout.Token).GetAwaiter().GetResult();
        Console.WriteLine($"SENSORES: coleta em {watch.ElapsedMilliseconds} ms");
        check(live.CpuTemperature != null && live.Storage != null, "Consulta real de sensores retorna leitura ou indisponibilidade explícita");
        Console.WriteLine("SENSORES: temperatura " + HealthReportPresentation.DescribeTemperature(live) + "; discos " + HealthReportPresentation.DescribeStorage(live)
            + string.Concat(live.Storage!.Disks.Select(d => "; " + HealthReportPresentation.DescribeDisk(d))));
    }

    // No Windows usa powershell.exe, como no CI; fora dele, só com NEXO_CHECKS_PWSH.
    private static string? Shell() => OperatingSystem.IsWindows() ? "powershell.exe" : Environment.GetEnvironmentVariable("NEXO_CHECKS_PWSH") is { Length: > 0 } pwsh ? pwsh : null;

    // Executa os dois scripts reais pela coleta de verdade, com os cmdlets falsos na frente. Sem prazo informado, cada
    // processo tem 60 s, folga para o início a frio do PowerShell no CI; com prazo, simula o do Nexo de forma mais curta.
    private static HealthSnapshot Run(string temperatureScenario, string storageScenario, int? seconds = null)
    {
        var snapshot = new HealthSnapshot();
        HardwareSensors.Collect(snapshot, (script, _, _) =>
            Execute($"$temperatureScenario='{temperatureScenario}'; $storageScenario='{storageScenario}'\n" + Fakes + "\n" + script, seconds ?? 60),
            CancellationToken.None, () => Now).GetAwaiter().GetResult();
        return snapshot;
    }

    private static async Task<string> Execute(string script, int seconds)
    {
        var start = new ProcessStartInfo(Shell()!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(SupportService.PowershellCommand(script))) })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch (InvalidOperationException) { } throw new TimeoutException("O Windows demorou mais que o esperado. Tente novamente."); }
        if (process.ExitCode != 0) throw new InvalidOperationException("O Windows não concluiu a operação. " + (await stderr).Trim());
        return await stdout;
    }

    private static HealthSnapshot Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var snapshot = new HealthSnapshot { Time = Now };
        HardwareSensors.Read(document.RootElement, snapshot, Now);
        return snapshot;
    }
}
