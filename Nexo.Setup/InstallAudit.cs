using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Nexo.Setup;

public sealed class InstallAudit : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint Load;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    public static string DefaultDirectory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nexo", "InstallerLogs");

    private readonly StreamWriter writer;
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly object gate = new();
    private bool completed;

    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public string DeviceId { get; } = GetDeviceId();
    public string Path { get; }

    public InstallAudit(string? directory = null, string mode = "install")
    {
        var folder = directory ?? DefaultDirectory;
        Directory.CreateDirectory(folder);
        Path = System.IO.Path.Combine(folder, $"install-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{SessionId}.jsonl");
        writer = new StreamWriter(new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        long? freeBytes = null;
        long? diskBytes = null;
        ulong? physicalMemoryBytes = null;
        try
        {
            var drive = new DriveInfo(System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(folder))!);
            freeBytes = drive.AvailableFreeSpace;
            diskBytes = drive.TotalSize;
        }
        catch (IOException) { }
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (GlobalMemoryStatusEx(ref memory)) physicalMemoryBytes = memory.TotalPhysical;
        Record("run_started", "started", new
        {
            mode,
            installerVersion = typeof(Installer).Assembly.GetName().Version?.ToString(),
            machineName = Environment.MachineName,
            deviceId = DeviceId,
            os = RuntimeInformation.OSDescription,
            osVersion = Environment.OSVersion.VersionString,
            osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            logicalProcessors = Environment.ProcessorCount,
            physicalMemoryBytes,
            locale = CultureInfo.CurrentCulture.Name,
            timeZone = TimeZoneInfo.Local.Id,
            freeBytes,
            diskBytes,
            nexoInstalledBefore = File.Exists(System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Nexo", "Nexo.exe")),
            ollamaInstalledBefore = File.Exists(System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe"))
        });
    }

    public void Record(string eventName, string status, object? data = null)
    {
        var line = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            sessionId = SessionId,
            timestampUtc = DateTimeOffset.UtcNow,
            @event = eventName,
            status,
            data
        });
        lock (gate) writer.WriteLine(line);
    }

    public void Complete(string status, object? data = null)
    {
        if (completed) return;
        Record("run_finished", status, new { durationMs = elapsed.ElapsedMilliseconds, result = data });
        completed = true;
    }

    public void Dispose()
    {
        if (!completed) Complete("interrupted");
        writer.Dispose();
    }

    public static string GetDeviceId()
    {
        string? machineGuid;
        try { machineGuid = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", "MachineGuid", null) as string; }
        catch (System.Security.SecurityException) { machineGuid = null; }
        catch (UnauthorizedAccessException) { machineGuid = null; }
        var source = string.IsNullOrWhiteSpace(machineGuid)
            ? "fallback:" + Environment.MachineName + ":" + Environment.OSVersion.VersionString
            : "machine-guid:" + machineGuid;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("nexo-device-v1:" + source))).ToLowerInvariant();
    }
}

public sealed record GeoIpLocation(string CountryCode, string? Country, string? Region, string? City);

public static class GeoIpLookup
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    public static async Task<GeoIpLocation> LookupAsync()
    {
        using var response = await Client.GetAsync("https://ipwho.is/?fields=success,country_code,country,region,city");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 16_384) throw new InvalidDataException("Resposta GeoIP muito grande.");
        var body = await response.Content.ReadAsStringAsync();
        if (body.Length > 16_384) throw new InvalidDataException("Resposta GeoIP muito grande.");
        return Parse(body);
    }

    public static GeoIpLocation Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
            throw new InvalidDataException("Consulta GeoIP sem resultado válido.");
        var countryCode = root.GetProperty("country_code").GetString();
        if (countryCode == null || !System.Text.RegularExpressions.Regex.IsMatch(countryCode, "^[A-Z]{2}$"))
            throw new InvalidDataException("País inválido na consulta GeoIP.");
        static string? Field(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()?[..Math.Min(value.GetString()!.Length, 120)] : null;
        return new GeoIpLocation(countryCode, Field(root, "country"), Field(root, "region"), Field(root, "city"));
    }
}
