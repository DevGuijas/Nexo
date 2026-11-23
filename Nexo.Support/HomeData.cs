using System.Globalization;
using System.IO;

namespace Nexo.Support;

public enum HomeHealthState { Unavailable, Loading, Stable, Attention, Critical }

/// <summary>Estado do computador mostrado na tela Início, a partir do histórico do monitor de saúde.</summary>
public sealed record HomeHealth(HomeHealthState State, string Headline, string Detail, HealthSnapshot? Latest,
    IReadOnlyList<(HealthFinding Finding, OccurrenceStatus Status)> Today);

/// <summary>Atalho "Resolver rápido": abre o Assistente com o pedido escrito, sem enviar.</summary>
public sealed record HomeQuickFix(int Number, string Icon, string Title, string Subtitle, string Prompt);

/// <summary>
/// Dados da tela Início (#143), sem dependência do WPF para o Nexo.Checks. Só dados reais do Nexo: o histórico do monitor
/// (leituras e ocorrências), os atendimentos deste perfil e o nome da sessão do Windows. Sem histórico, sem leitura ou sem
/// atendimento, devolve os estados de indisponível, carregando e vazio, nunca um valor de exemplo.
/// </summary>
public static class HomeData
{
    private static readonly CultureInfo Portuguese = CultureInfo.GetCultureInfo("pt-BR");

    public static IReadOnlyList<HomeQuickFix> QuickFixes { get; } =
    [
        new(1, "network", "Sem internet", "Conexão, IP e DNS", "Estou sem internet"),
        new(2, "performance", "PC lento", "Lentidão e travamentos", "Meu computador está lento"),
        new(3, "printer", "Impressora não imprime", "Fila e impressão", "Minha impressora não imprime"),
        new(4, "audio", "Sem som", "Som e microfone", "Estou sem som"),
        new(5, "camera", "Câmera não funciona", "Imagem e permissões", "Minha câmera não funciona"),
        new(6, "bluetooth", "Bluetooth", "Dispositivos sem conexão", "Meu Bluetooth não está funcionando")
    ];

    public static string FirstName(string? name)
    {
        var first = (name ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrEmpty(first) ? "" : first;
    }

    public static string Greeting(string? name) => FirstName(name) is { Length: > 0 } first ? "Olá, " + first + "." : "Olá.";

    public static string DateLine(DateTime localNow) => localNow.ToString("dddd, d 'de' MMMM 'de' yyyy", Portuguese);

    /// <summary>Início do dia local de <paramref name="now"/>, com o fuso do Windows.</summary>
    public static DateTimeOffset StartOfToday(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local);
        var midnight = local.Date;
        return new DateTimeOffset(midnight, TimeZoneInfo.Local.GetUtcOffset(midnight));
    }

    public static HomeHealth Health(OperationalStore? store, string? storeError, HealthThresholds limits, DateTimeOffset now)
    {
        if (store == null)
            return new(HomeHealthState.Unavailable, "Monitor indisponível",
                "O histórico operacional não abriu" + (string.IsNullOrWhiteSpace(storeError) ? "." : ": " + storeError), null, []);
        try
        {
            var latest = store.RecentSnapshots(now.AddDays(-1), 1).FirstOrDefault();
            var today = Today(store, latest, limits, now);
            if (latest == null)
                return new(HomeHealthState.Loading, "Aguardando a primeira leitura",
                    "A coleta começa quando o Nexo abre. O estado aparece assim que o monitor gravar a primeira leitura.", null, today);
            var persistent = today.Where(item => item.Status.State == OccurrenceState.Persistent).ToArray();
            var critical = persistent.Count(item => item.Finding.Severity == "critical");
            var attention = persistent.Length - critical;
            var detail = "Última leitura às " + latest.Time.ToLocalTime().ToString("HH:mm", Portuguese) + " · coleta "
                + (latest.CollectionStatus switch { "complete" => "completa", "partial" => "parcial", _ => latest.CollectionStatus }) + ".";
            if (critical > 0)
                return new(HomeHealthState.Critical, critical == 1 ? "1 ocorrência crítica" : critical + " ocorrências críticas", detail, latest, today);
            if (attention > 0)
                return new(HomeHealthState.Attention, "Estável, com " + attention + (attention == 1 ? " ponto de atenção" : " pontos de atenção"), detail, latest, today);
            return new(HomeHealthState.Stable, "Estável", detail, latest, today);
        }
        catch (Exception ex) when (ex is OperationalStoreException or InvalidOperationException or IOException)
        {
            return new(HomeHealthState.Unavailable, "Monitor indisponível", "Não foi possível ler o histórico operacional: " + ex.Message, null, []);
        }
    }

    /// <summary>Ocorrências gravadas desde a meia-noite local, com o mesmo estado (persiste, resolvida…) da tela Saúde do PC.</summary>
    public static IReadOnlyList<(HealthFinding Finding, OccurrenceStatus Status)> Today(OperationalStore store, HealthSnapshot? latest, HealthThresholds limits, DateTimeOffset now)
    {
        // Parada normal sem prova de execução fica fora das ocorrências, como na tela Saúde do PC (#57).
        var findings = store.FindingsSince(StartOfToday(now)).Where(finding => !HealthOccurrences.SupersededServiceStop(finding, latest)).Take(20).ToList();
        if (findings.Count == 0) return [];
        return HealthOccurrences.Classify(findings, store.RecentSnapshots(now.AddHours(-6), 20), limits, now);
    }

    /// <summary>Atendimentos deste perfil, do mais recente para o mais antigo, como na tela Atendimentos.</summary>
    public static IReadOnlyList<Conversation> RecentConversations(Database data, string userId, int max = 5)
        => data.Conversations.Where(item => item.UserId == userId).OrderByDescending(item => item.Created).Take(max).ToList();
}
