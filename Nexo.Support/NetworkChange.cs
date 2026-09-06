using System.IO;
using System.Text;
using System.Text.Json;

namespace Nexo.Support;

public sealed record NetworkRequest(string Action, int Index, string Mode, string[] Servers, string Address, int Prefix, string Gateway)
{
    public string ExpectedGuid { get; init; } = "";
    public string Description => Action == "set_dns" ? $"Interface #{Index}: DNS {(Mode == "automatic" ? "automático (DHCP)" : string.Join(", ", Servers))}." : $"Interface #{Index}: {(Mode == "dhcp" ? "Obter um endereço IP automaticamente (DHCP). A rede precisa fornecer IP por DHCP; confirme se essa interface deve usar configuração automática" : $"IP {Address}/{Prefix}, gateway {(Gateway.Length == 0 ? "sem gateway" : Gateway)}")}. O DNS será preservado.";
}
public static class NetworkChange
{
    public static NetworkRequest Parse(string action, JsonElement args)
    {
        var index = WindowsTools.Number(args, "interface_index");
        if (index <= 0) throw new InvalidOperationException("Índice de interface inválido.");
        var mode = WindowsTools.Value(args, "mode");
        if (action == "set_dns")
        {
            if (mode is not ("manual" or "automatic")) throw new InvalidOperationException("Modo DNS inválido.");
            var servers = args.TryGetProperty("servers", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Select(s => WindowsTools.Ipv4(s.GetString() ?? "")).Distinct().ToArray() : mode == "automatic" ? [] : throw new InvalidOperationException("DNS manual exige servers com os endereços informados pelo usuário. Nenhum ajuste foi realizado.");
            if (mode == "manual" && servers.Length is < 1 or > 3 || mode == "automatic" && servers.Length != 0) throw new InvalidOperationException("Informe de 1 a 3 servidores no modo manual ou uma lista vazia no automático.");
            return new NetworkRequest(action, index, mode, servers, "", 0, "");
        }
        if (action != "set_ipv4" || mode is not ("static" or "dhcp")) throw new InvalidOperationException("Modo IPv4 inválido.");
        var address = WindowsTools.Value(args, "address"); var gateway = WindowsTools.Value(args, "gateway"); var prefix = WindowsTools.Number(args, "prefix");
        if (mode == "static") WindowsTools.ValidateStatic(address, prefix, gateway);
        else if (address.Length > 0 || gateway.Length > 0 || prefix != 0) throw new InvalidOperationException("Para DHCP, deixe IP e gateway vazios e prefixo 0.");
        return new NetworkRequest(action, index, mode, [], address, prefix, gateway);
    }

    public static async Task<string> Apply(NetworkRequest request, Store store, Func<ChangeRequest, Task<bool>> confirm, IProgress<string> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var dir = Path.Combine(store.Root, "operations", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var parameters = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request)));
        using var source = typeof(NetworkChange).Assembly.GetManifestResourceStream("Nexo.NetworkTransaction.ps1") ?? throw new IOException("Executor de rede ausente.");
        using var reader = new StreamReader(source);
        var script = "$requestJson=[System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + parameters + "')); $operationDir=" + WindowsTools.Quote(dir) + ";\n" + await reader.ReadToEndAsync(token);
        using var process = WindowsTools.StartElevated(script);
        var ready = Path.Combine(dir, "applied.json"); var final = Path.Combine(dir, "result.json"); var decision = Path.Combine(dir, "decision.txt");
        while (!process.HasExited && !File.Exists(final))
        {
            if (token.IsCancellationRequested) { await File.WriteAllTextAsync(decision, "rollback", CancellationToken.None); }
            if (File.Exists(ready) && !File.Exists(decision))
            {
                progress.Report("Configuração aplicada temporariamente. Conferindo o estado e aguardando sua confirmação…");
                var state = await File.ReadAllTextAsync(ready, CancellationToken.None);
                var keep = await confirm(new ChangeRequest("Manter a configuração de rede?", request.Description + "\n\nEstado obtido após a mudança:\n" + state + "\n\nTeste a conexão. Se não confirmar em até 45 segundos, a configuração anterior será restaurada.", true));
                await File.WriteAllTextAsync(decision, keep && !token.IsCancellationRequested ? "keep" : "rollback", CancellationToken.None);
            }
            await Task.Delay(250, CancellationToken.None);
        }
        await process.WaitForExitAsync();
        return File.Exists(final) ? await File.ReadAllTextAsync(final, CancellationToken.None) : JsonSerializer.Serialize(new { success = false, error = "Executor encerrado sem resultado. Verifique a rede; estado não confirmado.", recoveryFolder = dir });
    }
}
