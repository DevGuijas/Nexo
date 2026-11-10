using System.Globalization;
using System.Text;

namespace Nexo.Support;

/// <summary>Stable: o processo apontado segura a memória num patamar estável (#71). É informativo, não uma falha ativa.</summary>
public enum OccurrenceState { Persistent, Resolved, Unknown, Stable }
public sealed record OccurrenceStatus(OccurrenceState State, string Label);

/// <summary>Estado atual de uma ocorrência registrada. Resolver exige prova: sem ela, o estado é desconhecido.</summary>
public static class HealthOccurrences
{
    private sealed record Criterion(int Samples, TimeSpan MaxGap, Func<HealthSnapshot, bool> Clear);
    private enum Hold { Holding, Released, Unknown }

    /// <summary>Processo apontado por process-memory-growing: PID e início vêm do identificador; a subida, do registro (#60).</summary>
    private sealed record Pointed(HealthFinding Finding, int Pid, long StartTicks, string Label, ProcessMemoryGrowth? Growth)
    {
        // Segura a memória enquanto mantém mais da metade da subida registrada no alerta. Registro anterior à #60 não tem limite.
        public long? Limit => Growth is { } growth && growth.ToBytes > growth.FromBytes ? growth.FromBytes + (growth.ToBytes - growth.FromBytes) / 2 : null;
    }

    // A regra de memória do sistema alerta com três leituras; o fim das ocorrências de memória exige o mesmo.
    private const int MemorySamples = 3;

    // #71: um processo que segura a memória sem crescer deixa de "persistir" e fica "estável em patamar alto".
    // A janela é medida em tempo, para valer com qualquer intervalo de coleta. O nome do processo não entra no critério.
    internal static readonly TimeSpan PlateauWindow = TimeSpan.FromMinutes(30);
    // Amplitude (máx − mín) abaixo desta fração da média da janela.
    internal const double PlateauAmplitude = 0.05;
    // Inclinação da regressão linear do working set na janela, projetada para PlateauWindow, abaixo desta fração da média.
    internal const double PlateauSlope = 0.02;
    // Crescimento acumulado sobre o 1º patamar depois do alerta que volta a ocorrência a "Persiste" até um novo alerta.
    internal const double PlateauRise = 0.10;

    /// <summary>Working set do processo apontado (mesmo PID e início) numa leitura; nulo quando a leitura não o mostra.</summary>
    public sealed record ProcessReading(DateTimeOffset Time, long? Bytes);

    /// <summary>Resultado da busca do 1º patamar numa série desde o alerta.</summary>
    internal sealed record PlateauScan(double? Reference, DateTimeOffset? ProvenAt, DateTimeOffset? ExceededAt, long? ExceededBytes, string? Unavailable);

    /// <summary>
    /// Grava na ocorrência do processo a referência do 1º patamar, quando ele é comprovado, e o momento em que o acumulado passa
    /// de PlateauRise sobre ela (#71). Roda no monitor, a cada leitura, fora da interface. A série desde o alerta só é lida quando a
    /// janela que termina na leitura atual já é um patamar e a referência ainda não foi gravada; depois disso basta a leitura atual.
    /// </summary>
    public static void RecordPlateaus(OperationalStore store, HealthSnapshot latest, HealthThresholds thresholds)
        => RecordPlateaus(store, latest, thresholds, store.ProcessReadings);

    // readings é trocado nos testes para contar o que o monitor lê do banco.
    internal static void RecordPlateaus(OperationalStore store, HealthSnapshot latest, HealthThresholds thresholds,
        Func<int, long, DateTimeOffset, IReadOnlyList<ProcessReading>> readings)
    {
        if (latest.Processes.Count == 0) return;
        var updated = new List<HealthFinding>();
        foreach (var finding in store.RecentFindingsWithPrefix("process-memory-growing:", 50).GroupBy(BaseId).Select(group => group.MaxBy(f => f.Time)!))
        {
            if (PointedBy(finding, []) is not { } process || process.Limit == null) continue;
            if (latest.Processes.FirstOrDefault(p => Same(p, process.Pid, process.StartTicks)) is not { } current) continue;
            if (finding.MemoryPlateau is { } recorded)
            {
                if (recorded.ReferenceBytes is { } reference && recorded.ExceededAt == null && current.WorkingSetBytes >= reference * (1 + PlateauRise))
                    updated.Add(finding.WithMemoryPlateau(recorded with { ExceededAt = latest.Time, ExceededBytes = current.WorkingSetBytes }));
                continue;
            }
            if (latest.Time - finding.Time < PlateauWindow) continue;
            var recent = readings(process.Pid, process.StartTicks, latest.Time - PlateauWindow - thresholds.MaxSampleGap);
            if (!EndsInPlateau(recent, latest.Time, thresholds)) continue;
            var scan = Scan(finding.Time, readings(process.Pid, process.StartTicks, finding.Time), thresholds);
            // Sem referência e sem motivo definitivo, a busca recomeça na próxima leitura.
            if (scan.Reference == null && scan.Unavailable == null) continue;
            updated.Add(finding.WithMemoryPlateau(new MemoryPlateauEvidence(process.Pid, process.StartTicks, scan.Reference is { } level ? (long)level : null,
                scan.ProvenAt, scan.ExceededAt, scan.ExceededBytes, scan.Unavailable)));
        }
        if (updated.Count > 0) store.SaveFindings(updated);
    }

    // A janela que termina na leitura atual já é um patamar: só então vale ler a série desde o alerta.
    private static bool EndsInPlateau(IReadOnlyList<ProcessReading> readings, DateTimeOffset now, HealthThresholds thresholds)
    {
        var window = new List<(DateTimeOffset Time, double Bytes)>();
        for (var i = readings.Count - 1; i >= 0; i--)
        {
            if (readings[i].Bytes is not { } bytes || window.Count > 0 && window[^1].Time - readings[i].Time > thresholds.MaxSampleGap) return false;
            window.Add((readings[i].Time, bytes));
            if (readings[i].Time <= now - PlateauWindow) { window.Reverse(); return StableWindow(window) != null; }
        }
        return false;
    }

    public static IReadOnlyList<(HealthFinding Finding, OccurrenceStatus Status)> Classify(IReadOnlyList<HealthFinding> findings, IReadOnlyList<HealthSnapshot> history, HealthThresholds thresholds, DateTimeOffset now)
    {
        var ordered = history.OrderBy(s => s.Time).ToArray();
        var active = HealthRules.Evaluate(ordered, thresholds);
        var current = IsRecent(ordered.LastOrDefault(), thresholds, now);
        var newest = findings.GroupBy(BaseId).Select(group => group.MaxBy(f => f.Time)!).ToHashSet();
        var pointed = findings.Select(f => PointedBy(f, ordered)).OfType<Pointed>().ToArray();
        var cadence = Cadence(ordered, thresholds);
        return findings.Select(finding =>
        {
            var related = Related(finding);
            // A ocorrência do processo pode chegar leituras depois da do sistema: a ligação é feita aqui, sem regravar nada.
            var shown = related.Length == 0 ? finding : finding.WithCause(HealthRules.RelatedProcessCause(related.Select(p => p.Label)));
            var status = Status(finding, related);
            // No patamar estável, o texto exibido acompanha o estado: sem "crescimento persistente" e sem recomendação de ação.
            if (status.State == OccurrenceState.Stable) shown = shown.AsStablePlateau();
            return (shown, status);
        }).ToArray();

        // Referência do patamar: a gravada pelo monitor, conferida nas leituras recebidas desde que foi comprovada;
        // sem ela, a busca na série recebida, que precisa alcançar o alerta (os testes e quem já tem a série inteira).
        long? ReferenceOf(Pointed process)
        {
            if (process.Finding.MemoryPlateau is { } recorded)
            {
                if (recorded.Pid != process.Pid || recorded.StartTicks != process.StartTicks || recorded.ReferenceBytes is not { } reference
                    || recorded.ExceededAt != null || recorded.ProvenAt is not { } proven) return null;
                var exceeded = ordered.Where(s => s.Time >= proven).Select(s => s.Processes.FirstOrDefault(p => Same(p, process.Pid, process.StartTicks)))
                    .Any(p => p != null && p.WorkingSetBytes >= reference * (1 + PlateauRise));
                return exceeded ? null : reference;
            }
            var scan = Scan(process.Finding.Time, [.. ordered.Where(s => s.Time >= process.Finding.Time).Select(s => new ProcessReading(s.Time,
                s.Processes.FirstOrDefault(p => Same(p, process.Pid, process.StartTicks))?.WorkingSetBytes))], thresholds);
            return scan.Reference is { } level && scan.ExceededAt == null ? (long)level : null;
        }

        Pointed[] Related(HealthFinding system) => system.Source != "rules" || BaseId(system) != "memory-growing" ? [] : pointed
            .Where(p => Overlaps(system.Time, p.Finding.Time))
            .GroupBy(p => (p.Pid, p.StartTicks)).Select(group => group.MaxBy(p => p.Finding.Time)!).OrderBy(p => p.Finding.Time).ToArray();

        // A janela do sistema tem as três leituras até o alerta dele. A do processo tem as ProcessMemorySamples leituras com
        // lista de processos até o alerta dele, como na regra: leitura com a lista vazia (falha de coleta) não entra nela.
        // A ligação exige que as duas janelas dividam ao menos uma leitura.
        bool Overlaps(DateTimeOffset system, DateTimeOffset process)
        {
            var (before, after) = (MemorySamples - 1, thresholds.ProcessMemorySamples - 1);
            // Cada alerta tem o horário da leitura que o disparou.
            var (s, p) = (Array.FindIndex(ordered, r => r.Time == system), Array.FindIndex(ordered, r => r.Time == process));
            if (s >= 0 && p >= 0)
                return Enumerable.Range(0, p + 1).Where(i => ordered[i].Processes.Count > 0).TakeLast(thresholds.ProcessMemorySamples)
                    .Any(i => i >= s - before && i <= s);
            // Leitura fora do histórico consultado: conta as leituras pelo intervalo típico entre elas, com meia leitura de folga.
            // Aqui não se sabe quais leituras vieram sem lista, nem se o intervalo de coleta mudou depois dos alertas.
            if (cadence is not { } step) return false;
            var readings = (process - system) / step;
            return readings >= -before - 0.5 && readings <= after + 0.5;
        }

        bool StillStopped(string name)
        {
            var tail = ordered.TakeLast(thresholds.OtherSamples).ToArray();
            return tail.Length == thresholds.OtherSamples && tail.Zip(tail.Skip(1)).All(pair => pair.Second.Time - pair.First.Time <= thresholds.MaxSampleGap)
                && tail.All(s => s.Services.FirstOrDefault(x => x.Name == name) is { } service && HealthRules.UnexpectedlyStopped(service));
        }

        OccurrenceStatus Status(HealthFinding finding, Pointed[] related)
        {
            if (finding.Source != "rules") return Unknown("sem regra para reavaliar");
            if (!newest.Contains(finding)) return Unknown("há ocorrência mais recente do mesmo alerta");
            if (!current) return Unknown("sem leitura recente");
            var id = BaseId(finding);
            if (active.Any(currentFinding => currentFinding.Id == id && (id != "network-external-unconfirmed"
                || SameProbe(finding, currentFinding.ProbeTarget, currentFinding.ProbeConfigurationId))))
                return new(OccurrenceState.Persistent, "Persiste nas leituras atuais");
            // A prova do alerta (execução estável antes da parada) fica no registro: depois que essas leituras saem do
            // histórico consultado, a regra não volta a gerar o achado, mas o serviço continua parado (#57).
            if (id.StartsWith("service-stopped:", StringComparison.Ordinal) && finding.ServiceStop != null
                && StillStopped(id["service-stopped:".Length..]))
                return new(OccurrenceState.Persistent, $"Persiste: {HealthRules.SafeName(id["service-stopped:".Length..])} segue parado depois de ter sido visto em execução");
            // Memória do sistema depende dos processos apontados; a do processo, dele mesmo.
            Pointed[] holders = related.Length > 0 ? related : PointedBy(finding, ordered) is { } self ? [self] : [];
            OccurrenceStatus? stable = null;
            foreach (var holder in holders)
                if (HoldAt(holder, ordered[^1]) is (Hold.Holding, var bytes))
                {
                    // Qualquer processo que segura a memória sem patamar estável mantém a ocorrência persistindo.
                    if (ReferenceOf(holder) is not { } reference)
                        return new(OccurrenceState.Persistent, $"Persiste: {holder.Label} segue com {Megabytes(bytes)} MB, mais da metade da subida de {Megabytes(holder.Growth!.FromBytes)} para {Megabytes(holder.Growth.ToBytes)} MB registrada no alerta do processo");
                    stable ??= new(OccurrenceState.Stable, $"{StablePlateauLabel}: {holder.Label} segue com {Megabytes(bytes)} MB. "
                        + $"Patamar de referência: cerca de {Megabytes(reference)} MB, o primeiro medido depois do alerta, em {PlateauWindow.TotalMinutes:F0} min seguidos com variação abaixo de {PlateauAmplitude * 100:F0}% e sem tendência de alta. "
                        + $"Volta a persistir se chegar a {Megabytes((long)(reference * (1 + PlateauRise)))} MB");
                }
            if (stable != null) return stable;
            var criterion = CriterionFor(id, finding, thresholds, holders);
            if (criterion == null) return Unknown("sem critério para confirmar o fim");
            // Registro anterior à #57, sem a prova da execução: parada normal sem essa prova não é falha.
            if (id.StartsWith("service-stopped:", StringComparison.Ordinal) && finding.ServiceStop == null
                && ordered[^1].Services.FirstOrDefault(x => x.Name == id["service-stopped:".Length..]) is { } stopped && HealthRules.CleanStop(stopped))
                return Unknown("parado com saída normal, sem execução estável observada antes da parada");
            var recent = ordered.Where(s => s.Time > finding.Time).TakeLast(criterion.Samples).ToArray();
            var proven = recent.Length == criterion.Samples && recent.All(criterion.Clear)
                && recent.Zip(recent.Skip(1)).All(pair => pair.Second.Time - pair.First.Time <= criterion.MaxGap);
            if (proven) return new(OccurrenceState.Resolved, $"Resolvida: {recent.Length} leituras seguidas sem a condição");
            var unconfirmed = holders.FirstOrDefault(holder => HoldAt(holder, ordered[^1]).State == Hold.Unknown);
            return Unknown(unconfirmed != null ? $"sem leitura que confirme que {unconfirmed.Label} liberou a memória" : "leituras insuficientes para confirmar");
        }
    }

    /// <summary>
    /// Ocorrência de serviço parado sem a prova da execução (gravada antes da regra da #57, ou por código de saída) cujo
    /// serviço, na leitura atual, está parado com saída normal, sem atraso e sem gatilho. Reavaliada pela regra atual,
    /// ela não alertaria: sai do relatório do modelo e das ocorrências do painel, e o serviço aparece no aviso informativo.
    /// Uma leitura atual com código diferente de 0, ou com campo não lido, mantém a ocorrência como antes.
    /// </summary>
    public static bool SupersededServiceStop(HealthFinding finding, HealthSnapshot? latest)
    {
        var id = BaseId(finding);
        const string prefix = "service-stopped:";
        return finding.Source == "rules" && finding.ServiceStop == null && latest != null && id.StartsWith(prefix, StringComparison.Ordinal)
            && latest.Services.FirstOrDefault(service => service.Name == id[prefix.Length..]) is { } current && HealthRules.CleanStop(current);
    }

    public static string CollectionNotice(HealthSnapshot? latest, HealthThresholds thresholds, DateTimeOffset now)
    {
        if (latest == null) return "Nenhuma coleta disponível. O estado atual das ocorrências é desconhecido.";
        var time = "Última coleta: " + latest.Time.ToLocalTime().ToString("g");
        return IsRecent(latest, thresholds, now) ? time : time + ". Sem leitura recente: o estado atual das ocorrências é desconhecido.";
    }

    /// <summary>Começo exato do rótulo do patamar estável (#71). O resumo ao modelo (#88) reconhece o estado informativo só por ele.</summary>
    public const string StablePlateauLabel = "Estável em patamar alto (informativo, não é falha ativa)";
    /// <summary>Severidade exibida: o patamar estável é informativo, qualquer que seja a do registro (#71).</summary>
    public static string ShownSeverity(HealthFinding finding, OccurrenceStatus status) => status.State == OccurrenceState.Stable ? "info" : finding.Severity;

    private static OccurrenceStatus Unknown(string reason) => new(OccurrenceState.Unknown, $"Ocorrência histórica; estado atual desconhecido ({reason})");

    private static bool IsRecent(HealthSnapshot? latest, HealthThresholds thresholds, DateTimeOffset now)
        => latest != null && (now - latest.Time).Duration() <= thresholds.MaxSampleGap;

    // O monitor acrescenta ":{id da amostra}" ao identificador da regra.
    private static string BaseId(HealthFinding finding) => finding.Id.Contains(':') ? finding.Id[..finding.Id.LastIndexOf(':')] : finding.Id;

    private static bool SameProbe(HealthFinding finding, string target, string configurationId)
        => !string.IsNullOrWhiteSpace(finding.ProbeTarget) && !string.IsNullOrWhiteSpace(finding.ProbeConfigurationId)
            && string.Equals(finding.ProbeTarget, target, StringComparison.OrdinalIgnoreCase)
            && string.Equals(finding.ProbeConfigurationId, configurationId, StringComparison.Ordinal);

    private static Pointed? PointedBy(HealthFinding finding, HealthSnapshot[] history)
    {
        var parts = BaseId(finding).Split(':');
        if (finding.Source != "rules" || parts.Length != 3 || parts[0] != "process-memory-growing"
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)) return null;
        // Registro anterior à #60 não guarda o nome: usa o da leitura, se o processo ainda aparece no histórico.
        var name = finding.MemoryGrowth?.Name ?? history.SelectMany(s => s.Processes).LastOrDefault(p => Same(p, pid, ticks))?.Name;
        return new(finding, pid, ticks, $"{(name == null ? "processo" : HealthRules.SafeName(name))} PID {pid}", finding.MemoryGrowth);
    }

    /// <summary>
    /// Busca o 1º patamar depois do alerta (#71): a 1ª janela de PlateauWindow de leituras seguidas (mesmo PID e início, sem lacuna
    /// nem falha) com amplitude abaixo de PlateauAmplitude e inclinação abaixo de PlateauSlope. A média dela é a referência, fixa:
    /// a 1ª leitura posterior PlateauRise acima dela fica registrada. Sem leitura no início do alerta, ou com lacuna antes do 1º
    /// patamar, não se sabe qual foi ele: Unavailable, o lado conservador.
    /// </summary>
    internal static PlateauScan Scan(DateTimeOffset alert, IReadOnlyList<ProcessReading> readings, HealthThresholds thresholds)
    {
        if (readings.Count == 0 || readings[0].Time - alert > thresholds.MaxSampleGap) return new(null, null, null, null, "sem as leituras do início do alerta");
        double? reference = null;
        DateTimeOffset? proven = null;
        var segment = new List<(DateTimeOffset Time, double Bytes)>();
        foreach (var reading in readings)
        {
            var broken = reading.Bytes == null || segment.Count > 0 && reading.Time - segment[^1].Time > thresholds.MaxSampleGap;
            if (broken && reference == null) return new(null, null, null, null, "lacuna ou falha de leitura antes do 1º patamar");
            if (reading.Bytes is not { } bytes) continue;
            if (reference is { } fixedLevel)
            {
                if (bytes >= fixedLevel * (1 + PlateauRise)) return new(reference, proven, reading.Time, bytes, null);
                continue;
            }
            segment.Add((reading.Time, bytes));
            var start = segment.FindLastIndex(r => r.Time <= reading.Time - PlateauWindow);
            if (start >= 0 && StableWindow(segment.GetRange(start, segment.Count - start)) is { } level) (reference, proven) = (level, reading.Time);
        }
        return new(reference, proven, null, null, null);
    }

    // Média da janela quando ela é um patamar: amplitude e inclinação (regressão linear, projetada para PlateauWindow) abaixo dos limites.
    private static double? StableWindow(List<(DateTimeOffset Time, double Bytes)> window)
    {
        var level = window.Average(r => r.Bytes);
        if (level <= 0 || (window.Max(r => r.Bytes) - window.Min(r => r.Bytes)) / level >= PlateauAmplitude) return null;
        var origin = window[0].Time;
        var x = window.Select(r => (r.Time - origin).TotalSeconds).ToArray();
        var meanX = x.Average();
        var variance = x.Sum(v => (v - meanX) * (v - meanX));
        if (variance <= 0) return null;
        var slope = window.Select((r, i) => (x[i] - meanX) * (r.Bytes - level)).Sum() / variance;
        return slope * PlateauWindow.TotalSeconds / level >= PlateauSlope ? null : level;
    }

    private static bool Same(ProcessHealth process, int pid, long startTicks) => process.Id == pid && process.StartTime?.UtcTicks == startTicks;

    private static (Hold State, long Bytes) HoldAt(Pointed process, HealthSnapshot sample)
    {
        // Falha de coleta deixa a lista vazia: essa leitura não é evidência sobre o processo.
        if (sample.Processes.Count == 0) return (Hold.Unknown, 0);
        if (sample.Processes.FirstOrDefault(p => Same(p, process.Pid, process.StartTicks)) is { } same)
            return (process.Limit is not { } limit ? Hold.Unknown : same.WorkingSetBytes > limit ? Hold.Holding : Hold.Released, same.WorkingSetBytes);
        // O mesmo PID sem início legível pode ser o mesmo processo.
        if (sample.Processes.Any(p => p.Id == process.Pid && p.StartTime == null)) return (Hold.Unknown, 0);
        // Com outro início, é outro processo: o Windows só reutiliza o PID depois que o apontado encerra.
        if (sample.Processes.Any(p => p.Id == process.Pid)) return (Hold.Released, 0);
        // Fora da lista dos maiores, o processo encerrou ou ocupa no máximo o menor listado.
        return (process.Limit is not { } bound || sample.Processes.Min(p => p.WorkingSetBytes) <= bound ? Hold.Released : Hold.Unknown, 0);
    }

    // Mediana dos intervalos entre as leituras, limitada ao intervalo máximo: leituras espaçadas (o Nexo fechado) não alargam a ligação.
    private static TimeSpan? Cadence(HealthSnapshot[] ordered, HealthThresholds thresholds)
    {
        var gaps = ordered.Zip(ordered.Skip(1), (first, second) => second.Time - first.Time).Order().ToArray();
        if (gaps.Length == 0) return null;
        var median = gaps[(gaps.Length - 1) / 2];
        return median < thresholds.MaxSampleGap ? median : thresholds.MaxSampleGap;
    }

    private static string Megabytes(long bytes) => (bytes / (1024.0 * 1024)).ToString("F0", CultureInfo.InvariantCulture);

    private static Criterion? CriterionFor(string id, HealthFinding finding, HealthThresholds limits, Pointed[] holders)
    {
        var separator = id.IndexOf(':');
        var (rule, subject) = separator < 0 ? (id, "") : (id[..separator], id[(separator + 1)..]);
        return rule switch
        {
            "cpu-high" => new(limits.CpuSamples, limits.MaxSampleGap, s => s.CpuPercent < limits.CpuHighPercent),
            // A compressão do Windows reduz o uso total sem que o processo apontado libere a memória.
            "memory-growing" => new(MemorySamples, limits.MaxSampleGap, s => s.MemoryUsedPercent < limits.MemoryGrowthFinalPercent
                && holders.All(holder => HoldAt(holder, s).State == Hold.Released)),
            "process-memory-growing" when holders is [{ Limit: not null } self] => new(MemorySamples, limits.MaxSampleGap, s => HoldAt(self, s).State == Hold.Released),
            "network-none" => new(limits.NetworkSamples, limits.MaxSampleGap, s => s.Connectivity == "local"),
            "network-external-unconfirmed" when !string.IsNullOrWhiteSpace(finding.ProbeTarget) && !string.IsNullOrWhiteSpace(finding.ProbeConfigurationId)
                => new(limits.NetworkSamples, limits.ExternalProbeMaxSampleGap, s => s.ExternalProbe?.State == "ok"
                    && SameProbe(finding, s.ExternalProbe.Target, s.ExternalProbe.ConfigurationId)),
            "disk-low" => new(limits.OtherSamples, limits.MaxSampleGap, s => s.Disks.FirstOrDefault(d => d.Name.Equals(subject, StringComparison.OrdinalIgnoreCase)) is { TotalBytes: > 0 } disk
                && 100.0 * disk.FreeBytes / disk.TotalBytes >= limits.DiskLowFreePercent),
            "service-stopped" => new(limits.OtherSamples, limits.MaxSampleGap, s => s.Services.FirstOrDefault(x => x.Name == subject) is { } service
                && !HealthRules.UnexpectedlyStopped(service)),
            "adapter-disabled" => new(limits.OtherSamples, limits.MaxSampleGap, s => s.Adapters.FirstOrDefault(a => a.Id == subject) is { } adapter
                && (s.Connectivity == "local" || !adapter.Status.Equals("Disabled", StringComparison.OrdinalIgnoreCase))),
            _ => null
        };
    }
}

public static class HealthReportPresentation
{
    /// <summary>
    /// Serviços automáticos parados com saída normal que não contam como falha (#57): ficam visíveis no painel, mas não
    /// entram no relatório do modelo. Serviço com ocorrência persistente fica de fora, porque já aparece como alerta.
    /// </summary>
    public static string? CleanStopNotice(HealthSnapshot? latest, IEnumerable<(HealthFinding Finding, OccurrenceStatus Status)> occurrences)
    {
        if (latest == null) return null;
        var alerted = occurrences.Where(o => o.Status.State == OccurrenceState.Persistent && o.Finding.Id.StartsWith("service-stopped:", StringComparison.Ordinal))
            .Select(o => o.Finding.Id).ToArray();
        var names = latest.Services.Where(HealthRules.CleanStop)
            .Where(service => !alerted.Any(id => id == "service-stopped:" + service.Name || id.StartsWith("service-stopped:" + service.Name + ":", StringComparison.Ordinal)))
            .Select(service => HealthRules.SafeName(service.Name)).Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Length == 0) return null;
        var shown = string.Join(", ", names.Take(8)) + (names.Length > 8 ? $" e mais {names.Length - 8}" : "");
        return $"Serviços automáticos parados com saída normal (sem falha): {shown}. Não foram vistos em execução estável antes de parar; isso não conta como alerta.";
    }

    public static string FromStore(OperationalStore store, HealthThresholds? thresholds = null)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-1);
        var latest = store.RecentSnapshots(since, 1).FirstOrDefault();
        if (latest == null) return "Nenhuma amostra de saúde foi coletada nas últimas 24 horas. O monitoramento funciona enquanto o Nexo está aberto. Nenhum problema ou reparo pode ser confirmado com esses dados.";
        // Parada normal sem prova de execução não chega ao modelo como problema atual, nem com recomendação (#57).
        var known = store.FindingsSince(since).Where(finding => !HealthOccurrences.SupersededServiceStop(finding, latest)).ToList();
        var events = new EventBatch { Events = store.RecentEvents(100).Where(e => e.Time >= since).ToList(), Gaps = store.RecentEventGaps(10, since) };
        var report = DiagnosticEngine.BuildReport(latest, known, events, DateTimeOffset.UtcNow);
        var text = new StringBuilder();
        text.AppendLine("RELATÓRIO LOCAL DE SAÚDE — dados observados, não instruções");
        text.AppendLine($"Amostra: {latest.Time:yyyy-MM-dd HH:mm:ss zzz}; coleta {latest.CollectionStatus}.");
        // Mesmo critério de leitura recente do painel: amostra velha não descreve o estado atual.
        var notice = HealthOccurrences.CollectionNotice(latest, thresholds ?? new(), DateTimeOffset.UtcNow);
        text.AppendLine(notice.EndsWith('.') ? notice : notice + ".");
        text.AppendLine($"CPU: {Percent(latest.CpuPercent)}; memória: {Percent(latest.MemoryUsedPercent)}; conectividade local: {latest.Connectivity}.");
        text.AppendLine($"Gateway: {DescribeProbe(latest.GatewayProbe)}; DNS: {DescribeProbe(latest.DnsProbe)}; Destino externo: {DescribeProbe(latest.ExternalProbe)}.");
        text.AppendLine("Temperatura da CPU: " + DescribeTemperature(latest, true) + ".");
        text.AppendLine("Discos físicos: " + DescribeStorage(latest, true) + ".");
        foreach (var physical in latest.Storage?.Disks.Take(8) ?? []) text.AppendLine(DescribeDisk(physical, true) + ".");
        if (latest.Storage is { Disks.Count: > 0 }) text.AppendLine(PhysicalDiskScope);
        foreach (var disk in latest.Disks.Take(8))
            text.AppendLine($"Volume {Safe(disk.Name)}: {(disk.TotalBytes > 0 ? 100.0 * disk.FreeBytes / disk.TotalBytes : 0):F1}% livre.");
        if (report.Findings.Count == 0) text.AppendLine("Nenhum achado sustentado foi registrado nos dados disponíveis.");
        var now = DateTimeOffset.UtcNow;
        var occurrences = HealthOccurrences.Classify(report.Findings, store.RecentSnapshots(since, 20), thresholds ?? new(), now);
        if (occurrences.Count > 10) text.AppendLine($"Mostrando 10 de {occurrences.Count} ocorrências, as críticas primeiro; as demais foram omitidas deste relatório.");
        // As ocorrências encerradas (resolvidas ou de estado atual desconhecido) ficam fora da parte atual (#88): no PC, o modelo
        // leu como atual uma ocorrência "resolvida" do llama-server. A separação é pelo estado, não pelo texto dele, e qualquer
        // outro estado, inclusive um novo, fica na parte atual. O patamar estável da #71 é atual e informativo: a gravidade
        // mostrada é "info" e a recomendação é a dele ("nenhuma ação recomendada"). A parte encerrada não traz recomendação,
        // para não puxar uma ação sobre algo que não está acontecendo.
        var shown = occurrences.OrderByDescending(o => o.Finding.Severity == "critical").ThenByDescending(o => o.Finding.Time).Take(10).ToArray();
        static bool Closed(OccurrenceStatus status) => status.State is OccurrenceState.Resolved or OccurrenceState.Unknown;
        var currentPart = shown.Where(o => !Closed(o.Status)).ToArray();
        var otherPart = shown.Where(o => Closed(o.Status)).ToArray();
        if (currentPart.Length > 0) text.AppendLine(CurrentHeader);
        foreach (var (finding, status) in currentPart) text.AppendLine(Line(finding, status, true));
        if (otherPart.Length > 0) text.AppendLine(OtherHeader);
        foreach (var (finding, status) in otherPart) text.AppendLine(Line(finding, status, false));
        foreach (var limitation in report.Limitations.Take(10)) text.AppendLine("Limitação: " + Safe(limitation));
        text.AppendLine("Nenhuma alteração foi executada por este relatório. Confirme a função afetada antes de declarar recuperação.");
        return text.ToString()[..Math.Min(text.Length, 7000)];
    }
    internal const string CurrentHeader = "OCORRÊNCIAS ATUAIS (nas leituras atuais; as marcadas como informativas não são falha ativa):";
    internal const string OtherHeader = "OCORRÊNCIAS QUE NÃO SÃO ATUAIS (resolvidas ou com estado atual desconhecido): não as apresente como problemas presentes nem proponha ação para elas.";
    private static string Line(HealthFinding finding, OccurrenceStatus status, bool current) =>
        $"Achado {Safe(finding.Category)} ({Safe(HealthOccurrences.ShownSeverity(finding, status))}), {finding.Time:yyyy-MM-dd HH:mm zzz}, {char.ToLowerInvariant(status.Label[0]) + status.Label[1..]}: {Safe(finding.Problem)}. Evidência: {Safe(finding.Evidence)}. Causa: hipótese, {Safe(finding.Cause)}."
        + (current ? $" Recomendação: {Safe(finding.Recommendation)}." : "") + $" Confiança {Math.Clamp(finding.Confidence, 0, 1):P0}; confirmado: não.";
    public static string InterventionState(InterventionRecord intervention) => intervention.State switch
    {
        _ when PendingOperations.IsWaiting(intervention) => "em andamento ou incerto, aguardando o resultado do Windows",
        "unknown" => "incerto, sem resultado verificável",
        _ => intervention.State
    };

    private static string Percent(double? value) => value.HasValue ? value.Value.ToString("F0") + "%" : "indisponível";
    public const string StorageScope = "O estado dos discos é o informado pelo Windows Storage (HealthStatus e OperationalStatus); não é uma leitura SMART completa nem inclui contadores de confiabilidade.";
    // Discos físicos e volumes vêm de consultas diferentes (#19/#93): no PC, o modelo chamou o T: (um VHDX) de "USB DISK 2.0".
    public const string PhysicalDiskScope = "Esta leitura não liga os discos físicos às letras de unidade (C:, D:...); não associe uma letra a um disco pelo nome, pelo tipo ou pelo tamanho.";
    // Horário da leitura; com a data quando a leitura não é do dia de hoje, para uma amostra antiga não parecer atual.
    private static string ReadAt(DateTimeOffset time, string verb, DateTimeOffset? now)
    {
        var local = time.ToLocalTime();
        var today = (now ?? DateTimeOffset.Now).ToLocalTime().Date;
        return local.Date == today
            ? $", {verb} às {local.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}"
            : $", {verb} em {local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} às {local.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}";
    }
    public static string DescribeTemperature(HealthSnapshot snapshot, bool sanitize = false, DateTimeOffset? now = null)
    {
        string Clean(string value) => sanitize ? Safe(value) : value;
        var reading = snapshot.CpuTemperature;
        if (reading == null) return snapshot.CpuTemperatureC is { } legacy ? legacy.ToString("F0", CultureInfo.InvariantCulture) + " °C, origem não registrada" : "indisponível, leitura anterior à coleta de sensores";
        var at = reading.Time is { } time ? ReadAt(time, "lida", now) : "";
        return reading.State == "read" && reading.Celsius is { } celsius
            ? celsius.ToString("F0", CultureInfo.InvariantCulture) + " °C, sensor " + Clean(reading.Sensor) + ", fonte " + reading.Source + at
            : "indisponível, " + Clean(reading.Detail).TrimEnd('.') + at;
    }
    public static string StorageLabel(string state) => state switch
    {
        "healthy" => "sem alerta informado pelo Windows",
        "warning" => "atenção informada pelo Windows",
        "unhealthy" => "falha informada pelo Windows",
        "unknown" => "não confirmado",
        _ => "indisponível"
    };
    public static string DescribeStorage(HealthSnapshot snapshot, bool sanitize = false, DateTimeOffset? now = null)
    {
        string Clean(string value) => sanitize ? Safe(value) : value;
        var storage = snapshot.Storage;
        if (storage == null) return snapshot.StorageHealth == "unavailable" ? "indisponível, leitura anterior à coleta do estado dos discos" : StorageLabel(snapshot.StorageHealth) + ", origem não registrada";
        var at = ReadAt(storage.Time, "lido", now);
        if (storage.Total == 0) return "indisponível, " + Clean(storage.Detail).TrimEnd('.') + at;
        return StorageLabel(storage.State) + $", {storage.Disks.Count} de {storage.Total} discos físicos, fonte " + storage.Source + at + (storage.Detail.Length > 0 ? ". " + Clean(storage.Detail).TrimEnd('.') : "");
    }
    public static string DescribeDisk(PhysicalDiskReading disk, bool sanitize = false)
    {
        string Clean(string value) => sanitize ? Safe(value) : value;
        var kind = string.Join(", ", new[] { disk.MediaType, disk.BusType }.Where(v => v.Length > 0 && !v.Equals("Unspecified", StringComparison.OrdinalIgnoreCase)).Select(Clean));
        // No relatório ao modelo o nome vai entre aspas: é texto do fabricante, não instrução nem parte da frase.
        var name = sanitize ? "“" + Clean(disk.Name) + "”" : disk.Name;
        return $"Disco {Clean(disk.Device)} {name}{(kind.Length > 0 ? " (" + kind + ")" : "")}: {StorageLabel(disk.State)}; HealthStatus {Clean(disk.Health)}, OperationalStatus {(disk.OperationalStatus.Length == 0 ? "não informado" : string.Join(", ", disk.OperationalStatus.Select(Clean)))}";
    }
    public static string DescribeProbe(NetworkProbeReading? reading)
    {
        if (reading == null) return "indisponível";
        var state = reading.State switch
        {
            "disabled" => "desligado",
            "ok" => "respondeu",
            "no_response" => "sem resposta",
            "unexpected" => "resposta inesperada",
            _ => "indisponível"
        };
        return state + (string.IsNullOrWhiteSpace(reading.Target) ? "" : " (" + Safe(reading.Target) + ")");
    }
    private static string Safe(string value) => new string(value.Take(160).Select(c => char.IsLetterOrDigit(c) || c is ' ' or '.' or ':' or '%' or '/' or '-' or '_' ? c : '_').ToArray());
}
