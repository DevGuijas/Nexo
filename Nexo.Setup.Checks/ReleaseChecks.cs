using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

internal static class ReleaseChecks
{
    // Passo do docs/INSTALADOR-NUVEM.md: a data e a tabela entram juntas, depois do Build-CloudRelease.
    private const string DatedTable = "A versão atual, quando já tem data de publicação, registra tamanho e SHA-256 do Nexo.exe e do pacote: "
        + "preencha a tabela com os hashes impressos pelo Build-CloudRelease antes de commitar a data";

    public static void Run(Action<bool, string> check)
    {
        var root = RepositoryRoot();
        check(root != null, "Repositório encontrado para conferir a versão");
        if (root == null) return;
        string Read(params string[] path) => File.ReadAllText(Path.Combine([root, .. path]));
        var app = Version(Read("Nexo.Support", "Nexo.Support.csproj"));
        var setup = Version(Read("Nexo.Setup", "Nexo.Setup.csproj"));
        check(Regex.IsMatch(app, @"^\d+\.\d+\.\d+$") && Regex.IsMatch(setup, @"^\d+\.\d+\.\d+$"), "Versões do aplicativo e do instalador seguem MAJOR.MINOR.PATCH");

        Changelog(check, Read("CHANGELOG.md"), app);
        ChangelogFailures(check);

        var window = Read("Nexo.Support", "MainWindow.xaml.cs");
        check(window.Contains("Title = \"Nexo \" + AppVersion.Current") && !Regex.IsMatch(window, @"""Nexo \d+\.\d+\.\d+"), "O título da janela usa a versão do projeto, sem número fixo");
        check(Read("docs", "COMPILACAO-E-IA.md").Contains("$nexoVersion = \"" + app + "\""), "O tutorial de publicação usa a versão do projeto");

        var script = Read("scripts", "Build-CloudRelease.ps1");
        var guard = Read("scripts", "Release-Guard.ps1");
        var publish = script.IndexOf("dotnet publish", StringComparison.Ordinal);
        check(script.Contains("marcada como não publicada no CHANGELOG.md") && script.Contains("TryParseExact") && script.Contains("Release-Guard.ps1")
            && script.Contains("Assert-NexoNewRelease -Version $version") && !script.Contains("PublishedOrigin"),
            "O script de publicação exige data válida no CHANGELOG, usa a trava da versão publicada e não aceita trocar a origem");
        check(guard.Contains("Versões publicadas são imutáveis") && guard.Contains("releases/$Version/nexo-app.zip") && guard.Contains("[version]$Version -le [version]$published.version")
            && guard.Contains("VerifyData") && guard.Contains("'https://objectstorage.sa-saopaulo-1.oraclecloud.com/n/grlqtjwmlv51/b/nexo-releases/o/'")
            && Read("Nexo.Setup", "Program.cs").Contains("\"https://objectstorage.sa-saopaulo-1.oraclecloud.com/n/grlqtjwmlv51/b/nexo-releases/o/latest.json\""),
            "A trava confere a assinatura do latest.json publicado, exige versão maior e recusa versão já publicada, na mesma origem do instalador");
        check(script.Contains("GetEntry('Nexo.exe')") && script.Contains("SHA256SUMS.txt"), "O script de publicação registra o SHA-256 do Nexo.exe publicado");
        check(publish > 0 && script.IndexOf("marcada como não publicada", StringComparison.Ordinal) is var date && date >= 0 && date < publish
            && script.IndexOf("Assert-NexoNewRelease -Version", StringComparison.Ordinal) is var assert && assert >= 0 && assert < publish,
            "As recusas acontecem antes de compilar ou empacotar");
    }

    // Regras do CHANGELOG (#21), aplicadas ao arquivo real e a versões montadas que precisam reprovar sem exceção.
    private static void Changelog(Action<bool, string> check, string changelog, string app)
    {
        var headings = Regex.Matches(changelog, @"^## \[(\d+\.\d+\.\d+)\](?: — (.+))?\r?$", RegexOptions.Multiline).Select(m => (Version: m.Groups[1].Value, Date: m.Groups[2].Value.Trim())).ToArray();
        check(headings.Length > 0 && headings[0].Version == app, "A primeira entrada do CHANGELOG é a versão do aplicativo");
        check(headings.Select(h => h.Version).Distinct().Count() == headings.Length, "Cada versão aparece uma única vez no CHANGELOG");
        var current = headings.FirstOrDefault(h => h.Version == app);
        check(current.Version != null && (current.Date == "não publicada" || IsDate(current.Date)), "A versão atual está marcada como não publicada ou tem data de publicação válida");
        // A última publicada é a primeira entrada anterior com data real (AAAA-MM-DD que existe no calendário).
        var published = headings.Skip(1).Where(h => IsDate(h.Date)).Select(h => h.Version).FirstOrDefault();
        check(published != null && System.Version.Parse(app) > System.Version.Parse(published), "A versão do aplicativo é maior que a última publicada e não a sobrescreve");
        check(published != null && Registered(changelog, published), "A última versão publicada registra tamanho e SHA-256 do Nexo.exe e do pacote");
        // Ao trocar "não publicada" pela data, a mesma entrada precisa trazer a tabela preenchida com o que o script imprimiu.
        check(current.Version == null || !IsDate(current.Date) || Registered(changelog, current.Version),
            DatedTable);
    }

    // Tabela de artefatos da entrada da versão: só o trecho entre o título dela e o próximo título "## [".
    private static bool Registered(string changelog, string version)
    {
        var start = changelog.IndexOf("## [" + version + "]", StringComparison.Ordinal);
        if (start < 0) return false;
        var next = changelog.IndexOf("\n## [", start + 1, StringComparison.Ordinal);
        var section = next < 0 ? changelog[start..] : changelog[start..next];
        return Regex.IsMatch(section, @"\| `Nexo\.exe`[^|]*\| \d+ \| `[0-9A-F]{64}` \|")
            && Regex.IsMatch(section, @"\| `releases/" + Regex.Escape(version) + @"/nexo-app\.zip` \| \d+ \| `[0-9A-F]{64}` \|");
    }

    private static void ChangelogFailures(Action<bool, string> check)
    {
        const string Hashes = "| `Nexo.exe` | 1 | `" + "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF" + "` |\n";
        string Package(string version) => "| `releases/" + version + "/nexo-app.zip` | 1 | `0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF` |\n";
        string[] Failed(string changelog)
        {
            var failed = new List<string>();
            try { Changelog((ok, title) => { if (!ok) failed.Add(title); }, changelog, "1.7.0"); }
            catch (Exception e) { failed.Add("exceção " + e.GetType().Name); }
            return failed.ToArray();
        }
        var valid = Failed("## [1.7.0] — não publicada\n\n## [1.6.3] — 2026-10-22\n\n" + Hashes + Package("1.6.3"));
        var unpublished = Failed("## [1.7.0] — não publicada\n\n## [1.6.3]\n\n" + Hashes + Package("1.6.3"));
        var impossible = Failed("## [1.7.0] — não publicada\n\n## [1.6.3] — 2026-13-45\n\n" + Hashes + Package("1.6.3"));
        var current = Failed("## [1.7.0] — 2026-02-30\n\n## [1.6.3] — 2026-10-22\n\n" + Hashes + Package("1.6.3"));
        var lower = Failed("## [1.7.0] — não publicada\n\n## [1.8.0] — 2026-10-22\n\n" + Hashes + Package("1.8.0"));
        var unregistered = Failed("## [1.7.0] — não publicada\n\n## [1.6.3] — 2026-10-22\n\nSem hashes.\n");
        var datedEmpty = Failed("## [1.7.0] — 2026-11-10\n\n| `Nexo.exe` | | |\n| `releases/1.7.0/nexo-app.zip` | | |\n\n## [1.6.3] — 2026-10-22\n\n" + Hashes + Package("1.6.3"));
        // Só o Nexo.exe da 1.7.0 ficou sem hash: a linha preenchida da 1.6.3, logo abaixo, não pode contar por ela.
        var datedHalf = Failed("## [1.7.0] — 2026-11-10\n\n| `Nexo.exe` | | |\n" + Package("1.7.0") + "\n## [1.6.3] — 2026-10-22\n\n" + Hashes + Package("1.6.3"));
        var datedRegistered = Failed("## [1.7.0] — 2026-11-10\n\n" + Hashes + Package("1.7.0") + "\n## [1.6.3] — 2026-10-22\n\n" + Hashes + Package("1.6.3"));
        const string Newer = "A versão do aplicativo é maior que a última publicada e não a sobrescreve";
        const string Registered = "A última versão publicada registra tamanho e SHA-256 do Nexo.exe e do pacote";
        check(valid.Length == 0, "CHANGELOG montado com a 1.6.3 publicada e registrada passa nas regras de versão");
        check(unpublished.SequenceEqual([Newer, Registered]) && impossible.SequenceEqual([Newer, Registered]),
            "CHANGELOG sem versão publicada, ou com data impossível (2026-13-45), reprova sem exceção");
        check(current.SequenceEqual(["A versão atual está marcada como não publicada ou tem data de publicação válida"]),
            "Data de publicação inexistente no calendário (2026-02-30) na versão atual reprova");
        check(lower.SequenceEqual([Newer]) && unregistered.SequenceEqual([Registered]),
            "Versão atual abaixo da publicada, ou publicada sem hashes, reprova só a regra correspondente");
        check(datedEmpty.SequenceEqual([DatedTable])
            && datedHalf.SequenceEqual([DatedTable]) && datedRegistered.Length == 0,
            "Versão atual com data e tabela vazia reprova; com a tabela preenchida passa, sem usar os hashes da versão anterior");
    }

    private static bool IsDate(string text) => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static string Version(string project) => XDocument.Parse(project).Descendants("Version").Select(v => v.Value.Trim()).FirstOrDefault() ?? "";

    private static string? RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "CHANGELOG.md")) && File.Exists(Path.Combine(directory.FullName, "Nexo.Support", "Nexo.Support.csproj"))) return directory.FullName;
        return null;
    }
}
