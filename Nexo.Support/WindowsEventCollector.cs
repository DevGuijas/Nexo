using System.Text.Json;

namespace Nexo.Support;

public sealed class EventBatch
{
    public List<SystemEvent> Events { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public List<EventGap> Gaps { get; set; } = [];
    public Dictionary<string, EventCursor> Cursors { get; set; } = [];
}

public sealed record EventCursor(long RecordId, DateTimeOffset? Time);
public sealed record EventPageQuery(string Log, DateTimeOffset? Since, long AfterRecordId, int Limit);

public sealed class WindowsEventCollector
{
    public const int PageSize = 50;
    public const int MaxBudgetPerLog = 1000;
    private static readonly string[] Logs = ["System", "Application", "Setup"];
    private readonly Func<EventPageQuery, CancellationToken, Task<string>> page;
    private readonly Func<string, long, CancellationToken, Task<string>> bounds;

    public WindowsEventCollector(Func<EventPageQuery, CancellationToken, Task<string>>? page = null, Func<string, long, CancellationToken, Task<string>>? bounds = null)
    {
        this.page = page ?? QueryPage;
        this.bounds = bounds ?? (page == null ? QueryBounds : (_, _, _) => Task.FromResult("{}"));
    }

    public async Task<EventBatch> Collect(TimeSpan maxAge, int budgetPerLog, CancellationToken token, IReadOnlyDictionary<string, EventCursor>? cursors = null)
    {
        if (maxAge <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maxAge));
        if (budgetPerLog is < 1 or > MaxBudgetPerLog) throw new ArgumentOutOfRangeException(nameof(budgetPerLog));
        var result = new EventBatch();
        var now = DateTimeOffset.UtcNow;
        foreach (var log in Logs)
        {
            token.ThrowIfCancellationRequested();
            var saved = cursors != null && cursors.TryGetValue(log, out var previous) && previous.RecordId > 0 ? previous : null;
            var cursor = saved?.RecordId ?? 0;
            if (saved != null) result.Cursors[log] = saved;
            try { cursor = await CheckBounds(log, saved, now, result, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { result.Warnings.Add($"Canal {log}: não foi possível verificar se registros foram sobrescritos ({ex.Message})."); }
            try { await CollectLog(log, cursor, cursor > 0 ? null : now - maxAge, budgetPerLog, now, result, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { result.Warnings.Add($"Canal {log} indisponível: {ex.Message}"); }
        }
        return result;
    }

    private async Task<long> CheckBounds(string log, EventCursor? saved, DateTimeOffset now, EventBatch result, CancellationToken token)
    {
        if (saved == null) return 0;
        var cursor = saved.RecordId;
        using var json = JsonDocument.Parse(await bounds(log, cursor, token));
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Expected object");
        var oldest = Optional(json.RootElement, "Oldest");
        var newest = Optional(json.RootElement, "Newest");
        var observedTime = OptionalTime(json.RootElement, "CursorTime");
        var empty = oldest == null && newest == null;
        var reused = oldest <= cursor && newest >= cursor && (saved.Time == null || observedTime == null
            || saved.Time.Value.ToUniversalTime() != observedTime.Value.ToUniversalTime());
        if (empty || newest < cursor || reused)
        {
            result.Gaps.Add(new EventGap
            {
                Time = now, Log = log, Reason = "reset", FromRecordId = 1, ToRecordId = cursor,
                Message = $"Canal {log}: o log foi limpo, reiniciado ou a identidade do cursor anterior ({cursor}) mudou. Eventos removidos antes da coleta não podem ser recuperados; a coleta recomeçou pela janela recente."
            });
            result.Cursors.Remove(log);
            return 0;
        }
        if (oldest is { } first && first > cursor + 1)
        {
            result.Gaps.Add(new EventGap
            {
                Time = now, Log = log, Reason = "overwritten", FromRecordId = cursor + 1, ToRecordId = first - 1,
                Message = $"Canal {log}: os registros {cursor + 1} a {first - 1} foram sobrescritos pelo Windows antes da coleta. Eventos desse intervalo podem ter sido perdidos."
            });
        }
        return cursor;
    }

    private async Task CollectLog(string log, long cursor, DateTimeOffset? since, int budget, DateTimeOffset now, EventBatch result, CancellationToken token)
    {
        var collected = 0;
        while (collected < budget)
        {
            token.ThrowIfCancellationRequested();
            var limit = Math.Min(PageSize, budget - collected);
            var rows = Parse(log, await page(new EventPageQuery(log, since, cursor, limit), token));
            var fresh = rows.Where(e => e.RecordId > cursor).OrderBy(e => e.RecordId).Take(limit).ToList();
            if (rows.Count > 0 && fresh.Count == 0) throw new System.IO.InvalidDataException("a consulta retornou apenas registros já coletados");
            result.Events.AddRange(fresh);
            collected += fresh.Count;
            if (fresh.Count > 0) { cursor = fresh[^1].RecordId; result.Cursors[log] = new EventCursor(cursor, fresh[^1].Time); }
            if (fresh.Count < limit) return;
        }
        if (Parse(log, await page(new EventPageQuery(log, since, cursor, 1), token)).Any(e => e.RecordId > cursor))
        {
            result.Gaps.Add(new EventGap
            {
                Time = now, Log = log, Reason = "budget", FromRecordId = cursor + 1,
                Message = $"Canal {log}: limite de {budget} eventos por consulta atingido. Os registros após {cursor} ainda não foram coletados e serão buscados na próxima consulta."
            });
        }
    }

    private static List<SystemEvent> Parse(string log, string raw)
    {
        using var json = JsonDocument.Parse(raw);
        if (json.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("Expected array");
        var rows = new List<SystemEvent>();
        foreach (var row in json.RootElement.EnumerateArray())
        {
            rows.Add(new SystemEvent
            {
                Log = log,
                RecordId = row.GetProperty("RecordId").GetInt64(),
                Time = DateTimeOffset.Parse(row.GetProperty("TimeCreated").GetString()!),
                Provider = row.GetProperty("ProviderName").GetString() ?? "",
                EventId = row.GetProperty("Id").GetInt32(),
                Level = row.GetProperty("Level").GetInt64()
            });
        }
        return rows;
    }

    private static long? Optional(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;
    private static DateTimeOffset? OptionalTime(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), out var time) ? time : null;

    private const string ImportDiagnostics = "Import-Module ($env:SystemRoot+'\\System32\\WindowsPowerShell\\v1.0\\Modules\\Microsoft.PowerShell.Diagnostics\\Microsoft.PowerShell.Diagnostics.psd1') -ErrorAction Stop; ";

    // Arguments are constrained in Collect and log is from a closed internal list.
    private static Task<string> QueryPage(EventPageQuery query, CancellationToken token)
    {
        var filter = "(Level=1 or Level=2 or Level=3) and EventRecordID > " + query.AfterRecordId + (query.Since is { } since ? " and TimeCreated[@SystemTime >= '" + since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") + "']" : "");
        var script = ImportDiagnostics + "$rows=@(try { Get-WinEvent -LogName '" + query.Log + "' -FilterXPath \"*[System[" + filter + "]]\" -Oldest -MaxEvents " + query.Limit + " -ErrorAction Stop | ForEach-Object { [pscustomobject]@{RecordId=[long]$_.RecordId;TimeCreated=$_.TimeCreated.ToString('O');ProviderName=$_.ProviderName;Id=[int]$_.Id;Level=[long]$_.Level} } } catch { if($_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*'){ throw } }); ConvertTo-Json -Compress -Depth 3 -InputObject $rows";
        return SupportService.Powershell(script, token, 20);
    }

    private static Task<string> QueryBounds(string log, long cursor, CancellationToken token)
    {
        var script = ImportDiagnostics + "function Edge($oldest) { try { $item = if ($oldest) { Get-WinEvent -LogName '" + log + "' -Oldest -MaxEvents 1 -ErrorAction Stop } else { Get-WinEvent -LogName '" + log + "' -MaxEvents 1 -ErrorAction Stop }; [long]$item.RecordId } catch { if($_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*'){ throw }; $null } }; "
            + "function CursorTime { try { $item=Get-WinEvent -LogName '" + log + "' -FilterXPath '*[System[EventRecordID=" + cursor + "]]' -MaxEvents 1 -ErrorAction Stop; $item.TimeCreated.ToString('O') } catch { if($_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*'){ throw }; $null } }; "
            + "ConvertTo-Json -Compress -InputObject ([pscustomobject]@{Oldest=(Edge $true);Newest=(Edge $false);CursorTime=(CursorTime)})";
        return SupportService.Powershell(script, token, 20);
    }
}
