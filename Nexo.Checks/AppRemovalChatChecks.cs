using System.Text.RegularExpressions;
using System.Net;
using System.Text;
using System.Text.Json;
using Nexo.Support;
namespace Nexo.Checks;

// Pedido de remover apps pelo chat (#127). Na validação no PC da 63f01f0 (Windows 10 22H2, 23/11/2026), "remove os apps inúteis
// do windows pra mim" teve 4 recusas pelo motivo genérico da oferta "Se você quiser remover aplicativos específicos, posso ajudar
// com isso… me diga quais aplicativos você deseja remover" e terminou em "A IA repetiu a mesma resposta recusada", sem indicar o
// caminho. A remoção existe só no menu Ferramentas: a oferta tem motivo próprio e o fim do turno indica o cartão.
internal static class AppRemovalChatChecks
{
    // Texto real do PC (resposta recusada 4 vezes).
    private const string PcOffer = "Entendo que você quer remover aplicativos desnecessários do Windows. Se você quiser remover aplicativos específicos, posso ajudar com isso. Por favor, me diga quais aplicativos você deseja remover.";

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
                Offers(assert, culture);
                Requests(assert, culture);
            }
            GuidanceLine(assert);
            CopiedPath(assert);
            await DirectAnswer(store, actor, assert);
            await RepeatedOfferFlow(store, actor, assert);
            await SymptomKeepsNextStep(store, actor, assert);
            await MixedAnswerGetsPath(store, actor, assert);
            await NoPathWithoutRemoval(store, actor, assert);
            await QaCases(store, actor, assert);
            await PcMixedRefusal(store, actor, assert);
            await SelfOfferGetsLine(store, actor, assert);
            await CopiedWithoutRefusal(store, actor, assert);
            RepeatedAnswerRule(assert);
            await PcCopyRefused(store, actor, assert);
            await PcCopyGivesUp(store, actor, assert);
            ItemAnswers(assert);
            StepsAndUnused(assert);
            await PcXboxQuestion(store, actor, assert);
            await ChromeGoesToModel(store, actor, assert);
        }
        finally { store.Data.AiEnabled = previousAi; System.Globalization.CultureInfo.CurrentCulture = previousCulture; }
    }

    private static string? Validate(string text) => AgentProtocol.Validate("finish_support", JsonSerializer.SerializeToElement(new { }), text);

    private static void Offers(Action<bool, string> assert, string culture)
    {
        string[] refused =
        [
            PcOffer,
            "Se você quiser remover aplicativos específicos, posso ajudar com isso.",
            "Por favor, me diga quais aplicativos você deseja remover.",
            "Posso ajudar a remover os apps inúteis do Windows.",
            "Quer que eu desinstale os programas que você não usa?",
            "Me diga quais apps você quer remover e eu cuido disso.",
            "Posso limpar os aplicativos pré-instalados do Windows para você.",
            "Vou desinstalar o Clipchamp e o Dev Home.",
            "Deseja que eu remova o bloatware do Windows?",
            // @qa: variações do qwen3:8b.
            "Vamos remover juntos os apps que você não usa.",
            "Posso te ajudar a desinstalar o Xbox.",
            "Deseja que eu desinstale o Xbox?",
            "Me passe a lista dos aplicativos que você quer desinstalar.",
        ];
        string[] accepted =
        [
            AgentProtocol.AppRemovalGuidance,
            "Não posso remover aplicativos pelo chat.",
            "Posso explicar como desinstalar um programa em Configurações > Aplicativos.",
            "Você pode remover apps em Ferramentas > Apps pré-instalados e ajustes do Windows.",
            "Se quiser remover algum app, use o cartão Apps pré-instalados e ajustes do Windows, em Ferramentas.",
            "Posso verificar se o app foi removido.",
            "Posso limpar a fila de impressão do aplicativo.",
            "Posso apagar o cache do aplicativo.",
            "Posso desinstalar o driver da impressora.",
            "Vou reinstalar o driver de vídeo.",
            "O app do banco travou; posso consultar os erros recentes.",
            "Posso ajudar você a remover os apps pelo cartão Apps pré-instalados e ajustes do Windows, em Ferramentas.",
            // @qa: o passado é pergunta de diagnóstico de quem desinstalou algo e viu outro problema aparecer.
            "Me diga qual programa você desinstalou e quando isso aconteceu.",
            "Me diga quando você removeu o aplicativo.",
            "Posso verificar os eventos desde que você desinstalou o programa.",
            "Posso verificar se o programa que você removeu deixou erros nos eventos.",
            "Me diga se os aplicativos foram desinstalados por outra pessoa.",
            "Posso desinstalar a versão anterior do driver NVIDIA.",
            "Posso cancelar os trabalhos da impressora selecionada.",
            "Posso desabilitar o dispositivo e habilitá-lo de novo.",
        ];
        var wrongRefused = refused.Where(text => !AgentProtocol.OffersAppRemoval(AgentProtocol.Normalize(text)) || Validate(text) != AgentProtocol.AppRemovalReason).ToArray();
        var wrongAccepted = accepted.Where(text => AgentProtocol.OffersAppRemoval(AgentProtocol.Normalize(text)) || Validate(text) == AgentProtocol.AppRemovalReason).ToArray();
        assert(wrongRefused.Length == 0 && wrongAccepted.Length == 0,
            "#127 (PC 63f01f0), " + culture + ": a oferta de remover ou desinstalar apps pelo chat tem motivo próprio; a negação, a explicação, o caminho de Ferramentas, o driver e o cache passam"
            + (wrongRefused.Length > 0 ? " · deviam ser recusadas: " + string.Join(" | ", wrongRefused) : "") + (wrongAccepted.Length > 0 ? " · deviam passar: " + string.Join(" | ", wrongAccepted) : ""));
        // Repetição no PC da fcca5cd: o modelo copiou "indique **Ferramentas > Apps pré-instalados** e ajustes do Windows". O texto
        // do Nexo traz a frase dirigida ao usuário, com o nome inteiro do cartão entre aspas. Na repetição da e7c8b32, o motivo mandava
        // dizer o caminho "com estas palavras" e a resposta perdeu a lentidão consultada: o motivo não traz mais o caminho e pede
        // para manter o resto. O texto pronto diz o que "Verificar este PC" mostra ("ok, e o que eu removo?" não era respondido).
        const string card = "“Apps pré-instalados e ajustes do Windows”, no menu Ferramentas";
        var reason = AgentProtocol.AppRemovalReason;
        assert(!reason.Contains(card, StringComparison.Ordinal) && !reason.Contains("com estas palavras", StringComparison.Ordinal) && !reason.Contains("Apps pré-instalados", StringComparison.Ordinal)
            && reason.Contains("mantenha o resto", StringComparison.Ordinal) && reason.Contains("o que as consultas mostraram", StringComparison.Ordinal)
            && AgentProtocol.AppRemovalGuidance.Contains(card, StringComparison.Ordinal) && AgentProtocol.AppRemovalLine.Contains(card, StringComparison.Ordinal)
            && AgentProtocol.AppRemovalGuidance.Contains("“Verificar este PC”", StringComparison.Ordinal) && AgentProtocol.AppRemovalGuidance.Contains("já marcados", StringComparison.Ordinal)
            && AgentProtocol.AppRemovalLine.Contains("“Verificar este PC”", StringComparison.Ordinal) && AgentProtocol.AppRemovalLine.Length < AgentProtocol.AppRemovalGuidance.Length
            && !reason.Contains("indique", StringComparison.Ordinal) && !AgentProtocol.AppRemovalGuidance.Contains("indique", StringComparison.Ordinal) && !AgentProtocol.AppRemovalLine.Contains("indique", StringComparison.Ordinal)
            && Validate(AgentProtocol.AppRemovalGuidance) == null && Validate(AgentProtocol.AppRemovalLine) == null
            && AgentProtocol.Guidance(reason, PcOffer) == reason,
            "#127 (PC e7c8b32), " + culture + ": o motivo pede para tirar a oferta e manter o resto, sem o caminho; o texto e a linha do Nexo indicam o cartão pelo nome inteiro e o que \"Verificar este PC\" mostra");
        var repeated = new InvestigationEvidence().RepeatedGuidance(AgentProtocol.AppRemovalReason, PcOffer);
        assert(repeated.Contains("posso ajudar com isso", StringComparison.Ordinal) && repeated.Contains("me diga quais aplicativos", StringComparison.Ordinal),
            "#127, " + culture + ": na 2ª recusa, o controle cita os trechos da oferta · " + repeated);
    }

    private static void Requests(Action<bool, string> assert, string culture)
    {
        (string Text, bool Removal)[] cases =
        [
            ("remove os apps inúteis do windows pra mim", true),
            ("remove os apps inuteis do windows pra mim", true),
            ("quero desinstalar uns aplicativos que vieram no pc", true),
            ("como tirar o bloatware do windows?", true),
            ("faz um debloat no meu pc", true),
            ("tem muito app inútil aqui", true),
            ("limpa os programas que eu não uso", true),
            ("o app do banco travou", false),
            ("quero reinstalar o app da câmera", false),
            ("remover vírus do pc", false),
            ("limpar arquivos temporários", false),
            ("como tirar print do app?", false),
            ("apagar o cache do aplicativo", false),
            ("meu pc ta lento", false),
            ("quero remover e reinstalar o app da câmera", false),
            // @qa: relato no passado e tirar da inicialização não são pedido de remover apps.
            ("desinstalei o app e agora a impressora não imprime", false),
            ("removi um programa e o som parou", false),
            ("desinstalaram um aplicativo aqui e parou a câmera", false),
            ("apaguei o aplicativo sem querer e quero ele de volta", false),
            ("tirar o programa da inicialização", false),
            ("como tiro os programas que abrem junto com o windows?", false),
            ("como removo os apps que vieram no pc?", true),
            ("tira esses programas inúteis", true),
            ("meu pc ta lento, remove os apps inúteis", true),
        ];
        var wrong = cases.Where(c => SupportIntake.AsksAppRemoval(c.Text) != c.Removal).Select(c => c.Text).ToArray();
        assert(wrong.Length == 0, "#127 (PC 63f01f0), " + culture + ": o pedido de remover apps pré-instalados é reconhecido; app travado, reinstalar, vírus, temporários, print e cache não"
            + (wrong.Length > 0 ? " · erradas: " + string.Join(" | ", wrong) : ""));
    }

    // Repetição no PC da fcca5cd: o pedido só de remover apps recebe o texto do Nexo, sem chamar o modelo nem ferramentas.
    private static async Task DirectAnswer(Store store, Account actor, Action<bool, string> assert)
    {
        string[] requests = ["remove os apps inúteis do windows pra mim", "quero desinstalar uns aplicativos que vieram no pc", "faz um debloat no meu pc"];
        var wrong = new List<string>();
        foreach (var request in requests)
        {
            var model = new CapturingModel([]);
            using var http = new HttpClient(model);
            var conversation = new Conversation { UserId = actor.Id, Entries = [new Entry { Role = "user", Text = request }] };
            var tools = new FlowTools();
            var answer = (await new AgentService(store, tools, http).Run(actor, conversation, new Progress<string>(), (_, _) => { }, CancellationToken.None)).ReplaceLineEndings("\n");
            if (model.Calls != 0 || tools.Calls.Count != 0 || !answer.StartsWith(AgentProtocol.AppRemovalGuidance, StringComparison.Ordinal) || answer.Contains("indique", StringComparison.Ordinal))
                wrong.Add(request + " → " + answer.Replace("\n", " "));
        }
        assert(wrong.Count == 0, "#127 (PC fcca5cd): o pedido só de remover apps recebe o texto do Nexo, sem o modelo e sem ferramentas"
            + (wrong.Count > 0 ? " · erradas: " + string.Join(" | ", wrong) : ""));
    }

    // A oferta repetida 3 vezes num pedido que não é de remoção encerra o turno indicando o cartão, sem "A IA repetiu"; o motivo é o próprio.
    private static async Task RepeatedOfferFlow(Store store, Account actor, Action<bool, string> assert)
    {
        var model = new CapturingModel([Text(PcOffer), Text(PcOffer), Text(PcOffer)]);
        var answer = await Ask(store, actor, "o que dá pra fazer pra deixar o windows mais limpo?", model);
        var control = model.SystemMessages(1).LastOrDefault() ?? "";
        var prompt = model.SystemMessages(0).FirstOrDefault() ?? "";
        assert(answer.Contains(AgentProtocol.AppRemovalGuidance, StringComparison.Ordinal) && !answer.Contains("A IA repetiu", StringComparison.Ordinal)
            && !answer.Contains("posso ajudar com isso", StringComparison.Ordinal) && control.Contains(AgentProtocol.AppRemovalReason, StringComparison.Ordinal)
            && !control.Contains("Use a ferramenta para a próxima consulta", StringComparison.Ordinal)
            && prompt.Contains("O Nexo não remove nem desinstala apps pelo chat", StringComparison.Ordinal) && !prompt.Contains("indique Ferramentas", StringComparison.Ordinal)
            && prompt.Contains("sem repetir uma resposta anterior", StringComparison.Ordinal),
            "#127 (PC 63f01f0): a oferta repetida de remover apps é recusada com o motivo próprio e o turno termina indicando o cartão de Ferramentas · " + answer.Replace("\n", " "));
    }

    // A linha do cartão: entra antes da oferta ou pergunta final, não duplica quando a resposta já cita o cartão.
    private static void GuidanceLine(Action<bool, string> assert)
    {
        const string pc = "A lentidão do seu PC pode estar relacionada a processos em execução. 1. **Verifique os processos em execução**. 2. **Reinicie o PC**. 3. **Verifique se há atualizações pendentes**. Você pode me dizer se algum desses pontos se aplica ao seu caso?";
        var withLine = InvestigationEvidence.WithAppRemovalGuidance(pc);
        var line = withLine.IndexOf(AgentProtocol.AppRemovalLine, StringComparison.Ordinal);
        var question = withLine.IndexOf("Você pode me dizer", StringComparison.Ordinal);
        string[] cited =
        [
            "Para remover apps, use o cartão “Apps pré-instalados e ajustes do Windows”, no menu Ferramentas.",
            "Os apps saem pelo menu Ferramentas > Apps pré-instalados e ajustes do Windows.",
            AgentProtocol.AppRemovalGuidance,
            AgentProtocol.AppRemovalLine,
        ];
        var duplicated = cited.Where(text => InvestigationEvidence.WithAppRemovalGuidance(text) != text).ToArray();
        assert(line > 0 && question > line && withLine.EndsWith("ao seu caso?", StringComparison.Ordinal) && duplicated.Length == 0,
            "#127 (PC fcca5cd): a linha do cartão entra antes da pergunta final e não se repete quando a resposta já cita o cartão · "
            + withLine.Replace("\n", " ") + (duplicated.Length > 0 ? " · duplicou: " + string.Join(" | ", duplicated) : ""));
    }

    // Repetição no PC da fcca5cd: "meu pc ta lento, remove os apps inúteis" investigou a lentidão e terminou com o texto do modelo,
    // sem o caminho do cartão. A resposta aceita do pedido misto ganha a linha antes da pergunta final.
    private static async Task MixedAnswerGetsPath(Store store, Account actor, Action<bool, string> assert)
    {
        const string accepted = "A lentidão do seu PC pode estar relacionada a processos em execução. O monitor de saúde não mostra ocorrência ativa. Você pode me dizer se a lentidão aparece em algum programa específico?";
        var model = new CapturingModel([Text(accepted)]);
        using var http = new HttpClient(model);
        var conversation = new Conversation { UserId = actor.Id, Entries = [new Entry { Role = "user", Text = "meu pc ta lento, remove os apps inúteis" }] };
        var tools = new FlowTools(name => name == "get_health_report" ? WindowsTools.Ok("Espaço livre por volume: C: 77,7% livre.") : null);
        var answer = (await new AgentService(store, tools, http).Run(actor, conversation, new Progress<string>(), (_, _) => { }, CancellationToken.None)).ReplaceLineEndings("\n");
        var line = answer.IndexOf(AgentProtocol.AppRemovalLine, StringComparison.Ordinal);
        var question = answer.IndexOf("Você pode me dizer", StringComparison.Ordinal);
        assert(model.Consumed && tools.Calls.Contains("get_health_report") && line > 0 && question > line
            && answer.IndexOf(AgentProtocol.AppRemovalLine, line + 1, StringComparison.Ordinal) < 0,
            "#127 (PC fcca5cd): no pedido misto, a resposta aceita do modelo ganha o caminho do cartão antes da pergunta final · " + answer.Replace("\n", " "));
    }

    // Sem pedido de remoção (lentidão só, relato no passado), a resposta aceita não ganha a linha do cartão.
    private static async Task NoPathWithoutRemoval(Store store, Account actor, Action<bool, string> assert)
    {
        var wrong = new List<string>();
        foreach (var (request, accepted) in new[]
        {
            ("meu pc ta lento", "O monitor de saúde não mostra ocorrência ativa. Você pode me dizer se a lentidão aparece em algum programa específico?"),
            ("desinstalei um programa ontem e agora o som não funciona", "O monitor de saúde não mostra ocorrência ativa. Você pode me dizer se o som falha em todos os programas?"),
        })
        {
            var model = new CapturingModel([Text(accepted)]);
            using var http = new HttpClient(model);
            var conversation = new Conversation { UserId = actor.Id, Entries = [new Entry { Role = "user", Text = request }] };
            var tools = new FlowTools(name => name == "get_health_report" ? WindowsTools.Ok("Espaço livre por volume: C: 77,7% livre.") : null);
            var answer = await new AgentService(store, tools, http).Run(actor, conversation, new Progress<string>(), (_, _) => { }, CancellationToken.None);
            if (answer.Contains("Apps pré-instalados", StringComparison.Ordinal) || model.Calls == 0) wrong.Add(request + " → " + answer.ReplaceLineEndings(" "));
        }
        assert(wrong.Count == 0, "#127 (PC fcca5cd): sem pedido de remover apps (lentidão, relato no passado), a resposta não ganha o caminho do cartão"
            + (wrong.Count > 0 ? " · erradas: " + string.Join(" | ", wrong) : ""));
    }

    // @qa: o pedido que também traz um sintoma investiga o sintoma; no fim, o próximo passo do sintoma vem antes do caminho do cartão.
    private static async Task SymptomKeepsNextStep(Store store, Account actor, Action<bool, string> assert)
    {
        var model = new CapturingModel([Text(PcOffer), Text(PcOffer), Text(PcOffer)]);
        using var http = new HttpClient(model);
        var conversation = new Conversation { UserId = actor.Id, Entries = [new Entry { Role = "user", Text = "meu pc ta lento, remove os apps inúteis" }] };
        var tools = new FlowTools(name => name == "get_health_report" ? WindowsTools.Ok("Espaço livre por volume: C: 77,7% livre.") : null);
        var answer = (await new AgentService(store, tools, http).Run(actor, conversation, new Progress<string>(), (_, _) => { }, CancellationToken.None)).ReplaceLineEndings("\n");
        var next = answer.IndexOf(AgentService.NextStep, StringComparison.Ordinal);
        var path = answer.IndexOf(AgentProtocol.AppRemovalLine, StringComparison.Ordinal);
        assert(model.Consumed && tools.Calls.Contains("get_health_report") && next >= 0 && path > next && !answer.Contains("A IA repetiu", StringComparison.Ordinal),
            "#127 (@qa): \"meu pc ta lento, remove os apps inúteis\" consulta a saúde e termina com o próximo passo do sintoma e o caminho do cartão · " + answer.Replace("\n", " "));
    }

    // @qa (653d30d): a resposta sem o modelo vale só para o pedido sem sintoma nem pergunta de outro assunto; a conversa continua
    // com o modelo no turno seguinte; a linha do cartão entra depois da linha do Armazenamento e antes da pergunta final.
    private static async Task QaCases(Store store, Account actor, Action<bool, string> assert)
    {
        var routing = new List<string>();
        // Pedido de remoção com sintoma: investiga o sintoma (tópico inicial), não recebe o texto pronto.
        if (!SupportIntake.AsksAppRemoval("remove os apps inúteis, meu som parou") || SupportIntake.InitialTopic("remove os apps inúteis, meu som parou") != "audio")
            routing.Add("remove os apps inúteis, meu som parou");
        // App que trava e pergunta sobre um programa pelo nome (sem "app"/"programa") seguem o fluxo normal, com o modelo.
        foreach (var text in new[] { "como remover o app que trava?", "posso remover o OneDrive?", "ok, e o que eu removo?" })
            if (SupportIntake.AsksAppRemoval(text)) routing.Add(text);

        const string followUp = "Você escolhe os itens no cartão “Apps pré-instalados e ajustes do Windows”, no menu Ferramentas.";
        var model = new CapturingModel([Text(followUp)]);
        using var http = new HttpClient(model);
        var conversation = new Conversation
        {
            UserId = actor.Id,
            Entries =
            [
                new Entry { Role = "user", Text = "remove os apps inúteis do windows pra mim" },
                new Entry { Role = "assistant", Text = AgentProtocol.AppRemovalGuidance },
                new Entry { Role = "user", Text = "ok, e o que eu removo?" },
            ],
        };
        var answer = await new AgentService(store, new FlowTools(), http).Run(actor, conversation, new Progress<string>(), (_, _) => { }, CancellationToken.None);
        var followed = model.Consumed && answer.Contains(followUp, StringComparison.Ordinal) && !answer.Contains("indique", StringComparison.Ordinal);

        var storage = "O C: tem 5% livre. " + InvestigationEvidence.StorageGuidance + "\n\nQuer que eu consulte de novo?";
        var both = InvestigationEvidence.WithAppRemovalGuidance(storage);
        var s = both.IndexOf(InvestigationEvidence.StorageGuidance, StringComparison.Ordinal);
        var a = both.IndexOf(AgentProtocol.AppRemovalLine, StringComparison.Ordinal);
        var q = both.IndexOf("Quer que eu consulte", StringComparison.Ordinal);
        var ordered = s >= 0 && a > s && q > a && both.IndexOf(InvestigationEvidence.StorageGuidance, s + 1, StringComparison.Ordinal) < 0
            && InvestigationEvidence.WithAppRemovalGuidance(both) == both;

        assert(routing.Count == 0 && followed && ordered,
            "#127 (@qa 653d30d): pedido com sintoma investiga o sintoma; app que trava, OneDrive pelo nome e a continuação vão ao modelo; a linha do cartão vem depois do Armazenamento e antes da pergunta, sem duplicar"
            + (routing.Count > 0 ? " · roteadas errado: " + string.Join(" | ", routing) : "") + (followed ? "" : " · continuação: " + answer.ReplaceLineEndings(" "))
            + (ordered ? "" : " · ordem: " + both.ReplaceLineEndings(" ")));
    }

    // Repetição no PC da e7c8b32: a frase que só repete o caminho do cartão (copiada do texto do Nexo) não conta como conteúdo.
    private static void CopiedPath(Action<bool, string> assert)
    {
        const string pcFinal = "Para os apps que vêm com o Windows, use o cartão “Apps pré-instalados e ajustes do Windows”, no menu Ferramentas: o Nexo verifica o que existe neste PC, você escolhe os itens e confirma antes de qualquer mudança, e pode desfazer depois. Outros programas são desinstalados em Configurações > Aplicativos do Windows.";
        const string kept = "O seu PC está com uso moderado de CPU e memória, e há espaço suficiente em disco.";
        var wrong = new List<string>();
        foreach (var copied in new[] { pcFinal, AgentProtocol.AppRemovalGuidance, AgentProtocol.AppRemovalLine })
            if (AgentProtocol.WithoutAppRemovalPath(copied).Length != 0) wrong.Add(copied + " → " + AgentProtocol.WithoutAppRemovalPath(copied));
        var mixed = AgentProtocol.WithoutAppRemovalPath(kept + " " + AgentProtocol.AppRemovalLine);
        if (mixed != kept) wrong.Add("misto → " + mixed);
        if (AgentProtocol.WithoutAppRemovalPath("Reinicie o PC e me diga se a lentidão continua.") != "Reinicie o PC e me diga se a lentidão continua.") wrong.Add("sem caminho");
        // @qa: orientação real que cita Configurações > Aplicativos ou outro cartão de Ferramentas não é cópia do caminho.
        foreach (var legit in new[]
        {
            "Abra Configurações > Aplicativos > Inicialização e desative os programas que abrem junto com o Windows.",
            "Para limpar os temporários, use Ferramentas > Otimizar PC.",
            "O Office pode ser instalado pelo menu Ferramentas.",
        })
            if (AgentProtocol.WithoutAppRemovalPath(legit) != legit) wrong.Add("legítima → " + legit);
        assert(wrong.Count == 0, "#127 (PC e7c8b32): a cópia do caminho do cartão sai antes de medir o conteúdo da resposta; o resto fica" + (wrong.Count > 0 ? " · " + string.Join(" | ", wrong) : ""));
    }

    // Repetição no PC da e7c8b32: "meu pc ta lento, remove os apps inúteis" teve 1 recusa pela oferta de remover, e a resposta aceita
    // foi só o caminho do cartão, sem a lentidão consultada. Com o caminho copiado, o Nexo mostra o que as consultas trouxeram e a
    // linha do cartão; com o resto mantido (o que o motivo pede), a resposta do modelo fica e ganha a linha, uma vez só.
    private static async Task PcMixedRefusal(Store store, Account actor, Action<bool, string> assert)
    {
        const string refused = "O seu PC está com uso moderado de CPU e memória, mas há espaço suficiente em disco. Se quiser, posso ajudar a identificar e remover aplicativos que você não usa. Você deseja que eu remova algum aplicativo?";
        const string copied = "Para os apps que vêm com o Windows, use o cartão “Apps pré-instalados e ajustes do Windows”, no menu Ferramentas: o Nexo verifica o que existe neste PC, você escolhe os itens e confirma antes de qualquer mudança, e pode desfazer depois. Outros programas são desinstalados em Configurações > Aplicativos do Windows.";
        const string keptAnswer = "O seu PC está com uso moderado de CPU e memória, e há espaço suficiente em disco. Se a lentidão continuar, reinicie o PC e me diga se ela aparece em algum programa específico.";
        var results = new List<string>();
        foreach (var (second, keepsModel) in new[] { (copied, false), (keptAnswer, true) })
        {
            var model = new CapturingModel([Text(refused), Text(second)]);
            using var http = new HttpClient(model);
            var conversation = new Conversation { UserId = actor.Id, Entries = [new Entry { Role = "user", Text = "meu pc ta lento, remove os apps inúteis" }] };
            var tools = new FlowTools(name => name == "get_health_report" ? WindowsTools.Ok("Espaço livre por volume: C: 77,7% livre.") : null);
            var answer = (await new AgentService(store, tools, http).Run(actor, conversation, new Progress<string>(), (_, _) => { }, CancellationToken.None)).ReplaceLineEndings("\n");
            var control = model.Calls > 1 ? model.SystemMessages(1).LastOrDefault() ?? "" : "";
            var line = answer.IndexOf(AgentProtocol.AppRemovalLine, StringComparison.Ordinal);
            var ok = model.Consumed && tools.Calls.Contains("get_health_report") && control.Contains(AgentProtocol.AppRemovalReason, StringComparison.Ordinal)
                && !control.Contains("Apps pré-instalados", StringComparison.Ordinal) && line >= 0 && answer.IndexOf(AgentProtocol.AppRemovalLine, line + 1, StringComparison.Ordinal) < 0
                && !answer.Contains("posso ajudar a identificar e remover", StringComparison.Ordinal)
                && (keepsModel ? answer.Contains("uso moderado de CPU e memória", StringComparison.Ordinal) && line > answer.IndexOf("uso moderado", StringComparison.Ordinal)
                    : answer.Contains("O monitor de saúde não mostra ocorrência ativa", StringComparison.Ordinal) && line > answer.IndexOf("O monitor de saúde", StringComparison.Ordinal)
                        && !answer.Contains("Outros programas são desinstalados", StringComparison.Ordinal));
            if (!ok) results.Add((keepsModel ? "mantida" : "copiada") + " → " + answer.Replace("\n", " ") + " · controle: " + control);
        }
        assert(results.Count == 0, "#127 (PC e7c8b32): no pedido misto, depois da recusa da oferta de remover, a resposta traz a lentidão consultada e a linha do cartão uma vez" + (results.Count > 0 ? " · " + string.Join(" | ", results) : ""));
    }

    // O modelo ofereceu remover apps sem o usuário pedir: depois da recusa, a resposta aceita ganha a linha do cartão, porque o
    // motivo diz que o Nexo acrescenta a orientação.
    private static async Task SelfOfferGetsLine(Store store, Account actor, Action<bool, string> assert)
    {
        const string accepted = "Você pode reiniciar o PC e fechar os programas que não está usando para deixar o Windows mais leve.";
        var model = new CapturingModel([Text(PcOffer), Text(accepted)]);
        var answer = await Ask(store, actor, "o que dá pra fazer pra deixar o windows mais limpo?", model);
        var line = answer.IndexOf(AgentProtocol.AppRemovalLine, StringComparison.Ordinal);
        assert(answer.Contains(accepted, StringComparison.Ordinal) && line > answer.IndexOf(accepted, StringComparison.Ordinal)
            && answer.IndexOf(AgentProtocol.AppRemovalLine, line + 1, StringComparison.Ordinal) < 0,
            "#127 (PC e7c8b32): sem pedido de remoção, a oferta recusada do modelo faz a resposta aceita ganhar a linha do cartão · " + answer.Replace("\n", " "));
    }

    // @qa: no pedido misto, o modelo copia o caminho do cartão já na 1ª resposta, sem recusa no turno. O que as consultas mostraram
    // não pode sumir: a resposta vira o relato das consultas, com a linha do cartão uma vez.
    private static async Task CopiedWithoutRefusal(Store store, Account actor, Action<bool, string> assert)
    {
        var model = new CapturingModel([Text(AgentProtocol.AppRemovalGuidance)]);
        using var http = new HttpClient(model);
        var conversation = new Conversation { UserId = actor.Id, Entries = [new Entry { Role = "user", Text = "meu pc ta lento, remove os apps inúteis" }] };
        var tools = new FlowTools(name => name == "get_health_report" ? WindowsTools.Ok("Espaço livre por volume: C: 77,7% livre.") : null);
        var answer = (await new AgentService(store, tools, http).Run(actor, conversation, new Progress<string>(), (_, _) => { }, CancellationToken.None)).ReplaceLineEndings("\n");
        var findings = answer.IndexOf("O monitor de saúde", StringComparison.Ordinal);
        var cards = Regex.Matches(answer, "Apps pré-instalados e ajustes do Windows").Count;
        assert(model.Consumed && tools.Calls.Contains("get_health_report") && findings >= 0 && cards == 1 && answer.IndexOf("Apps pré-instalados", StringComparison.Ordinal) > findings,
            "#127 (@qa): no pedido misto, a 1ª resposta que só copia o caminho do cartão, sem recusa, vira o relato das consultas com o cartão uma vez · " + answer.Replace("\n", " "));
    }

    // Repetição no PC da a6fcbea (23/11): depois da resposta pronta, "ok, e o que eu removo?" recebeu do modelo o mesmo texto, palavra por
    // palavra. A trava recusa a resposta que cobre 80% ou mais dos trechos de 4 palavras da última resposta do assistente, só com as
    // duas longas (60 caracteres e 10 palavras); respostas curtas iguais e o pedido explícito de repetir passam.
    private static readonly string PriorGuidance = AgentProtocol.AppRemovalGuidance + "\n\n" + TurnFacts.Prefix + " nenhuma.";

    private static void RepeatedAnswerRule(Action<bool, string> assert)
    {
        const string health = "O monitor de saúde não mostra ocorrência ativa. A CPU está em 12% e a memória em 54%, e o disco C: tem 77,7% de espaço livre. Se a lentidão continuar, me diga em que momento ela aparece.";
        (string Message, string Prior, string Request)[] repeated =
        [
            (AgentProtocol.AppRemovalGuidance, PriorGuidance, "ok, e o que eu removo?"),
            ("**" + AgentProtocol.AppRemovalGuidance.Replace("“", "\"").Replace("”", "\"") + "**", PriorGuidance, "ok, e o que eu removo?"),
            (AgentProtocol.AppRemovalGuidance + " Se precisar de mais ajuda, é só falar.", PriorGuidance, "e agora?"),
            ("Claro! " + health, health, "e agora?"),
        ];
        (string Message, string Prior, string Request)[] accepted =
        [
            ("Certo.", "Certo.", "ok"),
            ("Tudo bem, qualquer coisa estou à disposição.", "Tudo bem, qualquer coisa estou à disposição.", "valeu"),
            ("Você escolhe os itens no cartão “Apps pré-instalados e ajustes do Windows”, no menu Ferramentas.", PriorGuidance, "ok, e o que eu removo?"),
            ("Depende do que aparecer no seu PC: clique em “Verificar este PC” e veja os itens sugeridos, que já vêm marcados. Se tiver dúvida sobre algum, deixe desmarcado.", PriorGuidance, "ok, e o que eu removo?"),
            ("A CPU está em 12% e a memória em 54%. Como a lentidão aparece ao abrir o navegador, vale conferir as extensões instaladas nele e testar com elas desativadas.", health, "aparece quando abro o navegador"),
            (AgentProtocol.AppRemovalGuidance, PriorGuidance, "pode repetir?"),
            (AgentProtocol.AppRemovalGuidance, PriorGuidance, "repita, por favor"),
            // @qa: resposta longa com conteúdo novo que só reaproveita a anterior curta (o estado medido) não é cópia.
            (health + " Como a lentidão começou depois da atualização de ontem, vale abrir o Windows Update, ver o histórico de atualizações instaladas e conferir se alguma delas aparece com falha; se aparecer, me diga o código do erro e o nome da atualização para eu seguir com a investigação a partir dela.", health, "começou depois da atualização de ontem"),
            (AgentProtocol.AppRemovalGuidance, "", "ok, e o que eu removo?"),
        ];
        var wrong = repeated.Where(c => !AgentProtocol.RepeatsPriorAnswer(c.Message, c.Prior, c.Request)).Select(c => "passou: " + c.Message[..Math.Min(60, c.Message.Length)])
            .Concat(accepted.Where(c => AgentProtocol.RepeatsPriorAnswer(c.Message, c.Prior, c.Request)).Select(c => "recusada: " + c.Message[..Math.Min(60, c.Message.Length)])).ToArray();
        string[] followUps = ["ok, e o que eu removo?", "quais eu tiro?", "o que dá pra remover?", "quais apps eu desinstalo?", "qual programa eu removo?", "o que vale a pena tirar?"];
        string[] notFollowUps = ["ok, obrigado", "e como eu reinstalo o que removi?", "o que é o Clipchamp?", "meu pc ta lento", "o que tirar da inicialização?"];
        var routing = followUps.Where(f => !SupportIntake.AsksWhatToRemove(f)).Select(f => "não reconhecido: " + f)
            .Concat(notFollowUps.Where(SupportIntake.AsksWhatToRemove).Select(f => "reconhecido: " + f)).ToArray();
        assert(wrong.Length == 0 && routing.Length == 0 && !AgentProtocol.AppRemovalFollowUp.Contains(AgentProtocol.AppRemovalGuidance[..40], StringComparison.Ordinal)
            && AgentProtocol.AppRemovalFollowUp.Contains("Verificar este PC", StringComparison.Ordinal),
            "#127 (PC a6fcbea): a resposta que repete a anterior é recusada (longa, 80% dos trechos), as curtas e o pedido de repetir passam; o seguimento sobre o que remover é reconhecido"
            + (wrong.Length > 0 ? " · " + string.Join(" | ", wrong) : "") + (routing.Length > 0 ? " · " + string.Join(" | ", routing) : ""));
    }

    private static Conversation FollowUpConversation(Account actor) => new()
    {
        UserId = actor.Id,
        Entries =
        [
            new Entry { Role = "user", Text = "remove os apps inúteis do windows pra mim" },
            new Entry { Role = "assistant", Text = PriorGuidance },
            new Entry { Role = "user", Text = "ok, e o que eu removo?" },
        ],
    };

    // O fluxo do PC: a cópia é recusada pelo motivo próprio, e a 2ª tentativa, com palavras próprias, é aceita.
    private static async Task PcCopyRefused(Store store, Account actor, Action<bool, string> assert)
    {
        const string own = "Depende do que aparecer no seu PC: clique em “Verificar este PC” e veja os itens sugeridos, que já vêm marcados. Se tiver dúvida sobre algum, deixe desmarcado.";
        var model = new CapturingModel([Text(AgentProtocol.AppRemovalGuidance), Text(own)]);
        using var http = new HttpClient(model);
        var answer = (await new AgentService(store, new FlowTools(), http).Run(actor, FollowUpConversation(actor), new Progress<string>(), (_, _) => { }, CancellationToken.None)).ReplaceLineEndings("\n");
        var control = model.Calls > 1 ? model.SystemMessages(1).LastOrDefault() ?? "" : "";
        assert(model.Consumed && answer.StartsWith(own, StringComparison.Ordinal) && !answer.Contains(AgentProtocol.AppRemovalGuidance, StringComparison.Ordinal)
            && control.Contains(AgentProtocol.RepeatedAnswerReason, StringComparison.Ordinal),
            "#127 (PC a6fcbea): no seguimento \"ok, e o que eu removo?\", a cópia da resposta anterior é recusada e a resposta nova é aceita · " + answer.Replace("\n", " "));
    }

    // Três cópias idênticas: a regra da 3ª recusa idêntica (#125) encerra o turno, com a explicação curta do seguimento.
    private static async Task PcCopyGivesUp(Store store, Account actor, Action<bool, string> assert)
    {
        var model = new CapturingModel([Text(AgentProtocol.AppRemovalGuidance), Text(AgentProtocol.AppRemovalGuidance), Text(AgentProtocol.AppRemovalGuidance)]);
        using var http = new HttpClient(model);
        var answer = (await new AgentService(store, new FlowTools(), http).Run(actor, FollowUpConversation(actor), new Progress<string>(), (_, _) => { }, CancellationToken.None)).ReplaceLineEndings("\n");
        assert(model.Consumed && answer.Contains(AgentProtocol.AppRemovalFollowUp, StringComparison.Ordinal) && !answer.Contains(AgentProtocol.AppRemovalGuidance, StringComparison.Ordinal)
            && !answer.Contains("A IA repetiu", StringComparison.Ordinal),
            "#127 (PC a6fcbea): três cópias da resposta anterior encerram o turno com a explicação curta do seguimento, diferente do texto pronto · " + answer.Replace("\n", " "));
    }

    private const int Windows10 = 19045;
    private const int Windows11 = 22631;

    // Pergunta sobre remover um item conhecido (PC 3a88880, 23/11): "e o Xbox, posso tirar?" recebeu um caminho inventado.
    private static void ItemAnswers(Action<bool, string> assert)
    {
        var wrong = new List<string>();
        void Item(string request, int build, string title, bool suggested, params string[] contains)
        {
            var answer = AppRemovalQuestion.Answer(request, build);
            if (answer == null || !answer.Contains("“" + title + "”", StringComparison.Ordinal) || !answer.Contains("“Apps pré-instalados e ajustes do Windows”", StringComparison.Ordinal)
                || !answer.Contains("Verificar este PC", StringComparison.Ordinal) || !answer.Contains(suggested ? "sugerido, já marcado" : "opcional, desmarcado", StringComparison.Ordinal)
                || contains.Any(part => !answer.Contains(part, StringComparison.Ordinal)) || answer.Contains("indique", StringComparison.Ordinal)
                || answer.Contains("existe neste PC e", StringComparison.Ordinal) || Regex.IsMatch(answer, @"Configurações >"))
                wrong.Add(request + " [" + build + "] → " + answer);
        }
        // Frase exata do PC, no Windows 10 do PC: o aviso do app e o do login, com a trava de confirmação.
        Item("e o Xbox, posso tirar?", Windows10, "Xbox (app)", false, "Quem joga pela conta Xbox ou pelo Game Pass deve manter.", "“Login do Xbox”", "login do Minecraft e do Game Pass para de funcionar", "confirmação explícita");
        Item("posso remover o login do xbox?", Windows10, "Login do Xbox", false, "O login do Minecraft e do Game Pass para de funcionar.", "confirmação explícita");
        Item("dá pra tirar a game bar?", Windows11, "Xbox Game Bar e sobreposições", false, "grava a tela");
        Item("posso desinstalar a Cortana?", Windows10, "Cortana", true);
        Item("e o clima, removo?", Windows11, "Clima (Bing Weather)", true);
        Item("posso tirar as notícias?", Windows10, "Notícias (Bing News)", true);
        Item("da pra desinstalar o solitaire", Windows11, "Solitaire Collection", true);
        Item("posso tirar o Teams?", Windows11, "Microsoft Teams (pessoal)", false, "Conversas do Teams pessoal ficam só na web.");
        Item("posso remover o copilot?", Windows11, "Copilot (app)", false);
        Item("posso tirar os widgets?", Windows10, "Widgets (pacote)", false, "Windows Web Experience Pack");
        Item("posso tirar o sysmain?", Windows10, "SysMain (Superfetch)", false, "SSD", "O Nexo não aplica esse ajuste pelo chat.");
        Item("o Xbox não abre, tento desinstalar?", Windows10, "Xbox (app)", false);
        Item("posso tirar o Xbox do PC?", Windows10, "Xbox (app)", false);
        Item("dá pra remover o app do clima?", Windows10, "Clima (Bing Weather)", true);
        Item("a cortana, posso tirar ela?", Windows10, "Cortana", true);
        assert(wrong.Count == 0, "#127 (PC 3a88880): a pergunta sobre remover um item do catálogo recebe o nome do cartão, sugerido ou opcional, o aviso e o caminho, sem caminho do Windows"
            + (wrong.Count > 0 ? " · erradas: " + string.Join(" | ", wrong) : ""));

        // Item só do 11 perguntado no 10, e o Teams do 10, que é excluído.
        var clipchamp = AppRemovalQuestion.Answer("posso tirar o clipchamp?", Windows10) ?? "";
        var copilot10 = AppRemovalQuestion.Answer("posso remover o copilot?", Windows10) ?? "";
        var teams10 = AppRemovalQuestion.Answer("posso tirar o Teams?", Windows10) ?? "";
        assert(clipchamp.Contains("só no Windows 11", StringComparison.Ordinal) && clipchamp.Contains("não o oferece", StringComparison.Ordinal)
            && copilot10.Contains("só no Windows 11", StringComparison.Ordinal)
            && teams10.Contains("não remove o Teams do Windows 10 de propósito", StringComparison.Ordinal) && !teams10.Contains("Configurações", StringComparison.Ordinal),
            "#127: item só do Windows 11 perguntado no 10 diz que o cartão não o oferece; o Teams do 10 é excluído · " + clipchamp + " | " + teams10);

        // Excluídos de propósito: o motivo, sem outro caminho de remoção.
        var excluded = new (string Request, string Name, string Why)[]
        {
            ("posso tirar o OneDrive?", "o OneDrive", "dados do usuário"),
            ("dá pra remover a microsoft store?", "a Microsoft Store", "suporte ou a gestão"),
            ("posso desinstalar o app da câmera?", "a Câmera", "uso real"),
            ("e a assistência rápida, posso tirar?", "a Assistência Rápida", "suporte ou a gestão"),
        };
        var wrongExcluded = excluded.Select(e => (e, answer: AppRemovalQuestion.Answer(e.Request, Windows10)))
            .Where(x => x.answer == null || !x.answer.Contains("não remove " + x.e.Name + " de propósito", StringComparison.Ordinal) || !x.answer.Contains(x.e.Why, StringComparison.Ordinal)
                || x.answer.Contains("Configurações", StringComparison.Ordinal) || x.answer.Contains("Painel", StringComparison.Ordinal))
            .Select(x => x.e.Request + " → " + x.answer).ToArray();
        assert(wrongExcluded.Length == 0, "#127: alvo fora do escopo (OneDrive, Store, Câmera…) recebe o motivo e nenhum outro caminho de remoção"
            + (wrongExcluded.Length > 0 ? " · erradas: " + string.Join(" | ", wrongExcluded) : ""));

        // Não é pergunta de remover um item conhecido: segue o fluxo normal.
        string[] none =
        [
            "posso tirar o Chrome?", "posso tirar um print?", "posso tirar o pendrive?", "o Xbox não abre", "o xbox travou, como reinstalar?",
            "removi o xbox e agora o jogo não abre", "posso tirar o xbox da inicialização?", "a câmera não funciona, posso remover o driver?",
            "como apagar um email?", "posso remover tudo?", "remove os apps inúteis do windows pra mim", "o clima está quente, o pc esquenta",
            "posso tirar o bing do navegador?", "quero remover as notificações das dicas", "posso desinstalar a atualização do xbox?",
            "posso remover o driver do xbox?", "dá pra desinstalar o dispositivo do xbox?",
            // Gate @qa da d8a2cce: remover algo DO app, ou o app DE um lugar, não é remover o app.
            "o OneDrive está cheio, posso apagar arquivos?", "excluir e-mails do Outlook", "como tiro a conta do Outlook?",
            "remover a senha do Xbox", "como remover o Teams da barra de tarefas?", "remover o ícone do Teams", "tirar o clima da barra de tarefas",
            "posso tirar o som do Teams?", "excluir uma reunião do Teams", "a store não abre, removo o cache?", "como tirar a Cortana do Iniciar?",
            "posso tirar o Copilot da barra?", "remover dicas da tela de bloqueio", "desinstalar as noticias e interesses",
            "remover o family da minha conta", "remover o widget da área de trabalho", "o OneDrive não sincroniza, tiro e coloco de novo?",
            "tirei o Xbox e agora o controle não conecta", "tirar notificações do Xbox", "como desinstalar a atualização do Clima?",
        ];
        var wrongNone = none.Where(request => AppRemovalQuestion.Answer(request, Windows10) != null || AppRemovalQuestion.Answer(request, Windows11) != null).ToArray();
        assert(wrongNone.Length == 0, "#127: print, pendrive, app que não abre, passado, inicialização, driver, e-mail e programa fora do catálogo não viram resposta do cartão"
            + (wrongNone.Length > 0 ? " · erradas: " + string.Join(" | ", wrongNone.Select(r => r + " → " + (AppRemovalQuestion.Answer(r, Windows10) ?? AppRemovalQuestion.Answer(r, Windows11)))) : ""));
    }

    // Oferta de procurar apps não usados e passo a passo de remoção inventado (PC 3a88880).
    private static void StepsAndUnused(Action<bool, string> assert)
    {
        const string pcAnswer = "O Xbox é um app que vem com o Windows e pode ser removido, mas é importante saber que ele está associado a recursos como jogos, streaming e controle remoto do Xbox. Se você não usa esses recursos, pode removê-lo.\n\nPara remover o Xbox:\n1. Vá em **Configurações > Sistema > Xbox**.\n2. Clique em **Remover**.\n3. Confirme a ação.\n\nSe quiser, posso ajudar a encontrar outros apps que talvez você também não use.";
        string[] refused =
        [
            pcAnswer,
            "Se quiser, posso ajudar a encontrar outros apps que talvez você também não use.",
            "Posso identificar os aplicativos que você não usa.",
            "Quer que eu liste os programas desnecessários deste PC?",
            "Para remover o Xbox: 1. Vá em **Configurações > Sistema > Xbox**. 2. Clique em **Remover**.",
            "Para desinstalar a Cortana, abra Configurações > Aplicativos > Cortana e clique em Desinstalar.",
        ];
        string[] accepted =
        [
            "Posso ajudar a identificar os apps que mais consomem memória.",
            "Posso verificar quais programas estão usando mais CPU.",
            "Para desinstalar o Chrome, abra Configurações > Aplicativos e clique em Desinstalar.",
            AgentProtocol.AppRemovalGuidance,
            AgentProtocol.AppRemovalLine,
            "O Xbox (app) está no cartão “Apps pré-instalados e ajustes do Windows”, no menu Ferramentas: para remover, use o cartão.",
            "O app do Xbox não abre: vou verificar o serviço de jogos.",
            // Gate @qa da d8a2cce: o item do catálogo não é o objeto da remoção, ou a remoção é negada.
            "O Teams não abre. Para remover o cache do Teams, abra Configurações > Aplicativos e clique em Redefinir.",
            "Para desinstalar a atualização problemática, abra Configurações > Windows Update > Histórico e clique em Desinstalar atualizações. O app Clima voltou a abrir?",
            "Não remova o Xbox se você joga. Para ver o que ocupa espaço, abra Configurações > Sistema > Armazenamento.",
        ];
        var wrongRefused = refused.Where(text => Validate(text) != AgentProtocol.AppRemovalReason).ToArray();
        var wrongAccepted = accepted.Where(text => Validate(text) == AgentProtocol.AppRemovalReason).ToArray();
        assert(wrongRefused.Length == 0 && wrongAccepted.Length == 0,
            "#127 (PC 3a88880): a oferta de procurar apps não usados e o passo a passo inventado para um item do catálogo são recusados; consumo de memória, outro programa e o cartão passam"
            + (wrongRefused.Length > 0 ? " · passaram: " + string.Join(" | ", wrongRefused) : "") + (wrongAccepted.Length > 0 ? " · recusadas: " + string.Join(" | ", wrongAccepted) : ""));
    }

    // O fluxo do PC: a pergunta do Xbox, na conversa do cartão, recebe o texto do Nexo, sem o modelo e sem ferramentas.
    private static async Task PcXboxQuestion(Store store, Account actor, Action<bool, string> assert)
    {
        var model = new CapturingModel([]);
        using var http = new HttpClient(model);
        var conversation = new Conversation
        {
            UserId = actor.Id,
            Entries =
            [
                new Entry { Role = "user", Text = "remove os apps inúteis do windows pra mim" },
                new Entry { Role = "assistant", Text = PriorGuidance },
                new Entry { Role = "user", Text = "e o Xbox, posso tirar?" },
            ],
        };
        var tools = new FlowTools();
        var answer = (await new AgentService(store, tools, http) { WindowsBuild = () => Windows10 }.Run(actor, conversation, new Progress<string>(), (_, _) => { }, CancellationToken.None)).ReplaceLineEndings("\n");
        assert(model.Calls == 0 && tools.Calls.Count == 0 && answer.StartsWith(AppRemovalQuestion.Answer("e o Xbox, posso tirar?", Windows10)!, StringComparison.Ordinal)
            && !answer.Contains("Configurações > Sistema", StringComparison.Ordinal) && !answer.Contains("posso ajudar a encontrar", StringComparison.Ordinal),
            "#127 (PC 3a88880): \"e o Xbox, posso tirar?\" recebe o texto do Nexo com o aviso do Xbox e do login, sem o modelo · " + answer.Replace("\n", " "));
    }

    // Fora do catálogo e dos excluídos: segue com o modelo.
    private static async Task ChromeGoesToModel(Store store, Account actor, Action<bool, string> assert)
    {
        const string reply = "O Chrome é um navegador instalado por você. Se quiser desinstalá-lo, abra Configurações > Aplicativos, procure o Google Chrome e escolha Desinstalar.";
        var model = new CapturingModel([Text(reply)]);
        using var http = new HttpClient(model);
        var conversation = new Conversation { UserId = actor.Id, Entries = [new Entry { Role = "user", Text = "posso tirar o Chrome?" }] };
        var answer = (await new AgentService(store, new FlowTools(), http) { WindowsBuild = () => Windows10 }.Run(actor, conversation, new Progress<string>(), (_, _) => { }, CancellationToken.None)).ReplaceLineEndings("\n");
        assert(model.Calls >= 1 && answer.Contains("Chrome", StringComparison.Ordinal) && !answer.Contains("de propósito", StringComparison.Ordinal),
            "#127: \"posso tirar o Chrome?\" segue com o modelo · " + answer.Replace("\n", " "));
    }

    private static async Task<string> Ask(Store store, Account actor, string request, CapturingModel model)
    {
        using var http = new HttpClient(model);
        var conversation = new Conversation { UserId = actor.Id, Entries = [new Entry { Role = "user", Text = request }] };
        var tools = new FlowTools();
        var answer = await new AgentService(store, tools, http).Run(actor, conversation, new Progress<string>(), (_, _) => { }, CancellationToken.None);
        if (!model.Consumed) throw new Exception("FAIL: o roteiro do modelo não foi consumido em: " + request);
        if (tools.Calls.Count > 0) throw new Exception("FAIL: o pedido de remover apps chamou ferramentas: " + string.Join(", ", tools.Calls));
        return answer.ReplaceLineEndings("\n");
    }

    private static string Text(string content) => JsonSerializer.Serialize(new { message = new { role = "assistant", content } });

    private sealed class FlowTools(Func<string, string?>? results = null) : IAgentTools
    {
        internal List<string> Calls { get; } = [];
        public Task<string> Execute(string name, JsonElement args, CancellationToken token)
        {
            Calls.Add(name);
            return Task.FromResult(results?.Invoke(name) ?? JsonSerializer.Serialize(new { success = false, error = "indisponível no teste" }));
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
