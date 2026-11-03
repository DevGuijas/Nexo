using System.IO;
using System.Text.Json;

namespace Nexo.Support;

public sealed class HealthSettings
{
    public int IntervalSeconds { get; set; } = 30;
    public double CpuHighPercent { get; set; } = 90;
    public double DiskLowFreePercent { get; set; } = 10;
    public int RetentionDays { get; set; } = 30;

    public HealthThresholds Thresholds() => new() { CpuHighPercent = CpuHighPercent, DiskLowFreePercent = DiskLowFreePercent };
    public void Validate()
    {
        if (IntervalSeconds is < 10 or > 3600 || CpuHighPercent is < 50 or > 100 || DiskLowFreePercent is < 1 or > 50 || RetentionDays is < 1 or > 365)
            throw new ArgumentOutOfRangeException(nameof(HealthSettings), "Intervalo, limiares ou retenção fora dos limites permitidos.");
    }
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
