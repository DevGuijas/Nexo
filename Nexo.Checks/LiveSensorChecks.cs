using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Nexo.Support;
namespace Nexo.Checks;

/// <summary>
/// #142, opção 2: leitor de sensores do sistema (tarefa "Nexo Sensores", SYSTEM, cópia em %ProgramFiles%\Nexo\Sensors). Tudo
/// simulado: o live.json, a ACL, o relógio, o UAC e os cmdlets do agendador; o script real roda num PowerShell com cmdlets falsos.
/// </summary>
internal static class LiveSensorChecks
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    internal static async Task Run(Action<bool, string> check)
    {
        Loop(check);
        Live(check);
        Screens(check);
        Report(check);
        Refusal(check);
        await Scripts(check);
        await Apply(check);
    }

    private static void Loop(Action<bool, string> check)
    {
        var folder = Path.Combine(Path.GetTempPath(), "NexoPF-" + Guid.NewGuid().ToString("N"), "Nexo", "Sensors");
        var exe = Path.Combine(folder, "Nexo.exe");
        var extract = Path.Combine(folder, "extract");
        check(SystemSensorReader.ValidateLoop(["--sensor-loop"], exe, folder, false, null) == null && SystemSensorReader.ValidateLoop(["--sensor-loop"], exe, folder, true, extract) == null,
            "#142 leitor do sistema: --sensor-loop aceita só o Nexo.exe da pasta protegida");
        var user = Path.Combine(Path.GetTempPath(), "Users", "u", "AppData", "Local", "Programs", "Nexo", "Nexo.exe");
        (string[] Args, string? Path, bool Single, string? Extract)[] refused =
        [
            (["--sensor-loop", "x"], exe, false, null),
            (["--sensor-loop", "--theme", "glacial"], exe, false, null),
            (["--sensor-loop", "--sensor-loop"], exe, false, null),
            ([], exe, false, null),
            (["--SENSOR-LOOP"], exe, false, null),
            (["--read-sensors"], exe, false, null),
            (["--sensor-loop"], user, false, null),
            (["--sensor-loop"], Path.Combine(folder + "-x", "Nexo.exe"), false, null),
            (["--sensor-loop"], Path.Combine(folder, "sub", "Nexo.exe"), false, null),
            (["--sensor-loop"], Path.Combine(folder, "..", "Sensors", "Nexo.exe"), false, null),
            (["--sensor-loop"], Path.Combine(folder, "Outro.exe"), false, null),
            (["--sensor-loop"], "Nexo.exe", false, null),
            (["--sensor-loop"], null, false, null),
            (["--sensor-loop"], exe, true, null),
            (["--sensor-loop"], exe, true, Path.Combine(Path.GetTempPath(), ".net")),
        ];
        var accepted = refused.Where(item => SystemSensorReader.ValidateLoop(item.Args, item.Path, folder, item.Single, item.Extract) == null).Select(item => string.Join(" ", item.Args) + " @ " + item.Path).ToList();
        check(accepted.Count == 0, "#142 leitor do sistema: recusa argumento extra, outro modo, executável fora de %ProgramFiles%\\Nexo\\Sensors (pasta do usuário, vizinha, subpasta, \"..\") e extração nativa fora da pasta protegida"
            + (accepted.Count > 0 ? " · aceitos: " + string.Join(" | ", accepted) : ""));
        check(SystemSensorReader.RunLoop(["--sensor-loop"]) == 2 && SystemSensorReader.RunLoop(["--sensor-loop", "x"], exe, folder, false, null) == 2
            && SystemSensorReader.RunLoop(["--sensor-loop"], user, folder, false, null) == 2,
            "#142 leitor do sistema: fora da cópia protegida ou com argumento extra, o modo sai com código 2 antes de ler qualquer sensor");
        check(SystemSensorReader.RunLoop(["--sensor-loop"], exe, folder, false, null) == 3, "#142 leitor do sistema: no caminho certo, mas sem ser SYSTEM, sai com código 3");
    }

    private sealed class FakeLive : ILiveSensorSystem
    {
        public bool IsInstalled { get; set; } = true;
        public string? Text { get; set; }
        public string? Problem { get; set; }
        public Exception? Fails { get; set; }
        public DateTimeOffset Boot { get; set; } = DateTimeOffset.UnixEpoch;
        public int Reads { get; private set; }
        public bool Installed() => IsInstalled;
        public string? Untrusted(string file) => Problem;
        public string? ReadText(string file, int maxBytes) { Reads++; if (Fails != null) throw Fails; return Text; }
        public DateTimeOffset BootTime() => Boot;
        public DateTimeOffset? Installed_ { get; set; }
        public DateTimeOffset? InstalledTime() => Installed_;
    }

    internal static string Doc(DateTimeOffset time, string state = "read", double? celsius = 78.6, string version = "1.7.0", int pid = 4242) =>
        SystemSensorReader.LiveDocument(new TemperatureReading(state, celsius, "Core (Tctl/Tdie) (/amdcpu/0/temperature/2)", HardwareSensors.TemperatureSource, time,
            state == "read" ? "" : "O sensor não abriu."), time, version, pid);

    internal static readonly DateTimeOffset Now = new(2026, 11, 23, 17, 30, 0, TimeSpan.Zero);

    private static LiveTemperature Eval(string? text, string? problem = null, bool installed = true, DateTimeOffset? boot = null) =>
        SystemSensorReader.Evaluate(new FakeLive { Text = text, Problem = problem, IsInstalled = installed, Boot = boot ?? Now.AddHours(-1) }, Now);

    private static void Live(Action<bool, string> check)
    {
        var fresh = Eval(Doc(Now.AddSeconds(-3)));
        var at = Now.AddSeconds(-3).ToLocalTime().ToString("HH:mm:ss", PtBr);
        check(fresh is { State: "live", Celsius: 78.6, Version: "1.7.0" } && fresh.Valid && fresh.Message == "78,6 °C · Core (Tctl/Tdie) · ao vivo, leitor de sensores do sistema, às " + at,
            "#142 live.json fresco vira valor ao vivo com a origem e o horário: " + fresh.Message);
        var edge = Eval(Doc(Now.AddSeconds(-10)));
        var old = Eval(Doc(Now.AddSeconds(-11)));
        check(edge.Valid && old is { State: "stale", Celsius: null } && old.Message == "leitor de sensores sem resposta desde " + Now.AddSeconds(-11).ToLocalTime().ToString("HH:mm", PtBr),
            "#142 live.json com mais de 10 s vira \"sem resposta desde HH:mm\", sem valor: " + old.Message);
        var afterBoot = Eval(Doc(Now.AddHours(-9)), boot: Now.AddSeconds(-40));
        var longAfterBoot = Eval(Doc(Now.AddHours(-9)), boot: Now.AddMinutes(-3));
        LiveTemperature Missing(DateTimeOffset boot, DateTimeOffset? installed) => SystemSensorReader.Evaluate(new FakeLive { Text = null, Boot = boot, Installed_ = installed }, Now);
        var missingAfterBoot = Missing(Now.AddSeconds(-40), Now.AddDays(-3));
        var missingAfterInstall = Missing(Now.AddHours(-5), Now.AddSeconds(-30));
        var neverWrote = Missing(Now.AddHours(-5), Now.AddMinutes(-3));
        var neverSinceBoot = Missing(Now.AddMinutes(-3), Now.AddDays(-3));
        check(afterBoot is { State: "waiting", Celsius: null } && afterBoot.Message == SystemSensorReader.WaitingText && longAfterBoot.State == "stale"
            && missingAfterBoot is { State: "waiting", Celsius: null } && missingAfterInstall is { State: "waiting", Celsius: null }
            && neverWrote is { State: "stale", Celsius: null } && neverWrote.Message == "leitor de sensores sem resposta desde " + Now.AddMinutes(-3).ToLocalTime().ToString("HH:mm", PtBr) + ", sem nenhuma leitura gravada"
            && neverSinceBoot.State == "stale" && Missing(Now.AddHours(-5), null).State == "stale",
            "#142 logo depois de ligar o PC ou de ativar o leitor (até 2 min), com arquivo de antes da inicialização ou sem arquivo: \"aguardando o leitor…\"; depois disso, \"sem resposta\", também quando o leitor nunca gravou o live.json: "
            + neverWrote.Message);
        var minimum = SystemSensorReader.Evaluate(new FakeLive { Text = Doc(DateTimeOffset.MinValue), Boot = Now.AddHours(-1) }, Now);
        check(minimum.Celsius == null && minimum.State is "ignored" or "stale", "#142 live.json com horário 0001-01-01 não derruba a tela na conversão para o fuso local: " + minimum.State);
        var off = Eval(Doc(Now), installed: false);
        check(off is { State: "off", Celsius: null }, "#142 sem o leitor instalado, o live.json não vale (estado \"off\")");
        var owner = Eval(Doc(Now), problem: "C:\\ProgramData\\NexoSupport\\sensors\\live.json: dono não confiável (S-1-5-21-1-2-3-1001)");
        var acl = Eval(Doc(Now), problem: "C:\\ProgramData\\NexoSupport\\sensors: ACE de escrita para BUILTIN\\Users S-1-5-32-545 (Write)");
        check(owner is { State: "ignored", Celsius: null } && owner.Message.Contains("dono não confiável") && acl is { State: "ignored", Celsius: null } && acl.Message.Contains("ACE de escrita"),
            "#142 live.json com dono errado ou ACL frouxa é ignorado, mesmo com valor válido");
        string[] broken = ["", "{", "[]", "null", Doc(Now).Replace("\"Format\":1", "\"Format\":2"), Doc(Now).Replace("\"Kind\":\"live\"", "\"Kind\":\"reading\""),
            Doc(Now).Replace("78.6", "0"), Doc(Now).Replace("78.6", "140"), Doc(Now).Replace("78.6", "\"78.6\""), Doc(Now).Replace("Core (Tctl/Tdie) (/amdcpu/0/temperature/2)", ""),
            Doc(Now.AddMinutes(2)), Doc(Now, version: "1.7.0; rm"), Doc(Now, pid: 0), Doc(Now).Replace("\"Pid\":4242", "\"Pid\":\"4242\""),
            Doc(Now)[..^1] + ",\"Pad\":\"" + new string('x', 5000) + "\"}"];
        var parsed = broken.Select(text => (text, result: Eval(text))).Where(item => item.result.State != "ignored" || item.result.Celsius != null).Select(item => item.text.Length > 80 ? item.text[..80] : item.text).ToList();
        var unreadable = SystemSensorReader.Evaluate(new FakeLive { Fails = new IOException("arquivo maior que o limite de 4096 bytes") }, Now);
        check(parsed.Count == 0 && unreadable is { State: "ignored", Celsius: null }, "#142 live.json inválido, grande (mais de 4 KB), do futuro, com zero, fora da faixa ou sem sensor é ignorado"
            + (parsed.Count > 0 ? " · aceitos: " + string.Join(" | ", parsed) : ""));
        var error = Eval(Doc(Now, "error", null));
        check(error is { State: "error", Celsius: null } && error.Message.Contains("não obteve temperatura válida") && !Doc(Now, "error", 55).Contains("55"),
            "#142 erro de leitura do leitor: estado de erro, sem valor (o JSON nem grava o número)");
        var longDetail = SystemSensorReader.LiveDocument(new TemperatureReading("error", Detail: new string('d', 5000)), Now, "1.7.0", 1);
        check(Encoding.UTF8.GetByteCount(longDetail) < SystemSensorReader.MaxBytes && Eval(longDetail).State == "error",
            "#142 o live.json tem tamanho limitado (detalhe cortado), abaixo de " + SystemSensorReader.MaxBytes + " bytes: o arquivo não cresce");
    }

    // A amostra sai do mesmo caminho da coleta (HardwareSensors.ReadTemperature), que também grava o aviso "Temperatura da CPU: O
    // sensor retornou zero…" em Warnings, e esse aviso vai às limitações do relatório (@qa: montada à mão, a amostra não tinha o aviso).
    private static HealthSnapshot Zero(DateTimeOffset time)
    {
        var snapshot = new HealthSnapshot
        {
            Time = time, CpuPercent = 12, MemoryUsedPercent = 40, CollectionStatus = "complete",
            StorageHealth = "healthy", Storage = new StorageReading("healthy", HardwareSensors.StorageSource, time, 1, [new("1", "AMD-RAID Array 1", "SSD", "RAID", "Healthy", ["OK"], "healthy")])
        };
        using var document = System.Text.Json.JsonDocument.Parse(EmbeddedCpuTemperature.SensorsJson([new CpuSensorValue("Core (Tctl/Tdie)", "/amdcpu/0/temperature/2", 0)]));
        HardwareSensors.ReadTemperature(document.RootElement, snapshot, time);
        return snapshot;
    }

    private static void Screens(Action<bool, string> check)
    {
        var zero = Zero(Now);
        var off = new LiveTemperature("off", "");
        var neutral = SystemSensorReader.TemperatureText(zero, off, true);
        var discarded = SystemSensorReader.TemperatureText(zero, off, false);
        var range = Zero(Now);
        range.CpuTemperature = zero.CpuTemperature! with { Detail = "O sensor Core (Tctl/Tdie) informou valor ausente ou fora de 0–125 °C; a leitura foi descartada." };
        check(neutral is { Tone: "Neutral" } && neutral.Value.Text.Contains("precisa de permissão de administrador") && neutral.Value.Text.Contains("Ative a temperatura ao vivo")
            && !neutral.Value.Text.Contains("descartada") && !neutral.Value.Text.Contains("zero")
            && discarded is { Tone: "Warning" } && discarded.Value.Text.Contains("descartada")
            && SystemSensorReader.TemperatureText(range, off, true)!.Value.Text.Contains("descartada")
            && SystemSensorReader.AdminNeededTitle == "A temperatura precisa de permissão de administrador" && SystemSensorReader.AdminNeededDetail.Contains("Ative a temperatura ao vivo"),
            "#142 PawnIO instalado e acesso negado (sensor em zero): texto neutro \"precisa de permissão de administrador. Ative a temperatura ao vivo\"; sem o PawnIO ou com outro valor, continua \"descartada\"");

        // A Início é a tela de abertura: com o live.json fresco, o texto já está na 1ª montagem, mesmo sem a 1ª leitura do monitor.
        var fresh = SystemSensorReader.Evaluate(new FakeLive { Text = Doc(Now.AddSeconds(-1)) }, Now);
        var first = SystemSensorReader.TemperatureText(null, fresh, true);
        var waiting = SystemSensorReader.TemperatureText(null, SystemSensorReader.Evaluate(new FakeLive { Text = null, Boot = Now.AddSeconds(-30) }, Now), true);
        var stale = SystemSensorReader.TemperatureText(zero, SystemSensorReader.Evaluate(new FakeLive { Text = Doc(Now.AddMinutes(-5)) }, Now), true);
        check(first is { Tone: "Success" } && first.Value.Text.StartsWith("Temperatura da CPU: 78,6 °C") && first.Value.Text.Contains("leitor de sensores do sistema")
            && waiting is { Tone: "Neutral" } && waiting.Value.Text == "Temperatura: aguardando o leitor…" && !waiting.Value.Text.Contains("indisponível")
            && stale!.Value.Text.Contains("sem resposta desde") && !stale.Value.Text.Contains("descartada"),
            "#142 ao montar a Início com live.json fresco simulado, a temperatura já está no texto (" + first?.Text + "); sem arquivo logo depois de ligar o PC, \"aguardando o leitor…\", nunca \"indisponível\"");

        var code = Path.Combine(AppContext.BaseDirectory, "ThemeSources", "Code");
        var home = File.ReadAllText(Path.Combine(code, "HomePage.cs"));
        var window = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ThemeSources", "MainWindow.xaml.cs"));
        var panel = File.ReadAllText(Path.Combine(code, "LiveMetricsPanel.cs"));
        var card = home[home.IndexOf("private Border HomeHealthCard", StringComparison.Ordinal)..];
        int Index(string text, string value) => text.IndexOf(value, StringComparison.Ordinal);
        check(Index(card, "SystemSensorReader.Current()") > 0 && Index(card, "SystemSensorReader.TemperatureText(health.Latest, live,") > Index(card, "SystemSensorReader.Current()")
            && Index(card, "SystemSensorReader.TemperatureText(health.Latest") < Index(card, "if (health.Latest is { } latest)")
            && Index(panel, "RenderTemperature(SystemSensorReader.Evaluate(sensors, DateTimeOffset.UtcNow));") is var sync && sync > 0 && sync < Index(panel, "timer.Tick +=")
            && Index(panel, "SystemSensorReader.Evaluate(sensors, DateTimeOffset.UtcNow)))") > 0,
            "#142 Início e cartão \"Agora\": o live.json é lido de forma síncrona na montagem (antes do 1º tique e sem depender do monitor) e de novo a cada tique visível");
        check(window.Contains("SystemSensorReader.NeedsAdministrator(latest.CpuTemperature, pawnIoInstalled)")
            && window.Contains("PageKit.Notice(\"health\", SystemSensorReader.AdminNeededTitle, SystemSensorReader.AdminNeededDetail, \"Neutral\")")
            && window.Contains("Action(SystemSensorReader.ActivateLabel,") && window.Contains("Action(SystemSensorReader.DeactivateLabel,") && window.Contains("Action(SystemSensorReader.UpdateLabel,")
            && window.Contains("store.Log(user, name + \": \" + result.State)") && window.Contains("healthStore.SaveIntervention(new InterventionRecord")
            && Regex.IsMatch(window, @"if \(!await ConfirmChange\(new ChangeRequest\(title, details\)\)\) return;\s+liveReaderRunning = true;"),
            "#142 Saúde do PC: estado neutro com o PawnIO instalado, botões Ativar/Desativar/Atualizar só depois da confirmação, com auditoria e histórico");
        var details = SystemSensorReader.ConfirmationDetails;
        check(details.Contains("leitor mínimo de sensores") && details.Contains("pasta protegida em Arquivos de Programas") && details.Contains("SYSTEM") && details.Contains("desde a inicialização")
            && details.Contains("grava só esse valor") && details.Contains("(UAC) uma vez") && details.Contains("Desativar temperatura ao vivo") && details.Contains("remove a tarefa e a pasta"),
            "#142 confirmação: cópia para pasta protegida em Arquivos de Programas, tarefa SYSTEM desde a inicialização, só a temperatura, um UAC e como desativar");
    }

    private static string FromStore(HealthSnapshot sample, ElevatedTemperature? elevated, LiveTemperature? live, bool pawnIo)
    {
        var root = Path.Combine(Path.GetTempPath(), "nexo-live-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var db = new OperationalStore(root);
            db.SaveSnapshot(sample);
            return HealthReportPresentation.FromStore(db, null, elevated, live, pawnIo).ReplaceLineEndings("\n");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void Report(Action<bool, string> check)
    {
        var now = DateTimeOffset.UtcNow;
        var sample = Zero(now);
        var live = SystemSensorReader.Evaluate(new FakeLive { Text = Doc(now.AddSeconds(-2), celsius: 79.4) }, now);
        var elevated = new ElevatedTemperature(80.2, "Core (Tctl/Tdie) (/amdcpu/0/temperature/2)", ElevatedSensorReader.Source, now.AddMinutes(-1));
        var withLive = FromStore(sample, null, live, true);
        var withElevated = FromStore(sample, elevated, null, true);
        var neither = FromStore(sample, null, null, true);
        var control = FromStore(sample, null, null, false);
        string Line(string report) => report.Split('\n').First(line => line.StartsWith("Temperatura da CPU: ", StringComparison.Ordinal));
        check(control.Contains("retornou zero") && control.Contains("Limitação: " + HardwareSensors.TemperatureWarning + "O sensor retornou zero"),
            "#142 relatório (controle): sem leitura válida e sem o PawnIO, a linha descartada aparece, inclusive o aviso da amostra nas limitações, então a ausência abaixo é real");
        check(Line(withLive).StartsWith("Temperatura da CPU: 79 °C, sensor Core _Tctl/Tdie_, fonte " + SystemSensorReader.ReportSource + ", lida às ")
            && !withLive.Contains("retornou zero") && !withLive.Contains("descartada") && !withLive.Contains("Limitação: " + DiagnosticEngine.TemperatureLimitation),
            "#142 relatório: com o leitor do sistema fresco, a temperatura leva a origem e o horário, e a linha da leitura sem admin descartada não vai ao modelo: " + Line(withLive));
        check(Line(withElevated).Contains("80 °C") && Line(withElevated).Contains("com administrador, a pedido do usuário") && !withElevated.Contains("retornou zero") && !withElevated.Contains("descartada"),
            "#142 relatório: com a leitura elevada recente, também sem a linha descartada");
        check(Line(neither).Contains("o PawnIO está instalado, mas o Nexo sem administrador não tem acesso ao sensor") && !neither.Contains("retornou zero") && !neither.Contains("problema")
            && neither.Contains("não indica defeito no driver"),
            "#142 relatório: PawnIO instalado e sem leitura válida, a falta de permissão vai em tom neutro, sem \"retornou zero\"");
        var stale = FromStore(sample, null, SystemSensorReader.Evaluate(new FakeLive { Text = Doc(now.AddMinutes(-4)) }, now), true);
        check(Line(stale).Contains("indisponível, leitor de sensores sem resposta desde"), "#142 relatório: leitor sem resposta não vira valor");
        var evidence = RaidTemperatureChecks.Observed(withLive); evidence.AsksTemperature = true;
        var completed = evidence.WithSensorFigures("A CPU está funcionando.");
        check(completed.Contains("79 °C") && completed.Contains("leitor de sensores do sistema") && !completed.Contains("zero"),
            "#142 chat: a resposta sem o valor ganha a linha com a origem do leitor do sistema: " + completed);
    }

    private static void Refusal(Action<bool, string> check)
    {
        var report = FromStore(Zero(DateTimeOffset.UtcNow), null, SystemSensorReader.Evaluate(new FakeLive { Text = Doc(DateTimeOffset.UtcNow) }, DateTimeOffset.UtcNow), true);
        var evidence = RaidTemperatureChecks.Observed(report); evidence.AsksTemperature = true;
        string[] offers = ["Posso ativar a temperatura ao vivo para você.", "Quer que eu instale o leitor de sensores do sistema?", "Posso verificar se o leitor de sensores está funcionando.",
            "Vou atualizar o leitor de sensores.", "Posso desativar a temperatura ao vivo, se preferir.", "Posso habilitar a temperatura em tempo real.", "Posso reiniciar a tarefa Nexo Sensores."];
        var accepted = offers.Where(text => evidence.ValidateStatement(text, false) != InvestigationEvidence.LiveReaderOfferReason).ToList();
        check(accepted.Count == 0, "#142/#135: o modelo não oferece ativar, instalar, verificar, atualizar ou desativar a temperatura ao vivo nem o leitor de sensores"
            + (accepted.Count > 0 ? " · aceitos: " + string.Join(" | ", accepted) : ""));
        string[] allowed = ["Você pode ativar a temperatura ao vivo na Saúde do PC, pelo botão Ativar temperatura ao vivo.", "Não posso ativar a temperatura ao vivo por aqui; use o botão na Saúde do PC.",
            "Posso explicar como ativar a temperatura ao vivo.", "A CPU está a 79 °C, pelo leitor de sensores do sistema."];
        var refused = allowed.Where(text => evidence.ValidateStatement(text, false) == InvestigationEvidence.LiveReaderOfferReason).ToList();
        check(refused.Count == 0, "#142/#135: orientar o usuário, a negação e citar a leitura passam" + (refused.Count > 0 ? " · recusados: " + string.Join(" | ", refused) : ""));
        var guidance = evidence.RepeatedGuidance(InvestigationEvidence.LiveReaderOfferReason, "A CPU está a 79 °C. Posso ativar a temperatura ao vivo para você.");
        check(guidance.Contains("Posso ativar a temperatura ao vivo para você."), "#142: na repetição, a trava cita o trecho da oferta a remover");
    }

    // ---------- scripts elevados ----------

    private static async Task Scripts(Action<bool, string> check)
    {
        var source = Path.Combine(Path.GetTempPath(), "Nexo-src");
        var files = SystemSensorReader.FilesToCopy(false, ["Nexo.exe", "Nexo.dll", "Nexo.deps.json", "Nexo.runtimeconfig.json", "LibreHardwareMonitorLib.dll", "Nexo.pdb", "notas.txt", "a'b.dll", "..x.dll", "x.dll.config"]);
        var single = SystemSensorReader.FilesToCopy(true, ["Nexo.exe", "wpfgfx_cor3.dll", "PresentationNative_cor3.dll", "Nexo.pdb", "notas.txt", "a'b.dll", "..x.dll", "Nexo.runtimeconfig.json"]);
        check(SystemSensorReader.FilesToCopy(true, ["Nexo.exe"]).SequenceEqual(["Nexo.exe"]) && single.SequenceEqual(["Nexo.exe", "PresentationNative_cor3.dll", "wpfgfx_cor3.dll"])
            && files.SequenceEqual(["LibreHardwareMonitorLib.dll", "Nexo.deps.json", "Nexo.dll", "Nexo.exe", "Nexo.runtimeconfig.json"]),
            "#142 cópia: no single-file, o Nexo.exe e as .dll nativas deixadas ao lado dele (publicação sem IncludeNativeLibrariesForSelfExtract), para o SYSTEM nunca as procurar fora da pasta protegida; no build, só .exe, .dll e os JSON do runtime do primeiro nível: "
            + string.Join(", ", single) + " | " + string.Join(", ", files));
        var missing = false;
        try { SystemSensorReader.FilesToCopy(false, ["Nexo.exe"]); } catch (InvalidOperationException) { missing = true; }
        var badName = false;
        try { SystemSensorReader.InstallScript(source, ["..\\Nexo.dll"], ""); } catch (ArgumentException) { badName = true; }
        check(missing && badName, "#142 cópia: sem Nexo.dll ou com nome fora do formato, nada é montado");

        var install = SystemSensorReader.InstallScript(source, files, ProtectedFolderScript.Text);
        var remove = SystemSensorReader.RemoveScript(ProtectedFolderScript.Text);
        // O checkout do Windows (CI e PC) traz os scripts com CRLF; a ordem é conferida com o texto em LF.
        int At(string text, string value) => text.ReplaceLineEndings("\n").IndexOf(value, StringComparison.Ordinal);
        var outer = OptimizationDiskExecutor.BuildScript(NetworkRecovery.Compact(install), Path.Combine("C:\\Program Files", "NexoOptimize-x"), "C:\\Program Files\\NexoOptimize-x\\result.json", "S-1-5-21-1-2-3-1001");
        var launchOk = true;
        try { WindowsTools.CheckElevatedLaunch(["-NoProfile", "-NonInteractive", "-Command", outer]); } catch (InvalidOperationException) { launchOk = false; }
        check(install.StartsWith(PowerShellModulePath.Guard, StringComparison.Ordinal) && remove.StartsWith(PowerShellModulePath.Guard, StringComparison.Ordinal) && launchOk,
            "#142 scripts de instalação e remoção começam com a fixação do PSModulePath (#131) e passam na conferência do executor elevado antes do UAC");
        check(install.Contains("$nxTask='Nexo Sensores'") && install.Contains("New-ScheduledTaskTrigger -AtStartup") && install.Contains("-UserId 'SYSTEM' -LogonType ServiceAccount")
            && install.Contains("-ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1)") && install.Contains("$nxExe + '\" --sensor-loop'")
            && install.Contains("Start-ScheduledTask -TaskName $nxTask") && install.Contains("$nxTarget=Join-Path $nxParent 'Sensors'") && install.Contains("$nxParent=Join-Path $nxBase 'Nexo'")
            && install.Contains("[Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)") && install.Contains("DOTNET_BUNDLE_EXTRACT_BASE_DIR=")
            && !Regex.IsMatch(install, @"\$env:(?!PSModulePath)", RegexOptions.IgnoreCase),
            "#142 script de instalação: tarefa \"Nexo Sensores\" como SYSTEM, na inicialização, com reinício e sem limite, rodando --sensor-loop da pasta em Arquivos de Programas, sem variável de ambiente do usuário");
        check(At(install, "Use-ProtectedDirectory $nxTarget") > 0 && At(install, "Use-ProtectedDirectory $nxTarget") < At(install, "Stop-NexoSensorLoop\n")
            && At(install, "Stop-NexoSensorLoop\n") < At(install, "foreach($name in $nxFiles)") && At(install, "foreach($name in $nxFiles)") < At(install, "Register-ScheduledTask -TaskName")
            && install.Contains("$nxIn=New-Object System.IO.FileStream($from,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)") && install.Contains("$sha.ComputeHash($nxIn)") && install.Contains("$nxIn.CopyTo($output)")
            && install.Contains("if($actual -ne $expected){ Remove-Item -LiteralPath $temporary -Force; throw")
            && At(remove, "Assert-NexoTree $nxTarget") < At(remove, "Unregister-ScheduledTask") && remove.Contains("Remove-NexoTree $nxTarget"),
            "#142 scripts: a pasta é conferida antes de parar, copiar ou registrar; o hash sai do mesmo handle aberto sem escrita que é copiado; a remoção confere os links antes de mexer na tarefa");

        var shell = Environment.GetEnvironmentVariable("NEXO_CHECKS_PWSH") is { Length: > 0 } configured ? configured
            : OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe") : null;
        if (shell == null)
        {
            check(!string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase), "#142 scripts com cmdlets falsos exigem PowerShell no CI");
            Console.WriteLine("IGNORADO: #142 scripts do leitor com cmdlets falsos (sem PowerShell; defina NEXO_CHECKS_PWSH)");
            return;
        }
        await RunScripts(shell, check);
    }

    private const string Fakes = """

        $script:acl=@{}; $script:links=@(); $script:reg=$null; $script:started=''; $script:unreg=$false
        function Assert-Trusted($path) {
          if($script:links -contains [string]$path){ throw ('A cópia protegida do Nexo em ' + $path + ' é um link e não é usada. Nenhuma alteração foi feita.') }
          $entry=$script:acl[[string]$path]
          if($entry){ $reason=Get-UntrustedReason $path $true $entry.Owner $entry.Protected $entry.Rules; if($reason){ throw ('A cópia protegida do Nexo não é usada: ' + $reason + '. Nenhuma alteração foi feita.') } }
        }
        function Use-ProtectedDirectory($path) { if(-not (Test-Path -LiteralPath $path)){ [void](New-Item -ItemType Directory -Path $path) }; Assert-Trusted $path }
        function New-ProtectedFileSecurity { $null }
        function New-NexoCopyStream { param($path) New-Object System.IO.FileStream($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None) }
        function Get-ScheduledTask { param($TaskName,$TaskPath,$ErrorAction) if($script:reg){ $script:reg } }
        function Stop-ScheduledTask { param($TaskName,$TaskPath,$ErrorAction) }
        function Start-ScheduledTask { param($TaskName,$TaskPath) $script:started=$TaskName }
        function Unregister-ScheduledTask { param($TaskName,$TaskPath,$Confirm) $script:unreg=$true; $script:reg=$null }
        function New-ScheduledTaskAction { param($Execute,$Argument,$WorkingDirectory) [pscustomobject]@{Execute=$Execute;Argument=$Argument;WorkingDirectory=$WorkingDirectory} }
        function New-ScheduledTaskTrigger { param([switch]$AtStartup) [pscustomobject]@{AtStartup=[bool]$AtStartup} }
        function New-ScheduledTaskPrincipal { param($UserId,$LogonType,$RunLevel) [pscustomobject]@{UserId=$UserId;LogonType=[string]$LogonType;RunLevel=[string]$RunLevel} }
        function New-ScheduledTaskSettingsSet { param($ExecutionTimeLimit,$RestartCount,$RestartInterval,[switch]$AllowStartIfOnBatteries,[switch]$DontStopIfGoingOnBatteries,[switch]$StartWhenAvailable,$MultipleInstances,$Priority) [pscustomobject]@{Limit=[string]$ExecutionTimeLimit;RestartCount=$RestartCount;Restart=[string]$RestartInterval;Multiple=[string]$MultipleInstances} }
        function Register-ScheduledTask { param($TaskName,$TaskPath,$Action,$Trigger,$Principal,$Settings,$Description,[switch]$Force) $script:reg=[pscustomobject]@{TaskName=$TaskName;TaskPath=$TaskPath;Action=$Action;Trigger=$Trigger;Principal=$Principal;Settings=$Settings} }

        """;

    // O script real, com o PowerShell da máquina: só a raiz de Arquivos de Programas, o live.json, a conferência de admin e o fluxo
    // com descritor (que só existe no Windows) são trocados; a cópia, o hash, a ordem e as recusas são os do Nexo.
    private static string Prepare(string script, string baseFolder, string live) => script
        .Replace("$nxBase=[Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)", "$nxBase='" + baseFolder.Replace("'", "''") + "'")
        .Replace("$nxLive=Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'NexoSupport\\sensors\\live.json'", "$nxLive='" + live.Replace("'", "''") + "'")
        .Replace("function New-NexoCopyStream($path) {", "function New-NexoCopyStreamReal($path) {")
        .Split('\n').Where(line => !line.TrimStart().StartsWith("if(-not (New-Object Security.Principal.WindowsPrincipal", StringComparison.Ordinal)).Aggregate(new StringBuilder(), (b, l) => b.Append(l).Append('\n')).ToString();

    private static async Task<string> Pwsh(string shell, string body)
    {
        var path = Path.Combine(Path.GetTempPath(), "nexo-live-check-" + Guid.NewGuid().ToString("N") + ".ps1");
        try
        {
            var wrapped = "try {\n$nxResult=@(. {\n" + body + "\n})\nWrite-Output ('OK:' + ($nxResult[-1] | ConvertTo-Json -Compress -Depth 4))\n} catch { Write-Output ('ERRO:' + $_.Exception.Message) }\n"
                + "Write-Output ('REG:' + ($script:reg | ConvertTo-Json -Compress -Depth 4))\nWrite-Output ('START:' + $script:started)\nWrite-Output ('UNREG:' + $script:unreg)\n";
            File.WriteAllText(path, wrapped, new UTF8Encoding(true));
            var start = new ProcessStartInfo(shell) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (await output).Trim() + ((await error).Trim() is { Length: > 0 } stderr ? "\nSTDERR:" + stderr : "");
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    private static async Task RunScripts(string shell, Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "nexo-live-ps-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = Path.Combine(root, "user", "Nexo");
            Directory.CreateDirectory(source);
            var exeBytes = new byte[70_000]; new Random(142).NextBytes(exeBytes);
            var dllBytes = new byte[9_000]; new Random(143).NextBytes(dllBytes);
            File.WriteAllBytes(Path.Combine(source, "Nexo.exe"), exeBytes);
            File.WriteAllBytes(Path.Combine(source, "Nexo.dll"), dllBytes);
            string[] files = ["Nexo.dll", "Nexo.exe"];
            string Case(string name) { var folder = Path.Combine(root, name, "Program Files"); Directory.CreateDirectory(folder); return folder; }
            string Target(string folder) => Path.Combine(folder, "Nexo", "Sensors");
            string Install(string folder, string setup) => Prepare(SystemSensorReader.InstallScript(source, files, ProtectedFolderScript.Text + Fakes + setup), folder, Path.Combine(root, "live.json"));
            string Literal(string path) => "'" + path.Replace("'", "''") + "'";

            var good = Case("good");
            var output = await Pwsh(shell, Install(good, ""));
            var target = Target(good);
            var copied = File.Exists(Path.Combine(target, "Nexo.exe")) && File.ReadAllBytes(Path.Combine(target, "Nexo.exe")).SequenceEqual(exeBytes)
                && File.ReadAllBytes(Path.Combine(target, "Nexo.dll")).SequenceEqual(dllBytes) && !Directory.EnumerateFiles(target, "*.tmp").Any();
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(exeBytes));
            check(copied && output.Contains("OK:") && output.Contains("Nexo.exe " + sha) && output.Contains("START:Nexo Sensores")
                && output.Contains("\"TaskName\":\"Nexo Sensores\"") && output.Contains("\"AtStartup\":true") && output.Contains("\"UserId\":\"SYSTEM\"") && output.Contains("\"Limit\":\"00:00:00\"")
                && output.Contains("\"RestartCount\":999") && output.Contains("--sensor-loop") && output.Contains(Path.Combine(target, "Nexo.exe").Replace("\\", "\\\\")) && output.Contains("cmd.exe")
                && Regex.IsMatch(output, @"/d /c set \\""PATH=[^%""]*?\\"" (?:&&|\\u0026\\u0026) set \\""DOTNET_BUNDLE_EXTRACT_BASE_DIR="),
                "#142 script real (cmdlets falsos): copia com SHA-256 igual ao da origem, registra \"Nexo Sensores\" (SYSTEM, AtStartup, sem limite, reinício, PATH fixo nas pastas do Windows) e inicia: " + Short(output));

            foreach (var (name, setup, expected) in new[]
            {
                ("loose", "$script:acl[__T__]=@{Owner='S-1-5-32-544';Protected=$true;Rules=@([pscustomobject]@{Sid='S-1-5-32-545';Rights=0x2;Type='Allow';Propagation=0})}", "ACE de escrita"),
                ("inherit", "$script:acl[__T__]=@{Owner='S-1-5-32-544';Protected=$false;Rules=@()}", "herda permissões"),
                ("owner", "$script:acl[__T__]=@{Owner='S-1-5-21-1-2-3-1001';Protected=$true;Rules=@()}", "dono não confiável"),
                ("link", "$script:links=@(__T__)", "é um link"),
            })
            {
                var folder = Case(name);
                var destination = Target(folder);
                Directory.CreateDirectory(destination);
                var result = await Pwsh(shell, Install(folder, setup.Replace("__T__", Literal(destination)) + "\n"));
                check(result.Contains("ERRO:") && result.Contains(expected) && !File.Exists(Path.Combine(destination, "Nexo.exe"))
                    && Regex.IsMatch(result, @"^REG:(null)?\s*$", RegexOptions.Multiline) && Regex.IsMatch(result, @"^START:\s*$", RegexOptions.Multiline),
                    "#142 script real: pasta de destino existente com " + name switch { "loose" => "ACL frouxa", "inherit" => "herança", "owner" => "dono de usuário", _ => "reparse point" }
                    + " é recusada antes de copiar ou registrar a tarefa: " + Short(result));
            }

            // @qa: as demais recusas e a troca, cada uma pelo script real.
            bool Untouched(string result) => Regex.IsMatch(result, @"^REG:(null)?\s*$", RegexOptions.Multiline) && Regex.IsMatch(result, @"^START:\s*$", RegexOptions.Multiline);
            var parentLoose = Case("parent-loose");
            var parentResult = await Pwsh(shell, Install(parentLoose, "$script:acl[" + Literal(Path.Combine(parentLoose, "Nexo")) + "]=@{Owner='S-1-5-32-544';Protected=$true;Rules=@([pscustomobject]@{Sid='S-1-5-32-545';Rights=0x10000;Type='Allow';Propagation=0})}\n"));
            check(parentResult.Contains("ERRO:") && parentResult.Contains("ACE de escrita") && !File.Exists(Path.Combine(Target(parentLoose), "Nexo.exe")) && Untouched(parentResult),
                "#142 script real: a pasta-pai %ProgramFiles%\\Nexo com ACE de escrita (exclusão) para Usuários é recusada, porque com ela daria para renomear Sensors e trocar o leitor: " + Short(parentResult));
            var fileLoose = Case("file-loose");
            var fileResult = await Pwsh(shell, Install(fileLoose, "$script:acl[" + Literal(Path.Combine(Target(fileLoose), "Nexo.dll")) + "]=@{Owner='S-1-5-32-544';Protected=$true;Rules=@([pscustomobject]@{Sid='S-1-5-32-545';Rights=0x2;Type='Allow';Propagation=0})}\n"));
            check(fileResult.Contains("ERRO:") && fileResult.Contains("ACE de escrita") && Untouched(fileResult),
                "#142 script real: arquivo copiado que não fica protegido depois da troca para a instalação antes de registrar a tarefa: " + Short(fileResult));
            var update = Case("update");
            var updateTarget = Target(update);
            Directory.CreateDirectory(Path.Combine(updateTarget, "extract", "abc"));
            File.WriteAllText(Path.Combine(updateTarget, "extract", "abc", "wpfgfx_cor3.dll"), "antiga");
            File.WriteAllText(Path.Combine(updateTarget, "Antigo.dll"), "antiga");
            File.WriteAllText(Path.Combine(updateTarget, "Nexo.exe"), "antiga");
            var updated = await Pwsh(shell, Install(update, ""));
            check(updated.Contains("OK:") && !Directory.Exists(Path.Combine(updateTarget, "extract")) && !File.Exists(Path.Combine(updateTarget, "Antigo.dll"))
                && File.ReadAllBytes(Path.Combine(updateTarget, "Nexo.exe")).SequenceEqual(exeBytes) && updated.Contains("START:Nexo Sensores"),
                "#142 script real (Atualizar leitor): a extração antiga e os arquivos que não fazem mais parte do leitor saem, e o Nexo.exe é o novo: " + Short(updated));
            var self = await Pwsh(shell, Prepare(SystemSensorReader.InstallScript(Target(update), files, ProtectedFolderScript.Text + Fakes), update, Path.Combine(root, "live.json")));
            check(self.Contains("ERRO:") && self.Contains("já é a cópia do leitor") && Untouched(self), "#142 script real: o Nexo aberto da própria cópia do leitor não se copia sobre ela: " + Short(self));
            var linkSource = Path.Combine(root, "user", "Atalho");
            var linkBase = Path.Combine(root, "base-link");
            var canLinkSource = true;
            try { Directory.CreateSymbolicLink(linkSource, source); Directory.CreateSymbolicLink(linkBase, Case("base-real")); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { canLinkSource = false; }
            if (canLinkSource)
            {
                var fromLink = await Pwsh(shell, Prepare(SystemSensorReader.InstallScript(linkSource, files, ProtectedFolderScript.Text + Fakes), Case("source-link"), Path.Combine(root, "live.json")));
                var baseLink = await Pwsh(shell, Install(linkBase, ""));
                check(fromLink.Contains("ERRO:") && fromLink.Contains("é um link") && Untouched(fromLink) && baseLink.Contains("ERRO:") && baseLink.Contains("Arquivos de Programas é um link") && Untouched(baseLink),
                    "#142 script real: origem que é link e Arquivos de Programas que é link são recusados antes de copiar: " + Short(fromLink) + " | " + Short(baseLink));
                var inner = Case("install-link");
                Directory.CreateDirectory(Path.Combine(Target(inner), "extract"));
                Directory.CreateSymbolicLink(Path.Combine(Target(inner), "extract", "x"), Path.Combine(root, "outside-install"));
                var innerResult = await Pwsh(shell, Install(inner, ""));
                check(innerResult.Contains("ERRO:") && innerResult.Contains("Nada foi removido") && Untouched(innerResult),
                    "#142 script real: link dentro da pasta do leitor para a instalação na conferência, antes de parar a tarefa: " + Short(innerResult));
            }
            else Console.WriteLine("IGNORADO: #142 instalação com link (o sistema não deixou criar o link de teste)");

            // Remoção: um link dentro da pasta para tudo, sem tocar no destino dele; sem link, a pasta e a tarefa somem.
            var linked = Case("remove-link");
            var linkedTarget = Target(linked);
            Directory.CreateDirectory(Path.Combine(linkedTarget, "extract", "Nexo"));
            File.WriteAllBytes(Path.Combine(linkedTarget, "Nexo.exe"), exeBytes);
            var outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "keep.txt"), "x");
            var canLink = true;
            try { Directory.CreateSymbolicLink(Path.Combine(linkedTarget, "extract", "Nexo", "link"), outside); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { canLink = false; }
            string Remove(string folder, string setup) => Prepare(SystemSensorReader.RemoveScript(ProtectedFolderScript.Text + Fakes + setup), folder, Path.Combine(root, "live.json"));
            if (canLink)
            {
                var refused = await Pwsh(shell, Remove(linked, "$script:reg=[pscustomobject]@{TaskName='Nexo Sensores'}\n"));
                check(refused.Contains("ERRO:") && refused.Contains("link") && File.Exists(Path.Combine(outside, "keep.txt")) && File.Exists(Path.Combine(linkedTarget, "Nexo.exe")) && refused.Contains("UNREG:False"),
                    "#142 script real de remoção: link dentro da pasta do leitor para antes de remover a tarefa ou qualquer arquivo: " + Short(refused));
            }
            else Console.WriteLine("IGNORADO: #142 remoção com link (o sistema não deixou criar o link de teste)");
            var clean = Case("remove");
            var cleanTarget = Target(clean);
            Directory.CreateDirectory(Path.Combine(cleanTarget, "extract", "Nexo", "abc"));
            File.WriteAllBytes(Path.Combine(cleanTarget, "Nexo.exe"), exeBytes);
            File.WriteAllText(Path.Combine(cleanTarget, "extract", "Nexo", "abc", "wpfgfx_cor3.dll"), "x");
            File.WriteAllText(Path.Combine(root, "live.json"), "{}");
            var removed = await Pwsh(shell, Remove(clean, "$script:reg=[pscustomobject]@{TaskName='Nexo Sensores'}\n"));
            check(removed.Contains("OK:") && !Directory.Exists(cleanTarget) && !Directory.Exists(Path.GetDirectoryName(cleanTarget)) && removed.Contains("UNREG:True") && !File.Exists(Path.Combine(root, "live.json")),
                "#142 script real de remoção: tarefa removida, pasta Sensors (com a extração) e a pasta Nexo vazia apagadas, live.json apagado: " + Short(removed));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private static string Short(string text) => text.Length <= 700 ? text.ReplaceLineEndings(" ⏎ ") : text[..700].ReplaceLineEndings(" ⏎ ") + "…";

    private static async Task Apply(Action<bool, string> check)
    {
        var canceled = await SystemSensorReader.ApplyAsync((_, _) => Task.FromException<string>(new Win32Exception(1223)), "install", "s", CancellationToken.None);
        var done = await SystemSensorReader.ApplyAsync((_, _) => Task.FromResult("{\"success\":true,\"data\":{\"Task\":\"Nexo Sensores\"}}"), "install", "s", CancellationToken.None);
        var removed = await SystemSensorReader.ApplyAsync((_, _) => Task.FromResult("{\"success\":true}"), "remove", "s", CancellationToken.None);
        var failed = await SystemSensorReader.ApplyAsync((_, _) => Task.FromResult("{\"success\":false,\"error\":\"A cópia protegida do Nexo não é usada: ACE de escrita.\"}"), "install", "s", CancellationToken.None);
        var garbage = await SystemSensorReader.ApplyAsync((_, _) => Task.FromResult("<html>"), "update", "s", CancellationToken.None);
        check(canceled.State == "canceled" && canceled.Message.Contains("Nada foi alterado") && done.State == "done" && done.Message.Contains("Nexo Sensores") && done.Message.Contains("Desativar temperatura ao vivo")
            && removed.State == "done" && removed.Message.Contains("removidas") && failed.State == "failed" && failed.Message.Contains("ACE de escrita") && garbage.State == "failed",
            "#142 executor elevado: UAC recusado, sucesso, remoção, falha do script e resultado ilegível viram estado claro");
    }
}
