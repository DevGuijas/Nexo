using System.Net;
using System.Text;
using System.Text.Json;
using Nexo.Support;

// #128: achados da validação no PC da #10 (22/11/2026, fb905f9), com o executor de rede derrubado nas fases confirming,
// restoring e keeping. A recuperação funcionou em todos os casos; ficaram cinco problemas de texto, fluxo e modelo:
// 1. depois de "Manter configuração", o resultado sem result.json dizia "A restauração automática não foi confirmada";
// 2. com a janela "Restaurar configuração de rede anterior?" aberta, o Nexo não fechava;
// 3. a recuperação feita na abertura seguinte aparecia só no cartão da tela inicial, e a conversa de origem ficava sem resposta;
// 4. com o DNS já de volta, aceitar a recuperação pedia UAC só para dizer que nada precisava mudar;
// 5. set_dns(mode=automatic, interface_index=1) sem consulta de rede chegava ao executor e o turno terminava como falha.
internal static class NetworkFollowUpChecks
{
    private const string Guid = "0f0e0d0c-0b0a-0908-0706-050403020113";
    private const string Request = "volte o DNS da Ethernet para o que era antes, automático";

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
                KeepingText(assert, culture);
                AlreadyRestoredRules(assert, culture);
                ConversationNotes(assert, culture);
            }
            System.Globalization.CultureInfo.CurrentCulture = previousCulture;
            await RecoveryWithoutElevation(assert);
            await PostponedRecovery(assert);
            await OriginConversation(store, actor, assert);
            NetworkTargetRules(assert);
            await NetworkTargetFromQuery(store, actor, assert);
        }
        finally { store.Data.AiEnabled = previousAi; System.Globalization.CultureInfo.CurrentCulture = previousCulture; }
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    // 1. A fase keeping fala da permanência não conferida, nunca de restauração.
    private static void KeepingText(Action<bool, string> assert, string culture)
    {
        // O caso do PC (op e301f676): o executor caiu depois de "Manter", e a abertura imediata leu o registro protegido em keeping.
        var interrupted = AgentService.NetworkFailureText(Json(new { success = false, changed = true, reverted = false, phase = "keeping",
            error = "Executor encerrado sem resultado. Você confirmou manter a nova configuração de rede, mas o executor terminou antes de registrar o resultado. A configuração atual não foi conferida; consulte a rede antes de outra alteração.\nBackup: C:\\ProgramData\\NexoSupport\\operations\\e301f676\\before.json",
            backup = "before.json" }));
        // Prazo esgotado depois de entregar "keep": o executor ainda está em execução.
        var timedOut = AgentService.NetworkFailureText(Json(new { success = false, reverted = false, state = "unknown", timedOut = true, processRunning = true, phase = "keeping",
            error = NetworkChange.UnconfirmedError("keeping", 90, false), backup = "before.json" }));
        var keepingError = NetworkChange.UnconfirmedError("keeping", 90, false);
        // As outras fases continuam com o texto de antes.
        var restoring = AgentService.NetworkFailureText(Json(new { success = false, reverted = false, state = "unknown", phase = "restoring", error = NetworkChange.UnconfirmedError("restoring", 90, false), backup = "b" }));
        var changed = AgentService.NetworkFailureText(Json(new { success = false, changed = true, reverted = false, phase = "confirming", error = "Executor encerrado sem resultado.", backup = "b" }));
        var noPhase = AgentService.NetworkFailureText(Json(new { success = false, reverted = false, error = "Falhou.", backup = "b" }));
        static bool Restoration(string text) => text.Contains("restaur", StringComparison.OrdinalIgnoreCase);
        assert(!Restoration(interrupted) && interrupted.Contains("manter", StringComparison.OrdinalIgnoreCase) && interrupted.Contains("não foi conferid")
            && !Restoration(timedOut) && timedOut.Contains("não foi conferid") && !Restoration(keepingError)
            && restoring.Contains("nenhuma restauração foi confirmada") && changed.Contains("A restauração automática não foi confirmada")
            && noPhase.Contains("A restauração automática não foi confirmada"),
            "#128, " + culture + ": na fase keeping sem resultado, o texto fala só da permanência não conferida; restoring e as demais continuam como antes"
            + " · keeping: " + interrupted.Replace("\n", " ") + " · prazo: " + timedOut.Replace("\n", " "));
    }

    private static string Before(string dhcp = "Enabled", string staticDns = "", string[]? dns = null, object[]? addresses = null, object[]? routes = null, string guid = "{" + Guid + "}")
        => JsonSerializer.Serialize(new { Index = 13, Name = "Ethernet", Guid = guid, Dhcp = dhcp, StaticDns = staticDns, Dns = dns ?? ["192.168.1.1"],
            Addresses = addresses ?? [new { IPAddress = "192.168.1.9", PrefixLength = 24, PrefixOrigin = "Dhcp", AddressState = "Preferred" }],
            Routes = routes ?? [new { NextHop = "192.168.1.1", RouteMetric = 0 }] });
    private static string Current(string dhcp = "Enabled", string staticDns = "", string[]? dns = null, object[]? addresses = null, object[]? routes = null, string guid = Guid, int index = 13)
        => JsonSerializer.Serialize(new { Index = index, Guid = guid, Dhcp = dhcp, StaticDns = staticDns, Dns = dns ?? ["192.168.1.1"],
            Addresses = addresses ?? [new { IPAddress = "192.168.1.9", PrefixLength = 24 }], Routes = routes ?? [new { NextHop = "192.168.1.1" }] });
    private static NetworkTransactionRecord Record(string action) => new(action, 13, Guid, DateTimeOffset.UtcNow);

    // 4. A comparação sem elevação segue as regras do Assert-Restored do executor.
    private static void AlreadyRestoredRules(Action<bool, string> assert, string culture)
    {
        var dns = Record("set_dns"); var ipv4 = Record("set_ipv4");
        var manual = Before(staticDns: "8.8.8.8,1.1.1.1", dns: ["8.8.8.8", "1.1.1.1"]);
        var fixedIp = Before(dhcp: "Disabled", staticDns: "10.0.0.2", dns: ["10.0.0.2"],
            addresses: [new { IPAddress = "10.0.0.10", PrefixLength = 24, PrefixOrigin = 1, AddressState = 4 }, new { IPAddress = "169.254.3.3", PrefixLength = 16, PrefixOrigin = 4, AddressState = 4 }],
            routes: [new { NextHop = "10.0.0.1", RouteMetric = 256 }]);
        var twoManual = Before(dhcp: "Disabled", staticDns: "10.0.0.2", dns: ["10.0.0.2"],
            addresses: [new { IPAddress = "10.0.0.10", PrefixLength = 24, PrefixOrigin = 1, AddressState = 4 }, new { IPAddress = "10.0.0.11", PrefixLength = 24, PrefixOrigin = "Manual", AddressState = 4 }],
            routes: [new { NextHop = "10.0.0.1", RouteMetric = 256 }]);
        (string Label, bool Expected, bool Actual)[] cases =
        [
            ("DNS automático de volta", true, NetworkRecovery.MatchesBefore(Before(), Current(), dns)),
            ("DNS ainda manual", false, NetworkRecovery.MatchesBefore(Before(), Current(staticDns: "1.1.1.1", dns: ["1.1.1.1"]), dns)),
            ("DNS manual igual, na mesma ordem", true, NetworkRecovery.MatchesBefore(manual, Current(staticDns: "8.8.8.8,1.1.1.1", dns: ["8.8.8.8", "1.1.1.1"]), dns)),
            ("DNS manual em outra ordem", false, NetworkRecovery.MatchesBefore(manual, Current(staticDns: "1.1.1.1,8.8.8.8", dns: ["1.1.1.1", "8.8.8.8"]), dns)),
            ("DNS manual sem um servidor", false, NetworkRecovery.MatchesBefore(manual, Current(staticDns: "8.8.8.8", dns: ["8.8.8.8"]), dns)),
            ("DNS manual virou automático", false, NetworkRecovery.MatchesBefore(manual, Current(dns: ["192.168.1.1"]), dns)),
            ("GUID diferente", false, NetworkRecovery.MatchesBefore(Before(), Current(guid: "00000000-0000-0000-0000-000000000099"), dns)),
            ("GUID igual com chaves e maiúsculas", true, NetworkRecovery.MatchesBefore(Before(), Current(guid: "{" + Guid.ToUpperInvariant() + "}"), dns)),
            ("índice diferente", false, NetworkRecovery.MatchesBefore(Before(), Current(index: 14), dns)),
            ("DHCP diferente no set_ipv4", false, NetworkRecovery.MatchesBefore(Before(), Current(dhcp: "Disabled"), ipv4)),
            ("DHCP igual em outra caixa", true, NetworkRecovery.MatchesBefore(Before(), Current(dhcp: "enabled"), ipv4)),
            ("IP fixo restaurado", true, NetworkRecovery.MatchesBefore(fixedIp, Current(dhcp: "Disabled", staticDns: "10.0.0.2", dns: ["10.0.0.2"],
                addresses: [new { IPAddress = "10.0.0.10", PrefixLength = 24 }], routes: [new { NextHop = "10.0.0.1" }]), ipv4)),
            ("IP fixo sem o endereço manual", false, NetworkRecovery.MatchesBefore(fixedIp, Current(dhcp: "Disabled", staticDns: "10.0.0.2", dns: ["10.0.0.2"],
                addresses: [new { IPAddress = "10.0.0.20", PrefixLength = 24 }], routes: [new { NextHop = "10.0.0.1" }]), ipv4)),
            ("IP fixo com outro prefixo", false, NetworkRecovery.MatchesBefore(fixedIp, Current(dhcp: "Disabled", staticDns: "10.0.0.2", dns: ["10.0.0.2"],
                addresses: [new { IPAddress = "10.0.0.10", PrefixLength = 16 }], routes: [new { NextHop = "10.0.0.1" }]), ipv4)),
            ("IP fixo sem o gateway", false, NetworkRecovery.MatchesBefore(fixedIp, Current(dhcp: "Disabled", staticDns: "10.0.0.2", dns: ["10.0.0.2"],
                addresses: [new { IPAddress = "10.0.0.10", PrefixLength = 24 }], routes: []), ipv4)),
            ("IP fixo com o DNS trocado", false, NetworkRecovery.MatchesBefore(fixedIp, Current(dhcp: "Disabled", staticDns: "1.1.1.1", dns: ["1.1.1.1"],
                addresses: [new { IPAddress = "10.0.0.10", PrefixLength = 24 }], routes: [new { NextHop = "10.0.0.1" }]), ipv4)),
            ("leitura ilegível", false, NetworkRecovery.MatchesBefore(Before(), "não é json", dns)),
            ("leitura vazia", false, NetworkRecovery.MatchesBefore(Before(), "", dns)),
            ("ação desconhecida", false, NetworkRecovery.MatchesBefore(Before(), Current(), Record("enable_network_adapter"))),
            // @qa: o backup e o registro protegidos precisam falar da mesma interface que a leitura de agora.
            ("backup de outro índice", false, NetworkRecovery.MatchesBefore(Before().Replace("\"Index\":13", "\"Index\":12"), Current(), dns)),
            ("registro com outro GUID esperado", false, NetworkRecovery.MatchesBefore(Before(), Current(), dns with { ExpectedGuid = "00000000-0000-0000-0000-000000000099" })),
            ("IP fixo sem o 2º endereço manual", false, NetworkRecovery.MatchesBefore(twoManual, Current(dhcp: "Disabled", staticDns: "10.0.0.2", dns: ["10.0.0.2"],
                addresses: [new { IPAddress = "10.0.0.10", PrefixLength = 24 }], routes: [new { NextHop = "10.0.0.1" }]), ipv4)),
            ("IP fixo com os 2 endereços manuais", true, NetworkRecovery.MatchesBefore(twoManual, Current(dhcp: "Disabled", staticDns: "10.0.0.2", dns: ["10.0.0.2"],
                addresses: [new { IPAddress = "10.0.0.11", PrefixLength = 24 }, new { IPAddress = "10.0.0.10", PrefixLength = 24 }], routes: [new { NextHop = "10.0.0.1" }]), ipv4)),
        ];
        // Sem a propriedade StaticDns na leitura não se presume DNS automático.
        var missingStatic = NetworkRecovery.MatchesBefore(Before(), "{\"Index\":13,\"Guid\":\"" + Guid + "\",\"Dhcp\":\"Enabled\",\"Dns\":[\"192.168.1.1\"],\"Addresses\":[],\"Routes\":[]}", dns);
        var wrong = cases.Where(c => c.Expected != c.Actual).Select(c => c.Label).ToArray();
        assert(wrong.Length == 0 && !missingStatic, "#128, " + culture + ": a conferência sem elevação segue as regras do Assert-Restored (identidade, DHCP, IP manual, gateway, DNS na mesma ordem) e recusa leitura incompleta"
            + (wrong.Length > 0 ? " · erradas: " + string.Join(" | ", wrong) : "") + (missingStatic ? " · leitura sem StaticDns aceita" : ""));
    }

    // 3. A nota anexada à conversa de origem traz a linha de fatos e não afirma restauração que não houve.
    private static void ConversationNotes(Action<bool, string> assert, string culture)
    {
        var when = new DateTimeOffset(2026, 11, 22, 15, 50, 0, TimeSpan.Zero);
        var restored = NetworkRecovery.ConversationNote(JsonSerializer.Serialize(new { success = true, restored = true, alreadyRestored = false, message = "Configuração anterior restaurada e conferida." }), when);
        var already = NetworkRecovery.ConversationNote(JsonSerializer.Serialize(new { success = true, restored = false, alreadyRestored = true, message = "A configuração atual já corresponde ao estado anterior salvo. Nenhuma alteração foi feita." }), when);
        var declined = NetworkRecovery.ConversationNote(JsonSerializer.Serialize(new { success = false, restored = false, declined = true, message = "Você optou por não restaurar." }), when);
        var failed = NetworkRecovery.ConversationNote(JsonSerializer.Serialize(new { success = false, restored = false, error = "Falhou." }), when);
        var garbage = NetworkRecovery.ConversationNote("{", when);
        string Facts(string note) => note.Split('\n').LastOrDefault(l => l.StartsWith(TurnFacts.Prefix, StringComparison.Ordinal)) ?? "";
        assert(Facts(restored).Contains("restaurada e conferida") && Facts(already).Contains("nenhuma") && !Facts(already).Contains("restaurada e")
            && Facts(declined).Contains("nenhuma") && Facts(failed).Contains("nenhuma confirmada") && Facts(garbage).Contains("nenhuma confirmada")
            && restored.Contains("Configuração anterior restaurada e conferida.") && new[] { restored, already, declined, failed, garbage }.All(n => n.Contains("22/11")),
            "#128, " + culture + ": a nota da recuperação na conversa de origem traz o resultado e a linha de fatos, sem afirmar restauração que não houve"
            + " · " + Facts(restored) + " | " + Facts(already) + " | " + Facts(failed));
    }

    private sealed class FakeProcess : IElevatedProcess
    {
        public int? Id => 4242;
        public bool HasExited => true;
        public Task WaitForExitAsync(CancellationToken token) => Task.CompletedTask;
        public void Dispose() { }
    }

    private static (string Root, InterruptedNetworkTransaction Item) Interrupted(string action = "set_dns", string? before = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "NexoFollowUp-" + System.Guid.NewGuid().ToString("N"));
        var id = System.Guid.NewGuid().ToString("N");
        var local = Path.Combine(root, "local", "operations", id); var secured = Path.Combine(root, "protected", id);
        Directory.CreateDirectory(local); Directory.CreateDirectory(secured);
        var record = Record(action);
        return (root, new InterruptedNetworkTransaction(local, record, "restoring", NetworkRecovery.Validate(before ?? Before(), record), null, secured));
    }

    // 4. O estado atual já corresponde: nada é perguntado nem elevado, e o resultado fica registrado; uma diferença segue o caminho de antes.
    private static async Task RecoveryWithoutElevation(Action<bool, string> assert)
    {
        var script = NetworkRecovery.CurrentStateScript("13");
        var modules = script.IndexOf("$env:PSModulePath=[IO.Path]::Combine($PSHOME,'Modules');", StringComparison.Ordinal);
        assert(modules == 0 && script.IndexOf("Get-", StringComparison.Ordinal) > modules,
            "#128 (@qa): a leitura sem elevação usa só os módulos do Windows ($PSHOME), antes do primeiro cmdlet; um módulo plantado no perfil do usuário não falsifica o estado atual");
        var (root, item) = Interrupted();
        try
        {
            var prompts = 0; var starts = 0; var reads = 0; ChangeRequest? lastPrompt = null;
            Task<bool> Confirm(ChangeRequest request) { prompts++; lastPrompt = request; return Task.FromResult(false); }
            IElevatedProcess Start(string _) { starts++; return new FakeProcess(); }
            var first = await NetworkRecovery.Recover(item, Confirm, Start, TimeSpan.FromSeconds(1), CancellationToken.None, (_, _) => { reads++; return Task.FromResult<string?>(Current()); });
            using var firstJson = JsonDocument.Parse(first);
            var local = Path.Combine(item.Directory, NetworkRecovery.ResultFile);
            var second = await NetworkRecovery.Recover(item, Confirm, Start, TimeSpan.FromSeconds(1), CancellationToken.None, (_, _) => Task.FromResult<string?>(Current()));
            using var secondJson = JsonDocument.Parse(second);
            assert(prompts == 0 && starts == 0 && reads == 1 && NetworkRecovery.Restored(first)
                && firstJson.RootElement.GetProperty("alreadyRestored").GetBoolean() && firstJson.RootElement.GetProperty("verifiedWithoutElevation").GetBoolean()
                && File.Exists(local) && !firstJson.RootElement.TryGetProperty("alreadyRecorded", out _)
                && secondJson.RootElement.GetProperty("alreadyRecorded").GetBoolean() && NetworkRecovery.Restored(second),
                "#128: com o estado atual igual ao backup protegido, a recuperação registra o resultado sem perguntar nem pedir UAC; na abertura seguinte não repete o aviso");

            // A marca local só suprime o aviso quando a leitura de agora também confere; com o DNS trocado de novo, a oferta volta.
            prompts = starts = 0;
            await NetworkRecovery.Recover(item, Confirm, Start, TimeSpan.FromSeconds(1), CancellationToken.None, (_, _) => Task.FromResult<string?>(Current(staticDns: "1.1.1.1", dns: ["1.1.1.1"])));
            var differentPrompts = prompts;
            // @qa: a conferência sem elevação não encerra a transação na pasta protegida. Uma diferença depois dela pode ser mudança
            // posterior do usuário: a oferta volta (a marca local não a suprime), mas avisa que restaurar desfaria essa mudança.
            var laterWarned = lastPrompt?.Details.Contains(NetworkRecovery.LaterChangeWarning.Trim()) == true;
            // Leitura que falha ou não devolve nada: caminho de antes (a janela de restauração).
            prompts = 0;
            await NetworkRecovery.Recover(item, Confirm, Start, TimeSpan.FromSeconds(1), CancellationToken.None, (_, _) => throw new IOException("Get-NetAdapter falhou"));
            var failingPrompts = prompts;
            prompts = 0;
            await NetworkRecovery.Recover(item, Confirm, Start, TimeSpan.FromSeconds(1), CancellationToken.None, (_, _) => Task.FromResult<string?>(null));
            var nullPrompts = prompts;
            // Sem cópia protegida conhecida, a leitura não é usada.
            var (otherRoot, other) = Interrupted();
            var unprotected = other with { ProtectedDirectory = null };
            var unprotectedReads = 0; prompts = 0;
            await NetworkRecovery.Recover(unprotected, Confirm, Start, TimeSpan.FromSeconds(1), CancellationToken.None, (_, _) => { unprotectedReads++; return Task.FromResult<string?>(Current()); });
            var unprotectedPrompts = prompts;
            var freshWarned = lastPrompt?.Details.Contains(NetworkRecovery.LaterChangeWarning.Trim()) == true;
            try { Directory.Delete(otherRoot, true); } catch (IOException) { }
            assert(laterWarned && !freshWarned, "#128 (@qa): com a conferência sem elevação já registrada e a rede diferente agora, a oferta volta avisando que restaurar desfaria uma mudança posterior; sem a marca, o aviso não aparece");
            assert(differentPrompts == 1 && failingPrompts == 1 && nullPrompts == 1 && unprotectedReads == 0 && unprotectedPrompts == 1,
                "#128: estado diferente, leitura que falha, leitura vazia ou transação sem cópia protegida seguem para a janela de restauração"
                + $" · diferente {differentPrompts}, falha {failingPrompts}, vazia {nullPrompts}, sem cópia {unprotectedReads}/{unprotectedPrompts}");
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    // 2. Janela fechada sem escolha: é "Agora não" — nada é restaurado nem gravado como recusa.
    private static async Task PostponedRecovery(Action<bool, string> assert)
    {
        var (root, item) = Interrupted();
        try
        {
            ChangeRequest? shown = null; var starts = 0;
            IElevatedProcess Start(string _) { starts++; return new FakeProcess(); }
            var postponed = await NetworkRecovery.Recover(item, request => { shown = request; request.OnDismissed?.Invoke(); return Task.FromResult(false); }, Start, TimeSpan.FromSeconds(1), CancellationToken.None);
            using var json = JsonDocument.Parse(postponed);
            var local = Path.Combine(item.Directory, NetworkRecovery.ResultFile);
            var postponedWrote = File.Exists(local);
            var declined = await NetworkRecovery.Recover(item, _ => Task.FromResult(false), Start, TimeSpan.FromSeconds(1), CancellationToken.None);
            assert(shown is { Postponable: true } && json.RootElement.GetProperty("postponed").GetBoolean() && !json.RootElement.TryGetProperty("declined", out _)
                && !NetworkRecovery.Restored(postponed) && starts == 0 && !postponedWrote && NetworkRecovery.Explain(postponed).Contains("próxima abertura")
                && declined.Contains("\"declined\":true") && File.Exists(local),
                "#128: fechar a janela de restauração sem escolher adia a oferta para a próxima abertura, sem restaurar nem gravar recusa; o botão de recusa continua como antes");
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    // 3. A origem fica na pasta local da operação, e o resultado vai para a conversa do mesmo usuário.
    private static async Task OriginConversation(Store store, Account actor, Action<bool, string> assert)
    {
        var (root, item) = Interrupted();
        try
        {
            var mine = new Conversation { UserId = actor.Id, Title = "troca de DNS", Entries = [new Entry { Role = "user", Text = "Troque o DNS da minha conexão Ethernet para 1.1.1.1." }] };
            var other = new Account { Login = "outra-" + System.Guid.NewGuid().ToString("N")[..6] };
            var theirs = new Conversation { UserId = other.Id, Title = "de outra pessoa" };
            store.Data.Conversations.Add(mine); store.Data.Conversations.Add(theirs);
            NetworkRecovery.WriteOrigin(item.Directory, new NetworkOrigin(mine.Id, actor.Id));
            var outcome = JsonSerializer.Serialize(new { success = true, restored = true, alreadyRestored = false, message = "Configuração anterior restaurada e conferida." });
            var attached = NetworkRecovery.AttachToOrigin(store, actor, item.Directory, outcome, DateTimeOffset.UtcNow);
            var note = mine.Entries.LastOrDefault();
            // Outro usuário na sessão: a conversa de origem não é dele, nada é anexado.
            var wrongUser = NetworkRecovery.AttachToOrigin(store, other, item.Directory, outcome, DateTimeOffset.UtcNow);
            // Origem apontando para a conversa de outro usuário (arquivo local alterado): também nada.
            NetworkRecovery.WriteOrigin(item.Directory, new NetworkOrigin(theirs.Id, actor.Id));
            var forged = NetworkRecovery.AttachToOrigin(store, actor, item.Directory, outcome, DateTimeOffset.UtcNow);
            File.WriteAllText(Path.Combine(item.Directory, NetworkRecovery.OriginFile), "{ não é json");
            var unreadable = NetworkRecovery.AttachToOrigin(store, actor, item.Directory, outcome, DateTimeOffset.UtcNow);
            var missing = NetworkRecovery.AttachToOrigin(store, actor, Path.Combine(root, "sem-origem"), outcome, DateTimeOffset.UtcNow);
            assert(ReferenceEquals(attached, mine) && note is { Role: "assistant", Presentation: "result" } && note.Text.Contains("Configuração anterior restaurada e conferida.")
                && note.Text.Contains(TurnFacts.Prefix) && mine.Entries.Count == 2 && theirs.Entries.Count == 0
                && wrongUser == null && forged == null && unreadable == null && missing == null,
                "#128: o resultado da recuperação vai para a conversa de origem do mesmo usuário, com a linha de fatos; outro usuário, origem forjada ou ilegível não recebem nada");
            // @qa: fechar a oferta sem escolher não é resultado; nada vai para a conversa a cada abertura adiada.
            NetworkRecovery.WriteOrigin(item.Directory, new NetworkOrigin(mine.Id, actor.Id));
            var entriesBefore = mine.Entries.Count;
            var postponedNote = NetworkRecovery.AttachToOrigin(store, actor, item.Directory, JsonSerializer.Serialize(new { success = false, restored = false, postponed = true, message = NetworkRecovery.PostponedMessage }), DateTimeOffset.UtcNow);
            assert(postponedNote == null && mine.Entries.Count == entriesBefore, "#128 (@qa): a oferta adiada não é anexada à conversa de origem");
            var failedNote = NetworkRecovery.AttachToOrigin(store, actor, item.Directory, JsonSerializer.Serialize(new { success = false, restored = false, error = "Acesso negado." }), DateTimeOffset.UtcNow);
            assert(ReferenceEquals(failedNote, mine) && mine.Entries[^1].Presentation == "error" && !mine.Entries[^1].Text.Contains("restaurada e conferida"),
                "#128 (@qa): uma recuperação que falhou chega à conversa de origem como erro, sem afirmar restauração");
            store.Data.Conversations.Remove(mine); store.Data.Conversations.Remove(theirs); store.Save();

            // O set_dns grava a origem na pasta da operação antes de iniciar o executor (aqui, UAC recusado).
            var applyRoot = Path.Combine(root, "apply"); var applyStore = new Store(applyRoot, directAccess: true);
            var trust = new NetworkRecoveryTrust(Path.Combine(root, "apply-protected"), _ => true);
            var request = new NetworkRequest("set_dns", 13, "automatic", [], "", 0, "") { ExpectedGuid = Guid };
            try
            {
                await NetworkChange.Apply(request, applyStore, _ => Task.FromResult(true), new Progress<string>(), CancellationToken.None,
                    _ => throw new System.ComponentModel.Win32Exception(1223), trust: trust, origin: new NetworkOrigin("conversa-1", actor.Id));
            }
            catch (System.ComponentModel.Win32Exception) { }
            var operation = Directory.EnumerateDirectories(Path.Combine(applyRoot, "operations")).Single();
            var origin = NetworkRecovery.ReadOrigin(operation);
            assert(origin == new NetworkOrigin("conversa-1", actor.Id), "#128: a alteração de rede grava a conversa de origem na pasta local da operação");
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    private static object Adapter(int index = 13, string name = "Ethernet") => new
    {
        Index = index, Guid = "{" + Guid + "}", Name = name, Description = "Realtek PCIe GbE", Status = "Up", Disabled = false, Hardware = true, Virtual = false, InterfaceType = 6,
        Dhcp = "Enabled", Addresses = new[] { new { IPAddress = "192.168.1.9", PrefixLength = 24, PrefixOrigin = "Dhcp", AddressState = "Preferred" } },
        Gateways = new[] { "192.168.1.1" }, Dns = new[] { "1.1.1.1" }, DnsSource = "manual", StaticDns = new[] { "1.1.1.1" }, DhcpDns = new[] { "192.168.1.1" }
    };
    private static string Network(params object[] adapters) => WindowsTools.Ok(new { Adapters = adapters.Length == 0 ? new[] { Adapter() } : adapters });

    // 5. Cada consulta substitui a anterior: a interface que sumiu da rede deixa de ser aceita; sem índice numérico, nada é aceito.
    private static void NetworkTargetRules(Action<bool, string> assert)
    {
        var none = Json(new { });
        var evidence = new InvestigationEvidence();
        var before = evidence.NetworkTargetProblem("set_dns", Json(new { interface_index = 13, mode = "automatic" }));
        evidence.Observe("get_network", "n1", none, Network(Adapter(), Adapter(7, "Wi-Fi")));
        var wifiFirst = evidence.NetworkTargetProblem("set_ipv4", Json(new { interface_index = 7, mode = "dhcp" }));
        evidence.Observe("get_network", "n2", none, Network(Adapter()));
        var wifiGone = evidence.NetworkTargetProblem("enable_network_adapter", Json(new { interface_index = 7 }));
        var ethernet = evidence.NetworkTargetProblem("set_dns", Json(new { interface_index = 13, mode = "automatic" }));
        var text = evidence.NetworkTargetProblem("set_dns", Json(new { interface_index = "13", mode = "automatic" }));
        var other = evidence.NetworkTargetProblem("restart_service", Json(new { service = "Spooler" }));
        assert(before != null && before.Contains("get_network") && before.Contains("não foi consultada") && wifiFirst == null && wifiGone != null && wifiGone.Contains("#7") && ethernet == null && text != null && other == null,
            "#128: o índice precisa estar na consulta de rede mais recente do turno; antes da consulta, com interface que sumiu ou sem número, a alteração de rede é recusada");
    }

    // 5. O caso real: set_dns(mode=automatic, interface_index=1) sem consulta. O Nexo consulta a rede, recusa o índice com a lista
    // do que é aceito, e nada chega ao executor; o índice certo, depois, passa.
    private static async Task NetworkTargetFromQuery(Store store, Account actor, Action<bool, string> assert)
    {
        var tools = new FlowTools((name, _) => name switch
        {
            "get_network" => Network(Adapter(), Adapter(7, "Wi-Fi")),
            "set_dns" => JsonSerializer.Serialize(new { success = false, denied = true }),
            _ => throw new Exception("Ferramenta inesperada: " + name)
        });
        var model = new ModelSequence([Call("set_dns", new { interface_index = 1, mode = "automatic" }), Call("set_dns", new { interface_index = 13, mode = "automatic" })]);
        var answer = await Ask(store, actor, Request, tools, model);
        var refusal = model.Requests.Count > 1 ? LastTool(model.Requests[1]) : "";
        assert(tools.Calls.SequenceEqual(["get_network", "set_dns"]) && refusal.Contains("#13") && refusal.Contains("Ethernet") && refusal.Contains("#7")
            && refusal.Contains("interface #1") && !answer.Contains("pode ter sido alterado") && answer.Contains("Você recusou"),
            "#128: set_dns com índice que não veio da consulta é recusado antes do executor, com as interfaces consultadas; o índice certo chega ao executor"
            + " · chamadas " + string.Join(",", tools.Calls) + " · " + answer.Replace("\n", " ")[..Math.Min(answer.Length, 200)]);

        // O modelo já consultou a rede neste turno: o Nexo não consulta de novo e recusa o índice que não apareceu.
        var queried = new FlowTools((name, _) => name == "get_network" ? Network() : throw new Exception("Ferramenta inesperada: " + name));
        var queriedModel = new ModelSequence([Call("get_network", new { }), Call("set_dns", new { interface_index = 1, mode = "automatic" }), Call("finish_support", new { message = "A interface #1 não existe nesta consulta; a Ethernet é a #13." })]);
        var queriedAnswer = await Ask(store, actor, Request, queried, queriedModel);
        // A consulta falhou: a recusa pede a consulta e nada chega ao executor.
        var failing = new FlowTools((name, _) => name == "get_network" ? throw new IOException("Get-NetAdapter indisponível") : throw new Exception("Ferramenta inesperada: " + name));
        var failingModel = new ModelSequence([Call("set_dns", new { interface_index = 1, mode = "automatic" }), Call("finish_support", new { message = "Não consegui consultar a rede; nenhuma alteração foi feita." })]);
        var failingAnswer = await Ask(store, actor, Request, failing, failingModel);
        var failingRefusal = failingModel.Requests.Count > 1 ? LastTool(failingModel.Requests[1]) : "";
        assert(queried.Calls.SequenceEqual(["get_network"]) && queriedModel.Requests.Count == 3 && LastTool(queriedModel.Requests[2]).Contains("#13")
            && !queriedAnswer.Contains("pode ter sido alterado")
            && failing.Calls.SequenceEqual(["get_network"]) && failingRefusal.Contains("get_network") && !failingAnswer.Contains("pode ter sido alterado"),
            "#128: com a rede já consultada, o índice ausente é recusado sem nova consulta; com a consulta falhando, a recusa pede get_network e nada é alterado");
    }

    private static async Task<string> Ask(Store store, Account actor, string prompt, IAgentTools tools, ModelSequence model)
    {
        using var http = new HttpClient(model);
        var conversation = new Conversation { UserId = actor.Id, Entries = [new Entry { Role = "user", Text = prompt }] };
        return (await new AgentService(store, tools, http).Run(actor, conversation, new Progress<string>(), (_, _) => { }, CancellationToken.None)).ReplaceLineEndings("\n");
    }

    // Conteúdo da última mensagem de ferramenta enviada ao modelo: é nela que a recusa chega.
    private static string LastTool(string request)
    {
        using var json = JsonDocument.Parse(request);
        return json.RootElement.GetProperty("messages").EnumerateArray().LastOrDefault(m => m.GetProperty("role").GetString() == "tool") is { ValueKind: JsonValueKind.Object } tool
            ? tool.GetProperty("content").GetString() ?? "" : "";
    }

    private static string Call(string name, object args) => JsonSerializer.Serialize(new { message = new { role = "assistant", content = "", tool_calls = new[] { new { function = new { name, arguments = args } } } } });

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
        internal List<string> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (Requests.Count > responses.Length) throw new Exception("O modelo foi chamado além do roteiro (" + responses.Length + ").");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responses[Requests.Count - 1], Encoding.UTF8, "application/json") };
        }
    }
}
