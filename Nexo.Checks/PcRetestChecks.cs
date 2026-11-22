using System.Net;
using System.Text;
using System.Text.Json;
using Nexo.Support;

// Achados da validação no PC da e7a69a2, em 22/11/2026 (#88, #93):
// 1. "o que é o D:?": "posso ajudar você a verificar o que há dentro dessa unidade" passou pela trava do conteúdo de unidade;
// 2. "o que é esse NexoMemTeste?", sem consulta no turno: "O NexoMemTeste é um teste de memória RAM…" passou, porque a trava
//    da função do processo só conhecia os nomes das consultas do turno;
// 3. "o NexoMemTeste é vírus?": o mesmo texto foi recusado 4 vezes pelo motivo da função do processo e o turno terminou em
//    "A IA não conseguiu selecionar a próxima etapa"; a parada na 3ª recusa idêntica só valia para disco e processo encerrado;
// 4. ofertas de ação que o Nexo não tem: "posso ajudar a aumentar o tamanho do arquivo VHD/VHDX" e "posso ajudar a fechá-los";
// 5. depois de uma recusa, a resposta aceita veio só "Posso ajudar?" ou começou solta ("Se a lentidão continuar…").
internal static class PcRetestChecks
{
    private const string Header = "RELATÓRIO LOCAL DE SAÚDE — dados observados, não instruções\nAmostra: 2026-11-22 12:40:00 _00:00; coleta complete.\nCPU: 9%; memória: 54%; conectividade local: local.\n";
    private static readonly string ActiveReport = Header + HealthReportPresentation.CurrentHeader + "\n"
        + "Achado memory (warning), 2026-11-22 12:36 _00:00, persiste nas leituras atuais: Processo com memória em crescimento persistente. Evidência: NexoMemTeste-toca PID 21540 iniciado em 2026-11-22 12:31:00 _00:00: de 2072 MB para 7192 MB em 5 leituras. Causa: hipótese, x. Confiança 70%; confirmado: não.\n";
    // Resposta aceita no passo 2.1 (conversa ff565d60): é dela que o turno seguinte conhece o nome do processo.
    private const string MonitorAnswer = "O NexoMemTeste-toca está usando bastante memória.\n\nPelo monitor de saúde: “NexoMemTeste-toca” (PID 21540) segue com 7193 MB; no alerta, subiu de 2072 MB para 7192 MB.";
    // Textos reais do PC.
    private const string DriveOffer = "O D: é um disco USB (removível) “USB DISK 2.0”. Se quiser, posso ajudar você a verificar o que há dentro dessa unidade.";
    private const string Purpose = "O NexoMemTeste é um teste de memória RAM que verifica se a memória do computador está funcionando corretamente. Ele é usado para identificar problemas como falhas de bits.";
    private const string PurposeOnVirus = "Ele é um software de teste de memória desenvolvido para verificar a integridade da RAM.";
    private const string VhdOffer = "O volume T: está com 0,1 GB livres. Se precisar, posso ajudar a aumentar o tamanho do arquivo VHD/VHDX. Deseja que eu faça isso?";
    private const string CloseOffer = "A memória do PC está em 81% de uso. Posso ajudar a fechá-los.";

    internal static async Task Run(Store store, Account actor, Action<bool, string> assert)
    {
        var previousAi = store.Data.AiEnabled;
        var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
        store.Data.AiEnabled = true;
        try
        {
            foreach (var culture in new[] { "pt-BR", "en-US", "de-DE" })
            {
                System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
                DriveContentsOffer(assert, culture);
                ProcessPurposeWithoutQuery(assert, culture);
                UnsupportedActions(assert, culture);
            }
            await RepeatedPurpose(store, actor, assert);
            await RepeatedAnyReason(store, actor, assert);
            await PurposeFlowWithoutQuery(store, actor, assert);
            await ThinAnswerAfterRefusal(store, actor, assert);
        }
        finally { store.Data.AiEnabled = previousAi; System.Globalization.CultureInfo.CurrentCulture = previousCulture; }
    }

    // 1. Oferta de olhar dentro da unidade, em qualquer forma; a orientação para o Explorador continua valendo.
    private static void DriveContentsOffer(Action<bool, string> assert, string culture)
    {
        var evidence = new InvestigationEvidence();
        string[] refused =
        [
            DriveOffer,
            DriveOffer.ReplaceLineEndings("\r\n").Replace(". Se", ".\r\nSe"),
            "Posso ver o que tem no D: para você.",
            "Posso conferir o que está guardado no pendrive D:.",
            "Deseja que eu veja o que há dentro do D:?",
            "Vou verificar o que existe nessa unidade.",
            "Posso verificar o que há atualmente no D:.",
            "Posso verificar o que está no D:.",
        ];
        string[] accepted =
        [
            "O D: é um disco USB (removível) “USB DISK 2.0”.",
            "Para ver o que há dentro do D:, abra o Explorador de Arquivos.",
            "Posso te orientar a ver o que há dentro do D: pelo Explorador de Arquivos.",
            "O inventário não mostra o que há dentro do D:; abra o Explorador de Arquivos para ver.",
            "Posso verificar o que está causando a lentidão no disco C:.",
            "Não posso ver o que há dentro do D:.",
            "Posso verificar o que há de errado no disco C:.",
            "Posso verificar o que está errado no disco C:.",
            "Posso verificar o que está acontecendo no disco C:.",
            // Sem unidade na resposta, "o que ficou salvo" não é conteúdo de disco (@qa).
            "Posso verificar o que ficou salvo no histórico do navegador.",
        ];
        var wrong = refused.Where(text => evidence.ValidateStatement(text, false) != InvestigationEvidence.DriveContentsReason)
            .Concat(accepted.Where(text => evidence.ValidateStatement(text, false) != null)).ToArray();
        assert(wrong.Length == 0, "Validação de 22/11 (#93), " + culture + ": oferta de ver \"o que há dentro\" da unidade é recusada; orientação ao Explorador e negação passam"
            + (wrong.Length > 0 ? " · erradas: " + string.Join(" | ", wrong) : ""));
    }

    // 2. Sem consulta no turno, o nome vem do pedido do usuário ou da linha do monitor numa resposta anterior.
    private static void ProcessPurposeWithoutQuery(Action<bool, string> assert, string culture)
    {
        var asked = new InvestigationEvidence { UserRequest = "o que é esse NexoMemTeste?" };
        var followUp = new InvestigationEvidence { UserRequest = "e isso é normal?", PriorAnswers = [MonitorAnswer.ReplaceLineEndings("\r\n")] };
        string[] refused =
        [
            Purpose,
            "O NexoMemTeste é usado para identificar falhas na memória.",
            "O NexoMemTeste serve para verificar a RAM.",
            "O NexoMemTeste é uma ferramenta de verificação da memória.",
        ];
        string[] refusedFollowUp =
        [
            "O NexoMemTeste-toca é um teste de estresse de memória.",
            "O NexoMemTeste-toca, que testa a memória, está usando 7193 MB.",
        ];
        string[] accepted =
        [
            "O NexoMemTeste usa 7193 MB, segundo o monitor de saúde.",
            "Não tenho informação sobre a função do NexoMemTeste; confira o nome e o local do arquivo no Gerenciador de Tarefas.",
            "Você sabe se o NexoMemTeste é um teste que você iniciou?",
            "Se você iniciou um teste de memória, confira o nome do programa no Gerenciador de Tarefas.",
            "O monitor de saúde verifica a memória a cada leitura.",
            "Não tenho informação se o NexoMemTeste é um teste de memória.",
        ];
        var wrong = refused.Where(text => asked.ValidateStatement(text, false) != InvestigationEvidence.ProcessPurposeReason)
            .Concat(refusedFollowUp.Where(text => followUp.ValidateStatement(text, false) != InvestigationEvidence.ProcessPurposeReason))
            .Concat(accepted.Where(text => asked.ValidateStatement(text, false) != null || followUp.ValidateStatement(text, false) != null)).ToArray();
        // O malware também: sem consulta, o nome citado pelo usuário liga a frase ao processo.
        var malware = new InvestigationEvidence { UserRequest = "o que é esse NexoMemTeste?" }.ValidateStatement("O NexoMemTeste pode ser um malware.", false);
        // O nome com ".exe" no pedido também conta, mesmo sem maiúscula no meio (@qa).
        var exe = new InvestigationEvidence { UserRequest = "o que é esse svcmon.exe?" }.ValidateStatement("O svcmon.exe serve para monitorar serviços.", false);
        assert(wrong.Length == 0 && malware == InvestigationEvidence.MalwareReason && exe == InvestigationEvidence.ProcessPurposeReason,
            "Validação de 22/11 (#93), " + culture + ": sem consulta no turno, a função inventada do processo citado pelo usuário ou pelo monitor é recusada; consumo, dúvida e pergunta passam"
            + (wrong.Length > 0 ? " · erradas: " + string.Join(" | ", wrong) : "") + (malware != InvestigationEvidence.MalwareReason ? " · malware: " + malware : "")
            + (exe != InvestigationEvidence.ProcessPurposeReason ? " · .exe: " + exe : ""));
    }

    // 4. Ação que nenhuma ferramenta faz: aumentar VHDX/volume e fechar programas.
    private static void UnsupportedActions(Action<bool, string> assert, string culture)
    {
        var evidence = new InvestigationEvidence();
        string[] refused =
        [
            VhdOffer,
            VhdOffer.Replace(". Se", ".\r\nSe"),
            CloseOffer,
            "Quer que eu encerre o processo NexoMemTeste-toca?",
            "Posso expandir o volume T: para você.",
            "Posso aumentar o arquivo VHDX do T:.",
            "Vou redimensionar a partição do T: para você.",
            "Se quiser, eu te ajudo a fechar os programas abertos.",
        ];
        string[] accepted =
        [
            "Para aumentar o tamanho do VHDX, use o Gerenciamento de Disco do Windows.",
            "Posso explicar como fechar os programas pelo Gerenciador de Tarefas.",
            "Feche os programas que não estiver usando pelo Gerenciador de Tarefas.",
            "Não posso fechar programas, mas você pode usar o Gerenciador de Tarefas.",
            "Vou encerrar este atendimento com o que as consultas mostraram.",
            "Para fechar um programa, abra o Gerenciador de Tarefas (Ctrl+Shift+Esc), selecione-o e clique em Finalizar tarefa.",
            "Posso ampliar a verificação para o disco C:.",
            "Posso estender a análise ao volume D:.",
            "Posso estender o diagnóstico para o disco T:.",
        ];
        var wrong = refused.Where(text => evidence.ValidateStatement(text, false) != InvestigationEvidence.UnsupportedActionReason)
            .Concat(accepted.Where(text => evidence.ValidateStatement(text, false) != null)).ToArray();
        var reason = InvestigationEvidence.UnsupportedActionReason;
        assert(wrong.Length == 0 && reason.Contains("Gerenciador de Tarefas") && reason.Contains("Gerenciamento de Disco"),
            "Validação de 22/11 (#88), " + culture + ": oferta de aumentar VHDX/volume ou fechar programas é recusada com o caminho manual; a explicação ao usuário passa"
            + (wrong.Length > 0 ? " · erradas: " + string.Join(" | ", wrong) : ""));
        var repeated = evidence.RepeatedGuidance(reason, VhdOffer);
        assert(repeated.Contains("“Se precisar, posso ajudar a aumentar o tamanho do arquivo VHD/VHDX.”") && !repeated.Contains("0,1 GB"),
            "Validação de 22/11 (#88), " + culture + ": na 2ª recusa da oferta sem ferramenta, o controle cita só a frase da oferta");
    }

    // 3. O caso real f5774bf6: 4 recusas idênticas pela função do processo e "A IA não conseguiu…".
    private static async Task RepeatedPurpose(Store store, Account actor, Action<bool, string> assert)
    {
        var tools = new FlowTools((name, _) => name == "get_health_report" ? WindowsTools.Ok(ActiveReport) : throw new Exception("Ferramenta inesperada: " + name));
        var model = new CapturingModel([Text(PurposeOnVirus), Text(PurposeOnVirus), Text(PurposeOnVirus), Text(PurposeOnVirus)]);
        var answer = await Ask(store, actor, [User("meu pc ta lento"), Assistant(MonitorAnswer), User("o NexoMemTeste é vírus?")], tools, model, consumeAll: false);
        // Sem lentidão antes na conversa, a recusa sobre o processo também leva ao relatório de saúde.
        var alone = new FlowTools((name, _) => name == "get_health_report" ? WindowsTools.Ok(ActiveReport) : throw new Exception("Ferramenta inesperada: " + name));
        var aloneModel = new CapturingModel([Text(Purpose), Text(Purpose), Text(Purpose), Text(Purpose)]);
        var aloneAnswer = await Ask(store, actor, [User("o NexoMemTeste é vírus?")], alone, aloneModel, consumeAll: false);
        assert(model.Calls == 3 && !answer.Contains("A IA não conseguiu", StringComparison.Ordinal) && !answer.Contains(PurposeOnVirus, StringComparison.Ordinal)
            && answer.Contains("NexoMemTeste-toca PID 21540") && answer.Contains(InvestigationEvidence.SecurityGuidance)
            && aloneModel.Calls == 3 && aloneAnswer.Contains("NexoMemTeste-toca PID 21540") && !aloneAnswer.Contains("A IA", StringComparison.Ordinal),
            "Validação de 22/11 (#93): a 3ª resposta idêntica recusada pela função do processo encerra com o relatório de saúde e a orientação da Segurança do Windows, sem 4ª chamada ao modelo"
            + " · chamadas " + model.Calls + " · " + answer.Replace("\n", " ")[..Math.Min(answer.Length, 220)]);
    }

    // 3. Qualquer motivo: o texto idêntico recusado 3 vezes para na 3ª, também no finish_support.
    private static async Task RepeatedAnyReason(Store store, Account actor, Action<bool, string> assert)
    {
        var tools = new FlowTools((name, _) => name switch
        {
            "get_health_report" => WindowsTools.Ok(ActiveReport),
            "diagnose_device" => WindowsTools.Ok("Memória livre: 14.7 GB de 31.9 GB\r\nDisco C:: 250.0 GB livres de 476.3 GB"),
            _ => throw new Exception("Ferramenta inesperada: " + name)
        });
        var text = new CapturingModel([Text(CloseOffer), Text(CloseOffer), Text(CloseOffer), Text(CloseOffer)]);
        var stopped = await Ask(store, actor, [User("meu pc ta lento")], tools, text, consumeAll: false);
        var finish = new CapturingModel([Call("finish_support", new { message = CloseOffer }), Call("finish_support", new { message = CloseOffer }),
            Call("finish_support", new { message = CloseOffer }), Call("finish_support", new { message = CloseOffer })]);
        var finished = await Ask(store, actor, [User("meu pc ta lento")], tools, finish, consumeAll: false);
        assert(text.Calls == 3 && finish.Calls == 3 && stopped.StartsWith("O que as consultas deste turno mostraram:", StringComparison.Ordinal)
            && finished.StartsWith("O que as consultas deste turno mostraram:", StringComparison.Ordinal)
            && !stopped.Contains("A IA não conseguiu", StringComparison.Ordinal) && !finished.Contains("A IA não conseguiu", StringComparison.Ordinal),
            "Validação de 22/11 (#88/#93): resposta idêntica recusada 3 vezes por qualquer motivo encerra com os dados, no texto e no finish_support");
    }

    // 2. O fluxo real do passo 2.2: nenhuma ferramenta no turno, o nome vem do pedido e da resposta anterior.
    private static async Task PurposeFlowWithoutQuery(Store store, Account actor, Action<bool, string> assert)
    {
        const string honest = "Não tenho informação sobre a função do NexoMemTeste. Pelo monitor, ele usa cerca de 7193 MB; confira o nome e o local do arquivo no Gerenciador de Tarefas.";
        var tools = new FlowTools((name, _) => name == "get_health_report" ? WindowsTools.Ok(ActiveReport) : throw new Exception("Ferramenta inesperada: " + name));
        var model = new CapturingModel([Text(Purpose), Text(honest)]);
        var answer = await Ask(store, actor, [User("meu pc ta lento"), Assistant(MonitorAnswer), User("o que é esse NexoMemTeste?")], tools, model);
        // O pedido não traz o nome: ele vem da linha do monitor na resposta anterior.
        var unnamed = new CapturingModel([Text("O NexoMemTeste-toca é um teste de estresse de memória."), Text(honest)]);
        var unnamedAnswer = await Ask(store, actor, [User("meu pc ta lento"), Assistant(MonitorAnswer), User("e esse programa aí, o que ele faz?")], tools, unnamed);
        assert(answer.StartsWith(honest, StringComparison.Ordinal) && (model.SystemMessages(1).LastOrDefault() ?? "").Contains(InvestigationEvidence.ProcessPurposeReason)
            && unnamedAnswer.StartsWith(honest, StringComparison.Ordinal) && (unnamed.SystemMessages(1).LastOrDefault() ?? "").Contains(InvestigationEvidence.ProcessPurposeReason),
            "Validação de 22/11 (#93): sem consulta, a função inventada é recusada com o nome do pedido ou da linha do monitor da resposta anterior, e a resposta honesta passa");
    }

    // 5. Resposta aceita depois de recusa, sem nenhum fato do turno: o Nexo põe antes o que as consultas mostraram.
    private static async Task ThinAnswerAfterRefusal(Store store, Account actor, Action<bool, string> assert)
    {
        var tools = new FlowTools((name, _) => name switch
        {
            "get_health_report" => WindowsTools.Ok(ActiveReport),
            "diagnose_device" => WindowsTools.Ok("Memória livre: 14.7 GB de 31.9 GB\r\nDisco C:: 250.0 GB livres de 476.3 GB"),
            _ => throw new Exception("Ferramenta inesperada: " + name)
        });
        var results = new List<(string Answer, string Thin)>();
        foreach (var thin in new[] { "Posso ajudar?", "Se a lentidão continuar, feche os programas que não estiver usando." })
        {
            var model = new CapturingModel([Text(CloseOffer), Text(thin)]);
            results.Add((await Ask(store, actor, [User("meu pc ta lento")], tools, model), thin));
        }
        // Sem recusa antes, a resposta do modelo não muda; com recusa, uma resposta que cita a leitura também não.
        const string grounded = "O NexoMemTeste-toca (PID 21540) está com 7193 MB. Feche-o pelo Gerenciador de Tarefas se não o reconhece.";
        var direct = await Ask(store, actor, [User("meu pc ta lento")], tools, new CapturingModel([Text("Posso ajudar?")]));
        var kept = await Ask(store, actor, [User("meu pc ta lento")], tools, new CapturingModel([Text(CloseOffer), Text(grounded)]));
        assert(results.All(r => r.Answer.StartsWith("O que as consultas deste turno mostraram:", StringComparison.Ordinal) && r.Answer.Contains("NexoMemTeste-toca PID 21540") && r.Answer.Contains(r.Thin))
            && direct.StartsWith("Posso ajudar?", StringComparison.Ordinal) && kept.StartsWith(grounded, StringComparison.Ordinal),
            "Validação de 22/11 (#88): depois de uma recusa, a resposta sem nenhum fato do turno vem depois do que as consultas mostraram; sem recusa ou com fato citado, fica como está"
            + " · " + string.Join(" | ", results.Select(r => r.Answer.Replace("\n", " ")[..Math.Min(r.Answer.Length, 120)])));
        // Um número ou uma letra de unidade já é fato do turno, mesmo numa resposta curta ou que começa por condição (@qa).
        var probe = new InvestigationEvidence();
        string[] grounded2 = ["Se a memória passar de 90%, feche o que não estiver usando.", "O C: está ok.", "Caso o T: encha de novo, abra o Armazenamento."];
        string[] thin2 = ["Posso ajudar?", "Se a lentidão continuar, reinicie o computador."];
        var misjudged = grounded2.Where(probe.IsThinAnswer).Concat(thin2.Where(t => !probe.IsThinAnswer(t))).ToArray();
        assert(misjudged.Length == 0, "Validação de 22/11 (#88): número ou letra de unidade tira a resposta da categoria pobre; sem fato, curta ou condicional continua pobre"
            + (misjudged.Length > 0 ? " · erradas: " + string.Join(" | ", misjudged) : ""));
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
    private static string Call(string name, object args) => JsonSerializer.Serialize(new { message = new { role = "assistant", content = "", tool_calls = new[] { new { function = new { name, arguments = args } } } } });

    private sealed class FlowTools(Func<string, JsonElement, string> execute) : IAgentTools
    {
        public Task<string> Execute(string name, JsonElement args, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(execute(name, args));
        }
    }

    private sealed class CapturingModel(string[] responses) : HttpMessageHandler
    {
        private readonly List<JsonElement> requests = [];
        private int calls;
        internal bool Consumed => calls == responses.Length;
        internal int Calls => calls;
        internal IEnumerable<string> SystemMessages(int call) => requests[call].GetProperty("messages").EnumerateArray()
            .Where(m => m.GetProperty("role").GetString() == "system").Select(m => m.GetProperty("content").GetString() ?? "");
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (calls >= responses.Length) throw new Exception("O modelo foi chamado além do roteiro (" + responses.Length + ").");
            using (var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))) requests.Add(body.RootElement.Clone());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responses[calls++], Encoding.UTF8, "application/json") };
        }
    }
}
