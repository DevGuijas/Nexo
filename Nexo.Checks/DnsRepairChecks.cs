using System.Net;
using System.Text;
using System.Text.Json;
using Nexo.Support;

// Cenário 3 do ensaio de 08/11/2026 (#63): DNS fixo 192.0.2.53 numa Ethernet com DHCP ativo e gateway 192.168.1.1.
// O Assessment aponta o DNS fixo, o teste do nome vem acompanhado de um teste TCP por IP numérico, e o reparo
// volta ao DNS automático em vez de inventar servidores.
internal static class DnsRepairChecks
{
    internal static async Task Run(Store store, Account actor, Action<bool, string> assert)
    {
        var previousAi = store.Data.AiEnabled;
        store.Data.AiEnabled = true;
        try
        {
            Assessment(assert);
            Evidence(assert);
            await NumericProbe(store, actor, assert);
            await AutomaticDnsPreferred(store, actor, assert);
        }
        finally { store.Data.AiEnabled = previousAi; }
    }

    private static object Adapter(string dhcp = "Enabled", string? source = "manual", string[]? fixedDns = null, string[]? dhcpDns = null, string name = "Ethernet", bool hardware = true, string[]? staticDns = null) => new
    {
        Index = 13, Guid = "{13}", Name = name, Description = "Realtek PCIe GbE", Status = "Up", Hardware = hardware, Virtual = !hardware, Dhcp = dhcp,
        Addresses = new[] { new { IPAddress = "192.168.1.20", PrefixLength = 24, PrefixOrigin = dhcp == "Enabled" ? "Dhcp" : "Manual", AddressState = "Preferred" } },
        Gateways = new[] { "192.168.1.1" }, Dns = fixedDns ?? ["192.0.2.53"], DnsSource = source,
        StaticDns = staticDns ?? (source == "manual" ? fixedDns ?? ["192.0.2.53"] : []), DhcpDns = dhcpDns ?? ["192.168.1.1"]
    };

    private static JsonElement Assess(object adapter)
    {
        var enriched = NetworkAssessment.Enrich(JsonSerializer.SerializeToElement(new { Adapters = new[] { adapter }, Warnings = Array.Empty<string>(), Partial = false }));
        return enriched.GetProperty("Assessment")[0];
    }
    private static string[] Findings(JsonElement assessment) => assessment.GetProperty("Findings").EnumerateArray().Select(f => f.GetString() ?? "").ToArray();

    private static void Assessment(Action<bool, string> assert)
    {
        var scenario = Assess(Adapter());
        var finding = Findings(scenario).SingleOrDefault(f => f.StartsWith("DNS IPv4 fixo", StringComparison.Ordinal)) ?? "";
        assert(finding.StartsWith("DNS IPv4 fixo (192.0.2.53) com DHCP ativo: o DHCP desta rede fornece 192.168.1.1.", StringComparison.Ordinal)
            && finding.Contains("set_dns mode=automatic") && finding.Contains("Pode ser intencional"), "Assessment: DNS fixo diferente do fornecido pelo DHCP é sinalizado");
        assert(scenario.GetProperty("DnsMode").GetString() == "manual" && scenario.GetProperty("DnsAutomaticCandidate").GetBoolean()
            && !scenario.GetProperty("DhcpCandidate").GetBoolean() && scenario.GetProperty("NextStep").GetString()!.Contains("set_dns(mode=automatic)"),
            "Assessment: DNS fixo com DHCP ativo indica a volta ao DNS automático, não a troca do IP");

        var noDhcpList = Findings(Assess(Adapter(dhcpDns: [])));
        assert(noDhcpList.Any(f => f.Contains("o DNS fornecido pelo DHCP não foi informado pelo Windows; o gateway é 192.168.1.1.")),
            "Assessment: sem a lista do DHCP, o achado compara com o gateway");
        var same = Assess(Adapter(fixedDns: ["192.168.1.2", "192.168.1.1"], dhcpDns: ["192.168.1.1", "192.168.1.2"]));
        assert(!Findings(same).Any(f => f.StartsWith("DNS IPv4 fixo", StringComparison.Ordinal)) && !same.GetProperty("DnsAutomaticCandidate").GetBoolean(),
            "Assessment: DNS fixo igual ao do DHCP, em outra ordem, não é achado");
        var blanks = Assess(Adapter(staticDns: ["", "192.168.1.1"], dhcpDns: ["192.168.1.1"]));
        assert(!Findings(blanks).Any(f => f.StartsWith("DNS IPv4 fixo", StringComparison.Ordinal)), "Assessment: entradas vazias na lista de DNS são ignoradas na comparação");
        var automatic = Assess(Adapter(source: "automatic", fixedDns: ["192.168.1.1"]));
        assert(!Findings(automatic).Any(f => f.StartsWith("DNS IPv4 fixo", StringComparison.Ordinal)) && automatic.GetProperty("DnsMode").GetString() == "automatic",
            "Assessment: DNS automático não é achado");
        var staticIp = Assess(Adapter(dhcp: "Disabled"));
        assert(!Findings(staticIp).Any(f => f.StartsWith("DNS IPv4 fixo", StringComparison.Ordinal)) && !staticIp.GetProperty("DnsAutomaticCandidate").GetBoolean(),
            "Assessment: com IP fixo, o DNS fixo é a configuração esperada e não é achado");
        var unknown = Assess(Adapter(source: null));
        var manualWithoutList = Assess(Adapter(staticDns: []));
        assert(!Findings(manualWithoutList).Any(f => f.StartsWith("DNS IPv4 fixo", StringComparison.Ordinal)) && !manualWithoutList.GetProperty("DnsAutomaticCandidate").GetBoolean(),
            "Assessment: origem manual sem lista de DNS fixo não gera achado com lista vazia");
        var unknownWithList = Assess(Adapter(source: "unknown", staticDns: ["192.0.2.53"]));
        assert(!Findings(unknownWithList).Any(f => f.StartsWith("DNS IPv4 fixo", StringComparison.Ordinal)) && !unknownWithList.GetProperty("DnsAutomaticCandidate").GetBoolean(),
            "Assessment: só a origem manual confirmada gera o achado de DNS fixo");
        assert(!Findings(unknown).Any(f => f.StartsWith("DNS IPv4 fixo", StringComparison.Ordinal)) && unknown.GetProperty("DnsMode").GetString() == "unknown",
            "Assessment: inventário sem a origem do DNS não inventa achado");
        var vpn = Assess(Adapter(name: "VPN corporativa", hardware: false));
        assert(Findings(vpn).Any(f => f.StartsWith("DNS IPv4 fixo", StringComparison.Ordinal)) && !vpn.GetProperty("DnsAutomaticCandidate").GetBoolean()
            && !vpn.GetProperty("NextStep").GetString()!.Contains("set_dns"), "Assessment: interface que o Nexo não configura mostra o achado, sem propor set_dns");
    }

    private static void Evidence(Action<bool, string> assert)
    {
        var none = JsonSerializer.SerializeToElement(new { });
        var fixedDns = new InvestigationEvidence();
        fixedDns.Observe("get_network", "n", none, WindowsTools.Ok(new { Adapters = new[] { Adapter() } }));
        var staticIp = new InvestigationEvidence();
        staticIp.Observe("get_network", "n", none, WindowsTools.Ok(new { Adapters = new[] { Adapter(dhcp: "Disabled") } }));
        assert(fixedDns.Report("").Contains("Rede: “Ethernet” conectada, gateway 192.168.1.1, DNS 192.0.2.53 (fixo, com DHCP ativo).")
            && staticIp.Report("").Contains("DNS 192.0.2.53.") && !staticIp.Report("").Contains("fixo"), "Resposta final diz que o DNS é fixo quando o DHCP está ativo");
        var unknownSource = new InvestigationEvidence();
        unknownSource.Observe("get_network", "n", none, WindowsTools.Ok(new { Adapters = new[] { Adapter(source: "unknown") } }));
        assert(unknownSource.Report("").Contains("DNS 192.0.2.53.") && !unknownSource.Report("").Contains("fixo"), "Resposta final não chama de fixo o DNS de origem desconhecida");
        var tests = new InvestigationEvidence();
        tests.Observe("test_connection", "t1", JsonSerializer.SerializeToElement(new { host = "google.com", port = 443 }), WindowsTools.Ok(new { host = "google.com", dnsResolved = false, tcpConnected = false }));
        assert(!tests.HasNumericConnectionTest, "Teste por nome não conta como teste por IP numérico");
        tests.Observe("test_connection", "t2", JsonSerializer.SerializeToElement(new { host = "8.8.8.8", port = 53 }), WindowsTools.Ok(new { host = "8.8.8.8", dnsResolved = true, tcpConnected = true }));
        var report = tests.Report("");
        assert(tests.HasNumericConnectionTest && report.Contains("Resolução DNS de google.com: não confirmada no teste.") && report.Contains("Conexão TCP com 8.8.8.8: funcionando.")
            && report.Contains("Há conectividade parcial"), "Nome sem resolução e TCP por IP funcionando aparecem como conectividade parcial");
    }

    private static string Network() => WindowsTools.Ok(new { Adapters = new[] { Adapter() } });
    private static string Test(JsonElement args, bool dns, bool tcp) => WindowsTools.Ok(new { host = args.GetProperty("host").GetString(), dnsResolved = dns, tcpConnected = tcp });
    private const string Sites = "Os sites que eu tava acessando nao tao abrindo mais";

    // Cenário 3: o modelo só testou o nome; agora o executor testa também TCP por IP numérico.
    private static async Task NumericProbe(Store store, Account actor, Action<bool, string> assert)
    {
        var hosts = new List<string>();
        var tools = new FlowTools((name, args) =>
        {
            if (name == "get_network") return Network();
            if (name == "test_connection") { hosts.Add(args.GetProperty("host").GetString() + ":" + args.GetProperty("port").GetInt32()); return Test(args, dns: hosts.Count > 1, tcp: hosts.Count > 1); }
            if (name == "set_dns") return JsonSerializer.Serialize(new { success = false, denied = true });
            throw new Exception("Ferramenta inesperada no teste por IP: " + name);
        });
        var model = new ModelSequence([Call("get_network", new { }), Call("test_connection", new { host = "google.com", port = 443 }), Call("set_dns", new { interface_index = 13, mode = "automatic" })]);
        var conversation = Conversation(actor, Sites);
        var answer = await Execute(store, actor, conversation, tools, model);
        assert(tools.Calls.SequenceEqual(["get_network", "test_connection", "test_connection", "set_dns"]) && hosts.SequenceEqual(["google.com:443", "8.8.8.8:53"]),
            "Cenário 3: nome sem resolução leva o executor a testar TCP por IP numérico antes do próximo passo do modelo");
        assert(model.Requests[2].Contains("8.8.8.8") && !model.Requests[1].Contains("8.8.8.8"), "Cenário 3: o modelo recebe o teste por IP antes de escolher o reparo");
        assert(answer.Contains("Você recusou") && conversation.AgentHistory.Count(m => m.role == "tool" && m.tool_name == "test_connection") == 2,
            "Cenário 3: o teste por IP fica no histórico como uma chamada com resultado");

        foreach (var (label, numericFirst, nameDns) in new[] { ("teste por IP já feito pelo modelo", true, false), ("nome resolvido com TCP falhando", false, true) })
        {
            var seen = new List<string>();
            var quiet = new FlowTools((name, args) =>
            {
                if (name != "test_connection") throw new Exception("Ferramenta inesperada: " + name);
                seen.Add(args.GetProperty("host").GetString()!);
                return Test(args, dns: args.GetProperty("host").GetString() == "8.8.8.8" || nameDns, tcp: args.GetProperty("host").GetString() == "8.8.8.8");
            });
            var calls = numericFirst
                ? new[] { Call("test_connection", new { host = "8.8.8.8", port = 53 }), Call("test_connection", new { host = "google.com", port = 443 }), Call("finish_support", new { message = "O nome não resolveu e o IP respondeu." }) }
                : new[] { Call("test_connection", new { host = "google.com", port = 443 }), Call("finish_support", new { message = "O nome resolveu, mas a conexão TCP falhou." }) };
            var quietModel = new ModelSequence(calls);
            await Execute(store, actor, Conversation(actor, Sites), quiet, quietModel);
            assert(quietModel.Calls == calls.Length && seen.Count == (numericFirst ? 2 : 1), "Teste por IP não é repetido pelo executor: " + label);
        }

        // Teste por nome que não concluiu não é falha de resolução: o executor não acrescenta o teste por IP.
        var unfinished = new FlowTools((name, _) => name == "test_connection"
            ? JsonSerializer.Serialize(new { success = false, error = "Tempo esgotado.", data = new { host = "google.com", dnsResolved = false, tcpConnected = false } })
            : throw new Exception("Ferramenta inesperada: " + name));
        var unfinishedModel = new ModelSequence([Call("test_connection", new { host = "google.com", port = 443 }), Call("finish_support", new { message = "O teste não terminou." })]);
        await Execute(store, actor, Conversation(actor, Sites), unfinished, unfinishedModel);
        assert(unfinished.Calls.SequenceEqual(["test_connection"]) && unfinishedModel.Calls == 2, "Teste por nome que não concluiu não dispara o teste por IP");

        // Pedido que cita a internet: a limpeza de DNS só fica disponível depois de o TCP por IP responder.
        var flush = new FlowTools((name, args) => name switch
        {
            "test_connection" => Test(args, dns: false, tcp: args.GetProperty("host").GetString() == "8.8.8.8"),
            "flush_dns" => JsonSerializer.Serialize(new { success = false, denied = true }),
            _ => throw new Exception("Ferramenta inesperada: " + name)
        });
        var flushModel = new ModelSequence([Call("test_connection", new { host = "google.com", port = 443 }), Call("flush_dns", new { })]);
        var flushAnswer = await Execute(store, actor, Conversation(actor, "a internet não abre os sites"), flush, flushModel);
        assert(flush.Calls.SequenceEqual(["test_connection", "test_connection", "flush_dns"]) && flushAnswer.Contains("Você recusou")
            && !flushModel.Requests[0].Contains("\"flush_dns\"") && flushModel.Requests[1].Contains("\"flush_dns\""),
            "TCP por IP funcionando libera a limpeza de DNS no passo seguinte");
    }

    // O reparo volta ao DNS automático: DNS manual exige servidores escritos pelo usuário.
    private static async Task AutomaticDnsPreferred(Store store, Account actor, Action<bool, string> assert)
    {
        FlowTools Tools() => new((name, _) => name switch
        {
            "get_network" => Network(),
            "set_dns" => JsonSerializer.Serialize(new { success = false, denied = true }),
            _ => throw new Exception("Ferramenta inesperada no reparo de DNS: " + name)
        });
        var invented = Call("set_dns", new { interface_index = 13, mode = "manual", servers = new[] { "8.8.8.8" } });
        var tools = Tools();
        var model = new ModelSequence([Call("get_network", new { }), invented, Call("set_dns", new { interface_index = 13, mode = "automatic" })]);
        var conversation = Conversation(actor, Sites);
        var answer = await Execute(store, actor, conversation, tools, model);
        using (var blocked = JsonDocument.Parse(conversation.AgentHistory.First(m => m.role == "tool" && m.tool_name == "set_dns").content))
        {
            var message = blocked.RootElement.GetProperty("error").GetString() ?? "";
            assert(tools.Calls.SequenceEqual(["get_network", "set_dns"]) && answer.Contains("Você recusou") && message.Contains("mode=automatic") && message.Contains("não foram informados pelo usuário"),
                "Cenário 3: DNS manual com servidor inventado é barrado antes da aprovação, e o DNS automático segue para a confirmação");
        }

        var repeated = Tools();
        var repeatedModel = new ModelSequence([Call("get_network", new { }), invented, Call("set_dns", new { interface_index = 13, mode = "manual", servers = new[] { "1.1.1.1" } })]);
        var repeatedAnswer = await Execute(store, actor, Conversation(actor, Sites), repeated, repeatedModel);
        assert(repeated.Calls.SequenceEqual(["get_network"]) && repeatedModel.Calls == 3
            && repeatedAnswer.Contains("O Nexo não executou a etapa “Configurando os servidores DNS” porque os servidores DNS escolhidos pela IA não foram informados por você")
            && repeatedAnswer.Split("O Nexo não executou").Length == 2 && !repeatedAnswer.Contains("Não terminaram com sucesso") && !repeatedAnswer.Contains("Se o problema continuar"),
            "Cenário 3: a segunda tentativa de DNS inventado encerra o turno e explica o motivo uma vez");

        // #73: barrado o DNS inventado, o modelo não pode dizer na resposta final que trocou o DNS.
        var claimed = Tools();
        var claimedModel = new ModelSequence([Call("get_network", new { }), invented,
            Call("finish_support", new { message = "O DNS da Ethernet foi trocado para 8.8.8.8 e os sites voltam a abrir." })]);
        var claimedAnswer = await Execute(store, actor, Conversation(actor, Sites), claimed, claimedModel);
        assert(claimed.Calls.SequenceEqual(["get_network"]) && claimedModel.Calls == 3 && !claimedAnswer.Contains("foi trocado")
            && claimedAnswer.EndsWith("\n\nAlterações feitas pelo Nexo neste turno: nenhuma. Não executadas: DNS manual “8.8.8.8” da interface 13 (barrada pelo Nexo).")
            && claimedAnswer.Contains("O Nexo não executou a etapa “Configurando os servidores DNS” porque os servidores DNS escolhidos pela IA não foram informados por você"),
            "#73: com o DNS inventado barrado, a resposta não afirma a troca e diz por que não foi feita");

        // #73, gate do 8b4a260: falha da mudança de rede que pode ter alterado a interface. N1: mudou e não restaurou;
        // N2: o executor terminou sem resultado. A linha não pode dizer "nenhuma".
        foreach (var (label, result, start) in new[]
        {
            ("N1", JsonSerializer.Serialize(new { success = false, error = "A verificação depois da mudança falhou.", changed = true, reverted = false, backup = "before.json" }), "A alteração de rede não foi concluída."),
            ("N2", JsonSerializer.Serialize(new { success = false, error = "Executor encerrado sem resultado. Verifique a rede; estado não confirmado.", recoveryFolder = "op" }),
                "A conclusão da etapa “Configurando os servidores DNS” não foi confirmada: o Windows pode ter sido alterado."),
        })
        {
            var failing = new FlowTools((name, _) => name switch { "get_network" => Network(), "set_dns" => result, _ => throw new Exception("Ferramenta inesperada: " + name) });
            var failedAnswer = await Execute(store, actor, Conversation(actor, Sites), failing, new ModelSequence([Call("get_network", new { }), Call("set_dns", new { interface_index = 13, mode = "automatic" })]));
            assert(failedAnswer.StartsWith(start, StringComparison.Ordinal)
                && failedAnswer.EndsWith("\n\nAlterações feitas pelo Nexo neste turno: nenhuma confirmada. Sem confirmação do resultado: DNS automático da interface 13 — confira antes de continuar.", StringComparison.Ordinal),
                "#73: " + label + ": mudança de rede sem restauração confirmada fica sem confirmação na linha de fatos (" + failedAnswer + ")");
        }

        // #9: prazo esgotado com o executor ainda ativo (state=unknown). A resposta diz que o estado não foi confirmado e
        // não fala em restauração automática.
        var unknownResult = JsonSerializer.Serialize(new { success = false, reverted = false, state = "unknown", timedOut = true, processRunning = true, cancelled = false, phase = "applying",
            error = "O executor de rede não chegou à confirmação em 300 segundos e ainda está em execução. A mudança pode ter sido aplicada parcialmente; o estado da rede não pôde ser confirmado e nenhuma restauração foi confirmada.", backup = "before.json", recoveryFolder = "op" });
        var unknownTools = new FlowTools((name, _) => name switch { "get_network" => Network(), "set_dns" => unknownResult, _ => throw new Exception("Ferramenta inesperada: " + name) });
        var unknownAnswer = await Execute(store, actor, Conversation(actor, Sites), unknownTools, new ModelSequence([Call("get_network", new { }), Call("set_dns", new { interface_index = 13, mode = "automatic" })]));
        assert(unknownAnswer.StartsWith("A alteração de rede não foi concluída.", StringComparison.Ordinal)
            && unknownAnswer.Contains("O estado da rede não pôde ser confirmado e nenhuma restauração foi confirmada. O caminho do backup está no cartão acima; consulte a rede antes de tentar outra mudança.", StringComparison.Ordinal)
            && !unknownAnswer.Contains("A restauração automática não foi confirmada", StringComparison.Ordinal) && !unknownAnswer.Contains("restaurada e conferida", StringComparison.Ordinal)
            && unknownAnswer.Contains("Sem confirmação do resultado: DNS automático da interface 13", StringComparison.Ordinal),
            "#9: prazo esgotado com estado desconhecido não anuncia restauração automática (" + unknownAnswer + ")");

        foreach (var (label, request, server, reaches) in new[]
        {
            ("servidor escrito no pedido", "Os sites não abrem; configure o DNS 1.1.1.1 na Ethernet", "1.1.1.1", true),
            ("servidor escrito em mensagem anterior", "O DNS da minha empresa é 10.0.0.53 e os sites não abrem", "10.0.0.53", true),
            ("endereço só parecido com o escrito", "Os sites não abrem; o DNS antigo era 11.1.1.10", "1.1.1.1", false),
            ("ponto final da frase depois do endereço", "Os sites não abrem. O DNS da empresa é 10.1.2.3.", "10.1.2.3", true),
            ("segundo servidor no fim da frase", "Os sites não abrem; use 10.1.2.3 e 10.1.2.4.", "10.1.2.4", true),
            ("escrito só com um dígito a mais no fim", "Os sites não abrem; o DNS antigo era 1.1.1.10", "1.1.1.1", false),
            ("escrito só com mais um octeto no fim", "Os sites não abrem; o DNS antigo era 1.1.1.1.5", "1.1.1.1", false),
            ("escrito só com um dígito a mais no começo", "Os sites não abrem; o DNS antigo era 11.1.1.1", "1.1.1.1", false),
            ("IPv6 escrito com um dígito hexadecimal a mais", "Os sites não abrem; o DNS é 2001:db8::1f", "2001:db8::1", false),
            ("IPv6 escrito com mais um grupo", "Os sites não abrem; o DNS é 2001:db8::1:5", "2001:db8::1", false),
            ("IPv6 escrito com uma letra a mais no começo", "Os sites não abrem; o DNS é a2001:db8::1", "2001:db8::1", false),
            ("IPv6 escrito por inteiro", "Os sites não abrem; o DNS é 2001:db8::1.", "2001:db8::1", true),
        })
        {
            var entries = label.Contains("anterior") ? new[] { request, "continue" } : new[] { request };
            var typed = Tools();
            var typedModel = new ModelSequence([Call("get_network", new { }), Call("set_dns", new { interface_index = 13, mode = "manual", servers = new[] { server } }),
                .. (reaches ? Array.Empty<string>() : [Call("finish_support", new { message = "Preciso saber quais servidores DNS a sua rede usa." })])]);
            await Execute(store, actor, Conversation(actor, entries), typed, typedModel);
            assert(typed.Calls.SequenceEqual(reaches ? ["get_network", "set_dns"] : ["get_network"]) && typedModel.Calls == (reaches ? 2 : 3), "DNS manual: " + label);
        }

        // Só o que o usuário escreveu conta: o endereço citado pelo Nexo ou num cartão de resultado não é informação do usuário.
        var others = Tools();
        var othersModel = new ModelSequence([Call("get_network", new { }), invented, Call("finish_support", new { message = "Preciso saber quais servidores DNS a sua rede usa." })]);
        var othersConversation = new Conversation { UserId = actor.Id, Entries = [
            new Entry { Role = "user", Text = Sites }, new Entry { Role = "assistant", Text = "O teste por IP com 8.8.8.8 respondeu." },
            new Entry { Role = "tool", Text = "Teste de conexão: 8.8.8.8:53 respondeu." }, new Entry { Role = "user", Text = "continue" }] };
        await Execute(store, actor, othersConversation, others, othersModel);
        assert(others.Calls.SequenceEqual(["get_network"]) && othersModel.Calls == 3, "DNS manual: endereço em mensagem do Nexo ou em cartão de ferramenta não conta como informado");
    }

    private static Conversation Conversation(Account actor, params string[] userMessages) => new()
    {
        UserId = actor.Id,
        Entries = [.. userMessages.SelectMany((text, i) => i < userMessages.Length - 1
            ? new[] { new Entry { Role = "user", Text = text }, new Entry { Role = "assistant", Text = "A IA não conseguiu selecionar a próxima etapa." } }
            : [new Entry { Role = "user", Text = text }])]
    };
    private static string Call(string name, object args) => JsonSerializer.Serialize(new { message = new { role = "assistant", content = "", tool_calls = new[] { new { function = new { name, arguments = args } } } } });

    private static async Task<string> Execute(Store store, Account actor, Conversation conversation, FlowTools tools, ModelSequence model)
    {
        using (model)
        using (var http = new HttpClient(model, disposeHandler: false))
            return await new AgentService(store, tools, http).Run(actor, conversation, new Progress<string>(), (_, _) => { }, CancellationToken.None);
    }

    private sealed class FlowTools(Func<string, JsonElement, string> execute) : IAgentTools
    {
        internal List<string> Calls { get; } = [];
        public Task<string> Execute(string name, JsonElement args, CancellationToken token)
        {
            Calls.Add(name);
            token.ThrowIfCancellationRequested();
            return Task.FromResult(execute(name, args));
        }
    }

    private sealed class ModelSequence(string[] responses) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal List<string> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (Calls >= responses.Length) throw new Exception("Modelo recebeu mais chamadas que o roteiro.");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responses[Calls++], Encoding.UTF8, "application/json") };
        }
    }
}
