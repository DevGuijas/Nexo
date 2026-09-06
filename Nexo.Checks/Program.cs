using Nexo.Support;

var root = Path.Combine(Path.GetTempPath(), "NexoChecks-" + Guid.NewGuid().ToString("N"));
var count = 0;
void Assert(bool condition, string title) { if (!condition) throw new Exception("FAIL: " + title); Console.WriteLine("PASS: " + title); count++; }
void Reject(Action action, string title) { try { action(); } catch (InvalidOperationException) { Assert(true, title); return; } throw new Exception("FAIL: " + title); }
try
{
    var store = new Store(root);
    store.Data.AiEnabled = false;
    var admin = store.Login("Guisbz", "1234");
    Assert(admin.Admin && admin.MustChangePassword, "Conta inicial e indicação de troca de senha");
    Assert(!File.ReadAllText(Path.Combine(root, "data.json")).Contains("1234"), "Senha inicial não armazenada em texto");
    Reject(() => store.Login("Guisbz", "wrong"), "Senha incorreta recusada");
    var limited = new Account { Login = "usuario", Name = "Usuário", Diagnose = false, Repair = false, Tickets = false };
    store.SaveAccount(admin, limited, "senha-teste-123");
    Assert(store.Login("USUARIO", "senha-teste-123").Id == limited.Id, "Login ignora maiúsculas");
    Reject(() => store.SaveAccount(limited, new Account { Login = "hacker", Name = "X" }, "senha-teste"), "Usuário comum não cria contas");
    Reject(() => store.Require(limited, a => a.Repair), "Correção exige permissão");
    Reject(() => store.Require(limited, a => a.Tickets), "Encaminhamento exige permissão");
    Reject(() => store.SaveAccount(admin, new Account { Login = "USUARIO", Name = "Duplicado" }, "senha-teste"), "Login duplicado recusado");
    Reject(() => store.SaveAccount(admin, new Account { Id = admin.Id, Login = admin.Login, Name = admin.Name, Admin = false }, null), "Último administrador preservado");
    Reject(() => store.UpdateProfile(limited, "Teste", "email inválido", "", "", ""), "E-mail inválido recusado");
    Reject(() => store.UpdateProfile(limited, "Teste", "", "", "errada", "nova-senha-123"), "Troca de senha exige senha atual");
    store.UpdateProfile(limited, "Perfil editado", "teste@example.com", "", "senha-teste-123", "nova-senha-123");
    Assert(store.Login("usuario", "nova-senha-123").Name == "Perfil editado" && !limited.MustChangePassword, "Perfil e senha atualizados");
    Reject(() => store.Login("usuario", "senha-teste-123"), "Senha antiga invalidada");
    limited.Active = false;
    Reject(() => store.Login("usuario", "nova-senha-123"), "Conta desativada não entra");
    for (var i = 0; i < 5; i++) { try { store.Login("Guisbz", "wrong"); } catch (InvalidOperationException) { } }
    Reject(() => store.Login("Guisbz", "1234"), "Bloqueio temporário após falhas");
    store.Save(); var reopened = new Store(root);
    Assert(reopened.Data.Accounts.Count == 2 && reopened.Data.Audit.Count > 0, "Contas e auditoria persistem");
    Assert(SupportService.Topic("Minha câmera parou") == "camera" && SupportService.Topic("sem internet") == "network" && SupportService.Topic("sem áudio") == "audio", "Roteamento dos diagnósticos");
    var service = new SupportService(store);
    try { await service.Diagnose(limited, "camera", new Progress<string>(), CancellationToken.None); throw new Exception("Permissão ignorada"); } catch (InvalidOperationException) { Assert(true, "Diagnóstico bloqueado no serviço"); }
    var answer = await service.AskAi(admin, new Conversation(), CancellationToken.None);
    Assert(answer.Contains("modo de diagnóstico local"), "Modo sem IA identificado explicitamente");
    Reject(() => WindowsTools.Ipv4("1.1.1.1'; Remove-Item C:\\ -Recurse"), "Entrada de shell em endereço rejeitada");
    Reject(() => WindowsTools.ValidateStatic("192.168.1.0", 24, "192.168.1.1"), "Endereço de rede rejeitado");
    Reject(() => WindowsTools.ValidateStatic("192.168.1.5", 24, "192.168.2.1"), "Gateway fora da sub-rede rejeitado");
    WindowsTools.ValidateStatic("192.168.1.5", 24, "192.168.1.1"); Assert(true, "IPv4 estático e gateway válidos");
    await AgentChecks.Run(store, admin, Assert);
    await ContinuousChecks.Run(store, admin, Assert);
    await ObservationChecks.Run(store, admin, Assert);
    await MultiInterfaceChecks.Run(Assert);
    if (args.Contains("--interface-probe-live"))
    {
        var probeTools = new WindowsTools(store, admin, _ => throw new Exception("Uma leitura solicitou alteração"), new Progress<string>());
        using var result = System.Text.Json.JsonDocument.Parse(await probeTools.Execute("probe_network_interfaces", System.Text.Json.JsonSerializer.SerializeToElement(new { }), CancellationToken.None));
        var reports = result.RootElement.GetProperty("data").GetProperty("Interfaces").EnumerateArray().ToArray();
        Console.WriteLine(result.RootElement.GetRawText());
        Assert(reports.Length > 0 && reports.Any(r => r.GetProperty("ProbeCompleted").GetBoolean()), "Sondagem real somente leitura vincula a saída à interface");
    }
    if (args.Contains("--diagnostics"))
    {
        foreach (var topic in new[] { "camera", "audio", "network", "system" })
        {
            var result = await service.Diagnose(admin, topic, new Progress<string>(s => Console.WriteLine("  " + s)), CancellationToken.None);
            Assert(!string.IsNullOrWhiteSpace(result.Summary), "Consulta real do Windows: " + topic);
        }
        var tools = new WindowsTools(store, admin, _ => throw new Exception("Uma leitura solicitou uma alteração"), new Progress<string>());
        foreach (var name in new[] { "get_network", "get_printers", "get_services", "get_processes", "get_recent_errors" })
        {
            var result = await tools.Execute(name, System.Text.Json.JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
            Assert(result.Contains("\"success\":true"), "Ferramenta real somente leitura: " + name);
        }
        foreach (var topic in new[] { "bluetooth", "printer" })
        {
            var result = await tools.Execute("diagnose_device", System.Text.Json.JsonSerializer.SerializeToElement(new { topic }), CancellationToken.None);
            Assert(result.Contains("\"success\":true"), "Consulta real de dispositivos: " + topic);
        }
    }
    if (args.Contains("--agent-live")) await AgentChecks.Live(store, admin, Assert);
    if (args.Contains("--adapter-evals")) await AdapterEvals.Run(store, admin, Assert);
    if (args.Contains("--network-evals")) await NetworkEvals.Run(store, admin, Assert);
    if (args.Contains("--interaction-evals")) await InteractionEvals.Run(store, admin, Assert);
    Console.WriteLine($"\n{count} verificações aprovadas. Nenhuma correção de hardware executada.");
}
finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
