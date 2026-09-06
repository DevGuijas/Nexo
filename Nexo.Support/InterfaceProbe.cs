using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace Nexo.Support;

public static class InterfaceProbe
{
    public static async Task<object> Run(JsonElement inventory, CancellationToken token)
    {
        using var limit = new SemaphoreSlim(4);
        var reports = await Task.WhenAll(inventory.GetProperty("Adapters").EnumerateArray().Select(async adapter =>
        {
            await limit.WaitAsync(token);
            try { return await Inspect(adapter, token); }
            finally { limit.Release(); }
        }));
        return new { Interfaces = reports, Note = "TCP IPv4 vinculado ao índice e endereço de origem de cada interface. Falha dos destinos não comprova defeito; interfaces locais sem gateway são inventariadas sem teste externo. DNS global é testado separadamente." };
    }

    private static async Task<object> Inspect(JsonElement a, CancellationToken token)
    {
        var index = a.GetProperty("Index").GetInt32();
        var name = a.GetProperty("Name").GetString();
        var guid = a.GetProperty("Guid").GetString();
        var status = a.GetProperty("Status").GetString();
        var addresses = a.GetProperty("Addresses").EnumerateArray().Where(x =>
            x.GetProperty("AddressState").GetString() is "Preferred" or "4")
            .Select(x => x.GetProperty("IPAddress").GetString()!).Where(x => !x.StartsWith("169.254.")).ToArray();
        var hasGateway = a.GetProperty("Gateways").GetArrayLength() > 0;
        var attempts = new List<object>();
        var completed = false; var connected = false;
        string? error = null;
        if (status == "Up" && hasGateway && addresses.Length > 0)
        {
            try
            {
                var nic = NetworkInterface.GetAllNetworkInterfaces().SingleOrDefault(n => Guid.TryParse(n.Id, out var id) && id == Guid.Parse(guid!));
                if (nic?.GetIPProperties().GetIPv4Properties()?.Index != index) throw new InvalidOperationException("Identidade da interface mudou; é necessária nova consulta.");
                foreach (var destination in new[] { "1.1.1.1", "8.8.8.8" })
                {
                    token.ThrowIfCancellationRequested();
                    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    // IP_UNICAST_IF expects an interface index in network byte order on Windows.
                    socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31 /* IP_UNICAST_IF */, IPAddress.HostToNetworkOrder(index));
                    socket.Bind(new IPEndPoint(IPAddress.Parse(addresses[0]), 0));
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(4));
                    try
                    {
                        await socket.ConnectAsync(IPAddress.Parse(destination), 443, deadline.Token);
                        attempts.Add(new { destination, port = 443, connected = true, source = addresses[0] });
                        connected = true;
                    }
                    catch (Exception ex) when (ex is SocketException or OperationCanceledException)
                    { token.ThrowIfCancellationRequested(); attempts.Add(new { destination, port = 443, connected = false, source = addresses[0], error = ex.Message }); }
                }
                completed = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { error = ex.Message; }
        }
        return new { Index = index, Guid = guid, Name = name, Status = status, HasGateway = hasGateway,
            HasUsableAddress = addresses.Length > 0, ProbeCompleted = completed, TcpConnected = connected, Attempts = attempts, Error = error };
    }
}
