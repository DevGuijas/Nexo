using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nexo.Support;

namespace Nexo.Checks;

// Issue #57: serviços automáticos que o Windows para de propósito não são falha.
// A linha de base vem do cenário 4 da #8 (08/11/2026, Windows 10 Pro 19045): edgeupdate, MapsBroker e sppsvc
// estavam Auto, parados e com DelayedAutoStart. O gatilho de início dos GoogleUpdater e do gpsvc ainda não foi
// confirmado no Windows real; por isso o caso de gatilho usa um serviço fictício.
internal static class ServiceStopChecks
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    public static void Run(Action<bool, string> check)
    {
        Rule(check);
        Compatibility(check);
        Occurrence(check);
        Collection(check);
        ScriptRun(check);
    }

    private static HealthSnapshot At(int secondsAgo, params ServiceHealth[] services)
        => new() { Time = Now.AddSeconds(-secondsAgo), CollectionStatus = "complete", Services = [.. services] };

    private static List<string> Stopped(params HealthSnapshot[] samples)
        => HealthRules.Evaluate(samples, new HealthThresholds()).Where(f => f.Category == "service").Select(f => f.Id).ToList();

    private static List<string> Twice(params ServiceHealth[] services) => Stopped(At(30, services), At(0, services));

    private static ServiceHealth Delayed(string name, long exitCode) => new(name, "Stopped", "Automatic", DelayedAutoStart: true, TriggerStart: false, ExitCode: exitCode);
    private static ServiceHealth Trigger(string name, long exitCode) => new(name, "Stopped", "Automatic", DelayedAutoStart: false, TriggerStart: true, ExitCode: exitCode);
    private static ServiceHealth Plain(string name, long exitCode = 0) => new(name, "Stopped", "Automatic", DelayedAutoStart: false, TriggerStart: false, ExitCode: exitCode);

    private static void Rule(Action<bool, string> check)
    {
        check(Twice(Delayed("edgeupdate", 0), Delayed("MapsBroker", 0), Delayed("sppsvc", 0)).Count == 0,
            "Linha de base do cenário 4: serviços de início atrasado parados com código 0 não geram alerta");
        check(Twice(Trigger("ServicoPorGatilho", 0)).Count == 0,
            "Serviço de início por gatilho parado com código 0 não gera alerta");
        check(Twice(Plain("Spooler")).SequenceEqual(["service-stopped:Spooler"]),
            "Spooler (Auto comum) parado com código 0 continua gerando alerta em 2 leituras");
        check(Stopped(At(0, Plain("Spooler"))).Count == 0,
            "Spooler parado em uma só leitura ainda não gera alerta");
        check(Twice(Delayed("sppsvc", 1067)).SequenceEqual(["service-stopped:sppsvc"]),
            "Serviço de início atrasado parado com código 1067 gera alerta");
        check(Twice(Trigger("ServicoPorGatilho", 1066)).SequenceEqual(["service-stopped:ServicoPorGatilho"]),
            "Serviço por gatilho parado com código diferente de 0 gera alerta");
        check(Twice(new ServiceHealth("sppsvc", "Stopped", "Automatic", DelayedAutoStart: true)).SequenceEqual(["service-stopped:sppsvc"]),
            "Início atrasado sem código de saída lido não isenta o serviço");
        check(Twice(new ServiceHealth("ServicoPorGatilho", "Stopped", "Automatic", DelayedAutoStart: false, TriggerStart: null, ExitCode: 0))
                .SequenceEqual(["service-stopped:ServicoPorGatilho"]),
            "Gatilho não lido (nulo) com código 0 não isenta o serviço");
        check(Twice(new ServiceHealth("Spooler", "Stopped", "Automatic", DelayedAutoStart: null, TriggerStart: false, ExitCode: 0)).SequenceEqual(["service-stopped:Spooler"]),
            "Início atrasado não lido (nulo), sem gatilho e com código 0 não isenta o serviço");
        check(Twice(new ServiceHealth("Spooler", "Stopped", "Automatic", DelayedAutoStart: null, TriggerStart: null, ExitCode: 0)).SequenceEqual(["service-stopped:Spooler"]),
            "Início atrasado e gatilho não lidos com código 0 não isentam o serviço");
        check(Twice(Plain("Spooler", 1077), Delayed("sppsvc", 1077)).Order(StringComparer.Ordinal).SequenceEqual(["service-stopped:Spooler", "service-stopped:sppsvc"]),
            "Código 1077 (serviço que nunca iniciou) gera alerta, inclusive em serviço de início atrasado");
        check(Twice(Plain("Spooler") with { Status = "Paused" }, Plain("Fax") with { Status = "Stop Pending" }, Delayed("sppsvc", 1067) with { Status = "Start Pending" }).Count == 0,
            "Serviço pausado ou em transição não é tratado como parado pela regra");
        check(Twice(Delayed("sppsvc", 0) with { Status = "Running" }, Plain("Spooler") with { StartType = "Manual" }).Count == 0,
            "Isenção nova não muda serviço em execução nem serviço manual");
        check(Stopped(At(30, Delayed("sppsvc", 1067)), At(0, Delayed("sppsvc", 0))).Count == 0,
            "Leitura mais recente com parada normal interrompe a sequência do alerta");
        check(Stopped(At(30, Delayed("sppsvc", 0)), At(0, Delayed("sppsvc", 1067))).Count == 0,
            "Parada normal na leitura anterior não conta para o alerta; código 1067 em uma só leitura ainda não alerta");
        check(Stopped(At(60, Delayed("sppsvc", 0)), At(30, Delayed("sppsvc", 1067)), At(0, Delayed("sppsvc", 1067))).SequenceEqual(["service-stopped:sppsvc"]),
            "Código 1067 em 2 leituras seguidas depois de uma parada normal gera alerta");
    }

    private static void Compatibility(Action<bool, string> check)
    {
        // Formato gravado pela coleta anterior à #57 (System.Text.Json com as opções padrão do OperationalStore).
        const string legacy = """{"Id":"a","Time":"2026-11-08T00:22:37+00:00","CollectionStatus":"complete","Services":[{"Name":"sppsvc","Status":"Stopped","StartType":"Automatic"},{"Name":"Spooler","Status":"Running","StartType":"Automatic"}]}""";
        var old = JsonSerializer.Deserialize<HealthSnapshot>(legacy)!;
        var service = old.Services[0];
        check(service.DelayedAutoStart == null && service.TriggerStart == null && service.ExitCode == null,
            "Leitura antiga sem os campos novos é lida com os campos nulos");
        var previous = JsonSerializer.Deserialize<HealthSnapshot>(legacy)!;
        previous.Time = Now.AddSeconds(-30); old.Time = Now;
        check(Stopped(previous, old).SequenceEqual(["service-stopped:sppsvc"]),
            "Leituras antigas continuam gerando o mesmo alerta de antes para o histórico");

        check(JsonSerializer.Serialize(new ServiceHealth("Spooler", "Running", "Automatic")) == """{"Name":"Spooler","Status":"Running","StartType":"Automatic"}""",
            "Serviço sem os campos novos é gravado no mesmo formato de antes");
        var root = Path.Combine(Path.GetTempPath(), "nexo-services-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var store = new OperationalStore(root))
                store.SaveSnapshot(At(0, Delayed("sppsvc", 0), Plain("Spooler", 1067), new ServiceHealth("Audiosrv", "Running", "Automatic")));
            using var reopened = new OperationalStore(root);
            var saved = reopened.RecentSnapshots(Now.AddMinutes(-1), 1).Single().Services;
            check(saved.Count == 3 && saved[0] == Delayed("sppsvc", 0) && saved[1] == Plain("Spooler", 1067) && saved[2] == new ServiceHealth("Audiosrv", "Running", "Automatic"),
                "Banco operacional guarda e devolve início atrasado, gatilho e código de saída");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void Occurrence(Action<bool, string> check)
    {
        var limits = new HealthThresholds();
        var found = new HealthFinding { Id = "service-stopped:sppsvc:snapshot", Time = Now.AddSeconds(-600), Source = "rules" };
        OccurrenceState State(params ServiceHealth[] services) => HealthOccurrences.Classify([found], [At(30, services), At(0, services)], limits, Now)[0].Status.State;
        check(State(new ServiceHealth("sppsvc", "Stopped", "Automatic")) == OccurrenceState.Persistent,
            "Ocorrência antiga com leituras sem os campos novos continua persistente");
        check(State(Delayed("sppsvc", 0)) == OccurrenceState.Resolved,
            "Ocorrência de sppsvc gravada antes encerra quando as leituras novas mostram parada normal de início atrasado");
        check(State(Delayed("sppsvc", 1067)) == OccurrenceState.Persistent,
            "Ocorrência de serviço de início atrasado com código 1067 persiste");

        var spooler = new HealthFinding { Id = "service-stopped:Spooler:snapshot", Time = Now.AddSeconds(-600), Source = "rules" };
        OccurrenceStatus Spooler(params HealthSnapshot[] history) => HealthOccurrences.Classify([spooler], history, limits, Now)[0].Status;
        check(Spooler(At(30, Plain("Spooler")), At(0, Plain("Spooler"))).State == OccurrenceState.Persistent,
            "Ocorrência do Spooler (Auto comum) parado com código 0 continua persistente");
        // Aqui a regra já não está ativa e quem decide é o critério de resolução: código 0 sozinho não prova o fim.
        check(Spooler(At(30, Plain("Spooler")), At(0, Plain("Spooler") with { Status = "Running" })).State == OccurrenceState.Unknown,
            "Spooler parado com código 0 não conta como leitura sem a condição para resolver a ocorrência");
    }

    private static void Collection(Action<bool, string> check)
    {
        // Sem espaços nem quebras de linha e sem diferenciar maiúsculas, como o PowerShell: só a formatação pode mudar.
        static string Compact(string text) => Regex.Replace(text.ReplaceLineEndings("\n"), @"\s+", "");
        var script = Compact(WindowsHealthSampler.MetricsScript);
        bool Has(string fragment) => script.Contains(Compact(fragment), StringComparison.OrdinalIgnoreCase);
        check(Has("DelayedAutoStart = $_.DelayedAutoStart") && Has("ExitCode = $_.ExitCode")
            && Has("TriggerStart = (Get-NexoTriggerStart $_.Name)")
            && Has("'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\' + $Name + '\\TriggerInfo'")
            && Has(".Action -eq 1) { return $true }")
            && Has("if ($_.State -eq 'Running') { [pscustomobject]@{ Name = $_.Name; State = $_.State } }")
            && !Has("sc.exe") && !Has("qtriggerinfo"),
            "Script de coleta lê início atrasado, código de saída e gatilho do registro só para serviços fora de execução");

        var snapshot = new HealthSnapshot { Time = Now };
        using var document = JsonDocument.Parse("""
            {"Services":[
              {"Name":"sppsvc","State":"Stopped","DelayedAutoStart":true,"ExitCode":0,"TriggerStart":false},
              {"Name":"ServicoFalhou","State":"Stopped","DelayedAutoStart":false,"ExitCode":4294967295,"TriggerStart":null},
              {"Name":"ServicoTexto","State":"Stopped","DelayedAutoStart":"True","ExitCode":"0","TriggerStart":1},
              {"Name":"Audiosrv","State":"Running"}]}
            """);
        WindowsHealthSampler.ReadServices(document.RootElement, snapshot);
        var read = snapshot.Services.ToDictionary(s => s.Name);
        check(read["sppsvc"] == Delayed("sppsvc", 0)
            && read["ServicoFalhou"] == new ServiceHealth("ServicoFalhou", "Stopped", "Automatic", false, null, 4294967295)
            && read["ServicoTexto"] == new ServiceHealth("ServicoTexto", "Stopped", "Automatic")
            && read["Audiosrv"] == new ServiceHealth("Audiosrv", "Running", "Automatic"),
            "Leitura da coleta aceita os campos novos, o código de saída sem sinal e ignora tipos inesperados");
    }

    // Executa o script real de coleta, com o mesmo invólucro do SupportService, trocando Get-CimInstance e o
    // acesso ao registro por funções falsas. Roda no Windows; fora dele, só com NEXO_CHECKS_PWSH.
    private static void ScriptRun(Action<bool, string> check)
    {
        var shell = OperatingSystem.IsWindows() ? "powershell.exe" : Environment.GetEnvironmentVariable("NEXO_CHECKS_PWSH");
        if (string.IsNullOrWhiteSpace(shell)) return;
        var dir = Path.Combine(Path.GetTempPath(), "nexo-services-script-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            var harness = Path.Combine(dir, "harness.ps1");
            File.WriteAllText(harness, Harness, bom);
            File.WriteAllText(Path.Combine(dir, "run.ps1"), SupportService.PowershellCommand(WindowsHealthSampler.MetricsScript), bom);
            var start = new ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", harness, "-Dir", dir })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            var exited = process.WaitForExit(60_000);
            if (!exited) { try { process.Kill(true); } catch (InvalidOperationException) { } }
            check(exited && process.ExitCode == 0 && stderr.Result.Trim().Length == 0,
                "Script de coleta executado com cmdlets falsos termina sem erro do PowerShell " + stderr.Result.Trim() + stdout.Result.Trim());

            using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "meta.json")));
            var reads = meta.RootElement.GetProperty("RegistryReads").EnumerateArray().Select(r => r.GetString() ?? "").Order(StringComparer.Ordinal).ToArray();
            check(meta.RootElement.GetProperty("ScriptExit").GetInt32() == 0 && reads.SequenceEqual(
                    ["MapsBroker", "ServicoFalhou", "ServicoNegado", "ServicoPorGatilho", "ServicoSemPropriedade", "ServicoSoParada", "Spooler", "edgeupdate", "sppsvc"]),
                "Script executado: o registro só é consultado para serviços Auto fora de execução, um por serviço");

            using var output = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "out.json")));
            var sample = new HealthSnapshot { Time = Now };
            WindowsHealthSampler.ReadServices(output.RootElement, sample);
            var read = sample.Services.ToDictionary(s => s.Name);
            check(output.RootElement.GetProperty("Cpu").GetDouble() == 12 && output.RootElement.GetProperty("TotalKb").GetInt64() == 33554432
                && read.Count == 10 && !read.ContainsKey("ServicoManual")
                && read["sppsvc"] == Delayed("sppsvc", 0) && read["Spooler"] == Plain("Spooler")
                && read["ServicoPorGatilho"] == Trigger("ServicoPorGatilho", 0)
                && read["ServicoSoParada"] == Plain("ServicoSoParada")
                && read["ServicoNegado"] == new ServiceHealth("ServicoNegado", "Stopped", "Automatic", false, null, 0)
                && read["ServicoFalhou"] == Delayed("ServicoFalhou", 1067)
                && read["ServicoSemPropriedade"] == new ServiceHealth("ServicoSemPropriedade", "Stopped", "Automatic", null, true, 0)
                && read["Audiosrv"] == new ServiceHealth("Audiosrv", "Running", "Automatic"),
                "Script executado: traz início atrasado, código de saída e gatilho; gatilho só de parada não conta e registro negado vira nulo sem derrubar a coleta");

            var previous = new HealthSnapshot { Time = Now.AddSeconds(-30) };
            WindowsHealthSampler.ReadServices(output.RootElement, previous);
            check(Stopped(previous, sample).Order(StringComparer.Ordinal).SequenceEqual(
                    ["service-stopped:ServicoFalhou", "service-stopped:ServicoNegado", "service-stopped:ServicoSoParada", "service-stopped:Spooler"]),
                "Script executado: em 2 leituras alerta Spooler, código 1067, gatilho só de parada e gatilho ilegível, e não alerta a linha de base");
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    private const string Harness = """
        param([string]$Dir)
        $ErrorActionPreference='Stop'
        $global:Root='HKLM:\SYSTEM\CurrentControlSet\Services\'
        # Ação de cada gatilho em TriggerInfo: 1 inicia o serviço, 2 para. Serviço ausente daqui não tem a chave.
        $global:Triggers=@{ 'ServicoPorGatilho'=@(2,1); 'ServicoSoParada'=@(2); 'ServicoSemPropriedade'=@(1); 'ServicoNegado'=@(1) }
        $global:RegistryReads=[System.Collections.Generic.List[string]]::new()
        function global:FakeService($name, $state, $mode, $delayed, $exit) {
            [pscustomobject]@{ Name=$name; State=$state; StartMode=$mode; DelayedAutoStart=$delayed; ExitCode=[uint32]$exit }
        }
        function global:Get-CimInstance {
            [CmdletBinding()] param([Parameter(Position=0)][string]$ClassName)
            switch($ClassName){
                'Win32_OperatingSystem' { [pscustomobject]@{ TotalVisibleMemorySize=33554432; FreePhysicalMemory=16777216 } }
                'Win32_Processor' { [pscustomobject]@{ LoadPercentage=12 } }
                'Win32_Service' {
                    FakeService 'edgeupdate' 'Stopped' 'Auto' $true 0
                    FakeService 'MapsBroker' 'Stopped' 'Auto' $true 0
                    FakeService 'sppsvc' 'Stopped' 'Auto' $true 0
                    FakeService 'Spooler' 'Stopped' 'Auto' $false 0
                    FakeService 'ServicoPorGatilho' 'Stopped' 'Auto' $false 0
                    FakeService 'ServicoSoParada' 'Stopped' 'Auto' $false 0
                    FakeService 'ServicoNegado' 'Stopped' 'Auto' $false 0
                    FakeService 'ServicoFalhou' 'Stopped' 'Auto' $true 1067
                    [pscustomobject]@{ Name='ServicoSemPropriedade'; State='Stopped'; StartMode='Auto'; ExitCode=[uint32]0 }
                    FakeService 'Audiosrv' 'Running' 'Auto' $false 0
                    FakeService 'ServicoManual' 'Stopped' 'Manual' $false 0
                }
                default { throw ('Classe inesperada: ' + $ClassName) }
            }
        }
        function global:NexoServiceName([string]$path) {
            if(-not $path.StartsWith($global:Root)){ throw ('Caminho inesperado: ' + $path) }
            $path.Substring($global:Root.Length).Split('\')[0]
        }
        function global:Test-Path {
            [CmdletBinding()] param([string]$LiteralPath)
            $name=NexoServiceName $LiteralPath
            $global:RegistryReads.Add($name)
            if(-not $LiteralPath.EndsWith('\TriggerInfo')){ throw ('Chave inesperada: ' + $LiteralPath) }
            $global:Triggers.ContainsKey($name)
        }
        function global:Get-ChildItem {
            [CmdletBinding()] param([string]$LiteralPath)
            $name=NexoServiceName $LiteralPath
            if($name -eq 'ServicoNegado'){ throw [System.Security.SecurityException]::new('Acesso negado simulado ao registro') }
            $actions=@($global:Triggers[$name])
            for($i=0; $i -lt $actions.Count; $i++){ [pscustomobject]@{ PSPath=($LiteralPath + '\' + $i) } }
        }
        function global:Get-ItemProperty {
            [CmdletBinding()] param([string]$LiteralPath)
            $name=NexoServiceName $LiteralPath
            [pscustomobject]@{ Type=1; Action=@($global:Triggers[$name])[[int]$LiteralPath.Split('\')[-1]] }
        }
        $global:LASTEXITCODE=0
        $out=& (Join-Path $Dir 'run.ps1')
        $exit=[int]$global:LASTEXITCODE
        Microsoft.PowerShell.Management\Set-Content -LiteralPath (Join-Path $Dir 'out.json') -Value ($out -join "`n") -Encoding UTF8
        [pscustomobject]@{ ScriptExit=$exit; RegistryReads=@($global:RegistryReads) } | ConvertTo-Json -Depth 4 -Compress | Microsoft.PowerShell.Management\Set-Content -LiteralPath (Join-Path $Dir 'meta.json') -Encoding UTF8
        """;
}
