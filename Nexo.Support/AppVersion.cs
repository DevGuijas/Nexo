namespace Nexo.Support;

public static class AppVersion
{
    public static string Current { get; } = Format(typeof(AppVersion).Assembly.GetName().Version);

    public static string Format(Version? version) => version is { Major: >= 0, Minor: >= 0, Build: >= 0 } ? $"{version.Major}.{version.Minor}.{version.Build}" : "desconhecida";
}
