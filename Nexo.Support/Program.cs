using System.Runtime.CompilerServices;

namespace Nexo.Support;

/// <summary>
/// Ponto de entrada do Nexo.exe (#142, P9). O Main que o WPF gera cria o App e roda InitializeComponent (App.xaml: tema, fontes,
/// geometrias) antes do OnStartup, o que já carrega o WPF. O leitor de sensores do sistema (--sensor-loop, como SYSTEM) desvia
/// aqui, antes de qualquer tipo do WPF, para que a primeira coisa do processo seja restringir a busca de DLLs
/// (SensorLoopEntry → SetDefaultDllDirectories). O app normal segue como antes, pelo App.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], SystemSensorReader.LoopArgument, StringComparison.Ordinal)) return SensorLoopEntry.Run(args);
        return RunApp();
    }

    // Fora do Main, para o JIT do Main não precisar carregar os tipos do WPF.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunApp()
    {
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
