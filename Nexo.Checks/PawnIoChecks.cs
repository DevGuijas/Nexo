using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Nexo.Support;
namespace Nexo.Checks;

/// <summary>#142: instalação do PawnIO e leitor elevado, com download, hash, assinatura, UAC e processo simulados.</summary>
internal static class PawnIoChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        await Install(check);
        Arguments(check);
        await Elevated(check);
        Report(check);
        Refusal(check);
    }

    private sealed class FakeInstall : IPawnIoInstallSystem
    {
        public required string Directory { get; init; }
        public Func<Uri, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        public (string, string) Sign { get; set; } = ("Valid", "CN=namazso");
        public int? Exit { get; set; } = 0;
        public bool InstalledBefore { get; set; }
        public bool InstalledAfter { get; set; }
        public bool Ran { get; private set; }
        public bool Signed { get; private set; }
        public bool ExistedWhenRun { get; private set; }
        public List<Uri> Requests { get; } = [];
        public string? File { get; private set; }
        public Task<HttpResponseMessage> Send(Uri url, CancellationToken token) { Requests.Add(url); return Task.FromResult(Respond(url)); }
        public Task<(string Status, string Subject)> Signature(string file, CancellationToken token) { Signed = true; File = file; return Task.FromResult(Sign); }
        public Task<int?> RunElevated(string file, CancellationToken token) { Ran = true; File = file; ExistedWhenRun = System.IO.File.Exists(file); return Task.FromResult(Exit); }
        public bool DriverInstalled() => Ran ? InstalledAfter : InstalledBefore;
        public string WorkDirectory() => Directory;
    }

    private static HttpResponseMessage Ok(byte[] body, long? length = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        response.Content.Headers.ContentLength = length ?? body.Length;
        return response;
    }
    private static HttpResponseMessage Redirect(string location) { var r = new HttpResponseMessage(HttpStatusCode.Found); r.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute); return r; }

    private static async Task Install(Action<bool, string> check)
    {
        var temp = Path.Combine(Path.GetTempPath(), "NexoPawnIo-" + Guid.NewGuid().ToString("N"));
        string Work() => Path.Combine(temp, Guid.NewGuid().ToString("N"));
        var body = new byte[PawnIoSetup.Size];
        new Random(142).NextBytes(body);
        var pinned = PawnIoSetup.Sha256;
        var bodyHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        check(PawnIoSetup.Url.ToString() == "https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe" && PawnIoSetup.Size == 3410960
            && pinned == "1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032" && !PawnIoSetup.Url.ToString().Contains("latest") && !PawnIoSetup.Url.ToString().Contains("nrestricted"),
            "#142 PawnIO: release 2.2.0 fixada (URL, tamanho e SHA-256), sem latest nem Unrestricted");
        var details = PawnIoSetup.ConfirmationDetails;
        check(details.Contains("driver de kernel de terceiros") && details.Contains("namazso") && details.Contains("GPL-2.0") && details.Contains("release oficial 2.2.0")
            && details.Contains("GitHub") && details.Contains("administrador") && details.Contains("instalador do PawnIO abre e é ele quem instala") && details.Contains("Configurações > Aplicativos > PawnIO"),
            "#142 PawnIO: a confirmação diz terceiro, namazso, GPL-2.0, release oficial, UAC, assistente próprio e como desinstalar");

        // O SHA-256 fixado não é o dos bytes simulados: o caminho real de hash diferente.
        var wrongHash = new FakeInstall { Directory = Work(), Respond = _ => Ok(body) };
        var result = await PawnIoSetup.InstallAsync(wrongHash, null, CancellationToken.None);
        check(bodyHash != pinned && result.State == "integrity" && result.Message.Contains("SHA-256") && !wrongHash.Signed && !wrongHash.Ran
            && !Directory.Exists(wrongHash.Directory) && !Directory.EnumerateFiles(temp, "*", SearchOption.AllDirectories).Any(),
            "#142 PawnIO: SHA-256 diferente para antes da assinatura e do UAC, e o arquivo é apagado");

        var shorter = new FakeInstall { Directory = Work(), Respond = _ => Ok(body[..^1]) };
        result = await PawnIoSetup.InstallAsync(shorter, null, CancellationToken.None);
        check(result.State == "integrity" && result.Message.Contains("bytes") && !shorter.Ran && !shorter.Signed, "#142 PawnIO: tamanho informado diferente para sem executar");
        var lying = new FakeInstall { Directory = Work(), Respond = _ => { var r = Ok(body[..^10]); r.Content.Headers.ContentLength = null; return r; } };
        result = await PawnIoSetup.InstallAsync(lying, null, CancellationToken.None);
        check(result.State == "integrity" && !lying.Ran && !Directory.Exists(lying.Directory), "#142 PawnIO: arquivo recebido menor que o esperado (sem Content-Length) para e apaga");
        var longer = new FakeInstall { Directory = Work(), Respond = _ => { var r = Ok([.. body, 1, 2]); r.Content.Headers.ContentLength = null; return r; } };
        result = await PawnIoSetup.InstallAsync(longer, null, CancellationToken.None);
        check(result.State == "integrity" && !longer.Ran, "#142 PawnIO: arquivo maior que o esperado para durante o download");

        // Para chegar à assinatura e ao UAC sem o binário real, a sobrecarga interna recebe o hash dos bytes simulados;
        // a pública usa sempre o SHA-256 fixado (o caso acima).
        var good = await Pinned();
        async Task<(bool invalid, bool ok, PawnIoInstallResult uac, PawnIoInstallResult failed, PawnIoInstallResult absent)> Pinned()
        {
            var hops = new FakeInstall { Directory = Work() };
            hops.Respond = url => url.Host == "github.com" ? Redirect("https://release-assets.githubusercontent.com/github-production-release-asset/x?sig=1") : Ok(body);
            hops.Sign = ("NotSigned", "");
            var signature = await PawnIoSetup.InstallAsync(hops, null, CancellationToken.None, bodyHash);
            var invalid = signature.State == "signature" && hops.Signed && !hops.Ran && !File.Exists(hops.File!) && hops.Requests.Count == 2;

            var installed = new FakeInstall { Directory = Work(), InstalledAfter = true, Respond = url => url.Host == "github.com" ? Redirect("https://objects.githubusercontent.com/a") : Ok(body) };
            var done = await PawnIoSetup.InstallAsync(installed, null, CancellationToken.None, bodyHash);
            var ok = done.State == "installed" && done.Signer == "CN=namazso" && done.Message.Contains("CN=namazso") && done.Message.Contains("Configurações > Aplicativos > PawnIO")
                && installed.ExistedWhenRun && !File.Exists(installed.File!);

            var canceled = new FakeInstall { Directory = Work(), Exit = null, Respond = _ => Ok(body) };
            var uac = await PawnIoSetup.InstallAsync(canceled, null, CancellationToken.None, bodyHash);
            var failing = new FakeInstall { Directory = Work(), Exit = 1603, Respond = _ => Ok(body) };
            var failed = await PawnIoSetup.InstallAsync(failing, null, CancellationToken.None, bodyHash);
            var closed = new FakeInstall { Directory = Work(), Exit = 0, Respond = _ => Ok(body) };
            var absent = await PawnIoSetup.InstallAsync(closed, null, CancellationToken.None, bodyHash);
            return (invalid, ok, uac, failed, absent);
        }
        check(good.invalid, "#142 PawnIO: assinatura inválida para antes do UAC e apaga o arquivo; redirecionamento para o host de arquivos do GitHub é aceito");
        check(good.ok, "#142 PawnIO: driver presente depois do instalador → instalado, com o assinante e o caminho de desinstalação; o arquivo é apagado no fim");
        check(good.uac.State == "canceled" && good.uac.Message.Contains("cancelada") && good.uac.ExitCode == null, "#142 PawnIO: UAC cancelado vira estado claro, sem código de saída");
        check(good.failed.State == "failed" && good.failed.Message.Contains("1603") && good.failed.ExitCode == 1603, "#142 PawnIO: instalador com erro mostra o código de saída");
        check(good.absent.State == "not_installed" && good.absent.Message.Contains("não foi encontrado"), "#142 PawnIO: instalador sem erro e driver ausente não é dado como instalado");

        foreach (var target in new[] { "https://evil.example.com/PawnIO_setup.exe", "http://objects.githubusercontent.com/a", "https://github.com.evil.example/a", "https://user@github.com/a", "https://objects.githubusercontent.com:8443/a" })
        {
            var hop = new FakeInstall { Directory = Work() };
            hop.Respond = url => url == PawnIoSetup.Url ? Redirect(target) : Ok(body);
            result = await PawnIoSetup.InstallAsync(hop, null, CancellationToken.None);
            check(result.State == "host" && hop.Requests.Count == 1 && !hop.Ran && !hop.Signed, "#142 PawnIO: redirecionamento recusado antes de pedir " + target);
        }
        var loop = new FakeInstall { Directory = Work(), Respond = _ => Redirect("https://github.com/loop") };
        result = await PawnIoSetup.InstallAsync(loop, null, CancellationToken.None);
        check(result.State == "network" && loop.Requests.Count == PawnIoSetup.MaxRedirects + 1 && !loop.Ran, "#142 PawnIO: redirecionamentos em laço param no limite");
        var offline = new FakeInstall { Directory = Work(), Respond = _ => throw new HttpRequestException("Nenhuma conexão") };
        result = await PawnIoSetup.InstallAsync(offline, null, CancellationToken.None);
        check(result.State == "network" && result.Message.Contains("Sem conexão") && !offline.Ran, "#142 PawnIO: sem rede vira estado claro");
        var already = new FakeInstall { Directory = Work(), InstalledBefore = true };
        result = await PawnIoSetup.InstallAsync(already, null, CancellationToken.None);
        check(result.State == "installed" && already.Requests.Count == 0 && !already.Ran, "#142 PawnIO: já instalado não baixa nada");
        check(PawnIoDriver.ValidVersion("2.2.0.0") && !PawnIoDriver.ValidVersion("") && !PawnIoDriver.ValidVersion(null) && !PawnIoDriver.ValidVersion(2),
            "#142 PawnIO: presença do driver pela mesma regra da LibreHardwareMonitor (DisplayVersion válido)");
        try { Directory.Delete(temp, true); } catch (IOException) { }
    }

    private static void Arguments(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "NexoSensors-" + Guid.NewGuid().ToString("N"), "NexoSupport", "sensors");
        var good = ElevatedSensorReader.NewOutputPath(root);
        check(ElevatedSensorReader.ValidateArguments(["--read-sensors", good], root, out var output) == null && output == good,
            "#142 leitor elevado: aceita só o arquivo gerado na pasta protegida");
        var name = Path.GetFileName(good);
        string[][] refused =
        [
            ["--read-sensors"],
            ["--read-sensors", good, "--theme"],
            ["--read-sensors", good, good],
            ["--screenshot", good],
            ["--READ-SENSORS", good],
            ["--read-sensors", Path.Combine(Path.GetTempPath(), name)],
            ["--read-sensors", Path.Combine(root, "sub", name)],
            ["--read-sensors", Path.Combine(root, "..", "sensors", name)],
            ["--read-sensors", Path.Combine(root + "-x", name)],
            ["--read-sensors", Path.Combine(root, "data.json")],
            ["--read-sensors", Path.Combine(root, "reading-" + new string('A', 32) + ".json")],
            ["--read-sensors", "reading-" + new string('a', 32) + ".json"],
            ["--read-sensors", ""],
        ];
        var accepted = refused.Where(args => ElevatedSensorReader.ValidateArguments(args, root, out _) == null).Select(args => string.Join(" ", args)).ToList();
        check(accepted.Count == 0, "#142 leitor elevado: recusa argumentos extras, outro modo, caminho fora da pasta protegida, subpasta, \"..\" e nome fora do formato"
            + (accepted.Count > 0 ? " · aceitos: " + string.Join(" | ", accepted) : ""));
        check(ElevatedSensorReader.Run(["--read-sensors", Path.Combine(Path.GetTempPath(), name)]) == 2 && ElevatedSensorReader.Run(["--read-sensors", ElevatedSensorReader.NewOutputPath(ElevatedSensorReader.Root), "x"]) == 2,
            "#142 leitor elevado: o modo recusa (código 2) antes de ler qualquer sensor");
    }

    private sealed class FakeElevated : IElevatedSensorSystem
    {
        public ElevatedRun Result { get; set; } = new("exited", 0);
        public string? Problem { get; set; }
        public Func<string, string> Text { get; set; } = _ => "";
        public int Runs { get; private set; }
        public string? Output { get; private set; }
        public Task<ElevatedRun> Run(string output, TimeSpan timeout, CancellationToken token) { Runs++; Output = output; return Task.FromResult(timeout == TimeSpan.FromSeconds(20) ? Result : new("exited", 99)); }
        public string? Untrusted(string file) => Problem;
        public string ReadText(string file) => Text(file);
    }

    private static string Json(DateTimeOffset time, string state = "read", double? celsius = 63.8) =>
        ElevatedSensorReader.Document(new TemperatureReading(state, celsius, "Core (Tctl/Tdie) (/amdcpu/0/temperature/2)", HardwareSensors.TemperatureSource, time, state == "read" ? "" : "O sensor retornou zero."), time);

    private static async Task Elevated(Action<bool, string> check)
    {
        var now = new DateTimeOffset(2026, 11, 23, 14, 5, 0, TimeSpan.Zero);
        var root = Path.Combine(Path.GetTempPath(), "NexoSupport", "sensors");
        ElevatedSensorReader.Forget();
        var good = new FakeElevated { Text = _ => Json(now) };
        var read = await ElevatedSensorReader.ReadAsync(good, root, () => now, CancellationToken.None);
        var expected = "63,8 °C · Core (Tctl/Tdie) · leitura com administrador às " + now.ToLocalTime().ToString("HH:mm");
        check(read.State == "read" && read.Message == expected && read.Reading!.Source.Contains("administrador") && good.Runs == 1
            && Path.GetDirectoryName(good.Output) == root && ElevatedSensorReader.Fresh(now)?.Celsius == 63.8,
            "#142 leitor elevado: leitura válida vira \"" + expected + "\", com a origem, em um único UAC");

        var canceled = await ElevatedSensorReader.ReadAsync(new FakeElevated { Result = new("canceled") }, root, () => now, CancellationToken.None);
        var timeout = await ElevatedSensorReader.ReadAsync(new FakeElevated { Result = new("timeout") }, root, () => now, CancellationToken.None);
        var exit = await ElevatedSensorReader.ReadAsync(new FakeElevated { Result = new("exited", 4) }, root, () => now, CancellationToken.None);
        check(canceled is { State: "canceled", Reading: null } && canceled.Message.Contains("cancelada") && timeout is { State: "timeout", Reading: null } && timeout.Message.Contains("20 s")
            && exit is { State: "failed", Reading: null } && exit.Message.Contains("pasta protegida"),
            "#142 leitor elevado: UAC cancelado, tempo esgotado e código de saída viram estado claro, sem valor");
        var untrusted = await ElevatedSensorReader.ReadAsync(new FakeElevated { Problem = "dono não confiável (S-1-5-21-1)", Text = _ => Json(now) }, root, () => now, CancellationToken.None);
        check(untrusted is { State: "untrusted", Reading: null } && untrusted.Message.Contains("dono não confiável"), "#142 leitor elevado: arquivo fora da proteção é ignorado mesmo com valor válido");

        var stale = ElevatedSensorReader.Parse(Json(now.AddMinutes(-6)), now.AddMinutes(-7), now, out var staleDetail);
        var old = await ElevatedSensorReader.ReadAsync(new FakeElevated { Text = _ => Json(now.AddMinutes(-3)) }, root, () => now, CancellationToken.None);
        var future = ElevatedSensorReader.Parse(Json(now.AddMinutes(3)), now, now, out _);
        check(stale == null && staleDetail.Contains("5 minutos") && old is { State: "invalid", Reading: null } && future == null,
            "#142 leitor elevado: JSON com mais de 5 min, de antes do pedido ou do futuro é ignorado");
        string[] broken = ["", "{", "[]", "{\"Format\":2}", Json(now).Replace("63.8", "0"), Json(now).Replace("63.8", "140"), Json(now).Replace("63.8", "\"63.8\""),
            Json(now).Replace("\"Format\":1", "\"Format\":\"1\""), Json(now).Replace("Core (Tctl/Tdie) (/amdcpu/0/temperature/2)", "")];
        var parsed = broken.Where(text => ElevatedSensorReader.Parse(text, now, now, out _) != null).ToList();
        var zero = ElevatedSensorReader.Parse(Json(now, "invalid", null), now, now, out var zeroDetail);
        check(parsed.Count == 0 && zero == null && zeroDetail.Contains("zero"), "#142 leitor elevado: JSON inválido, valor zero ou fora da faixa nunca vira temperatura"
            + (parsed.Count > 0 ? " · aceitos: " + string.Join(" | ", parsed) : ""));
        var invalidRead = await ElevatedSensorReader.ReadAsync(new FakeElevated { Text = _ => "{" }, root, () => now, CancellationToken.None);
        check(invalidRead is { State: "invalid", Reading: null }, "#142 leitor elevado: JSON inválido mostra estado claro");
        check(ElevatedSensorReader.Fresh(now.AddMinutes(5)) != null && ElevatedSensorReader.Fresh(now.AddMinutes(5).AddSeconds(1)) == null,
            "#142 leitor elevado: a leitura guardada vale para o relatório só por 5 minutos");
        ElevatedSensorReader.Forget();
    }

    private static void Report(Action<bool, string> check)
    {
        var now = DateTimeOffset.UtcNow;
        var reading = new ElevatedTemperature(63.8, "Core (Tctl/Tdie) (/amdcpu/0/temperature/2)", ElevatedSensorReader.Source, now);
        var invalid = new HealthSnapshot { CpuTemperature = new TemperatureReading("invalid", Source: HardwareSensors.TemperatureSource, Time: now, Detail: "O sensor retornou zero.") };
        var line = HealthReportPresentation.TemperatureLine(invalid, reading);
        var plain = HealthReportPresentation.TemperatureLine(invalid, null);
        var normal = new HealthSnapshot { CpuTemperatureC = 50, CpuTemperature = new TemperatureReading("read", 50, "CPU Package (/intelcpu/0/temperature/0)", HardwareSensors.TemperatureSource, now) };
        check(line.StartsWith("64 °C, sensor ") && line.Contains("processo separado com administrador") && line.Contains("lida às " + now.ToLocalTime().ToString("HH:mm")) && line.Contains("a pedido do usuário")
            && plain.StartsWith("indisponível") && HealthReportPresentation.TemperatureLine(normal, reading).StartsWith("50 °C") && !HealthReportPresentation.TemperatureLine(normal, reading).Contains("administrador"),
            "#142 relatório: leitura elevada recente entra com a origem e o horário só quando a leitura comum falhou");
    }

    private static void Refusal(Action<bool, string> check)
    {
        ElevatedSensorReader.Forget();
        var evidence = RaidTemperatureChecks.Observed(RaidTemperatureChecks.PcReport()); evidence.AsksTemperature = true;
        string[] offers = ["Posso instalar o driver PawnIO para você.", "Quer que eu baixe o instalador do PawnIO?", "Posso verificar se o PawnIO está instalado."];
        var accepted = offers.Where(text => evidence.ValidateStatement(text, false) != InvestigationEvidence.SensorDriverOfferReason).ToList();
        check(accepted.Count == 0, "#142/#135: com o botão de instalar na tela, o modelo continua recusando oferecer instalar, baixar ou verificar o PawnIO"
            + (accepted.Count > 0 ? " · aceitos: " + string.Join(" | ", accepted) : ""));
    }
}
