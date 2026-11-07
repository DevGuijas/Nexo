using Nexo.Support;

internal static class HealthOccurrenceChecks
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly HealthThresholds Limits = new();

    public static void Run(Action<bool, string> check)
    {
        Cpu(check);
        Disk(check);
        Service(check);
        Network(check);
        Memory(check);
        Identity(check);
        Notice(check);
        Report(check);
    }

    private static HealthSnapshot At(int secondsAgo, Action<HealthSnapshot>? change = null)
    {
        var snapshot = new HealthSnapshot { Time = Now.AddSeconds(-secondsAgo), CollectionStatus = "complete" };
        change?.Invoke(snapshot);
        return snapshot;
    }

    private static HealthSnapshot[] Series(Action<HealthSnapshot>? change, params int[] secondsAgo)
        => secondsAgo.Select(seconds => At(seconds, change)).ToArray();

    private static HealthFinding Found(string id, int secondsAgo = 600, string evidence = "", string source = "rules")
        => new() { Id = id + ":snapshot", Time = Now.AddSeconds(-secondsAgo), Evidence = evidence, Source = source };

    private static OccurrenceStatus State(HealthFinding finding, params HealthSnapshot[] history)
        => HealthOccurrences.Classify([finding], history, Limits, Now)[0].Status;

    private static void Cpu(Action<bool, string> check)
    {
        void Cpu(HealthSnapshot s, double? value) => s.CpuPercent = value;
        var alert = Found("cpu-high");
        check(State(alert, Series(s => Cpu(s, 95), 60, 30, 0)).State == OccurrenceState.Persistent, "Alerta com a condição nas leituras atuais aparece como persistente");
        var resolved = State(alert, Series(s => Cpu(s, 40), 60, 30, 0));
        check(resolved.State == OccurrenceState.Resolved && resolved.Label.Contains("3 leituras"), "Alerta que cessou aparece como resolvido, com as leituras que sustentam isso");
        check(State(alert, Series(s => Cpu(s, 40), 30, 0)).State == OccurrenceState.Unknown, "Menos leituras do que a regra exige não confirmam resolução");
        check(State(alert, At(60, s => Cpu(s, 40)), At(30, s => Cpu(s, 95)), At(0, s => Cpu(s, 40))).State == OccurrenceState.Unknown, "Leituras mistas não confirmam persistência nem resolução");
        check(State(alert, At(60, s => Cpu(s, 95)), At(30, s => Cpu(s, 40)), At(0, s => Cpu(s, 40))).State == OccurrenceState.Unknown, "Leitura recente com a condição impede a resolução");
        check(State(alert, Series(s => Cpu(s, null), 60, 30, 0)).State == OccurrenceState.Unknown, "Sensor indisponível não é tratado como condição resolvida");
        check(State(alert, Series(s => Cpu(s, Limits.CpuHighPercent), 60, 30, 0)).State == OccurrenceState.Persistent, "Valor no limiar conta como condição presente");
        var stale = State(alert, Series(s => Cpu(s, 95), 700, 670, 640));
        check(stale.State == OccurrenceState.Unknown && stale.Label.Contains("sem leitura recente"), "Sem leitura recente, alerta antigo não é chamado de falha atual");
        check(State(alert, Series(s => Cpu(s, 40), 700, 670, 640)).State == OccurrenceState.Unknown, "Sem leitura recente, alerta também não é chamado de resolvido");
        check(State(alert, Series(s => Cpu(s, 95), -600, -630, -660)).State == OccurrenceState.Unknown, "Leitura com horário futuro não confirma o estado atual");
        check(State(alert, At(60, s => Cpu(s, Limits.CpuHighPercent)), At(30, s => Cpu(s, 40)), At(0, s => Cpu(s, 40))).State == OccurrenceState.Unknown, "Leitura no limiar não conta como condição ausente");
        check(State(alert, Series(s => Cpu(s, 40), 0, 100, 200)).State == OccurrenceState.Unknown, "Lacuna entre leituras impede a resolução");
        check(State(Found("cpu-high", 30), Series(s => Cpu(s, 40), 90, 60, 30, 0)).State == OccurrenceState.Unknown, "Leituras anteriores ao alerta não contam para resolvê-lo");
        check(State(alert).State == OccurrenceState.Unknown, "Sem nenhuma leitura o estado é desconhecido");
    }

    private static void Disk(Action<bool, string> check)
    {
        static DiskHealth Volume(string name, long free) => new(name, free, 100);
        var alert = Found("disk-low:C:");
        check(State(alert, Series(s => s.Disks = [Volume("C:", 5)], 30, 0)).State == OccurrenceState.Persistent, "Volume ainda cheio persiste, mesmo com dois-pontos no identificador");
        check(State(alert, Series(s => s.Disks = [Volume("c:", 40)], 30, 0)).State == OccurrenceState.Resolved, "Espaço recuperado resolve a ocorrência sem diferenciar maiúsculas no nome");
        check(State(alert, Series(s => s.Disks = [], 30, 0)).State == OccurrenceState.Unknown, "Volume ausente da leitura não prova resolução");
        check(State(alert, Series(s => s.Disks = [Volume("D:", 90)], 30, 0)).State == OccurrenceState.Unknown, "Espaço livre em outro volume não resolve a ocorrência");
        check(State(alert, Series(s => s.Disks = [new DiskHealth("C:", 5, 0)], 30, 0)).State == OccurrenceState.Unknown, "Volume sem capacidade informada não prova resolução");
    }

    private static void Service(Action<bool, string> check)
    {
        var alert = Found("service-stopped:Spooler");
        check(State(alert, Series(s => s.Services = [new("Spooler", "Stopped", "Automatic")], 30, 0)).State == OccurrenceState.Persistent, "Serviço automático ainda parado persiste");
        check(State(alert, Series(s => s.Services = [new("Spooler", "Running", "Automatic")], 30, 0)).State == OccurrenceState.Resolved, "Serviço em execução resolve a ocorrência");
        check(State(alert, Series(s => s.Services = [new("Spooler", "Stopped", "Manual")], 30, 0)).State == OccurrenceState.Resolved, "Serviço que deixou de ser automático encerra a condição");
        check(State(alert, Series(s => s.Services = [], 30, 0)).State == OccurrenceState.Unknown, "Lista de serviços vazia ou truncada não prova resolução");
    }

    private static void Network(Action<bool, string> check)
    {
        var none = Found("network-none");
        check(State(none, Series(s => s.Connectivity = "none", 60, 30, 0)).State == OccurrenceState.Persistent, "Conectividade ausente persiste");
        check(State(none, Series(s => s.Connectivity = "local", 60, 30, 0)).State == OccurrenceState.Resolved, "Interface ativa resolve a ausência de conectividade local");
        check(State(none, Series(s => s.Connectivity = "unknown", 60, 30, 0)).State == OccurrenceState.Unknown, "Conectividade desconhecida não prova resolução");

        var adapter = Found("adapter-disabled:7");
        static AdapterHealth Nic(string status) => new("7", "Ethernet", status, status != "Disabled");
        check(State(adapter, Series(s => { s.Connectivity = "none"; s.Adapters = [Nic("Disabled")]; }, 30, 0)).State == OccurrenceState.Persistent, "Adaptador desativado sem rede persiste");
        check(State(adapter, Series(s => { s.Connectivity = "none"; s.Adapters = [Nic("Up")]; }, 30, 0)).State == OccurrenceState.Resolved, "Adaptador reativado resolve a ocorrência");
        check(State(adapter, Series(s => { s.Connectivity = "local"; s.Adapters = [Nic("Disabled")]; }, 30, 0)).State == OccurrenceState.Resolved, "Outra interface ativa encerra a condição do adaptador desativado");
        check(State(adapter, Series(s => s.Connectivity = "none", 30, 0)).State == OccurrenceState.Unknown, "Adaptador ausente da leitura não prova resolução");

        var external = Found("network-external-unconfirmed", evidence: "3 consultas consecutivas a example.com sem a resposta esperada");
        external.ProbeTarget = "example.com";
        external.ProbeConfigurationId = "config";
        static NetworkProbeReading Probe(string state, string target = "example.com", string configuration = "config") => new(state, target, "", configuration);
        check(State(external, Series(s => { s.Connectivity = "local"; s.ExternalProbe = Probe("unexpected"); }, 60, 30, 0)).State == OccurrenceState.Persistent, "Destino externo sem resposta persiste");
        check(State(external, Series(s => { s.Connectivity = "local"; s.ExternalProbe = Probe("ok"); }, 60, 30, 0)).State == OccurrenceState.Resolved, "Resposta esperada do mesmo destino resolve a ocorrência");
        check(State(external, Series(s => { s.Connectivity = "local"; s.ExternalProbe = Probe("ok", "outro.example"); }, 60, 30, 0)).State == OccurrenceState.Unknown, "Resposta de outro destino não resolve a ocorrência");
        check(State(external, Series(s => { s.Connectivity = "local"; s.ExternalProbe = Probe("ok", "api.example.com"); }, 60, 30, 0)).State == OccurrenceState.Unknown, "Subdomínio que contém o nome anterior não resolve a ocorrência");
        var subdomain = Found("network-external-unconfirmed", evidence: "3 consultas consecutivas a api.example.com sem a resposta esperada");
        subdomain.ProbeTarget = "api.example.com";
        subdomain.ProbeConfigurationId = "config-api";
        check(State(subdomain, Series(s => { s.Connectivity = "local"; s.ExternalProbe = Probe("ok", "example.com", "config-api"); }, 60, 30, 0)).State == OccurrenceState.Unknown, "Nome curto contido no host anterior não resolve a ocorrência");
        check(State(external, Series(s => { s.Connectivity = "local"; s.ExternalProbe = Probe("ok", "example.com", "outra-configuracao"); }, 60, 30, 0)).State == OccurrenceState.Unknown, "Mudança de URL ou resposta esperada no mesmo host não resolve a ocorrência antiga");
        check(State(external, Series(s => { s.Connectivity = "local"; s.ExternalProbe = Probe("unexpected", "example.com", "outra-configuracao"); }, 60, 30, 0)).State == OccurrenceState.Unknown, "Alerta de outra configuração no mesmo host não mantém a ocorrência antiga ativa");
        check(State(Found("network-external-unconfirmed", evidence: external.Evidence), Series(s => { s.Connectivity = "local"; s.ExternalProbe = Probe("ok"); }, 60, 30, 0)).State == OccurrenceState.Unknown,
            "Ocorrência antiga sem identidade de configuração não é declarada resolvida por texto livre");
        check(State(external, Series(s => { s.Connectivity = "local"; s.ExternalProbe = Probe("ok", ""); }, 60, 30, 0)).State == OccurrenceState.Unknown, "Resposta sem destino informado não resolve a ocorrência");
        check(State(external, Series(s => { s.Connectivity = "local"; s.ExternalProbe = Probe("disabled"); }, 60, 30, 0)).State == OccurrenceState.Unknown, "Sondagem desligada não prova resolução");
        check(State(external, Series(s => { s.Connectivity = "local"; s.ExternalProbe = Probe("ok"); }, 120, 60, 0)).State == OccurrenceState.Unknown, "Sondagem externa exige o mesmo intervalo curto da regra");
    }

    private static void Memory(Action<bool, string> check)
    {
        var alert = Found("memory-growing");
        check(State(alert, At(60, s => s.MemoryUsedPercent = 60), At(30, s => s.MemoryUsedPercent = 75), At(0, s => s.MemoryUsedPercent = 90)).State == OccurrenceState.Persistent, "Memória em crescimento persiste");
        check(State(alert, Series(s => s.MemoryUsedPercent = 50, 60, 30, 0)).State == OccurrenceState.Resolved, "Memória abaixo do limite final resolve a ocorrência");
        check(State(alert, Series(s => s.MemoryUsedPercent = 50, 30, 0)).State == OccurrenceState.Unknown, "Duas leituras não bastam para resolver a memória, que exige três para alertar");
        check(State(alert, Series(s => s.MemoryUsedPercent = 90, 60, 30, 0)).State == OccurrenceState.Unknown, "Memória alta sem crescimento não é chamada de resolvida");
        check(State(alert, Series(s => s.MemoryUsedPercent = null, 60, 30, 0)).State == OccurrenceState.Unknown, "Memória sem leitura não prova resolução");
    }

    private static void Identity(Action<bool, string> check)
    {
        var history = Series(s => s.CpuPercent = 95, 60, 30, 0);
        var older = Found("cpu-high", 3600);
        var newer = Found("cpu-high", 600);
        var both = HealthOccurrences.Classify([newer, older], history, Limits, Now);
        check(both[0].Finding == newer && both[0].Status.State == OccurrenceState.Persistent, "A ocorrência mais recente recebe o estado atual");
        check(both[1].Finding == older && both[1].Status.State == OccurrenceState.Unknown && both[1].Status.Label.Contains("mais recente"), "Ocorrência anterior do mesmo alerta não é chamada de falha atual");
        check(State(Found("cpu-high", source: "events"), history).State == OccurrenceState.Unknown, "Ocorrência que não veio das regras não é reavaliada por elas");
        check(State(Found("regra-futura"), history).State == OccurrenceState.Unknown, "Alerta sem critério conhecido fica como estado desconhecido");
        var label = State(Found("regra-futura"), history).Label;
        check(label.Contains("histórica", StringComparison.OrdinalIgnoreCase) && label.Contains("desconhecido"), "Estado desconhecido é dito com clareza e como histórico");
    }

    private static void Notice(Action<bool, string> check)
    {
        var last = Now.AddSeconds(-30);
        var fresh = HealthOccurrences.CollectionNotice(At(30), Limits, Now);
        check(fresh.Contains("Última coleta") && fresh.Contains(last.ToLocalTime().ToString("g")) && !fresh.Contains("desconhecido"), "Aviso mostra o horário da última coleta recente");
        var stale = HealthOccurrences.CollectionNotice(At(700), Limits, Now);
        check(stale.Contains(Now.AddSeconds(-700).ToLocalTime().ToString("g")) && stale.Contains("desconhecido"), "Coleta antiga avisa que o estado atual é desconhecido");
        check(HealthOccurrences.CollectionNotice(null, Limits, Now).Contains("desconhecido"), "Sem coleta, o aviso não afirma nenhum estado");
    }

    private static void Report(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "nexo-occurrence-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var db = new OperationalStore(root);
            var now = DateTimeOffset.UtcNow;
            foreach (var seconds in new[] { 60, 30, 0 })
                db.SaveSnapshot(new HealthSnapshot { Time = now.AddSeconds(-seconds), CpuPercent = 95, CollectionStatus = "complete", Disks = [new DiskHealth("C:", 40, 100)] });
            db.SaveFindings([
                new HealthFinding { Id = "cpu-high:a", Time = now.AddMinutes(-5), Problem = "CPU alta" },
                new HealthFinding { Id = "disk-low:C::b", Time = now.AddMinutes(-5), Problem = "Disco cheio" },
                new HealthFinding { Id = "service-stopped:Spooler:c", Time = now.AddMinutes(-5), Problem = "Spooler parado" }
            ]);
            var lines = HealthReportPresentation.FromStore(db).Split('\n');
            string Line(string text) => lines.First(line => line.Contains(text));
            check(Line("CPU alta").Contains("persiste nas leituras atuais"), "Relatório do assistente indica alerta ainda ativo");
            check(Line("Disco cheio").Contains("resolvida") && !Line("Disco cheio").Contains("persiste"), "Relatório do assistente indica alerta que cessou");
            check(Line("Spooler parado").Contains("estado atual desconhecido"), "Relatório do assistente indica estado desconhecido");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
