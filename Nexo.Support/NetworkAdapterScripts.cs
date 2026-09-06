using System.IO;

namespace Nexo.Support;
public sealed record ObservedAdapter(int Index, string Guid, string Name, string Status, bool Disabled);
public static class NetworkAdapterScripts
{
    public static string Read(string resource)
    {
        using var stream = typeof(NetworkAdapterScripts).Assembly.GetManifestResourceStream("Nexo." + resource) ?? throw new IOException("Executor de rede ausente: " + resource);
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    public static string Enable(ObservedAdapter adapter)
    {
        if (adapter.Index <= 0 || !Guid.TryParse(adapter.Guid, out _)) throw new InvalidOperationException("Identidade de rede inválida. Faça uma nova consulta.");
        return "$expectedIndex=" + adapter.Index + "; $expectedGuid=" + WindowsTools.Quote(adapter.Guid) + ";\n" + Read("EnableNetworkAdapter.ps1");
    }
}
