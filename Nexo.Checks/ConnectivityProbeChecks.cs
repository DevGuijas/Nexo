using Nexo.Support;
using System.Net;
using System.Net.Sockets;

namespace Nexo.Checks;

internal static class ConnectivityProbeChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        check(typeof(HealthSnapshot).Assembly.GetType("Nexo.Support.ConnectivityProbe") != null,
            "Sondagem independente de conectividade disponível para o monitor");
        var never = new ConnectivityProbe((_, _) => throw new Exception("Ping não autorizado"),
            (_, _) => throw new Exception("DNS não autorizado"), (_, _) => throw new Exception("HTTP não autorizado"));
        var disabled = await never.Run(new HealthSettings(), true, [IPAddress.Parse("192.0.2.1")], CancellationToken.None);
        check(disabled.Gateway.State == "disabled" && disabled.Dns.State == "disabled" && disabled.External.State == "disabled",
            "Preferências padrão não iniciam nenhuma consulta de rede");

        var allEnabled = new HealthSettings { CheckGateway = true, CheckDns = true, CheckExternal = true };
        var offline = await never.Run(allEnabled, false, [], CancellationToken.None);
        check(offline.Gateway.State == "unavailable" && offline.Dns.State == "unavailable" && offline.External.State == "unavailable",
            "Sem interface local, as três sondagens ficam indisponíveis sem criar falha externa");

        var gatewayMissing = await never.Run(new HealthSettings { CheckGateway = true }, true, [], CancellationToken.None);
        check(gatewayMissing.Gateway.State == "unavailable", "Interface ativa sem gateway não vira falha de ping");

        var noPing = new ConnectivityProbe((_, _) => Task.FromResult(false));
        var pingResult = await noPing.Run(new HealthSettings { CheckGateway = true }, true, [IPAddress.Parse("192.0.2.1")], CancellationToken.None);
        check(pingResult.Gateway.State == "no_response" && pingResult.Gateway.Target == "192.0.2.1",
            "Gateway sem resposta ICMP não é declarado desconectado");

        var failedDns = new ConnectivityProbe(resolve: (_, _) => Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound)));
        var dnsResult = await failedDns.Run(new HealthSettings { CheckDns = true, DnsProbeHost = "example.com" }, true, [], CancellationToken.None);
        check(dnsResult.Dns.State == "no_response" && dnsResult.Dns.Target == "example.com",
            "Nome não resolvido fica restrito ao alvo consultado");

        var settings = new HealthSettings { CheckExternal = true, HttpProbeUrl = "https://example.com/check", HttpExpectedText = "OK" };
        var redirected = new ConnectivityProbe(request: (_, _) => Task.FromResult(new HttpProbeResponse(302, "")));
        var redirectResult = await redirected.Run(settings, true, [], CancellationToken.None);
        check(redirectResult.External.State == "unexpected", "Redirecionamento de portal cativo não conta como acesso confirmado");
        var wrongPage = new ConnectivityProbe(request: (_, _) => Task.FromResult(new HttpProbeResponse(200, "Login")));
        var wrongResult = await wrongPage.Run(settings, true, [], CancellationToken.None);
        check(wrongResult.External.State == "unexpected", "Página inesperada não conta como acesso confirmado");
        var expectedPage = new ConnectivityProbe(request: (_, _) => Task.FromResult(new HttpProbeResponse(200, "OK")));
        var expectedResult = await expectedPage.Run(settings, true, [], CancellationToken.None);
        check(expectedResult.External.State == "ok", "Resposta esperada confirma acesso ao destino configurado");
        var changedSettings = new HealthSettings { CheckExternal = true, HttpProbeUrl = "https://example.com/other", HttpExpectedText = "OK" };
        var changedUrlResult = await expectedPage.Run(changedSettings, true, [], CancellationToken.None);
        changedSettings.HttpProbeUrl = settings.HttpProbeUrl;
        changedSettings.HttpExpectedText = "OTHER";
        var changedBodyResult = await expectedPage.Run(changedSettings, true, [], CancellationToken.None);
        check(!string.IsNullOrWhiteSpace(expectedResult.External.ConfigurationId)
            && expectedResult.External.ConfigurationId != changedUrlResult.External.ConfigurationId
            && expectedResult.External.ConfigurationId != changedBodyResult.External.ConfigurationId,
            "Identidade da sondagem muda com URL ou resposta esperada, mesmo no mesmo host");

        var requests = 0;
        var bounded = new ConnectivityProbe((_, _) => { requests++; return Task.FromResult(false); });
        await bounded.Run(new HealthSettings { CheckGateway = true }, true,
            [IPAddress.Parse("192.0.2.1"), IPAddress.Parse("192.0.2.2"), IPAddress.Parse("192.0.2.3"), IPAddress.Parse("192.0.2.4"), IPAddress.Parse("192.0.2.5")], CancellationToken.None);
        check(requests == 4, "Sondagem de gateway limita endereços por amostra");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancellationObserved = false;
        try { await expectedPage.Run(settings, true, [], cancelled.Token); }
        catch (OperationCanceledException) { cancellationObserved = true; }
        check(cancellationObserved, "Cancelamento não é registrado como falha de conectividade");
        var pending = new ConnectivityProbe(request: (_, _) => new TaskCompletionSource<HttpProbeResponse>().Task);
        using var duringRequest = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var cancelledInFlight = false;
        try { await pending.Run(settings, true, [], duringRequest.Token); }
        catch (OperationCanceledException) { cancelledInFlight = true; }
        check(cancelledInFlight, "Cancelamento durante HTTP interrompe a consulta sem registrar falha");
        var deadlineResult = await pending.Run(settings, true, [], CancellationToken.None);
        check(deadlineResult.External.State == "no_response", "Prazo da consulta HTTP vira resultado incerto sem travar a coleta");

        var constructor = typeof(WindowsHealthSampler).GetConstructor([typeof(HealthSettings), typeof(ConnectivityProbe)]);
        check(constructor != null, "Coletor aceita as preferências e uma sondagem controlada");
        if (Environment.GetEnvironmentVariable("NEXO_LIVE_SAMPLE") == "1")
        {
            var samplerSettings = new HealthSettings { CheckDns = true, DnsProbeHost = "example.com" };
            var fakeDns = new ConnectivityProbe(resolve: (_, _) => Task.FromResult(new[] { IPAddress.Parse("192.0.2.10") }));
            var sampler = (WindowsHealthSampler)constructor!.Invoke([samplerSettings, fakeDns]);
            using var sampleDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var sampled = await sampler.Sample(sampleDeadline.Token);
            check(sampled.DnsProbe.State == (sampled.Connectivity == "local" ? "ok" : "unavailable"),
                "Coletor grava a resolução do nome sem misturá-la com o estado local");
        }
    }
}
