using System.Collections;
using System.Reflection;
using System.Text.Json;

namespace Nexo.Checks;

internal static class HealthDataChecks
{
    public static void Run(string root, Action<bool, string> check)
    {
        var legacy = Path.Combine(root, "data.json");
        Directory.CreateDirectory(root);
        File.WriteAllText(legacy, "{\"legacy\":true}");
        var assembly = typeof(Nexo.Support.Store).Assembly;
        var storeType = assembly.GetType("Nexo.Support.OperationalStore");
        check(storeType != null, "Banco operacional local disponível");
        object store = Activator.CreateInstance(storeType!, root)!;
        try
        {
            object Model(string name, string json) => JsonSerializer.Deserialize(json, assembly.GetType("Nexo.Support." + name)!)!;
            Array One(string name, string json)
            {
                var type = assembly.GetType("Nexo.Support." + name)!;
                var array = Array.CreateInstance(type, 1);
                array.SetValue(Model(name, json), 0);
                return array;
            }
            object Invoke(string name, params object?[] args) => storeType!.GetMethod(name)!.Invoke(store, args)!;
            var now = DateTimeOffset.UtcNow;
            var old = now.AddDays(-45);
            Invoke("SaveSnapshot", Model("HealthSnapshot", JsonSerializer.Serialize(new { Time = now, CpuPercent = 77.5, MemoryUsedPercent = 42.0, CollectionStatus = "complete" })));
            Invoke("SaveSnapshot", Model("HealthSnapshot", JsonSerializer.Serialize(new { Time = old, CpuPercent = 10.0, MemoryUsedPercent = 20.0, CollectionStatus = "complete" })));
            Invoke("SaveFindings", One("HealthFinding", JsonSerializer.Serialize(new { Id = "disk-low", Time = now, Severity = "warning", Evidence = "C: 4% livre", Confidence = 0.95 })));
            Invoke("SaveEvents", One("SystemEvent", JsonSerializer.Serialize(new { Log = "System", Provider = "Service Control Manager", EventId = 7031, Time = now, Level = 2L })));
            Invoke("SaveIntervention", Model("InterventionRecord", JsonSerializer.Serialize(new { Id = "action-1", Time = now, Action = "restart_service", State = "refused", Target = "Audiosrv" })));
            string Json(string name, params object?[] args) => JsonSerializer.Serialize((IEnumerable)Invoke(name, args));
            check(Json("RecentSnapshots", now.AddDays(-60), 20).Contains("77.5"), "Amostra persiste com valor e horário reais");
            check(Json("RecentFindings", 20).Contains("disk-low"), "Ocorrência persiste para consulta histórica");
            check(Json("RecentEvents", 20).Contains("7031"), "Metadados de evento persistem");
            check(Json("RecentInterventions", 20).Contains("refused"), "Recusa é registrada como resultado distinto");
            Invoke("Prune", TimeSpan.FromDays(30));
            var recent = Json("RecentSnapshots", now.AddDays(-60), 20);
            check(recent.Contains("77.5") && !recent.Contains("\"CpuPercent\":10"), "Retenção remove apenas amostras antigas");
            check(File.ReadAllText(legacy) == "{\"legacy\":true}", "Banco operacional preserva data.json legado");
        }
        finally { (store as IDisposable)?.Dispose(); }
        Directory.Delete(root, true);
        check(!Directory.Exists(root), "Banco libera arquivos ao encerrar");
        var corrupt = Path.Combine(Path.GetDirectoryName(root)!, "corrupt-operational");
        Directory.CreateDirectory(corrupt);
        File.WriteAllText(Path.Combine(corrupt, "operations.db"), "not-a-database");
        try
        {
            using var ignored = new Nexo.Support.OperationalStore(corrupt);
            check(false, "Banco corrompido é comunicado sem tocar no legado");
        }
        catch (Nexo.Support.OperationalStoreException)
        {
            check(true, "Banco corrompido é comunicado sem tocar no legado");
        }
        finally { Directory.Delete(corrupt, true); }
    }
}
