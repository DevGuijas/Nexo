namespace Nexo.Support;

/// <summary>
/// Leituras da máquina que a Início, a Saúde do PC, o cartão "Agora" e o relatório de saúde do assistente usam (#142): o leitor de
/// sensores do sistema (live.json), a leitura elevada recente, o PawnIO e a versão da cópia instalada do leitor. Quem monta a tela
/// ou o relatório recebe esta fonte pronta e nunca consulta o Windows direto. O app real usa <see cref="System"/>, que lê o que
/// está instalado, como antes. A prévia (--screenshot, --tray-smoke) e o Nexo.Checks usam <see cref="Off"/> (nada instalado, nada
/// lido) ou uma fonte simulada. Assim, o resultado não depende do PC onde rodam: num PC com o leitor ativo, a temperatura ao vivo
/// tinha prioridade sobre os 52 °C de exemplo da prévia e quebrava a prévia e a suíte.
/// </summary>
public sealed class MachineSensors(ILiveSensorSystem live, Func<DateTimeOffset, ElevatedTemperature?> elevated, Func<bool> pawnIoInstalled, Func<string?> readerVersion, Func<string?> readerBuild)
{
    public ILiveSensorSystem LiveSystem => live;
    public LiveTemperature Live() => SystemSensorReader.Evaluate(live, DateTimeOffset.UtcNow);
    public ElevatedTemperature? Elevated() => elevated(DateTimeOffset.UtcNow);
    public bool PawnIoInstalled() => pawnIoInstalled();
    /// <summary>Versão (x.y.z) do Nexo.exe da cópia protegida do leitor, ou null sem ela.</summary>
    public string? ReaderVersion() => readerVersion();
    /// <summary>Versão informativa (ProductVersion) do Nexo.exe da cópia protegida, ou null sem ela.</summary>
    public string? ReaderBuild() => readerBuild();

    /// <summary>O Windows de verdade: o app real, fora da prévia.</summary>
    public static MachineSensors System { get; } = new(new RealLive(new WindowsLiveSensorSystem()),
        now => { Real("leitura elevada"); return ElevatedSensorReader.Fresh(now); },
        () => { Real("PawnIO"); return PawnIoDriver.Installed(); },
        () => { Real("versão do leitor"); return WindowsLiveSensorSystem.InstalledVersion(); },
        () => { Real("compilação do leitor"); return WindowsLiveSensorSystem.InstalledBuild(); });

    /// <summary>Desligado: sem leitor de sensores do sistema, sem leitura elevada e sem PawnIO. Vale a leitura gravada no histórico.</summary>
    public static MachineSensors Off { get; } = new(new NoLive(), _ => null, () => false, () => null, () => null);

    /// <summary>
    /// Só o Nexo.Checks define: chamado a cada leitura real pela fonte <see cref="System"/>. Nas verificações, uma chamada indica
    /// um caminho que ainda lê a máquina sem injeção.
    /// </summary>
    public static Action<string>? RealReadGuard { get; set; }

    private static void Real(string what) => RealReadGuard?.Invoke(what);

    private sealed class RealLive(ILiveSensorSystem inner) : ILiveSensorSystem
    {
        public bool Installed() { Real("leitor de sensores do sistema"); return inner.Installed(); }
        public string? Untrusted(string file) { Real("leitor de sensores do sistema"); return inner.Untrusted(file); }
        public string? ReadText(string file, int maxBytes) { Real("leitor de sensores do sistema"); return inner.ReadText(file, maxBytes); }
        public DateTimeOffset BootTime() => inner.BootTime();
        public DateTimeOffset? InstalledTime() => inner.InstalledTime();
    }

    private sealed class NoLive : ILiveSensorSystem
    {
        public bool Installed() => false;
        public string? Untrusted(string file) => null;
        public string? ReadText(string file, int maxBytes) => null;
        public DateTimeOffset BootTime() => DateTimeOffset.UnixEpoch;
        public DateTimeOffset? InstalledTime() => null;
    }
}
