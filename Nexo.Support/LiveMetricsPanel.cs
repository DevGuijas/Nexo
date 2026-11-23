using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Nexo.Support;

/// <summary>
/// Cartão "Agora" da tela Saúde do PC (#141). Lê a cada segundo enquanto o próprio cartão está visível e a janela está visível e
/// não minimizada; fora disso (outra página, inclusive o Assistente, janela minimizada ou na bandeja) o temporizador para. A MainWindow mantém uma única instância: a tela é recriada a cada leitura do monitor (30 s), e
/// remover e devolver o cartão é uma pausa curta, que mantém o gráfico. A amostra roda fora da thread da interface e nunca há
/// duas ao mesmo tempo. Os valores não são anunciados a cada segundo pelo leitor de tela; cada bloco tem um nome acessível com o
/// valor atual.
/// </summary>
public sealed class LiveMetricsPanel : Border
{
    private readonly LiveMetricsSampler sampler;
    private readonly ILiveMetricsSource source;
    private readonly DispatcherTimer timer = new() { Interval = LiveMetricsSampler.Interval };
    private readonly Tile cpu, memory, disk, network;
    private readonly TextBlock footer;
    private Window? window;
    private bool loaded, sampling;
    private double sampleMilliseconds;

    public LiveMetricsPanel() : this(new WindowsLiveMetricsSource()) { }

    public LiveMetricsPanel(ILiveMetricsSource source)
    {
        this.source = source;
        sampler = new LiveMetricsSampler(source);
        Background = Resource("Surface"); BorderBrush = Resource("Stroke"); BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(12); Padding = new Thickness(18); Margin = new Thickness(0, 0, 0, 14);
        var panel = new StackPanel();
        var title = new TextBlock { Text = "Agora", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Resource("Ink") };
        panel.Children.Add(title);
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4, Margin = new Thickness(0, 12, 0, 10) };
        cpu = new Tile("CPU", "performance"); memory = new Tile("Memória", "health"); disk = new Tile("Disco", "storage"); network = new Tile("Rede", "network");
        foreach (var tile in new[] { cpu, memory, disk, network }) grid.Children.Add(tile.Root);
        panel.Children.Add(grid);
        footer = new TextBlock { FontSize = 12, Foreground = Resource("Secondary"), TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(footer);
        Child = panel;
        AutomationProperties.SetName(this, "Uso do computador agora");
        Render(null);
        timer.Tick += async (_, _) => await Tick();
        Loaded += (_, _) => { loaded = true; Attach(); UpdateRunning(); };
        Unloaded += (_, _) => { loaded = false; UpdateRunning(); };
        // A página Assistente só recolhe a área das páginas: o painel continua carregado, mas deixa de estar visível.
        IsVisibleChanged += (_, _) => UpdateRunning();
    }

    private void Attach()
    {
        var current = Window.GetWindow(this);
        if (ReferenceEquals(current, window)) return;
        if (window != null) { window.StateChanged -= OnWindowChanged; window.IsVisibleChanged -= OnWindowVisibility; window.Closed -= OnWindowClosed; }
        window = current;
        if (window != null) { window.StateChanged += OnWindowChanged; window.IsVisibleChanged += OnWindowVisibility; window.Closed += OnWindowClosed; }
    }

    private void OnWindowChanged(object? sender, EventArgs e) => UpdateRunning();
    private void OnWindowVisibility(object sender, DependencyPropertyChangedEventArgs e) => UpdateRunning();
    private void OnWindowClosed(object? sender, EventArgs e) { timer.Stop(); source.Dispose(); }

    private bool Visible => LiveMetricsSampler.ShouldRun(loaded, IsVisible, window is { IsVisible: true }, window?.WindowState == WindowState.Minimized);

    private void UpdateRunning()
    {
        var now = DateTimeOffset.UtcNow;
        if (Visible)
        {
            if (timer.IsEnabled) return;
            sampler.Resume(now);
            timer.Start();
            if (sampler.NeedsBaseline) _ = Tick();
        }
        else if (timer.IsEnabled)
        {
            timer.Stop();
            sampler.Pause(now);
        }
    }

    private async Task Tick()
    {
        if (sampling || !Visible) return;
        sampling = true;
        try
        {
            var watch = Stopwatch.StartNew();
            var reading = await Task.Run(() => sampler.Sample(DateTimeOffset.UtcNow));
            sampleMilliseconds = watch.Elapsed.TotalMilliseconds;
            if (Visible) Render(reading);
        }
        catch (Exception ex) { footer.Text = "Leitura ao vivo interrompida: " + ex.Message; }
        finally { sampling = false; }
    }

    private void Render(LiveReading? reading)
    {
        if (reading == null)
        {
            foreach (var tile in new[] { cpu, memory, disk, network }) tile.Set("medindo…", "", []);
            footer.Text = "Atualiza a cada segundo enquanto esta tela está aberta. Não é gravado no histórico.";
            return;
        }
        cpu.Set(LiveMetricsSampler.FormatPercent(reading.Cpu), Detail(reading.Cpu, null), sampler.CpuHistory, 100);
        memory.Set(LiveMetricsSampler.FormatPercent(reading.Memory),
            Detail(reading.Memory, reading.MemoryUsedBytes is { } used && reading.MemoryTotalBytes is { } total ? Gigabytes(used) + " de " + Gigabytes(total) + " GB em uso" : null),
            sampler.MemoryHistory, 100);
        disk.Set(reading.DiskActive.Status == LiveStatus.Ok ? LiveMetricsSampler.FormatPercent(reading.DiskActive) + " ativo" : LiveMetricsSampler.FormatPercent(reading.DiskActive),
            Detail(reading.DiskActive, "Leitura " + LiveMetricsSampler.FormatBytesPerSecond(reading.DiskReadBytesPerSecond) + " · gravação " + LiveMetricsSampler.FormatBytesPerSecond(reading.DiskWriteBytesPerSecond)),
            sampler.DiskHistory, 100);
        network.Set(reading.Network.Status switch
            {
                LiveStatus.Ok => "↓ " + LiveMetricsSampler.FormatBitsPerSecond(reading.ReceiveBitsPerSecond),
                LiveStatus.Waiting => "medindo…",
                _ => "indisponível",
            },
            Detail(reading.Network, "↑ " + LiveMetricsSampler.FormatBitsPerSecond(reading.SendBitsPerSecond) + (reading.Adapter is { Length: > 0 } name ? " · " + name : "")),
            sampler.NetworkHistory, null);
        var nexo = reading.NexoCpu.Status == LiveStatus.Ok ? reading.NexoCpu.Value!.Value.ToString("F1", CultureInfo.CurrentCulture) + "%" : "medindo";
        footer.Text = "Atualiza a cada segundo enquanto esta tela está aberta. Não é gravado no histórico. Disco: tempo ativo do disco mais ocupado; leitura e gravação somam todos. Rede: o adaptador mais ativo. "
            + "Uso de CPU do próprio Nexo: " + nexo + " · amostra em " + sampleMilliseconds.ToString("F0", CultureInfo.CurrentCulture) + " ms.";
    }

    private static string Detail(LiveMetric metric, string? ok) => metric.Status switch
    {
        LiveStatus.Ok => ok ?? "",
        LiveStatus.Waiting => "aguardando a segunda leitura",
        _ => metric.Detail is { Length: > 0 } detail ? "Indisponível: " + detail : "Indisponível neste PC",
    };

    private static string Gigabytes(ulong bytes) => (bytes / 1024d / 1024 / 1024).ToString("F1", CultureInfo.CurrentCulture);

    // Pincel vivo do tema (#143): muda de cor na troca entre Ônix e Glacial sem recriar o cartão.
    private static Brush Resource(string key) => ThemeManager.Live(key);

    private sealed class Tile
    {
        private const double ChartWidth = 150, ChartHeight = 30;
        private readonly string label;
        private readonly TextBlock value, detail;
        private readonly Polyline line;
        public Border Root { get; }

        public Tile(string label, string icon)
        {
            this.label = label;
            var panel = new StackPanel();
            var top = new StackPanel { Orientation = Orientation.Horizontal };
            top.Children.Add(UiIcons.Create(icon, 16));
            top.Children.Add(new TextBlock { Text = label, Foreground = Resource("Secondary"), FontSize = 12, Margin = new Thickness(8, 0, 0, 0) });
            panel.Children.Add(top);
            // #143: os números do cartão usam IBM Plex Mono, como os da "Última leitura" (o Plex Mono embutido tem 400 e 500).
            value = new TextBlock { Foreground = Resource("Ink"), FontSize = 21, FontWeight = FontWeights.Medium, Margin = new Thickness(0, 8, 0, 2) };
            if (Application.Current?.TryFindResource("MonoFont") is FontFamily mono) value.FontFamily = mono;
            panel.Children.Add(value);
            detail = new TextBlock { Foreground = Resource("Secondary"), FontSize = 12, TextWrapping = TextWrapping.Wrap, MinHeight = 32 };
            panel.Children.Add(detail);
            line = new Polyline { Stroke = Resource("Accent"), StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round };
            var chart = new Canvas { Width = ChartWidth, Height = ChartHeight, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, ClipToBounds = true };
            chart.Children.Add(line);
            AutomationProperties.SetName(chart, "Gráfico de " + label + " no último minuto");
            panel.Children.Add(chart);
            Root = new Border { Background = Resource("SurfaceMuted"), CornerRadius = new CornerRadius(10), Padding = new Thickness(14), Margin = new Thickness(0, 0, 10, 0), Child = panel };
        }

        public void Set(string text, string detailText, IReadOnlyList<double?> history, double? scale = null)
        {
            value.Text = text; detail.Text = detailText;
            AutomationProperties.SetName(Root, label + ": " + text + (detailText.Length > 0 ? ", " + detailText : ""));
            var points = new PointCollection();
            var max = scale ?? Math.Max(1, history.Where(item => item.HasValue).Select(item => item!.Value).DefaultIfEmpty(0).Max());
            for (var index = 0; index < history.Count; index++)
            {
                if (history[index] is not { } sample) continue;
                var x = ChartWidth * index / Math.Max(1, LiveMetricsSampler.HistoryCapacity - 1);
                points.Add(new Point(x, ChartHeight - ChartHeight * Math.Clamp(sample / max, 0, 1)));
            }
            line.Points = points;
        }
    }
}
