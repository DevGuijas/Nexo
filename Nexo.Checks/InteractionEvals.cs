using System.Text.Json;
using Nexo.Support;

internal static class InteractionEvals
{
    public static async Task Run(Store store, Account actor, Action<bool, string> assert)
    {
        store.Data.AiEnabled = true; store.Data.Model = AgentService.DefaultModel;
        await LocalModel.EnsureRunning(CancellationToken.None);
        foreach (var device in new[] { false, true })
        {
            var fake = new InteractionTools(device);
            var conversation = new Conversation { UserId = actor.Id, Entries = [new Entry { Role = "user", Text = device ? "Meu fone USB para quando mexo no cabo. Pode acompanhar eu desconectar e conectar o fone?" : "Minha Ethernet está com cabo desconectado. Quero conectar o cabo e que você detecte quando fiz isso e teste a conexão." }] };
            var answer = await new AgentService(store, fake, workflowDelay: (_, _) => Task.CompletedTask).Run(actor, conversation, new Progress<string>(), (label, _) => Console.WriteLine(label), CancellationToken.None);
            Console.WriteLine(answer);
            assert(fake.Watched && (device ? answer.Contains("test", StringComparison.OrdinalIgnoreCase) || answer.Contains("funcion", StringComparison.OrdinalIgnoreCase) : fake.ReadsAfter > 0) && !fake.Unexpected, "Reconexão e verificação funcional: " + (device ? "USB com Qwen" : "cabo com fluxo guiado"));
        }
        var reboot = new InteractionTools(false);
        var restartConversation = new Conversation { UserId = actor.Id, Entries = [new Entry { Role = "user", Text = "O instalador do driver pediu reinicialização. Prepare este atendimento para eu salvar meus arquivos, reiniciar manualmente e retomar aqui." }] };
        var reply = await new AgentService(store, reboot).Run(actor, restartConversation, new Progress<string>(), (_, _) => { }, CancellationToken.None);
        assert(restartConversation.PendingRestartBoot == "boot-before" && reply.Contains("Salve seus arquivos"), "Qwen prepara reinício manual e salva ponto de retomada");
        reboot.Boot = "boot-after";
        restartConversation.Entries.Add(new Entry { Role = "user", Text = "Voltei. Apenas consulte a rede, sem alterar nada." });
        await new AgentService(store, reboot).Run(actor, restartConversation, new Progress<string>(), (_, _) => { }, CancellationToken.None);
        assert(restartConversation.PendingRestartBoot == null, "Retomada identifica nova inicialização e limpa pendência");
    }
    private sealed class InteractionTools(bool device) : IAgentTools
    {
        public bool Watched, Unexpected;
        public int ReadsAfter;
        public string Boot = "boot-before";
        public Task<string> Execute(string name, JsonElement args, CancellationToken token)
        {
            if (name is "get_boot_status" or "prepare_restart") return Task.FromResult(WindowsTools.Ok(new { BootId = Boot }));
            if (name == "get_network")
            {
                if (Watched) ReadsAfter++;
                return Task.FromResult(WindowsTools.Ok(new { Adapters = new[] { new { Index = 17, Guid = "00000000-0000-0000-0000-000000000017", Name = "Ethernet", InterfaceType = 6, Status = Watched ? "Up" : "Disconnected", Disabled = false, Hardware = true, Virtual = false, Dhcp = "Enabled" } } }));
            }
            if (name == "diagnose_device")
            {
                if (Watched) ReadsAfter++;
                return Task.FromResult(WindowsTools.Ok(new { count = 1, devices = new[] { new { Id = "USB\\HEADSET_TEST", Name = "Fone USB", Code = 0, Status = "OK" } } }));
            }
            if (name == "get_services") return Task.FromResult(WindowsTools.Ok(new[] { new { Name = "Audiosrv", Status = "Running" } }));
            if (name == "wait_for_reconnect")
            {
                if (Watched || (device ? args.GetProperty("device_id").GetString() != "USB\\HEADSET_TEST" : args.GetProperty("interface_index").GetInt32() != 17)) { Unexpected = true; throw new Exception("Alvo incorreto"); }
                Watched = true; return Task.FromResult(WindowsTools.Ok(new PhysicalResult(true, true, false, "Windows detectou desconexão e retorno do mesmo alvo; ainda precisa de teste funcional.")));
            }
            if (name == "test_connection") return Task.FromResult(WindowsTools.Ok(new { host = args.GetProperty("host").GetString(), dnsResolved = Watched, tcpConnected = Watched }));
            Unexpected = true; throw new Exception("Alteração indevida: " + name);
        }
    }
}
