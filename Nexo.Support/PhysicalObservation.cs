using System.Diagnostics;

namespace Nexo.Support;

public sealed record PhysicalState(bool Connected, string Identity);
public sealed record PhysicalResult(bool DisconnectedObserved, bool Reconnected, bool TimedOut, string Note);
public static class PhysicalObservation
{
    public static async Task<PhysicalResult> Wait(Func<CancellationToken, Task<PhysicalState>> read, Action<string> report,
        CancellationToken token, TimeSpan timeout, TimeSpan interval)
    {
        token.ThrowIfCancellationRequested();
        var before = await read(token);
        var disconnected = !before.Connected;
        report(before.Connected ? "Se for externo e acessível, desconecte o cabo/periférico identificado, espere 5 segundos e reconecte. Isso interromperá seu uso por alguns instantes. Estou acompanhando o estado no Windows; você pode cancelar." : "Conecte o cabo/periférico identificado, se ele for externo e acessível. Estou acompanhando o estado no Windows; você pode cancelar.");
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            await Task.Delay(interval, token);
            var current = await read(token);
            if (current.Identity != before.Identity) return new(disconnected, false, false, "A identidade do alvo mudou. Consulte os dispositivos novamente; reconexão não confirmada.");
            if (!current.Connected && !disconnected) { disconnected = true; report("O Windows passou a indicar desconexão do alvo. Aguardando a reconexão…"); }
            if (disconnected && current.Connected) return new(true, true, false, "O Windows voltou a indicar conexão/presença do mesmo alvo. Isso não comprova a causa física nem o funcionamento no aplicativo; faça o teste funcional.");
        }
        return new(disconnected, false, true, "Não observei a sequência de reconexão no prazo. Uma reconexão muito rápida pode não aparecer na amostragem; não concluí que o usuário deixou de fazê-la.");
    }
}
