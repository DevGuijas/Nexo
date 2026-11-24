namespace Nexo.Support;

public enum RuntimeArch { X86, X64 }
public enum RuntimeKind { VisualCpp, DirectX }

/// <summary>
/// Um componente do cartão "Componentes do Windows para programas" (#150). <see cref="Sha256"/> e <see cref="Size"/> só
/// existem nos instaladores de URL fixa e conteúdo imutável; no VC++ v14 (2015–2026), o link permanente da Microsoft muda de
/// versão, e o arquivo é aceito pela assinatura Authenticode da Microsoft e pelo tamanho até <see cref="MaxSize"/>. <see cref="Root"/> é a
/// impressão digital SHA-1 do certificado raiz em que a cadeia do assinante tem de terminar.
/// </summary>
public sealed record RuntimeItem(
    string Id, string Name, RuntimeKind Kind, RuntimeArch? Arch, int VcYear,
    Uri Url, string FileName, long? Size, string? Sha256, long MaxSize,
    string Arguments, string Signer, bool DefaultWhenMissing, string Root)
{
    public bool Pinned => Sha256 != null && Size != null;
    public string ArchLabel => Arch switch { RuntimeArch.X64 => "x64", RuntimeArch.X86 => "x86", _ => "" };
    public string Title => Arch == null ? Name : Name + " (" + ArchLabel + ")";
}

/// <summary>
/// Catálogo da #150. Fontes, data da conferência e como atualizar: docs/runtimes.md. Só instaladores oficiais da Microsoft, de
/// download.microsoft.com, download.visualstudio.microsoft.com e aka.ms; nenhum espelho, repack ou pacote "all in one".
/// </summary>
public static class RuntimeCatalog
{
    /// <summary>Assinante dos instaladores atuais da Microsoft, como o Windows o mostra em SignerCertificate.Subject.</summary>
    public const string MicrosoftSigner = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";
    /// <summary>Assinante dos VC++ 2012 e 2013 (assinatura de 2013/2014, certificado com OU=MOPR). Só vale com o SHA-256 fixado.</summary>
    public const string MicrosoftSignerMopr = "CN=Microsoft Corporation, OU=MOPR, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";
    /// <summary>
    /// Raízes da cadeia do assinante (SHA-1 do certificado raiz), conferidas em 23/11/2026: os intermediários de cada arquivo
    /// ("Microsoft Code Signing PCA 2011/2024" e "Microsoft Code Signing PCA") são assinados por estas raízes, baixadas das URLs AIA
    /// da própria Microsoft (docs/runtimes.md). A raiz de 2001 venceu em 2021, mas segue no repositório de raízes do Windows para
    /// assinaturas com carimbo de tempo.
    /// </summary>
    public const string MicrosoftRoot2011 = "8F43288AD272F3103B6FB1428485EA3014C0BCFE";
    public const string MicrosoftRoot2001 = "CDD4EEAE6000AC7F40C3802C171E30148030C072";
    public const int MaxRedirects = 5;
    /// <summary>Teto do VC++ v14, que não tem tamanho fixado: o x64 de 23/11/2026 tem 18,9 MB e o da linha do VS 2022, 25,6 MB.</summary>
    public const long LatestMaxSize = 64L * 1024 * 1024;
    public static readonly string[] Hosts = ["aka.ms", "download.microsoft.com", "download.visualstudio.microsoft.com"];

    /// <summary>Saídas documentadas do Windows Installer e dos pacotes da Microsoft.</summary>
    public const int ExitOk = 0, ExitReboot = 3010, ExitRebootStarted = 1641, ExitNewerInstalled = 1638;

    // Argumentos silenciosos. v14, 2012 e 2013 (pacotes Burn): /install /passive /norestart, os da página "Redistributing Visual
    // C++ files" da Microsoft. 2010: /passive /norestart, que o próprio instalador lista no /?. 2005 e 2008 (pacotes IExpress) e o
    // dxwebsetup: /q, o modo silencioso que o próprio pacote lista ("/Q -- Quiet modes for package").
    private const string Burn = "/install /passive /norestart";

    public static readonly IReadOnlyList<RuntimeItem> Items =
    [
        new("vc14-x64", "Visual C++ 2015–2022 e posteriores (v14)", RuntimeKind.VisualCpp, RuntimeArch.X64, 2015,
            new("https://aka.ms/vc14/vc_redist.x64.exe"), "vc_redist.x64.exe", null, null, LatestMaxSize, Burn, MicrosoftSigner, true, MicrosoftRoot2011),
        new("vc14-x86", "Visual C++ 2015–2022 e posteriores (v14)", RuntimeKind.VisualCpp, RuntimeArch.X86, 2015,
            new("https://aka.ms/vc14/vc_redist.x86.exe"), "vc_redist.x86.exe", null, null, LatestMaxSize, Burn, MicrosoftSigner, true, MicrosoftRoot2011),
        new("vc2013-x64", "Visual C++ 2013", RuntimeKind.VisualCpp, RuntimeArch.X64, 2013,
            new("https://download.visualstudio.microsoft.com/download/pr/10912041/cee5d6bca2ddbcd039da727bf4acb48a/vcredist_x64.exe"), "vcredist_x64.exe",
            7200744, "a4bba7701e355ae29c403431f871a537897c363e215cafe706615e270984f17c", 7200744, Burn, MicrosoftSignerMopr, false, MicrosoftRoot2001),
        new("vc2013-x86", "Visual C++ 2013", RuntimeKind.VisualCpp, RuntimeArch.X86, 2013,
            new("https://download.visualstudio.microsoft.com/download/pr/10912113/5da66ddebb0ad32ebd4b922fd82e8e25/vcredist_x86.exe"), "vcredist_x86.exe",
            6510136, "53b605d1100ab0a88b867447bbf9274b5938125024ba01f5105a9e178a3dcdbd", 6510136, Burn, MicrosoftSignerMopr, false, MicrosoftRoot2001),
        new("vc2012-x64", "Visual C++ 2012", RuntimeKind.VisualCpp, RuntimeArch.X64, 2012,
            new("https://download.microsoft.com/download/1/6/B/16B06F60-3B20-4FF2-B699-5E9B7962F9AE/VSU_4/vcredist_x64.exe"), "vcredist_x64.exe",
            7186992, "681be3e5ba9fd3da02c09d7e565adfa078640ed66a0d58583efad2c1e3cc4064", 7186992, Burn, MicrosoftSignerMopr, false, MicrosoftRoot2001),
        new("vc2012-x86", "Visual C++ 2012", RuntimeKind.VisualCpp, RuntimeArch.X86, 2012,
            new("https://download.microsoft.com/download/1/6/B/16B06F60-3B20-4FF2-B699-5E9B7962F9AE/VSU_4/vcredist_x86.exe"), "vcredist_x86.exe",
            6554576, "b924ad8062eaf4e70437c8be50fa612162795ff0839479546ce907ffa8d6e386", 6554576, Burn, MicrosoftSignerMopr, false, MicrosoftRoot2001),
        new("vc2010-x64", "Visual C++ 2010", RuntimeKind.VisualCpp, RuntimeArch.X64, 2010,
            new("https://download.microsoft.com/download/1/6/5/165255E7-1014-4D0A-B094-B6A430A6BFFC/vcredist_x64.exe"), "vcredist_x64.exe",
            10277328, "f3b7a76d84d23f91957aa18456a14b4e90609e4ce8194c5653384ed38dada6f3", 10277328, "/passive /norestart", MicrosoftSigner, false, MicrosoftRoot2011),
        new("vc2010-x86", "Visual C++ 2010", RuntimeKind.VisualCpp, RuntimeArch.X86, 2010,
            new("https://download.microsoft.com/download/1/6/5/165255E7-1014-4D0A-B094-B6A430A6BFFC/vcredist_x86.exe"), "vcredist_x86.exe",
            8993744, "99dce3c841cc6028560830f7866c9ce2928c98cf3256892ef8e6cf755147b0d8", 8993744, "/passive /norestart", MicrosoftSigner, false, MicrosoftRoot2011),
        new("vc2008-x64", "Visual C++ 2008", RuntimeKind.VisualCpp, RuntimeArch.X64, 2008,
            new("https://download.microsoft.com/download/5/D/8/5D8C65CB-C849-4025-8E95-C3966CAFD8AE/vcredist_x64.exe"), "vcredist_x64.exe",
            5211080, "c5e273a4a16ab4d5471e91c7477719a2f45ddadb76c7f98a38fa5074a6838654", 5211080, "/q", MicrosoftSigner, false, MicrosoftRoot2011),
        new("vc2008-x86", "Visual C++ 2008", RuntimeKind.VisualCpp, RuntimeArch.X86, 2008,
            new("https://download.microsoft.com/download/5/D/8/5D8C65CB-C849-4025-8E95-C3966CAFD8AE/vcredist_x86.exe"), "vcredist_x86.exe",
            4483040, "8742bcbf24ef328a72d2a27b693cc7071e38d3bb4b9b44dec42aa3d2c8d61d92", 4483040, "/q", MicrosoftSigner, false, MicrosoftRoot2011),
        new("vc2005-x64", "Visual C++ 2005", RuntimeKind.VisualCpp, RuntimeArch.X64, 2005,
            new("https://download.microsoft.com/download/8/b/4/8b42259f-5d70-43f4-ac2e-4b208fd8d66a/vcredist_x64.EXE"), "vcredist_x64.exe",
            3179000, "4487570bd86e2e1aac29db2a1d0a91eb63361fcaac570808eb327cd4e0e2240d", 3179000, "/q", MicrosoftSigner, false, MicrosoftRoot2011),
        new("vc2005-x86", "Visual C++ 2005", RuntimeKind.VisualCpp, RuntimeArch.X86, 2005,
            new("https://download.microsoft.com/download/8/b/4/8b42259f-5d70-43f4-ac2e-4b208fd8d66a/vcredist_x86.EXE"), "vcredist_x86.exe",
            2710520, "8648c5fc29c44b9112fe52f9a33f80e7fc42d10f3b5b42b2121542a13e44adfd", 2710520, "/q", MicrosoftSigner, false, MicrosoftRoot2011),
        // Instalador web do DirectX End-User Runtime (9.29.1974.1): pequeno, baixa o resto da própria Microsoft. Sem o DirectX SDK.
        new("directx", "DirectX End-User Runtime (junho de 2010)", RuntimeKind.DirectX, null, 0,
            new("https://download.microsoft.com/download/1/7/1/1718ccc4-6315-4d8e-9543-8e28a4e18c4c/dxwebsetup.exe"), "dxwebsetup.exe",
            295320, "2cf71d098c608c56e07f4655855a886c3102553f648df88458df616b26fd612f", 295320, "/Q", MicrosoftSigner, false, MicrosoftRoot2011),
    ];

    /// <summary>DLLs do DirectX de junho de 2010 conferidas em System32 e, no Windows de 64 bits, em SysWOW64.</summary>
    public static readonly string[] DirectXFiles = ["d3dx9_43.dll", "d3dx10_43.dll", "d3dx11_43.dll", "xinput1_3.dll", "XAudio2_7.dll", "xactengine3_7.dll"];

    public static RuntimeItem? Find(string id) => Items.FirstOrDefault(item => item.Id == id);

    public static bool AllowedHost(Uri url) => url.IsAbsoluteUri && url.Scheme == Uri.UriSchemeHttps && url.Port == 443 && url.UserInfo.Length == 0
        && Hosts.Contains(url.IdnHost, StringComparer.OrdinalIgnoreCase);

    /// <summary>O instalador antigo (2005 a 2013) e o DirectX nunca vêm marcados; o v14 que falta vem.</summary>
    public static bool DefaultSelected(RuntimeStatus status) => status.State == RuntimeState.Missing && status.Item.DefaultWhenMissing;
}
