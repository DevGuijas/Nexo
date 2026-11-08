using System.Text.RegularExpressions;
using System.Xml.Linq;

internal static class ReleaseChecks
{
    public static void Run(Action<bool, string> check)
    {
        var root = RepositoryRoot();
        check(root != null, "Repositório encontrado para conferir a versão");
        if (root == null) return;
        string Read(params string[] path) => File.ReadAllText(Path.Combine([root, .. path]));
        var app = Version(Read("Nexo.Support", "Nexo.Support.csproj"));
        var setup = Version(Read("Nexo.Setup", "Nexo.Setup.csproj"));
        check(Regex.IsMatch(app, @"^\d+\.\d+\.\d+$") && Regex.IsMatch(setup, @"^\d+\.\d+\.\d+$"), "Versões do aplicativo e do instalador seguem MAJOR.MINOR.PATCH");

        var changelog = Read("CHANGELOG.md");
        var headings = Regex.Matches(changelog, @"^## \[(\d+\.\d+\.\d+)\](?: — (.+))?\r?$", RegexOptions.Multiline).Select(m => (Version: m.Groups[1].Value, Date: m.Groups[2].Value.Trim())).ToArray();
        check(headings.Length > 0 && headings[0].Version == app, "A primeira entrada do CHANGELOG é a versão do aplicativo");
        check(headings.Select(h => h.Version).Distinct().Count() == headings.Length, "Cada versão aparece uma única vez no CHANGELOG");
        var current = headings.FirstOrDefault(h => h.Version == app);
        check(current.Date == "não publicada" || Regex.IsMatch(current.Date, @"^\d{4}-\d{2}-\d{2}$"), "A versão atual está marcada como não publicada ou tem data de publicação");
        var published = headings.Skip(1).FirstOrDefault(h => h.Date.Length > 0);
        check(published.Version != null && System.Version.Parse(app) > System.Version.Parse(published.Version), "A versão do aplicativo é maior que a última publicada e não a sobrescreve");
        var section = changelog[changelog.IndexOf("## [" + published.Version + "]", StringComparison.Ordinal)..];
        check(Regex.IsMatch(section, @"\| `Nexo\.exe`[^|]*\| \d+ \| `[0-9A-F]{64}` \|") && Regex.IsMatch(section, @"\| `releases/" + Regex.Escape(published.Version) + @"/nexo-app\.zip` \| \d+ \| `[0-9A-F]{64}` \|"),
            "A última versão publicada registra tamanho e SHA-256 do Nexo.exe e do pacote");

        var window = Read("Nexo.Support", "MainWindow.xaml.cs");
        check(window.Contains("Title = \"Nexo \" + AppVersion.Current") && !Regex.IsMatch(window, @"""Nexo \d+\.\d+\.\d+"), "O título da janela usa a versão do projeto, sem número fixo");
        check(Read("docs", "COMPILACAO-E-IA.md").Contains("$nexoVersion = \"" + app + "\""), "O tutorial de publicação usa a versão do projeto");

        var script = Read("scripts", "Build-CloudRelease.ps1");
        check(script.Contains("releases/$version/nexo-app.zip") && script.Contains("Versões publicadas são imutáveis") && script.Contains("marcada como não publicada no CHANGELOG.md"),
            "O script de publicação recusa versão já publicada e versão sem data no CHANGELOG");
        check(script.Contains("GetEntry('Nexo.exe')") && script.Contains("SHA256SUMS.txt"), "O script de publicação registra o SHA-256 do Nexo.exe publicado");
        check(script.IndexOf("marcada como não publicada", StringComparison.Ordinal) < script.IndexOf("dotnet publish", StringComparison.Ordinal)
            && script.IndexOf("Versões publicadas são imutáveis", StringComparison.Ordinal) < script.IndexOf("dotnet publish", StringComparison.Ordinal),
            "As recusas acontecem antes de compilar ou empacotar");
    }

    private static string Version(string project) => XDocument.Parse(project).Descendants("Version").Select(v => v.Value.Trim()).FirstOrDefault() ?? "";

    private static string? RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "CHANGELOG.md")) && File.Exists(Path.Combine(directory.FullName, "Nexo.Support", "Nexo.Support.csproj"))) return directory.FullName;
        return null;
    }
}
