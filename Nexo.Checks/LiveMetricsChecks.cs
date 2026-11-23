using System.Globalization;
using Nexo.Support;

namespace Nexo.Checks;

// #141: métricas ao vivo de CPU, memória, disco e rede com uma fonte simulada. Cobre o cálculo entre duas amostras, a primeira
// amostra, a fonte indisponível sem derrubar as outras, contador que zera, pausa curta e longa, o limite do histórico, as
// unidades e a separação do histórico operacional (nada de operations.db).
internal static class LiveMetricsChecks
{
    private sealed class FakeSource : ILiveMetricsSource
    {
        public int ProcessorCount { get; set; } = 4;
        public CpuTimes Cpu;
        public MemoryStatus Memory = new(16UL << 30, 4UL << 30);
        public DiskCounters Disk = new(LiveStatus.Waiting);
        public List<AdapterBytes> Adapters = [new("eth", "Ethernet", 1000, 500)];
        public TimeSpan ProcessCpu;
        public bool CpuFails, MemoryFails, DiskFails, NetworkFails, ProcessFails;
        public CpuTimes ReadCpuTimes() => CpuFails ? throw new InvalidOperationException("GetSystemTimes falhou") : Cpu;
        public MemoryStatus ReadMemory() => MemoryFails ? throw new InvalidOperationException("sem memória") : Memory;
        public DiskCounters ReadDisk() => DiskFails ? throw new InvalidOperationException("PDH falhou") : Disk;
        public IReadOnlyList<AdapterBytes> ReadAdapters() => NetworkFails ? throw new InvalidOperationException("rede falhou") : Adapters.ToList();
        public TimeSpan ReadProcessCpuTime() => ProcessFails ? throw new InvalidOperationException("processo") : ProcessCpu;
        public void Dispose() { }
    }

    private static readonly DateTimeOffset Start = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    internal static void Run(Action<bool, string> assert)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
            FirstAndSecondSample(assert);
            IndependentFailures(assert);
            CounterEdges(assert);
            PauseAndResume(assert);
            HistoryLimit(assert);
            Units(assert);
            Separation(assert);
            DiskAggregation(assert);
            if (OperatingSystem.IsWindows()) RealSource(assert);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static void FirstAndSecondSample(Action<bool, string> assert)
    {
        var source = new FakeSource { Cpu = new CpuTimes(1000, 3000, 2000), ProcessCpu = TimeSpan.FromSeconds(10) };
        var sampler = new LiveMetricsSampler(source);
        var first = sampler.Sample(Start);
        assert(first.Cpu.Status == LiveStatus.Waiting && first.Cpu.Value == null, "#141: a 1ª amostra de CPU espera a segunda (sem valor inventado)");
        assert(first.Network.Status == LiveStatus.Waiting && first.NexoCpu.Status == LiveStatus.Waiting, "#141: rede e CPU do Nexo também esperam a 2ª amostra");
        assert(first.DiskActive.Status == LiveStatus.Waiting, "#141: o disco espera a 2ª coleta do PDH");
        assert(first.Memory.Status == LiveStatus.Ok && Math.Abs(first.Memory.Value!.Value - 75) < 0.001 && first.MemoryUsedBytes == 12UL << 30 && first.MemoryTotalBytes == 16UL << 30,
            "#141: memória sai já na 1ª amostra: 12 de 16 GB = 75%");
        assert(LiveMetricsSampler.FormatPercent(first.Cpu) == "medindo…", "#141: CPU na 1ª amostra aparece como \"medindo…\"");

        // 2 s depois: ocioso +300, kernel (com o ocioso) +500, usuário +500 → 1000 de tempo total, 700 ocupado = 70%.
        source.Cpu = new CpuTimes(1300, 3500, 2500);
        source.Disk = new DiskCounters(LiveStatus.Ok, 80, 2048, 1024);
        source.Adapters = [new("eth", "Ethernet", 3000, 1500)];
        source.ProcessCpu = TimeSpan.FromSeconds(10.2);
        var second = sampler.Sample(Start.AddSeconds(2));
        assert(second.Cpu.Status == LiveStatus.Ok && Math.Abs(second.Cpu.Value!.Value - 70) < 0.001, "#141: CPU = 1 − ocioso/(kernel+usuário) entre as duas amostras (70%)");
        assert(second.DiskActive.Status == LiveStatus.Ok && Math.Abs(second.DiskActive.Value!.Value - 20) < 0.001 && second.DiskReadBytesPerSecond == 2048 && second.DiskWriteBytesPerSecond == 1024,
            "#141: disco ativo = 100 − % ocioso (20%), leitura e gravação por segundo vêm do PDH");
        assert(second.Network.Status == LiveStatus.Ok && second.ReceiveBitsPerSecond == 8000 && second.SendBitsPerSecond == 4000 && second.Adapter == "Ethernet",
            "#141: rede = bytes do adaptador entre amostras × 8 ÷ segundos (2000 B em 2 s = 8 Kbps recebidos, 4 Kbps enviados)");
        assert(second.NexoCpu.Status == LiveStatus.Ok && Math.Abs(second.NexoCpu.Value!.Value - 2.5) < 0.001,
            "#141: CPU do próprio Nexo = tempo de processador do processo ÷ (segundos × núcleos): 0,2 s em 2 s com 4 núcleos = 2,5%");
        assert(sampler.CpuHistory.Count == 2 && sampler.CpuHistory[0] == null && sampler.CpuHistory[1] == 70, "#141: o histórico guarda a lacuna da 1ª amostra e o valor da 2ª");
    }

    private static void IndependentFailures(Action<bool, string> assert)
    {
        var source = new FakeSource { Cpu = new CpuTimes(1000, 3000, 2000), DiskFails = true, NetworkFails = true };
        var sampler = new LiveMetricsSampler(source);
        sampler.Sample(Start);
        source.Cpu = new CpuTimes(1500, 4000, 3000);
        var reading = sampler.Sample(Start.AddSeconds(1));
        assert(reading.DiskActive.Status == LiveStatus.Unavailable && reading.DiskActive.Detail == "PDH falhou", "#141: exceção do disco vira indisponível, com o motivo");
        assert(reading.Network.Status == LiveStatus.Unavailable && reading.ReceiveBitsPerSecond == null, "#141: exceção da rede vira indisponível");
        assert(reading.Cpu.Status == LiveStatus.Ok && reading.Memory.Status == LiveStatus.Ok, "#141: disco e rede indisponíveis não derrubam CPU e memória");
        assert(LiveMetricsSampler.FormatPercent(reading.DiskActive) == "indisponível", "#141: métrica indisponível aparece como \"indisponível\"");

        source.DiskFails = false; source.Disk = new DiskCounters(LiveStatus.Unavailable, Error: "contador de disco ausente");
        source.CpuFails = true; source.MemoryFails = true;
        var absent = sampler.Sample(Start.AddSeconds(2));
        assert(absent.DiskActive.Status == LiveStatus.Unavailable && absent.DiskActive.Detail == "contador de disco ausente", "#141: contador de disco ausente (PDH) vira indisponível com o motivo");
        assert(absent.Cpu.Status == LiveStatus.Unavailable && absent.Memory.Status == LiveStatus.Unavailable && absent.NexoCpu.Status == LiveStatus.Ok,
            "#141: CPU e memória indisponíveis não derrubam o uso de CPU do Nexo");
        // Sem recomeçar, a amostra seguinte seria comparada com a de antes da falha (75%), cobrindo o tempo em que ela falhou.
        source.CpuFails = false; source.Cpu = new CpuTimes(2000, 5000, 4000);
        var after = sampler.Sample(Start.AddSeconds(3));
        assert(after.Cpu.Status == LiveStatus.Waiting, "#141: depois de uma falha, a CPU recomeça a linha de base (espera a próxima amostra)");

        source.MemoryFails = false; source.Memory = new MemoryStatus(0, 0);
        assert(sampler.Sample(Start.AddSeconds(4)).Memory.Status == LiveStatus.Unavailable, "#141: memória total zero é leitura inválida, não 0% nem 100%");
        source.Memory = new MemoryStatus(8, 9);
        assert(sampler.Sample(Start.AddSeconds(5)).Memory.Status == LiveStatus.Unavailable, "#141: memória disponível maior que a total é leitura inválida");

        // Falha ao ler o tempo de CPU do próprio processo: só o custo do Nexo fica indisponível, e a linha de base recomeça.
        var own = new FakeSource { Cpu = new CpuTimes(1000, 3000, 2000), ProcessCpu = TimeSpan.FromSeconds(10) };
        var ownSampler = new LiveMetricsSampler(own);
        ownSampler.Sample(Start);
        own.Cpu = new CpuTimes(1500, 4000, 3000); own.ProcessFails = true;
        var failed = ownSampler.Sample(Start.AddSeconds(1));
        assert(failed.NexoCpu.Status == LiveStatus.Unavailable && failed.NexoCpu.Detail == "processo" && failed.Cpu.Status == LiveStatus.Ok,
            "#141: falha ao ler a CPU do Nexo vira indisponível, sem derrubar a CPU do PC");
        own.ProcessFails = false; own.ProcessCpu = TimeSpan.FromSeconds(10.5); own.Cpu = new CpuTimes(2000, 5000, 4000);
        assert(ownSampler.Sample(Start.AddSeconds(2)).NexoCpu.Status == LiveStatus.Waiting,
            "#141: depois da falha, a CPU do Nexo recomeça a linha de base (espera a próxima amostra)");
    }

    private static void CounterEdges(Action<bool, string> assert)
    {
        var source = new FakeSource { Cpu = new CpuTimes(1000, 3000, 2000) };
        var sampler = new LiveMetricsSampler(source);
        sampler.Sample(Start);
        // Kernel que volta: sem a checagem, a subtração sem sinal daria um total enorme e uma CPU perto de 100%.
        source.Cpu = new CpuTimes(1100, 2900, 2600);
        assert(sampler.Sample(Start.AddSeconds(1)).Cpu.Status == LiveStatus.Waiting, "#141: contador de CPU que volta não gera valor");
        source.Cpu = new CpuTimes(1100, 2900, 2600);
        assert(sampler.Sample(Start.AddSeconds(2)).Cpu.Status == LiveStatus.Waiting, "#141: CPU sem tempo decorrido entre amostras espera a próxima");
        source.Cpu = new CpuTimes(2100, 3400, 2600);
        assert(sampler.Sample(Start.AddSeconds(3)).Cpu.Status == LiveStatus.Waiting, "#141: ocioso maior que o total (leitura incoerente) não gera valor");
        source.Cpu = new CpuTimes(2100, 3900, 3100);
        var busy = sampler.Sample(Start.AddSeconds(4));
        assert(busy.Cpu.Status == LiveStatus.Ok && busy.Cpu.Value == 100, "#141: sem tempo ocioso, a CPU é 100%");

        source.Disk = new DiskCounters(LiveStatus.Ok, 104.6, 0, 0);
        assert(sampler.Sample(Start.AddSeconds(5)).DiskActive.Value == 0, "#141: % ocioso acima de 100 (arredondamento do PDH) vira 0% ativo, não negativo");
        source.Disk = new DiskCounters(LiveStatus.Ok, double.NaN, 0, 0);
        assert(sampler.Sample(Start.AddSeconds(6)).DiskActive.Status == LiveStatus.Unavailable, "#141: contador de disco não numérico vira indisponível");

        // Rede: adaptador novo fica fora da conta; adaptador que zerou (caiu e voltou) também; só o comparável conta.
        var network = new FakeSource { Adapters = [new("eth", "Ethernet", 1000, 1000)] };
        var net = new LiveMetricsSampler(network);
        net.Sample(Start);
        network.Adapters = [new("eth", "Ethernet", 2000, 1000), new("wifi", "Wi-Fi", 900000, 900000)];
        var withNew = net.Sample(Start.AddSeconds(1));
        assert(withNew.ReceiveBitsPerSecond == 8000 && withNew.SendBitsPerSecond == 0 && withNew.Adapter == "Ethernet", "#141: adaptador que apareceu nesta amostra não entra na taxa");
        network.Adapters = [new("eth", "Ethernet", 10, 10), new("wifi", "Wi-Fi", 901000, 900000)];
        var withReset = net.Sample(Start.AddSeconds(2));
        assert(withReset.ReceiveBitsPerSecond == 8000 && withReset.Adapter == "Wi-Fi", "#141: adaptador com contador zerado fica fora; o outro segue");
        network.Adapters = [new("eth", "Ethernet", 5, 5)];
        assert(net.Sample(Start.AddSeconds(3)).Network.Status == LiveStatus.Waiting, "#141: sem nenhum adaptador comparável, a rede espera a próxima amostra");

        // Hyper-V com switch externo: o mesmo download passa pela placa física e pela vEthernet. Somar mostraria o dobro.
        var hyperV = new FakeSource { Adapters = [new("nic", "Ethernet", 0, 0), new("veth", "vEthernet (Externo)", 0, 0), new("wifi", "Wi-Fi", 0, 0)] };
        var host = new LiveMetricsSampler(hyperV);
        host.Sample(Start);
        hyperV.Adapters = [new("nic", "Ethernet", 1_000_000, 10_000), new("veth", "vEthernet (Externo)", 990_000, 9_000), new("wifi", "Wi-Fi", 5_000, 1_000)];
        var mirrored = host.Sample(Start.AddSeconds(1));
        assert(mirrored.ReceiveBitsPerSecond == 8_000_000 && mirrored.SendBitsPerSecond == 80_000 && mirrored.Adapter == "Ethernet"
            && host.NetworkHistory[^1] == 8_080_000,
            "#141: a rede mostra o adaptador mais ativo, não a soma (vEthernet do Hyper-V e placa física repetem o mesmo tráfego)");
        network.Adapters = [];
        assert(net.Sample(Start.AddSeconds(4)).Network.Status == LiveStatus.Unavailable, "#141: sem adaptador ativo, a rede fica indisponível com o motivo");
    }

    private static void PauseAndResume(Action<bool, string> assert)
    {
        var source = new FakeSource { Cpu = new CpuTimes(1000, 3000, 2000) };
        var sampler = new LiveMetricsSampler(source);
        sampler.Sample(Start);
        source.Cpu = new CpuTimes(1500, 4000, 3000);
        sampler.Sample(Start.AddSeconds(1));
        sampler.Pause(Start.AddSeconds(1));
        assert(sampler.Paused, "#141: pausa registrada (janela minimizada, escondida ou outra tela)");
        sampler.Resume(Start.AddSeconds(2));
        assert(!sampler.Paused && sampler.CpuHistory.Count == 2, "#141: pausa curta (a tela recriada pelo monitor) mantém o histórico");
        source.Cpu = new CpuTimes(2000, 5000, 4000);
        assert(sampler.Sample(Start.AddSeconds(3)).Cpu.Status == LiveStatus.Ok, "#141: depois de pausa curta, a CPU continua da mesma linha de base");

        sampler.Pause(Start.AddSeconds(3));
        sampler.Pause(Start.AddSeconds(9));
        sampler.Resume(Start.AddSeconds(3) + LiveMetricsSampler.ResetAfterPause);
        assert(sampler.CpuHistory.Count == 0 && sampler.NetworkHistory.Count == 0, "#141: pausa longa descarta o histórico (o gráfico não liga pontos separados pela pausa); a 2ª pausa não adia o início");
        source.Cpu = new CpuTimes(9000, 20000, 9000);
        var resumed = sampler.Sample(Start.AddMinutes(5));
        assert(resumed.Cpu.Status == LiveStatus.Waiting && resumed.Network.Status == LiveStatus.Waiting,
            "#141: depois de pausa longa, a 1ª leitura não é a média do tempo parado: espera a próxima amostra");
        sampler.Resume(Start.AddMinutes(6));
        assert(sampler.CpuHistory.Count == 1, "#141: retomar sem pausa não apaga nada");
    }

    private static void HistoryLimit(Action<bool, string> assert)
    {
        var source = new FakeSource();
        var sampler = new LiveMetricsSampler(source);
        for (var second = 0; second < LiveMetricsSampler.HistoryCapacity + 15; second++) sampler.Sample(Start.AddSeconds(second));
        assert(sampler.MemoryHistory.Count == LiveMetricsSampler.HistoryCapacity && LiveMetricsSampler.HistoryCapacity == 60, "#141: o histórico guarda só o último minuto (60 amostras)");
        assert(LiveMetricsSampler.Interval >= TimeSpan.FromSeconds(1) && LiveMetricsSampler.Interval <= TimeSpan.FromSeconds(2), "#141: a leitura ao vivo roda a cada 1–2 s");
        assert(LiveMetricsSampler.ResetAfterPause > LiveMetricsSampler.Interval, "#141: a pausa que zera o gráfico é maior que o intervalo de leitura");
    }

    private static void Units(Action<bool, string> assert)
    {
        assert(LiveMetricsSampler.FormatBytesPerSecond(0) == "0 B/s" && LiveMetricsSampler.FormatBytesPerSecond(1536) == "1,5 KB/s"
            && LiveMetricsSampler.FormatBytesPerSecond(5 * 1024 * 1024) == "5,0 MB/s", "#141: disco em B/s, KB/s e MB/s, base 1024, como no Gerenciador de Tarefas");
        assert(LiveMetricsSampler.FormatBitsPerSecond(999) == "999 bps" && LiveMetricsSampler.FormatBitsPerSecond(8000) == "8,0 Kbps"
            && LiveMetricsSampler.FormatBitsPerSecond(12_500_000) == "12,5 Mbps", "#141: rede em bps, Kbps e Mbps, base 1000, como no Gerenciador de Tarefas");
        assert(LiveMetricsSampler.FormatBytesPerSecond(null) == "—" && LiveMetricsSampler.FormatBitsPerSecond(null) == "—", "#141: taxa sem valor aparece como travessão");
        assert(LiveMetricsSampler.FormatPercent(new LiveMetric(LiveStatus.Ok, 42.4)) == "42%", "#141: percentual sem casas decimais");
    }

    private static void Separation(Action<bool, string> assert)
    {
        var root = RepositoryRoot();
        assert(root != null, "#141: repositório encontrado para conferir a separação do histórico");
        if (root == null) return;
        // Só o código conta: os comentários explicam justamente a separação (citam o operations.db e o monitor).
        var live = Code(Path.Combine(root, "Nexo.Support", "LiveMetrics.cs")) + Code(Path.Combine(root, "Nexo.Support", "LiveMetricsPanel.cs"));
        assert(!live.Contains("OperationalStore") && !live.Contains("SaveSnapshot") && !live.Contains("operations.db") && !live.Contains("Sqlite"),
            "#141: a leitura ao vivo não grava no histórico operacional (operations.db)");
        assert(!live.Contains("MonitoringEngine") && !live.Contains("HealthRules"), "#141: a leitura ao vivo não usa nem muda o monitor de saúde");
        assert(!live.Contains("powershell", StringComparison.OrdinalIgnoreCase) && !live.Contains("ManagementObject"),
            "#141: a leitura ao vivo usa APIs nativas, sem PowerShell nem WMI a cada segundo");
        var window = File.ReadAllText(Path.Combine(root, "Nexo.Support", "MainWindow.xaml.cs"));
        var health = window.IndexOf("private void Health()", StringComparison.Ordinal);
        assert(health >= 0 && window.IndexOf("LiveMetricsSection();", health, StringComparison.Ordinal) is var call && call > health && call < window.IndexOf("if (healthStore == null)", health, StringComparison.Ordinal),
            "#141: a tela Saúde mostra o painel ao vivo mesmo sem o histórico operacional");
    }

    private static void DiskAggregation(Action<bool, string> assert)
    {
        // Um disco a 100% (0% ocioso) ao lado de um parado: na _Total do % Idle Time ele sumiria (100 + 0 ≥ 100 → 0% ativo).
        assert(WindowsLiveMetricsSource.BusiestDiskIdle([("0 C:", 0, true), ("1 D:", 100, true), ("_Total", 100, true)]) == 0,
            "#141: o tempo ativo do disco é o do disco mais ocupado, e a _Total fica fora");
        assert(WindowsLiveMetricsSource.BusiestDiskIdle([("0 C:", 35, false), ("1 D:", 80, true)]) == 80,
            "#141: instância com status inválido no PDH fica fora do tempo ativo");
        assert(WindowsLiveMetricsSource.BusiestDiskIdle([("0 C:", double.NaN, true), ("1 D:", 60, true)]) == 60,
            "#141: valor não numérico de uma instância fica fora");
        assert(WindowsLiveMetricsSource.BusiestDiskIdle([("_TOTAL", 40, true)]) == null && WindowsLiveMetricsSource.BusiestDiskIdle([]) == null,
            "#141: sem instância de disco válida (só a _Total, ou nenhuma), o tempo ativo espera a próxima coleta");

        // Fechar a janela libera a consulta do PDH; uma amostra que chegue depois não pode usar a consulta liberada.
        var closed = new WindowsLiveMetricsSource();
        closed.Dispose();
        var refused = false;
        try { closed.ReadDisk(); } catch (ObjectDisposedException) { refused = true; }
        assert(refused && closed.DiskMode == null, "#141: depois do Dispose, a leitura de disco é recusada em vez de usar a consulta do PDH liberada");
    }

    // Só no Windows (CI e PC): a fonte real responde, com valores no intervalo e sem travar a 2ª leitura.
    private static void RealSource(Action<bool, string> assert)
    {
        using var source = new WindowsLiveMetricsSource();
        var sampler = new LiveMetricsSampler(source);
        var start = DateTimeOffset.UtcNow;
        LiveReading reading = sampler.Sample(start);
        for (var round = 1; round <= 3; round++)
        {
            Thread.Sleep(1100);
            reading = sampler.Sample(DateTimeOffset.UtcNow);
        }
        static bool InRange(LiveMetric metric) => metric.Status == LiveStatus.Ok && metric.Value is >= 0 and <= 100;
        assert(InRange(reading.Cpu), "#141: no Windows, a CPU pelo GetSystemTimes sai entre 0 e 100% (" + reading.Cpu + ")");
        assert(InRange(reading.Memory) && reading.MemoryTotalBytes > 0, "#141: no Windows, a memória pelo GlobalMemoryStatusEx sai entre 0 e 100% (" + reading.Memory + ")");
        assert(reading.DiskActive.Status == LiveStatus.Unavailable || InRange(reading.DiskActive) && reading.DiskReadBytesPerSecond >= 0 && reading.DiskWriteBytesPerSecond >= 0,
            "#141: no Windows, o disco pelo PDH sai entre 0 e 100% de tempo ativo, ou indisponível com o motivo (" + reading.DiskActive + ")");
        assert(reading.Network.Status != LiveStatus.Ok || reading.ReceiveBitsPerSecond >= 0 && reading.SendBitsPerSecond >= 0,
            "#141: no Windows, a rede sai com taxas não negativas (" + reading.Network + ")");
        Console.WriteLine("#141 fonte real: CPU " + reading.Cpu.Value + "%, memória " + reading.Memory.Value + "%, disco " + reading.DiskActive.Status
            + " (" + (source.DiskMode ?? "sem contador") + ", " + reading.DiskActive.Value + "%), rede " + reading.Network.Status + " (" + reading.Adapter + ").");
    }

    private static string Code(string path) => string.Join("\n", File.ReadAllLines(path).Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    // Sobe a partir do binário (dotnet run, CI) e, se o binário foi compilado fora do repositório (--artifacts-path), da pasta atual.
    private static string? RepositoryRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "Nexo.Support", "LiveMetrics.cs"))) return directory.FullName;
        return null;
    }
}
