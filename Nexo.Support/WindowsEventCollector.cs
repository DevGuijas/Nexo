using System.Text.Json;

namespace Nexo.Support;

public sealed class EventBatch
{
    public List<SystemEvent> Events { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class WindowsEventCollector
{
    private static readonly string[] Logs = ["System", "Application", "Setup"];
    private readonly Func<string, DateTimeOffset, int, CancellationToken, Task<string>> query;
    public WindowsEventCollector(Func<string, DateTimeOffset, int, CancellationToken, Task<string>>? query = null)
        => this.query = query ?? QueryWindows;

    public async Task<EventBatch> Collect(TimeSpan maxAge, int maxPerLog, CancellationToken token)
    {
        if (maxAge <= TimeSpan.Zero || maxPerLog is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(maxPerLog));
        var result = new EventBatch();
        var since = DateTimeOffset.UtcNow - maxAge;
        foreach (var log in Logs)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var raw = await query(log, since, maxPerLog, token);
                using var json = JsonDocument.Parse(raw);
                if (json.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("Expected array");
                foreach (var row in json.RootElement.EnumerateArray())
                {
                    result.Events.Add(new SystemEvent
                    {
                        Log = log,
                        RecordId = row.GetProperty("RecordId").GetInt64(),
                        Time = DateTimeOffset.Parse(row.GetProperty("TimeCreated").GetString()!),
                        Provider = row.GetProperty("ProviderName").GetString() ?? "",
                        EventId = row.GetProperty("Id").GetInt32(),
                        Level = row.GetProperty("Level").GetInt64()
                    });
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { result.Warnings.Add($"Canal {log} indisponível: {ex.Message}"); }
        }
        return result;
    }

    private static Task<string> QueryWindows(string log, DateTimeOffset since, int limit, CancellationToken token)
    {
        // Arguments are constrained in Collect and log is from a closed internal list.
        var script = "Import-Module ($env:SystemRoot+'\\System32\\WindowsPowerShell\\v1.0\\Modules\\Microsoft.PowerShell.Diagnostics\\Microsoft.PowerShell.Diagnostics.psd1') -ErrorAction Stop; $rows=@(try { Get-WinEvent -FilterHashtable @{LogName='" + log + "';StartTime=[datetime]'" + since.UtcDateTime.ToString("O") + "';Level=1,2,3} -MaxEvents " + limit + " -ErrorAction Stop | ForEach-Object { [pscustomobject]@{RecordId=[long]$_.RecordId;TimeCreated=$_.TimeCreated.ToString('O');ProviderName=$_.ProviderName;Id=[int]$_.Id;Level=[long]$_.Level} } } catch { if($_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*'){ throw } }); ConvertTo-Json -Compress -Depth 3 -InputObject $rows";
        return SupportService.Powershell(script, token, 20);
    }
}
