namespace Nexo.Support;

public sealed class HealthThresholds
{
    public double CpuHighPercent { get; set; } = 90;
    public double DiskLowFreePercent { get; set; } = 10;
    public double MemoryGrowthPoints { get; set; } = 20;
    public double MemoryGrowthFinalPercent { get; set; } = 80;
    public TimeSpan MaxSampleGap { get; set; } = TimeSpan.FromSeconds(90);
    public int CpuSamples { get; set; } = 3;
    public int OtherSamples { get; set; } = 2;
    public int NetworkSamples { get; set; } = 3;
}

public static class HealthRules
{
    public static List<HealthFinding> Evaluate(IReadOnlyList<HealthSnapshot> history, HealthThresholds thresholds)
    {
        if (thresholds.CpuSamples < 2 || thresholds.OtherSamples < 2 || thresholds.NetworkSamples < 2
            || thresholds.MaxSampleGap <= TimeSpan.Zero || thresholds.CpuHighPercent is < 1 or > 100
            || thresholds.DiskLowFreePercent is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(thresholds));
        var ordered = history.OrderBy(s => s.Time).ToArray();
        if (ordered.Length == 0) return [];
        var latest = ordered[^1];
        var findings = new List<HealthFinding>();
        bool Continuous(HealthSnapshot[] samples) => samples.Length > 0 && samples.Zip(samples.Skip(1)).All(pair => pair.Second.Time - pair.First.Time <= thresholds.MaxSampleGap);
        HealthSnapshot[] Tail(int count) => ordered.TakeLast(count).ToArray();
        HealthFinding Finding(string id, string category, string problem, string evidence, string cause, string severity, string impact, string recommendation, double confidence)
            => new() { Id = id, Time = latest.Time, Category = category, Problem = problem, Evidence = evidence, Cause = cause,
                Severity = severity, Impact = impact, Recommendation = recommendation, Confidence = confidence, Confirmed = false };

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
            foreach (var service in latest.Services.Where(s => s.StartType.Equals("Automatic", StringComparison.OrdinalIgnoreCase) && s.Status.Equals("Stopped", StringComparison.OrdinalIgnoreCase)))
            {
                if (other.All(s => s.Services.Any(x => x.Name == service.Name && x.StartType.Equals("Automatic", StringComparison.OrdinalIgnoreCase) && x.Status.Equals("Stopped", StringComparison.OrdinalIgnoreCase))))
                    findings.Add(Finding("service-stopped:" + service.Name, "service", "Serviço automático interrompido", service.Name + " parado em " + other.Length + " leituras", "Causa da parada ainda não identificada", "warning", "A função associada pode estar indisponível", "Consulte eventos do serviço antes de propor reinício", 0.85));
            }
        }

        var network = Tail(thresholds.NetworkSamples);
        if (network.Length == thresholds.NetworkSamples && Continuous(network) && network.All(s => s.Connectivity == "none"))
            findings.Add(Finding("network-none", "network", "Conectividade local ausente de forma recorrente", $"{network.Length} leituras sem conectividade informada pelo Windows", "Adaptador, cabo, Wi-Fi ou configuração ainda não identificados", "warning", "Recursos de rede podem ficar indisponíveis", "Execute o diagnóstico de interfaces antes de alterar configurações", 0.7));

        var memory = Tail(3);
        if (memory.Length == 3 && Continuous(memory) && memory.All(s => s.MemoryUsedPercent.HasValue)
            && memory[0].MemoryUsedPercent < memory[1].MemoryUsedPercent && memory[1].MemoryUsedPercent < memory[2].MemoryUsedPercent
            && memory[2].MemoryUsedPercent - memory[0].MemoryUsedPercent >= thresholds.MemoryGrowthPoints
            && memory[2].MemoryUsedPercent >= thresholds.MemoryGrowthFinalPercent)
            findings.Add(Finding("memory-growing", "memory", "Memória em crescimento persistente", $"De {memory[0].MemoryUsedPercent:F0}% para {memory[2].MemoryUsedPercent:F0}% em três leituras", "Processo responsável ainda não identificado", "warning", "Pode aumentar paginação e lentidão", "Compare o uso por processo antes de intervir", 0.75));
        return findings;
    }
}
