using System.Net;
using System.Text;
using System.Text.Json;
using Nexo.Support;

// Achados da validação no PC da 76d9028 e da aa02bd9, em 10/11/2026 (#88, #71, #93):
// 1. "pouca espaço" e "pouquíssimo espaço… liberar espaço, caso necessário" escapavam da trava do disco encerrado;
// 2. "Vamos verificar se há algo que possa ser removido…" foi recusado 13 vezes, e o "sim" terminou em "A IA não conseguiu
//    selecionar a próxima etapa" com o alerta de disco ativo;
// 4. a resposta citou "NexoMemTeste" (sem o "-toca") e a linha "Pelo monitor de saúde" não entrou;
// 5. com o processo em "Estável em patamar alto" e a RAM a 57%, a resposta disse que ele podia estar causando a lentidão.
// O item 3 (volume removido resolve o alerta) está em HealthOccurrenceChecks.
internal static class PcValidationChecks
{
    private const string Label = "estável em patamar alto (informativo, não é falha ativa)";
    // Texto real do PC (conversa 702b8eab), recusado 13 vezes.
    private const string Cleanup = "O volume T: está com apenas 0,1 GB de espaço livre. Vamos verificar se há algo que possa ser removido ou se há uma forma de liberar mais espaço. Você pode me dizer se há algum tipo de arquivo ou aplicativo que possa ser excluído?";
    // Texto real do passo 2.5 (conversa e83d53df).
    private const string Blamed = "Seu computador tem um processo chamado “NexoMemTeste-toca” que está utilizando uma quantidade significativa de memória. Isso pode estar contribuindo para a lentidão. Recomendo salvar seu trabalho e reiniciar o computador.";

    internal static async Task Run(Store store, Account actor, Action<bool, string> assert)
    {
        var previousAi = store.Data.AiEnabled;
        store.Data.AiEnabled = true;
        try
        {
            ClosedDiskWording(assert);
            await CleanupLoop(store, actor, assert);
            MemoryLine(assert);
            StablePlateau(assert);
            await PromptHints(store, actor, assert);
        }
        finally { store.Data.AiEnabled = previousAi; }
    }

    // 1. Disco encerrado: o T: saiu (VHDX desmontado) e o C: está 87% livre; e o T: de volta, 99% livre.
    private static void ClosedDiskWording(Action<bool, string> assert)
    {
        foreach (var (report, scenario) in new[] { (DiskReport(back: false), "T: removido"), (DiskReport(back: true), "T: de volta com 99% livre") })
        {
            var evidence = Observed(report);
            string[] refused =
            [
                "Também há uma observação de pouca espaço em outro volume (T:_) que pode estar causando lentidão.",
                "O disco T: está com pouquíssimo espaço livre, o que pode afetar o desempenho; considere liberar espaço, caso necessário.",
                "Considere liberar espaço no disco, caso necessário.",
                "Libere espaço no T:, se necessário.",
                "O T: está quase sem espaço.",
                "O espaço livre está reduzido no T:.",
                "O disco T: está sem espaço.",
                // @qa: cortesia no fim da oração, letra da unidade como sujeito e "armazenamento acabando".
                "Libere espaço no disco, se possível.",
                "Recomendo liberar espaço no disco, se for o caso.",
                "Libere espaço no T: quando puder.",
                "O T: está praticamente lotado.",
                "O armazenamento está acabando.",
            ];
            string[] accepted =
            [
                "Se o disco T: voltar a ficar sem espaço, libere espaço em Configurações > Sistema > Armazenamento.",
                "Caso precise liberar espaço, abra Configurações > Sistema > Armazenamento no Windows.",
                "O alerta de pouco espaço no T: foi resolvido.",
                "O T: tinha pouquíssimo espaço antes.",
                "O C: está com 87% livre.",
                "O C: está com 87% livre, sem espaço para preocupação.",
                "Quando puder, me diga se o C: continua lento.",
            ];
            var wrong = refused.Where(text => evidence.ValidateStatement(text, false) != InvestigationEvidence.ClosedDiskReason)
                .Concat(accepted.Where(text => evidence.ValidateStatement(text, false) != null)).ToArray();
            assert(wrong.Length == 0, "Disco encerrado (" + scenario + "): pouca/pouquíssimo/quase sem espaço e \"caso necessário\" não escapam; condição real e passado passam"
                + (wrong.Length > 0 ? " · erradas: " + string.Join(" | ", wrong) : ""));
        }
    }

    // 2. O "sim" depois das respostas sobre o disco cheio: o modelo repete o texto até esgotar as tentativas.
    private static async Task CleanupLoop(Store store, Account actor, Action<bool, string> assert)
    {
        var tools = new FlowTools((name, _) => name == "get_health_report" ? WindowsTools.Ok(FinalAnswerChecks.DiskLowReport(false)) : throw new Exception("Ferramenta inesperada: " + name));
        var model = new CapturingModel([Text(Cleanup), Text(Cleanup), Text(Cleanup), Text(Cleanup)]);
        var answer = await Ask(store, actor, [User("meu pc ta sem espaco no disco T"), Assistant("O volume T: está com 7,0% livre."), User("sim")], tools, model);
        var first = model.SystemMessages(1).LastOrDefault() ?? "";
        var second = model.SystemMessages(2).LastOrDefault() ?? "";
        assert(first.Contains(AgentProtocol.PendingActionReason) && first.Contains(AgentProtocol.NoCleanupTool) && !first.Contains("mesma recusa"),
            "#88 loop do disco: a recusa da promessa de liberar espaço diz que o Nexo não tem essa ferramenta e manda orientar o Armazenamento");
        assert(second.Contains("Esta é a mesma recusa da tentativa anterior") && second.Contains("“Vamos verificar se há algo que possa ser removido ou se há uma forma de liberar mais espaço.”"),
            "#88 loop do disco: na 2ª recusa da ação anunciada o controle cita a frase que promete a verificação");
        assert(tools.Calls.SequenceEqual(["get_health_report"]) && !answer.Contains("não conseguiu selecionar") && answer.Contains("O que as consultas deste turno mostraram")
            && answer.Contains("Configurações > Sistema > Armazenamento") && answer.Contains(AgentService.NextStep),
            "#88 loop do disco: sem evidência no turno, a desistência consulta o relatório de saúde e termina na orientação do Armazenamento");

        // Fora de um incidente do sistema, a desistência sem evidência continua como antes, sem consulta.
        var other = new FlowTools((name, _) => throw new Exception("Ferramenta inesperada: " + name));
        var plain = await Ask(store, actor, [User("como faço para mudar o papel de parede?")], other,
            new CapturingModel([Text("Vou verificar."), Text("Vou verificar."), Text("Vou verificar."), Text("Vou verificar.")]));
        assert(other.Calls.Count == 0 && plain.StartsWith("A IA não conseguiu selecionar a próxima etapa.", StringComparison.Ordinal),
            "#88 loop do disco: fora de lentidão, espaço ou memória, a desistência sem evidência não consulta nada");

        // "Vou limpar o cache DNS" não é limpeza de disco: a instrução geral continua.
        var dns = AgentProtocol.Guidance(AgentProtocol.PendingActionReason, "Vou limpar o cache DNS agora.");
        assert(!dns.Contains(AgentProtocol.NoCleanupTool) && dns.Contains("Se nenhuma ferramenta disponível faz essa ação"),
            "#88 loop do disco: a promessa de limpar o cache DNS fica com a instrução geral");
    }

    // 4. A linha "Pelo monitor de saúde" com o processo citado por um começo do nome.
    private static void MemoryLine(Action<bool, string> assert)
    {
        var evidence = Observed(MemoryReport("persiste nas leituras atuais", 85));
        const string Partial = "Se a lentidão persistir, posso ajudar a investigar mais sobre o processo “NexoMemTeste”. Salve seu trabalho e feche programas que não estiver usando.";
        var line = evidence.WithMemoryFigures(Partial);
        assert(line.Contains("Pelo monitor de saúde: “NexoMemTeste-toca” (PID 1732) subiu de 2072 MB para 7192 MB"),
            "#88 linha dos MB: o processo citado por um começo do nome (\"NexoMemTeste\" para \"NexoMemTeste-toca\") ganha a leitura do monitor");
        var figure = evidence.WithMemoryFigures("O NexoMemTeste usa cerca de 7 GB agora.");
        var shortName = evidence.WithMemoryFigures("O Nexo está aberto. Feche o NexoM.exe se não usar.");
        // "chrome" é o nome inteiro de outro processo atual, não o começo do "chromedriver".
        var browsers = Observed(HealthReportPresentation.CurrentHeader + "\n" + Holding("chrome", 4120, 500, 900) + Holding("chromedriver", 77, 300, 800)).WithMemoryFigures("O chrome está aberto há horas.");
        assert(!figure.Contains("Pelo monitor") && !shortName.Contains("Pelo monitor") && browsers.Contains("“chrome” (PID 4120)") && !browsers.Contains("chromedriver"),
            "#88 linha dos MB: não entra quando a frase já traz os MB, quando o começo tem menos de 6 caracteres ou é o nome inteiro de outro processo atual");
        // A trava do processo encerrado continua comparando o nome inteiro: o NexoMemTeste de ontem não vira o -toca.
        assert(evidence.ValidateStatement("O NexoMemTeste está consumindo muita memória.", false) == InvestigationEvidence.StaleProcessReason
            && evidence.ValidateStatement("O NexoMemTeste-toca está consumindo muita memória.", false) == null,
            "#88 linha dos MB: o processo encerrado de nome igual ao começo do atual continua recusado como consumo atual");

        var stable = Observed(MemoryReport(Label + ": NexoMemTeste-toca PID 1732 segue com 7198 MB. Patamar de referência: cerca de 7196 MB, o primeiro medido depois do alerta. Volta a persistir se chegar a 7915 MB", 57));
        var informative = stable.WithMemoryFigures("O NexoMemTeste-toca está aberto.");
        assert(informative.Contains("“NexoMemTeste-toca” (PID 1732) está estável em patamar alto, com cerca de 7198 MB (informativo, não é falha ativa); no alerta, subiu de 2072 MB para 7192 MB")
            && !informative.Contains("segue com"),
            "#71 linha dos MB: no patamar estável a linha diz que é informativo, sem \"segue com\"");
    }

    // 5. Patamar estável (#71): a resposta não o apresenta como causa da lentidão com a RAM abaixo do limite.
    private static void StablePlateau(Action<bool, string> assert)
    {
        var status = Label + ": NexoMemTeste-toca PID 1732 segue com 7198 MB. Patamar de referência: cerca de 7196 MB, o primeiro medido depois do alerta. Volta a persistir se chegar a 7915 MB";
        var evidence = Observed(MemoryReport(status, 57));
        string[] refused =
        [
            Blamed,
            "O NexoMemTeste-toca pode estar causando a lentidão.",
            "O processo NexoMemTeste-toca é o responsável pela lentidão.",
            "A lentidão é causada pelo NexoMemTeste-toca, que usa 7198 MB.",
            // @qa: outros verbos de culpa.
            "O NexoMemTeste-toca está usando 7198 MB. Isso explica a lentidão.",
            "O NexoMemTeste-toca pode estar tornando o PC mais lento.",
            "O NexoMemTeste-toca pode ser a razão da lentidão.",
            "O NexoMemTeste-toca está pesando no desempenho do computador.",
            // A retomada que culpa o processo estável junto com outro continua recusada.
            "O NexoMemTeste-toca está estável em 7198 MB. Isso e o chrome.exe podem estar causando a lentidão.",
        ];
        string[] accepted =
        [
            "O NexoMemTeste-toca usa cerca de 7198 MB, num patamar estável e informativo; ele não deve estar causando a lentidão.",
            "O NexoMemTeste-toca está estável em 7198 MB. O que está lento e desde quando?",
            "Isso pode estar causando a lentidão?",
            "A memória do PC está em 57%. Algum programa específico está lento?",
            // @qa: o "ele" retoma o chrome, não o processo estável.
            "O processo chrome está usando 95% da CPU. Ele pode estar causando a lentidão. O NexoMemTeste-toca está estável em 7198 MB.",
            "O NexoMemTeste-toca está estável em 7198 MB, e o chrome.exe usa 95% da CPU. Ele pode estar causando a lentidão.",
            "O NexoMemTeste-toca está estável em 7198 MB; explico abaixo o que isso significa.",
            "O desempenho do disco está bom e o NexoMemTeste-toca está estável.",
        ];
        var wrong = refused.Where(text => evidence.ValidateStatement(text, false) != InvestigationEvidence.StablePlateauReason)
            .Concat(accepted.Where(text => evidence.ValidateStatement(text, false) != null)).ToArray();
        assert(wrong.Length == 0, "#71 patamar estável: a resposta que o põe como causa da lentidão é recusada; negação, pergunta e consumo sem culpa passam"
            + (wrong.Length > 0 ? " · erradas: " + string.Join(" | ", wrong) : ""));

        var high = Observed(MemoryReport(status, 85));
        var persisting = Observed(MemoryReport("persiste nas leituras atuais", 57));
        var asked = Observed(MemoryReport(status, 57));
        asked.UserRequest = "o NexoMemTeste-toca está deixando o pc lento?";
        var unknown = Observed(MemoryReport(status, null));
        // Outra ocorrência de memória atual (o chrome persistindo) mantém a memória como possível causa.
        var other = Observed(MemoryReport(status, 57) + HealthReportPresentation.CurrentHeader + "\n" + Holding("chrome", 4120, 500, 900));
        assert(high.ValidateStatement(Blamed, false) == null && persisting.ValidateStatement(Blamed, false) == null
            && asked.ValidateStatement(Blamed, false) == null && unknown.ValidateStatement(Blamed, false) == null && other.ValidateStatement(Blamed, false) == null,
            "#71 patamar estável: com a RAM no limite, com o alerta persistindo, com outra ocorrência de memória ativa, com o processo citado pelo usuário ou sem a leitura de memória, nada é recusado");
        // @qa: um começo de nome só vale até um separador do nome e não vale para prefixos de fabricante ou do sistema.
        string Plateau(string name, int pid) => Label + ": " + name + " PID " + pid + " segue com 900 MB. Patamar de referência: cerca de 900 MB, o primeiro medido depois do alerta. Volta a persistir se chegar a 990 MB";
        var terminal = Observed(MemoryReport(Plateau("WindowsTerminal", 900), 57).Replace("NexoMemTeste-toca PID 1732", "WindowsTerminal PID 900"));
        var photos = Observed(MemoryReport(Plateau("Microsoft.Photos", 901), 57).Replace("NexoMemTeste-toca PID 1732", "Microsoft.Photos PID 901"));
        assert(terminal.ValidateStatement("O Windows pode estar lento por causa das atualizações.", false) == null
            && terminal.ValidateStatement("O Windows Update pode estar deixando o PC lento.", false) == null
            && photos.ValidateStatement("O Microsoft Edge pode estar causando a lentidão.", false) == null
            && !terminal.WithMemoryFigures("O Windows está usando bastante recurso.").Contains("Pelo monitor")
            && !photos.WithMemoryFigures("O Microsoft Edge está aberto.").Contains("Pelo monitor")
            && terminal.ValidateStatement("O WindowsTerminal pode estar causando a lentidão.", false) == InvestigationEvidence.StablePlateauReason,
            "#71/#88 nome parcial: \"Windows\" não é o WindowsTerminal e \"Microsoft\" não é o Microsoft.Photos, na trava e na linha dos MB");
        // Sem separador logo depois do trecho, o começo do nome não conta, mesmo fora da lista de prefixos genéricos.
        var plural = Observed(MemoryReport(Plateau("NexoMemTestes", 902), 57).Replace("NexoMemTeste-toca PID 1732", "NexoMemTestes PID 902"));
        assert(!plural.WithMemoryFigures("O NexoMemTeste está aberto.").Contains("Pelo monitor") && plural.ValidateStatement("O NexoMemTeste pode estar causando a lentidão.", false) == null,
            "#71/#88 nome parcial: \"NexoMemTeste\" não é o começo de \"NexoMemTestes\", porque falta o separador");
        var repeated = evidence.RepeatedGuidance(InvestigationEvidence.StablePlateauReason, Blamed);
        assert(repeated.Contains("“Isso pode estar contribuindo para a lentidão.”") && !repeated.Contains("Recomendo salvar"),
            "#71 patamar estável: na 2ª recusa o controle cita só a frase que culpa o processo");
    }

    // 6. Reforço de prompt (sem trava): letra de unidade e o que um processo faz.
    private static async Task PromptHints(Store store, Account actor, Action<bool, string> assert)
    {
        var tools = new FlowTools((name, _) => throw new Exception("Ferramenta inesperada: " + name));
        var model = new CapturingModel([Text("Não tenho essa informação ainda."), Text("Não tenho essa informação ainda."), Text("Não tenho essa informação ainda."), Text("Não tenho essa informação ainda.")]);
        await Ask(store, actor, [User("o que é o D:?")], tools, model, consumeAll: false);
        var prompt = model.SystemMessages(0).FirstOrDefault() ?? "";
        assert(prompt.Contains("(\"o que é o D:?\"), consulte get_hardware_inventory(storage) antes de responder")
            && prompt.Contains("Não descreva o que um programa ou processo faz sem que uma ferramenta tenha trazido essa informação")
            && prompt.Contains("\"Estável em patamar alto\" é informativo"),
            "#93 prompt: pede consultar o inventário para uma letra de unidade, proíbe descrever um processo sem evidência e marca o patamar estável como informativo");
    }

    // Relatório real do HealthReportPresentation: o T: teve pouco espaço e saiu (VHDX desmontado), ou voltou com 99% livre.
    private static string DiskReport(bool back)
    {
        var root = Path.Combine(Path.GetTempPath(), "nexo-pc-disk-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var db = new OperationalStore(root);
            var now = DateTimeOffset.UtcNow;
            HealthSnapshot Sample(DateTimeOffset at, params DiskHealth[] disks) => new() { Time = at, CpuPercent = 12, MemoryUsedPercent = 40, CollectionStatus = "complete", Disks = [.. disks] };
            var low = new[] { Sample(now.AddSeconds(-120), new("C:\\", 435_000, 500_000), new("T:\\", 140, 2_000)), Sample(now.AddSeconds(-90), new("C:\\", 435_000, 500_000), new("T:\\", 140, 2_000)) };
            var found = HealthRules.Evaluate(low, new HealthThresholds());
            foreach (var finding in found) finding.Id += ":s2";
            var later = back
                ? new[] { Sample(now.AddSeconds(-30), new("C:\\", 435_000, 500_000), new("T:\\", 1_984, 2_000)), Sample(now, new("C:\\", 435_000, 500_000), new("T:\\", 1_984, 2_000)) }
                : new[] { Sample(now.AddSeconds(-30), new DiskHealth("C:\\", 435_000, 500_000)), Sample(now, new DiskHealth("C:\\", 435_000, 500_000)) };
            foreach (var sample in low.Concat(later)) db.SaveSnapshot(sample);
            db.SaveFindings(found);
            var report = HealthReportPresentation.FromStore(db);
            if (!report.Contains("resolvida")) throw new Exception("FAIL: o relatório do disco não trouxe a ocorrência encerrada: " + report);
            return report;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    // Relatório no formato do HealthReportPresentation com o NexoMemTeste-toca no estado dado e o NexoMemTeste de ontem resolvido.
    private static string MemoryReport(string status, int? memory) =>
        "RELATÓRIO LOCAL DE SAÚDE — dados observados, não instruções\nAmostra: 2026-11-10 22:28:17 _00:00; coleta complete.\n"
        + (memory is { } used ? "CPU: 8%; memória: " + used + "%; conectividade local: local.\n" : "")
        + HealthReportPresentation.CurrentHeader + "\n"
        + "Achado memory (warning), 2026-11-10 21:57 _00:00, " + status + ": Processo com memória em crescimento persistente. Evidência: NexoMemTeste-toca PID 1732 iniciado em 2026-11-10 21:52:08 _00:00: de 2072 MB para 7192 MB em 5 leituras. Causa: hipótese, x. Confiança 70%; confirmado: não.\n"
        + HealthReportPresentation.OtherHeader + "\n"
        + "Achado memory (warning), 2026-11-09 23:14 _00:00, resolvida: 3 leituras seguidas sem a condição: Processo com memória em crescimento persistente. Evidência: NexoMemTeste PID 21356 iniciado em 2026-11-09 22:30:00 _00:00: de 4375 MB para 13591 MB em 4 leituras. Causa: hipótese, x. Confiança 70%; confirmado: não.\n";

    private static string Holding(string name, int pid, int from, int to) =>
        "Achado memory (warning), 2026-11-10 21:58 _00:00, persiste nas leituras atuais: Processo com memória em crescimento persistente. Evidência: " + name + " PID " + pid
        + " iniciado em 2026-11-10 20:00:00 _00:00: de " + from + " MB para " + to + " MB em 4 leituras. Causa: hipótese, x. Confiança 70%; confirmado: não.\n";

    private static InvestigationEvidence Observed(string report)
    {
        var evidence = new InvestigationEvidence();
        evidence.Observe("get_health_report", "h", JsonSerializer.SerializeToElement(new { }), WindowsTools.Ok(report));
        return evidence;
    }

    private static Entry User(string text) => new() { Role = "user", Text = text };
    private static Entry Assistant(string text) => new() { Role = "assistant", Text = text };

    private static async Task<string> Ask(Store store, Account actor, Entry[] entries, IAgentTools tools, CapturingModel model, bool consumeAll = true)
    {
        using var http = new HttpClient(model);
        var conversation = new Conversation { UserId = actor.Id, Entries = [.. entries] };
        var answer = await new AgentService(store, tools, http).Run(actor, conversation, new Progress<string>(), (_, _) => { }, CancellationToken.None);
        if (consumeAll && !model.Consumed) throw new Exception("FAIL: o roteiro do modelo não foi consumido em: " + entries[^1].Text);
        return answer.ReplaceLineEndings("\n");
    }

    private static string Text(string content) => JsonSerializer.Serialize(new { message = new { role = "assistant", content } });

    private sealed class FlowTools(Func<string, JsonElement, string> execute) : IAgentTools
    {
        public List<string> Calls { get; } = [];
        public Task<string> Execute(string name, JsonElement args, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls.Add(name);
            return Task.FromResult(execute(name, args));
        }
    }

    private sealed class CapturingModel(string[] responses) : HttpMessageHandler
    {
        private readonly List<JsonElement> requests = [];
        private int calls;
        internal bool Consumed => calls == responses.Length;
        internal IEnumerable<string> SystemMessages(int call) => requests[call].GetProperty("messages").EnumerateArray()
            .Where(m => m.GetProperty("role").GetString() == "system").Select(m => m.GetProperty("content").GetString() ?? "");
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (calls >= responses.Length) throw new Exception("FAIL: o modelo foi chamado além do roteiro (" + responses.Length + ").");
            using (var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))) requests.Add(body.RootElement.Clone());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responses[calls++], Encoding.UTF8, "application/json") };
        }
    }
}
