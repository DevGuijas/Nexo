using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Nexo.Support;

public sealed class OperationalStoreException(string message, Exception inner) : Exception(message, inner);

/// <summary>Incremental technical history. Conversation data.json is deliberately separate.</summary>
public sealed class OperationalStore : IDisposable
{
    private readonly string path;
    private readonly object gate = new();
    private static readonly JsonSerializerOptions Json = new();

    public OperationalStore(string root)
    {
        try
        {
            Directory.CreateDirectory(root);
            path = Path.Combine(root, "operations.db");
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT OR IGNORE INTO metadata(key,value) VALUES('schema','2');
                CREATE TABLE IF NOT EXISTS snapshots (id TEXT PRIMARY KEY, time TEXT NOT NULL, payload TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS snapshots_time ON snapshots(time);
                CREATE TABLE IF NOT EXISTS findings (id TEXT PRIMARY KEY, time TEXT NOT NULL, payload TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS findings_time ON findings(time);
                CREATE TABLE IF NOT EXISTS events (log TEXT NOT NULL, record_id INTEGER NOT NULL, time TEXT NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(log,record_id,time));
                CREATE INDEX IF NOT EXISTS events_time ON events(time);
                CREATE TABLE IF NOT EXISTS event_gaps (id TEXT PRIMARY KEY, time TEXT NOT NULL, payload TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS event_gaps_time ON event_gaps(time);
                CREATE TABLE IF NOT EXISTS interventions (id TEXT PRIMARY KEY, time TEXT NOT NULL, payload TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS interventions_time ON interventions(time);
                """;
            command.ExecuteNonQuery();
            command.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='events'";
            var eventsSchema = (string?)command.ExecuteScalar();
            if (eventsSchema?.Contains("PRIMARY KEY(log,record_id)", StringComparison.OrdinalIgnoreCase) == true)
            {
                using var transaction = connection.BeginTransaction();
                command.Transaction = transaction;
                command.CommandText = """
                    CREATE TABLE events_migrated (log TEXT NOT NULL, record_id INTEGER NOT NULL, time TEXT NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(log,record_id,time));
                    INSERT OR IGNORE INTO events_migrated(log,record_id,time,payload) SELECT log,record_id,time,payload FROM events;
                    DROP TABLE events;
                    ALTER TABLE events_migrated RENAME TO events;
                    CREATE INDEX events_time ON events(time);
                    UPDATE metadata SET value='2' WHERE key='schema';
                    """;
                command.ExecuteNonQuery();
                transaction.Commit();
                command.Transaction = null;
            }
            command.CommandText = "PRAGMA quick_check";
            if ((string?)command.ExecuteScalar() != "ok") throw new InvalidDataException("Banco operacional inconsistente.");
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidDataException)
        { throw new OperationalStoreException("Não foi possível abrir o histórico operacional local.", ex); }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private void Write(string sql, string id, DateTimeOffset time, object item, string? log = null, long recordId = 0)
    {
        lock (gate)
        {
            try
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$time", time.ToUniversalTime().ToString("O"));
                command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(item, Json));
                if (log != null) { command.Parameters.AddWithValue("$log", log); command.Parameters.AddWithValue("$record", recordId); }
                command.ExecuteNonQuery();
            }
            catch (SqliteException ex) { throw new OperationalStoreException("Falha ao gravar o histórico operacional.", ex); }
        }
    }

    public void SaveSnapshot(HealthSnapshot item) => Write("INSERT OR REPLACE INTO snapshots(id,time,payload) VALUES($id,$time,$payload)", item.Id, item.Time, item);
    public void SaveFindings(IEnumerable<HealthFinding> items)
    { foreach (var item in items) Write("INSERT OR REPLACE INTO findings(id,time,payload) VALUES($id,$time,$payload)", item.Id, item.Time, item); }
    public void SaveEvents(IEnumerable<SystemEvent> items)
    { foreach (var item in items) Write("INSERT OR REPLACE INTO events(log,record_id,time,payload) VALUES($log,$record,$time,$payload)", "", item.Time, item, item.Log, item.RecordId); }
    public void SaveEventGaps(IEnumerable<EventGap> items)
    { foreach (var item in items) Write("INSERT OR REPLACE INTO event_gaps(id,time,payload) VALUES($id,$time,$payload)", item.Id, item.Time, item); }
    public void SaveEventCursors(IReadOnlyDictionary<string, EventCursor> cursors)
    {
        lock (gate)
        {
            try
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction();
                using (var clear = connection.CreateCommand())
                {
                    clear.Transaction = transaction;
                    clear.CommandText = "DELETE FROM metadata WHERE key LIKE 'event_cursor:%'";
                    clear.ExecuteNonQuery();
                }
                foreach (var (log, cursor) in cursors)
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = "INSERT INTO metadata(key,value) VALUES($key,$value)";
                    command.Parameters.AddWithValue("$key", "event_cursor:" + log);
                    command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(cursor, Json));
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            catch (SqliteException ex) { throw new OperationalStoreException("Falha ao gravar a posição da coleta de eventos.", ex); }
        }
    }
    public Dictionary<string, EventCursor> EventCursors()
    {
        lock (gate)
        {
            try
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT key, value FROM metadata WHERE key LIKE 'event_cursor:%'";
                using var reader = command.ExecuteReader();
                var result = new Dictionary<string, EventCursor>();
                while (reader.Read())
                {
                    var value = reader.GetString(1);
                    var cursor = long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var oldRecordId)
                        ? new EventCursor(oldRecordId, null) : JsonSerializer.Deserialize<EventCursor>(value, Json);
                    if (cursor is { RecordId: > 0 }) result[reader.GetString(0)["event_cursor:".Length..]] = cursor;
                }
                return result;
            }
            catch (Exception ex) when (ex is SqliteException or JsonException) { throw new OperationalStoreException("Falha ao consultar a posição da coleta de eventos.", ex); }
        }
    }
    public void SaveIntervention(InterventionRecord item) => Write("INSERT OR REPLACE INTO interventions(id,time,payload) VALUES($id,$time,$payload)", item.Id, item.Time, item);

    private List<T> Read<T>(string table, int limit, DateTimeOffset? since = null)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (gate)
        {
            try
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT payload FROM {table}" + (since == null ? "" : " WHERE time >= $since") + " ORDER BY time DESC LIMIT $limit";
                if (since != null) command.Parameters.AddWithValue("$since", since.Value.ToUniversalTime().ToString("O"));
                command.Parameters.AddWithValue("$limit", limit);
                using var reader = command.ExecuteReader();
                var result = new List<T>();
                while (reader.Read()) result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), Json)!);
                return result;
            }
            catch (Exception ex) when (ex is SqliteException or JsonException) { throw new OperationalStoreException("Falha ao consultar o histórico operacional.", ex); }
        }
    }

    public List<HealthSnapshot> RecentSnapshots(DateTimeOffset since, int limit) => Read<HealthSnapshot>("snapshots", limit, since);
    public List<HealthFinding> RecentFindings(int limit) => Read<HealthFinding>("findings", limit);
    public List<SystemEvent> RecentEvents(int limit) => Read<SystemEvent>("events", limit);
    public List<EventGap> RecentEventGaps(int limit, DateTimeOffset? since = null) => Read<EventGap>("event_gaps", limit, since);
    public List<InterventionRecord> RecentInterventions(int limit) => Read<InterventionRecord>("interventions", limit);

    public void Prune(TimeSpan retention)
    {
        if (retention <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retention));
        var cutoff = DateTimeOffset.UtcNow.Subtract(retention).ToString("O");
        lock (gate)
        {
            try
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction();
                foreach (var table in new[] { "snapshots", "findings", "events", "event_gaps", "interventions" })
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = $"DELETE FROM {table} WHERE time < $cutoff";
                    command.Parameters.AddWithValue("$cutoff", cutoff);
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            catch (SqliteException ex) { throw new OperationalStoreException("Falha ao aplicar retenção do histórico.", ex); }
        }
    }

    public void Dispose() { }
}
