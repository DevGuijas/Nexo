using System.Net;
using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Nexo.Support;

public sealed record HttpProbeResponse(int StatusCode, string Body);

public sealed class ConnectivityProbe(
    Func<IPAddress, CancellationToken, Task<bool>>? ping = null,
    Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null,
    Func<Uri, CancellationToken, Task<HttpProbeResponse>>? request = null)
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly Func<IPAddress, CancellationToken, Task<bool>> pingGateway = ping ?? PingGateway;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> resolveHost = resolve ?? Dns.GetHostAddressesAsync;
    private readonly Func<Uri, CancellationToken, Task<HttpProbeResponse>> requestPage = request ?? RequestPage;

    public async Task<(NetworkProbeReading Gateway, NetworkProbeReading Dns, NetworkProbeReading External)> Run(
        HealthSettings settings, bool localActive, IReadOnlyList<IPAddress> gateways, CancellationToken token)
    {
        settings.Validate();
        token.ThrowIfCancellationRequested();
        var gateway = settings.CheckGateway ? await CheckGateway(gateways, localActive, token) : new NetworkProbeReading();
        var dns = settings.CheckDns ? await CheckDns(settings.DnsProbeHost, localActive, token) : new NetworkProbeReading();
        var external = settings.CheckExternal ? await CheckExternal(settings, localActive, token) : new NetworkProbeReading();
        return (gateway, dns, external);
    }

    private async Task<NetworkProbeReading> CheckGateway(IReadOnlyList<IPAddress> gateways, bool localActive, CancellationToken token)
    {
        if (!localActive || gateways.Count == 0) return new("unavailable", "", "Nenhum gateway da interface ativa está disponível para consulta.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        var addresses = gateways.Distinct().Take(4).ToArray();
        try
        {
            foreach (var address in addresses)
                if (await pingGateway(address, deadline.Token).WaitAsync(deadline.Token))
                    return new("ok", address.ToString(), "Gateway respondeu ao ICMP.");
            return new("no_response", string.Join(", ", addresses.Select(a => a.ToString())), "Sem resposta ICMP; a rede pode bloquear ping.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return new("no_response", string.Join(", ", addresses.Select(a => a.ToString())), "Prazo do ping esgotado; isso não prova falha do gateway."); }
        catch (Exception ex) when (ex is PingException or SocketException or InvalidOperationException)
        { return new("unavailable", string.Join(", ", addresses.Select(a => a.ToString())), "Não foi possível executar o ping neste ambiente."); }
    }

    private async Task<NetworkProbeReading> CheckDns(string host, bool localActive, CancellationToken token)
    {
        if (!localActive) return new("unavailable", host, "Sem interface local ativa para consultar este nome.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            var addresses = await resolveHost(host, deadline.Token).WaitAsync(deadline.Token);
            return addresses.Length > 0
                ? new("ok", host, "Nome resolvido neste computador.")
                : new("no_response", host, "O nome configurado não retornou endereços.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return new("no_response", host, "A resolução do nome excedeu quatro segundos."); }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        { return new("no_response", host, "O nome configurado não foi resolvido nesta leitura."); }
    }

    private async Task<NetworkProbeReading> CheckExternal(HealthSettings settings, bool localActive, CancellationToken token)
    {
        var target = new Uri(settings.HttpProbeUrl);
        var configurationId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settings.HttpProbeUrl + "\0" + settings.HttpExpectedText)));
        if (!localActive) return new("unavailable", target.Host, "Sem interface local ativa para consultar o destino.", configurationId);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            var response = await requestPage(target, deadline.Token).WaitAsync(deadline.Token);
            return response.StatusCode == 200 && string.Equals(response.Body.Trim(), settings.HttpExpectedText, StringComparison.Ordinal)
                ? new("ok", target.Host, "Destino respondeu com o texto esperado.", configurationId)
                : new("unexpected", target.Host, "Destino respondeu de outra forma; portal, proxy ou mudança no próprio destino são possíveis.", configurationId);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return new("no_response", target.Host, "A consulta ao destino excedeu quatro segundos.", configurationId); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
        { return new("no_response", target.Host, "O destino configurado não respondeu nesta leitura.", configurationId); }
    }

    private static async Task<bool> PingGateway(IPAddress address, CancellationToken token)
    {
        using var ping = new Ping();
        var reply = await ping.SendPingAsync(address, TimeSpan.FromSeconds(2), new byte[8], new PingOptions(), token);
        return reply.Status == IPStatus.Success;
    }

    private static async Task<HttpProbeResponse> RequestPage(Uri target, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        await using var body = await response.Content.ReadAsStreamAsync(token);
        var buffer = new byte[256];
        var size = 0;
        while (size < buffer.Length)
        {
            var read = await body.ReadAsync(buffer.AsMemory(size), token);
            if (read == 0) break;
            size += read;
        }
        return new((int)response.StatusCode, Encoding.UTF8.GetString(buffer, 0, size));
    }
}
