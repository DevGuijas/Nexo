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
            await RepeatedOfferFlow(store, actor, assert);
            await RequestEndsWithPath(store, actor, assert);
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
        ];
        var wrongRefused = refused.Where(text => !AgentProtocol.OffersAppRemoval(AgentProtocol.Normalize(text)) || Validate(text) != AgentProtocol.AppRemovalReason).ToArray();
        var wrongAccepted = accepted.Where(text => AgentProtocol.OffersAppRemoval(AgentProtocol.Normalize(text)) || Validate(text) == AgentProtocol.AppRemovalReason).ToArray();
        assert(wrongRefused.Length == 0 && wrongAccepted.Length == 0,
            "#127 (PC 63f01f0), " + culture + ": a oferta de remover ou desinstalar apps pelo chat tem motivo próprio; a negação, a explicação, o caminho de Ferramentas, o driver e o cache passam"
            + (wrongRefused.Length > 0 ? " · deviam ser recusadas: " + string.Join(" | ", wrongRefused) : "") + (wrongAccepted.Length > 0 ? " · deviam passar: " + string.Join(" | ", wrongAccepted) : ""));
        assert(AgentProtocol.AppRemovalReason.Contains("Ferramentas > Apps pré-instalados e ajustes do Windows", StringComparison.Ordinal)
            && AgentProtocol.AppRemovalGuidance.Contains("Ferramentas > Apps pré-instalados e ajustes do Windows", StringComparison.Ordinal)
            && AgentProtocol.Guidance(AgentProtocol.AppRemovalReason, PcOffer) == AgentProtocol.AppRemovalReason,
            "#127, " + culture + ": o motivo e o fim do turno indicam o cartão de Ferramentas pelo nome da tela");
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
        ];
        var wrong = cases.Where(c => SupportIntake.AsksAppRemoval(c.Text) != c.Removal).Select(c => c.Text).ToArray();
        assert(wrong.Length == 0, "#127 (PC 63f01f0), " + culture + ": o pedido de remover apps pré-instalados é reconhecido; app travado, reinstalar, vírus, temporários, print e cache não"
            + (wrong.Length > 0 ? " · erradas: " + string.Join(" | ", wrong) : ""));
    }

    // O fluxo do PC: a mesma oferta 3 vezes encerra o turno indicando o cartão, sem "A IA repetiu"; o motivo é o próprio.
    private static async Task RepeatedOfferFlow(Store store, Account actor, Action<bool, string> assert)
    {
        var model = new CapturingModel([Text(PcOffer), Text(PcOffer), Text(PcOffer)]);
        var answer = await Ask(store, actor, "remove os apps inúteis do windows pra mim", model);
        var control = model.SystemMessages(1).LastOrDefault() ?? "";
        var prompt = model.SystemMessages(0).FirstOrDefault() ?? "";
        assert(answer.Contains(AgentProtocol.AppRemovalGuidance, StringComparison.Ordinal) && !answer.Contains("A IA repetiu", StringComparison.Ordinal)
            && !answer.Contains("posso ajudar com isso", StringComparison.Ordinal) && control.Contains(AgentProtocol.AppRemovalReason, StringComparison.Ordinal)
            && !control.Contains("Use a ferramenta para a próxima consulta", StringComparison.Ordinal)
            && prompt.Contains("O Nexo não remove nem desinstala apps pelo chat", StringComparison.Ordinal),
            "#127 (PC 63f01f0): a oferta repetida de remover apps é recusada com o motivo próprio e o turno termina indicando Ferramentas > Apps pré-instalados e ajustes do Windows · " + answer.Replace("\n", " "));
    }

    // Pedido de remover apps que termina por outro motivo (aqui, a ação anunciada sem ferramenta): o fim também indica o cartão.
    private static async Task RequestEndsWithPath(Store store, Account actor, Action<bool, string> assert)
    {
        const string announced = "Vamos verificar quais aplicativos estão instalados no seu computador.";
        var answer = await Ask(store, actor, "quero desinstalar uns aplicativos que vieram no pc", new CapturingModel([Text(announced), Text(announced), Text(announced)]));
        assert(answer.Contains(AgentProtocol.AppRemovalGuidance, StringComparison.Ordinal) && !answer.Contains("A IA repetiu", StringComparison.Ordinal),
            "#127: num pedido de remover apps, o turno que termina por outra recusa também indica o cartão de Ferramentas · " + answer.Replace("\n", " "));
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

    private sealed class FlowTools : IAgentTools
    {
        internal List<string> Calls { get; } = [];
        public Task<string> Execute(string name, JsonElement args, CancellationToken token)
        {
            Calls.Add(name);
            return Task.FromResult(JsonSerializer.Serialize(new { success = false, error = "indisponível no teste" }));
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
            if (calls >= responses.Length) throw new Exception("O modelo foi chamado além do roteiro (" + responses.Length + ").");
            using (var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))) requests.Add(body.RootElement.Clone());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responses[calls++], Encoding.UTF8, "application/json") };
        }
    }
}
