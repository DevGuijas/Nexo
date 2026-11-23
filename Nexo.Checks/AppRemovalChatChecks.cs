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
