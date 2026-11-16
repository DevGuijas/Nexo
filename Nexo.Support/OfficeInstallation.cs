namespace Nexo.Support;

public sealed record OfficeInstallationEvidence(string ProductReleaseIds, string Platform, string ClientCulture,
    string VersionToReport, bool StreamingFinished, bool ApplicationFilesPresent);
public sealed record OfficeInstallationVerification(bool Verified, string Detail);

public static class OfficeDeploymentValidation
{
    // 4 GB Office minimum plus room for download, extraction and servicing.
    public const long RequiredInstallationBytes = 8L * 1024 * 1024 * 1024;
    public const long RequiredTemporaryBytes = 256L * 1024 * 1024;

    public static IReadOnlyList<string> ResourceBlockers(long installationFreeBytes, long temporaryFreeBytes, bool networkAvailable)
    {
        var blockers = new List<string>();
        if (installationFreeBytes < RequiredInstallationBytes) blockers.Add("Espaço insuficiente na unidade do Office: libere pelo menos 8 GB antes de instalar.");
        if (temporaryFreeBytes < RequiredTemporaryBytes) blockers.Add("Espaço insuficiente para preparação: libere pelo menos 256 MB na unidade temporária.");
        if (!networkAvailable) blockers.Add("Nenhuma conexão de rede está ativa. Conecte o computador para baixar o Office da Microsoft.");
        return blockers;
    }

    public static string Conflict(string products, string platform) => "Office Click-to-Run existente: edição "
        + (string.IsNullOrWhiteSpace(products) ? "não identificada" : products) + ", arquitetura "
        + (string.IsNullOrWhiteSpace(platform) ? "não identificada" : platform)
        + ". A instalação exige ProPlus2024Volume x64; a TI deve avaliar compatibilidade e remoção manual.";

    public static bool CanResume(string products, string platform, string culture, string streamingFinished)
    {
        var ids = products.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return ids.Length == 1 && ids[0].Equals("ProPlus2024Volume", StringComparison.OrdinalIgnoreCase)
            && platform.Equals("x64", StringComparison.OrdinalIgnoreCase)
            && culture.Equals("pt-br", StringComparison.OrdinalIgnoreCase)
            && !streamingFinished.Equals("True", StringComparison.OrdinalIgnoreCase) && streamingFinished != "1";
    }

    public static OfficeInstallationVerification Verify(OfficeInstallationEvidence? evidence)
    {
        if (evidence == null) return new(false, "O Windows não registrou uma instalação Office Click-to-Run.");
        var products = evidence.ProductReleaseIds.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (!products.Contains("ProPlus2024Volume", StringComparer.OrdinalIgnoreCase)) return new(false, "A edição ProPlus2024Volume não foi confirmada pelo Click-to-Run.");
        if (!evidence.Platform.Equals("x64", StringComparison.OrdinalIgnoreCase)) return new(false, "A arquitetura Office de 64 bits não foi confirmada.");
        if (!evidence.ClientCulture.Equals("pt-br", StringComparison.OrdinalIgnoreCase)) return new(false, "O idioma português do Brasil não foi confirmado.");
        if (!Version.TryParse(evidence.VersionToReport, out var version) || version.Major != 16 || version.Minor != 0 || version.Build < 17932) return new(false, "A versão instalada do Office LTSC 2024 não foi confirmada.");
        // This verification is called only after the official ODT returned exit code zero.
        // StreamingFinished is not present in every Click-to-Run installation.
        if (!evidence.ApplicationFilesPresent) return new(false, "Os aplicativos Word, Excel e PowerPoint não foram encontrados na instalação registrada.");
        return new(true, "Office LTSC Professional Plus 2024 x64 pt-BR confirmado pelo Windows/Click-to-Run, versão " + evidence.VersionToReport + ". Licença e ativação devem ser verificadas pela TI.");
    }
}
