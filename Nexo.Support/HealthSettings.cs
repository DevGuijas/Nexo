using System.Globalization;
using System.IO;
using System.Net;
using System.Text.Json;

namespace Nexo.Support;

public sealed class HealthSettings
{
    public int IntervalSeconds { get; set; } = 30;
    public double CpuHighPercent { get; set; } = 90;
    public double DiskLowFreePercent { get; set; } = 10;
    public int RetentionDays { get; set; } = 30;
    public bool BackgroundMode { get; set; }
    public double ProcessMemoryGrowthPoints { get; set; } = 5;
    public int ProcessMemorySamples { get; set; } = 4;
    public bool CheckGateway { get; set; }
    public bool CheckDns { get; set; }
    public bool CheckExternal { get; set; }
    public string DnsProbeHost { get; set; } = "www.msftconnecttest.com";
    public string HttpProbeUrl { get; set; } = "http://www.msftconnecttest.com/connecttest.txt";
    public string HttpExpectedText { get; set; } = "Microsoft Connect Test";

    // Sem separador de milhar: em pt-BR, "2.5" viraria 25 sem nenhum aviso.
    public static bool TryParsePercent(string text, IFormatProvider culture, out double value)
        => double.TryParse(text, NumberStyles.Float, culture, out value) && double.IsFinite(value);

    public HealthThresholds Thresholds() => new() { CpuHighPercent = CpuHighPercent, DiskLowFreePercent = DiskLowFreePercent,
        MaxSampleGap = TimeSpan.FromSeconds(Math.Max(90, IntervalSeconds * 1.5)),
        ExternalProbeMaxSampleGap = TimeSpan.FromSeconds(Math.Max(IntervalSeconds * 1.5, IntervalSeconds + 14)),
        ProcessMemoryGrowthPoints = ProcessMemoryGrowthPoints, ProcessMemorySamples = ProcessMemorySamples };
    public void Validate()
    {
        if (IntervalSeconds is < 10 or > 3600 || CpuHighPercent is < 50 or > 100 || DiskLowFreePercent is < 1 or > 50 || RetentionDays is < 1 or > 365)
            throw new ArgumentOutOfRangeException(nameof(HealthSettings), "Intervalo, limiares ou retenção fora dos limites permitidos.");
        // O monitor consulta no máximo 20 amostras; a saída do processo precisa de uma leitura além do mínimo.
        if (!(ProcessMemoryGrowthPoints is >= 1 and <= 50) || ProcessMemorySamples is < 3 or > 19)
            throw new ArgumentOutOfRangeException(nameof(HealthSettings), "Limiares de memória por processo fora dos limites permitidos.");
        if (CheckDns && (string.IsNullOrWhiteSpace(DnsProbeHost) || DnsProbeHost.Length > 253 || Uri.CheckHostName(DnsProbeHost) != UriHostNameType.Dns || IPAddress.TryParse(DnsProbeHost, out _)))
            throw new ArgumentException("Informe um nome DNS válido para a sondagem.", nameof(DnsProbeHost));
        if (CheckExternal)
        {
            if (string.IsNullOrWhiteSpace(HttpProbeUrl) || HttpProbeUrl.Length > 2048 || !Uri.TryCreate(HttpProbeUrl, UriKind.Absolute, out var target)
                || target.Scheme is not ("http" or "https") || target.HostNameType != UriHostNameType.Dns || !target.Host.Contains('.')
                || target.UserInfo.Length != 0 || target.Fragment.Length != 0)
                throw new ArgumentException("Informe uma URL HTTP(S) pública sem usuário nem fragmento.", nameof(HttpProbeUrl));
            if (string.IsNullOrWhiteSpace(HttpExpectedText) || HttpExpectedText.Length > 120)
                throw new ArgumentException("Informe o texto esperado da resposta, com até 120 caracteres.", nameof(HttpExpectedText));
        }
    }
    public static bool TryParseGrowthPoints(string text, IFormatProvider culture, out double points)
        => TryParsePercent(text, culture, out points);
    public static HealthSettings Load(string root)
    {
        var path = Path.Combine(root, "health-settings.json");
        if (!File.Exists(path)) return new();
        var settings = JsonSerializer.Deserialize<HealthSettings>(File.ReadAllText(path)) ?? throw new InvalidDataException("Configurações de saúde inválidas.");
        settings.Validate();
        return settings;
    }
    public void Save(string root)
    {
        Validate();
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "health-settings.json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this));
        File.Move(temporary, path, true);
    }
}
