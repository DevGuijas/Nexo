namespace Nexo.Support;

public sealed class HealthThresholds
{
    public double CpuHighPercent { get; set; } = 90;
    public double DiskLowFreePercent { get; set; } = 10;
    public double MemoryGrowthPoints { get; set; } = 20;
    public double MemoryGrowthFinalPercent { get; set; } = 80;
    public double ProcessMemoryGrowthPoints { get; set; } = 5;
    public int ProcessMemorySamples { get; set; } = 4;
    public TimeSpan ProcessMemoryWarmup { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan ProcessMemoryAlertCooldown { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan MaxSampleGap { get; set; } = TimeSpan.FromSeconds(90);
    public TimeSpan ExternalProbeMaxSampleGap { get; set; } = TimeSpan.FromSeconds(45);
    public int CpuSamples { get; set; } = 3;
    public int OtherSamples { get; set; } = 2;
    public int NetworkSamples { get; set; } = 3;
}

public static class HealthRules
{
    /// <summary>
    /// Serviço automático parado que conta como falha. O Windows para de propósito serviços de início atrasado
    /// ou por gatilho depois que terminam o trabalho; esses só ficam de fora quando saíram com código 0.
    /// Campo não lido (nulo, como nas leituras anteriores à #57) não isenta: o serviço volta à regra antiga.
    /// </summary>
    internal static bool UnexpectedlyStopped(ServiceHealth service)
        => service.StartType.Equals("Automatic", StringComparison.OrdinalIgnoreCase)
            && service.Status.Equals("Stopped", StringComparison.OrdinalIgnoreCase)
            && !(service.ExitCode == 0 && (service.DelayedAutoStart == true || service.TriggerStart == true));

    public static List<HealthFinding> Evaluate(IReadOnlyList<HealthSnapshot> history, HealthThresholds thresholds)
    {
        if (thresholds.CpuSamples < 2 || thresholds.OtherSamples < 2 || thresholds.NetworkSamples < 2
            || thresholds.MaxSampleGap <= TimeSpan.Zero || thresholds.ExternalProbeMaxSampleGap <= TimeSpan.Zero || thresholds.CpuHighPercent is < 1 or > 100
            || thresholds.DiskLowFreePercent is < 1 or > 100
            || thresholds.ProcessMemorySamples < 3 || !(thresholds.ProcessMemoryGrowthPoints is > 0 and <= 100))
            throw new ArgumentOutOfRangeException(nameof(thresholds));
        var ordered = history.OrderBy(s => s.Time).ToArray();
        if (ordered.Length == 0) return [];
        var latest = ordered[^1];
        var findings = new List<HealthFinding>();
        bool Continuous(HealthSnapshot[] samples, TimeSpan? maxGap = null) => samples.Length > 0
            && samples.Zip(samples.Skip(1)).All(pair => pair.Second.Time - pair.First.Time <= (maxGap ?? thresholds.MaxSampleGap));
        HealthSnapshot[] Tail(int count) => ordered.TakeLast(count).ToArray();
        HealthFinding Finding(string id, string category, string problem, string evidence, string cause, string severity, string impact, string recommendation, double confidence,
            string probeTarget = "", string probeConfigurationId = "")
            => new() { Id = id, Time = latest.Time, Category = category, Problem = problem, Evidence = evidence, Cause = cause,
                Severity = severity, Impact = impact, Recommendation = recommendation, Confidence = confidence, Confirmed = false,
                ProbeTarget = probeTarget, ProbeConfigurationId = probeConfigurationId };

        var cpu = Tail(thresholds.CpuSamples);
        if (cpu.Length == thresholds.CpuSamples && Continuous(cpu) && cpu.All(s => s.CpuPercent >= thresholds.CpuHighPercent))
            findings.Add(Finding("cpu-high", "cpu", "Uso persistente de CPU", $"{cpu.Length} leituras entre {cpu[0].CpuPercent:F0}% e {cpu[^1].CpuPercent:F0}%", "Processo ou carga de trabalho ainda não identificado", "warning", "Pode reduzir a resposta do computador", "Identifique os processos ativos antes de fechar qualquer um", 0.85));

        var other = Tail(thresholds.OtherSamples);
        if (other.Length == thresholds.OtherSamples && Continuous(other))
        {
            foreach (var disk in latest.Disks.Where(d => d.TotalBytes > 0))
            {
                var readings = other.Select(s => s.Disks.FirstOrDefault(d => d.Name.Equals(disk.Name, StringComparison.OrdinalIgnoreCase))).ToArray();
                if (readings.All(d => d != null && d.TotalBytes > 0 && 100.0 * d.FreeBytes / d.TotalBytes < thresholds.DiskLowFreePercent))
                {
                    var free = 100.0 * disk.FreeBytes / disk.TotalBytes;
                    findings.Add(Finding("disk-low:" + disk.Name, "disk", "Pouco espaço no volume " + disk.Name, $"{free:F1}% livre em {other.Length} leituras", "Arquivos ou aplicativos ocupam a capacidade; origem ainda não identificada", free < 5 ? "critical" : "warning", "Atualizações e gravações podem falhar", "Revise o uso do disco antes de remover arquivos", 0.95));
                }
            }
            foreach (var service in latest.Services.Where(UnexpectedlyStopped))
            {
                if (other.All(s => s.Services.Any(x => x.Name == service.Name && UnexpectedlyStopped(x))))
                    findings.Add(Finding("service-stopped:" + service.Name, "service", "Serviço automático interrompido", service.Name + " parado em " + other.Length + " leituras", "Causa da parada ainda não identificada", "warning", "A função associada pode estar indisponível", "Consulte eventos do serviço antes de propor reinício", 0.85));
            }
        }

        var network = Tail(thresholds.NetworkSamples);
        if (other.Length == thresholds.OtherSamples && Continuous(other) && other.All(s => s.Connectivity == "none"))
        {
            foreach (var adapter in latest.Adapters.Where(a => a.Status.Equals("Disabled", StringComparison.OrdinalIgnoreCase)))
                if (other.All(s => s.Adapters.Any(a => a.Id == adapter.Id && a.Status.Equals("Disabled", StringComparison.OrdinalIgnoreCase))))
                    findings.Add(Finding("adapter-disabled:" + adapter.Id, "network", "Adaptador desativado sem conectividade local", adapter.Name + " desativado em " + other.Length + " leituras", "Pode ser desativação intencional; relação com o sintoma ainda não confirmada", "warning", "Esta interface não pode conectar enquanto desativada", "Verifique se esta é a interface usada antes de propor ativação", 0.75));
        }
        if (network.Length == thresholds.NetworkSamples && Continuous(network) && network.All(s => s.Connectivity == "none"))
            findings.Add(Finding("network-none", "network", "Conectividade local ausente de forma recorrente", $"{network.Length} leituras sem conectividade informada pelo Windows", "Adaptador, cabo, Wi-Fi ou configuração ainda não identificados", "warning", "Recursos de rede podem ficar indisponíveis", "Execute o diagnóstico de interfaces antes de alterar configurações", 0.7));
        if (network.Length == thresholds.NetworkSamples && Continuous(network, thresholds.ExternalProbeMaxSampleGap)
            && network.All(s => s.Connectivity == "local" && s.ExternalProbe?.State is ("no_response" or "unexpected"))
            && !string.IsNullOrWhiteSpace(latest.ExternalProbe.Target) && !string.IsNullOrWhiteSpace(latest.ExternalProbe.ConfigurationId)
            && network.All(s => s.ExternalProbe.ConfigurationId == latest.ExternalProbe.ConfigurationId))
            findings.Add(Finding("network-external-unconfirmed", "network", "Acesso ao destino externo não confirmado",
                $"{network.Length} consultas consecutivas a {latest.ExternalProbe.Target} sem a resposta esperada",
                "Destino indisponível, proxy, portal cativo ou restrição de rede são possibilidades ainda não diferenciadas",
                "warning", "Serviços que dependem deste destino podem estar indisponíveis",
                "Confira gateway, DNS e o destino configurado antes de alterar a rede", 0.65,
                latest.ExternalProbe.Target, latest.ExternalProbe.ConfigurationId));

        // Falha de coleta deixa a lista vazia: essa leitura não é evidência sobre processos.
        var processHistory = ordered.Where(s => s.Processes.Count > 0).ToArray();
        var processesAreCurrent = processHistory.Length > 0 && latest.Time - processHistory[^1].Time <= thresholds.MaxSampleGap;
        HealthSnapshot[] ProcessTail(int count) => processesAreCurrent ? processHistory.TakeLast(count).ToArray() : [];

        // A identidade é PID + início: o Windows reutiliza PIDs e o nome não distingue instâncias.
        List<ProcessGrowth> GrowingProcesses(HealthSnapshot[] samples)
        {
            var total = samples.LastOrDefault(s => s.MemoryTotalBytes > 0)?.MemoryTotalBytes;
            if (samples.Length != thresholds.ProcessMemorySamples || !Continuous(samples) || total == null) return [];
            return samples[^1].Processes.Where(p => p.StartTime == null || samples[0].Time - p.StartTime >= thresholds.ProcessMemoryWarmup)
                .Select(candidate => samples.Select(s => s.Processes.FirstOrDefault(p => SameProcess(p, candidate))).ToArray())
                .Where(series => series.All(p => p != null) && Sustained(series.Select(p => p!.WorkingSetBytes).ToArray()))
                .Select(series => new ProcessGrowth(series[0]!, series[^1]!, 100.0 * (series[^1]!.WorkingSetBytes - series[0]!.WorkingSetBytes) / total.Value))
                .Where(growth => growth.Points >= thresholds.ProcessMemoryGrowthPoints)
                .OrderByDescending(growth => growth.Points).ToList();
        }

        var observedGrowth = GrowingProcesses(ProcessTail(thresholds.ProcessMemorySamples));
        var growing = observedGrowth.Where(growth => growth.Last.StartTime.HasValue).ToList();
        foreach (var growth in growing)
        {
            var finding = Finding(ProcessKey("process-memory-growing", growth.Last), "memory", "Processo com memória em crescimento persistente", $"{ProcessLabel(growth.Last)}: de {Megabytes(growth.First)} MB para {Megabytes(growth.Last)} MB em {thresholds.ProcessMemorySamples} leituras e {growth.Points:F0} pontos da RAM", "Crescimento observado neste processo; vazamento e carga legítima não distinguidos", "warning", "Pode reduzir a memória disponível e causar lentidão", "Verifique se o aplicativo está em uso e salve o trabalho antes de qualquer intervenção", 0.7);
            // A ocorrência guarda a subida para saber depois se o processo ainda segura essa memória (#60).
            finding.MemoryGrowth = new(SafeName(growth.Last.Name), growth.First.WorkingSetBytes, growth.Last.WorkingSetBytes);
            findings.Add(finding);
        }
        foreach (var growth in observedGrowth.Where(growth => !growth.Last.StartTime.HasValue))
            findings.Add(Finding($"process-memory-unverified:{growth.Last.Id}:{SafeName(growth.Last.Name)}", "memory", "Crescimento de memória sem identidade confirmada", $"{SafeName(growth.Last.Name)} PID {growth.Last.Id}: de {Megabytes(growth.First)} MB para {Megabytes(growth.Last)} MB em {thresholds.ProcessMemorySamples} leituras", "O Windows não informou o início do processo; as leituras podem ser de processos diferentes", "info", "A origem do crescimento continua sem confirmação", "Consulte os processos em uso antes de atribuir o crescimento a este PID", 0.4));

        var departure = ProcessTail(thresholds.ProcessMemorySamples + 1);
        if (departure.Length == thresholds.ProcessMemorySamples + 1)
            foreach (var growth in GrowingProcesses(departure[..^1]).Where(growth => growth.Last.StartTime.HasValue && !departure[^1].Processes.Any(p => SameProcess(p, growth.Last))))
            {
                var samePid = departure[^1].Processes.Where(p => p.Id == growth.Last.Id).ToArray();
                var unreadable = samePid.Length > 0 && !samePid.Any(p => p.StartTime.HasValue);
                var cause = unreadable ? "O início deste PID não pôde ser lido; não é possível concluir se ainda é o mesmo processo"
                    : samePid.Length > 0 ? "O mesmo PID agora pertence a outro processo; não é possível concluir se o anterior liberou a memória"
                    : "Pode ter encerrado, reiniciado ou saído dos 12 maiores consumidores; não é possível concluir a causa";
                var problem = unreadable ? "Processo que crescia ficou sem identidade confirmada" : "Processo que crescia deixou de ser observado";
                var outcome = unreadable ? "e o PID segue na lista sem início legível" : "e não aparece na leitura mais recente";
                findings.Add(Finding(ProcessKey("process-memory-left", growth.Last), "memory", problem, $"{ProcessLabel(growth.Last)} crescia até {Megabytes(growth.Last)} MB {outcome}", cause, "info", "A origem do crescimento anterior continua sem confirmação", "Verifique se o aplicativo ainda funciona antes de considerar o crescimento resolvido", 0.5));
            }

        var memory = Tail(3);
        if (memory.Length == 3 && Continuous(memory) && memory.All(s => s.MemoryUsedPercent.HasValue)
            && memory[0].MemoryUsedPercent < memory[1].MemoryUsedPercent && memory[1].MemoryUsedPercent < memory[2].MemoryUsedPercent
            && memory[2].MemoryUsedPercent - memory[0].MemoryUsedPercent >= thresholds.MemoryGrowthPoints
            && memory[2].MemoryUsedPercent >= thresholds.MemoryGrowthFinalPercent)
            findings.Add(Finding("memory-growing", "memory", "Memória em crescimento persistente", $"De {memory[0].MemoryUsedPercent:F0}% para {memory[2].MemoryUsedPercent:F0}% em três leituras", growing.Count > 0 ? RelatedProcessCause([$"{SafeName(growing[0].Last.Name)} PID {growing[0].Last.Id}"]) : "Processo responsável ainda não identificado", "warning", "Pode aumentar paginação e lentidão", "Compare o uso por processo antes de intervir", 0.75));
        return findings;
    }

    private readonly record struct ProcessGrowth(ProcessHealth First, ProcessHealth Last, double Points);

    /// <summary>Causa de memory-growing quando há processo apontado, na regra e na apresentação.</summary>
    internal static string RelatedProcessCause(IEnumerable<string> processes) => $"Possível relação com {string.Join(" e ", processes)}; ainda não confirmada";

    // Sem o início, o nome é o único sinal disponível contra a reutilização do PID.
    private static bool SameProcess(ProcessHealth left, ProcessHealth right)
        => left.Id == right.Id && left.StartTime == right.StartTime && (left.StartTime.HasValue || left.Name == right.Name);

    // Tolera oscilação, como a coleta de lixo, sem aceitar um degrau único como crescimento:
    // a maioria dos passos sobe, nenhum passo responde por mais de 2/3 da subida e nenhuma queda passa de 1/4 dela.
    private static bool Sustained(long[] sizes)
    {
        var net = sizes[^1] - sizes[0];
        var steps = sizes.Zip(sizes.Skip(1), (before, after) => after - before).ToArray();
        return net > 0 && steps.Count(step => step > 0) >= steps.Length - steps.Length / 3
            && steps.Max() * 3 <= net * 2 && steps.All(step => step >= -net / 4);
    }

    private static string ProcessKey(string prefix, ProcessHealth process) => $"{prefix}:{process.Id}:{process.StartTime!.Value.UtcTicks}";
    private static string ProcessLabel(ProcessHealth process) => $"{SafeName(process.Name)} PID {process.Id} iniciado em {process.StartTime!.Value:yyyy-MM-dd HH:mm:ss zzz}";
    private static string Megabytes(ProcessHealth process) => (process.WorkingSetBytes / (1024.0 * 1024)).ToString("F0");

    // O nome do processo é escolhido por quem criou o executável e pode chegar ao modelo no relatório.
    internal static string SafeName(string name)
    {
        var safe = new string(name.Take(40).Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray());
        return safe.Length == 0 ? "processo" : safe;
    }
}
