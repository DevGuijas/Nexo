using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// Travas de publicação (#21): roda scripts/Release-Guard.ps1 de verdade, com a leitura da Oracle trocada por respostas
// falsas e um par de chaves RSA gerado no teste. Nenhuma rede é usada. A trava exige PowerShell 7 (RSA.ImportFromPem):
// usa NEXO_CHECKS_PWSH e, no Windows, o pwsh do PATH (presente no windows-latest do CI). Sem pwsh, imprime IGNORADO,
// exceto no CI (CI=true) ou com NEXO_CHECKS_PWSH apontando para um arquivo que não existe: aí reprova.
// A leitura HTTP real (Invoke-NexoHttp) é testada contra um HttpListener em 127.0.0.1, também sem sair da máquina.
internal static class ReleaseGuardChecks
{
    private const string Origin = "https://objectstorage.sa-saopaulo-1.oraclecloud.com/n/grlqtjwmlv51/b/nexo-releases/o/";

    public static void Run(Action<bool, string> check)
    {
        var configured = Environment.GetEnvironmentVariable("NEXO_CHECKS_PWSH");
        var ci = string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            check(File.Exists(configured), "Trava de publicação: NEXO_CHECKS_PWSH aponta para um pwsh que existe (" + configured + ")");
        }
        var shell = !string.IsNullOrWhiteSpace(configured) ? configured : OperatingSystem.IsWindows() ? "pwsh" : null;
        if (shell == null) { Skip(check, ci); return; }
        var guard = Guard();
        check(guard != null, "Trava de publicação encontrada em scripts/Release-Guard.ps1");
        if (guard == null) return;

        using var key = RSA.Create(2048);
        using var other = RSA.Create(2048);
        byte[] Manifest(string version) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { schema = 1, version, model = "qwen3:8b" }));
        string Sign(RSA rsa, byte[] data) => Convert.ToBase64String(rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        JsonObject Response(int status, byte[]? content = null, bool text = false) => new()
        {
            ["status"] = status, ["text"] = text,
            ["content"] = content == null ? "" : Convert.ToBase64String(content),
        };
        JsonObject Published(string published, string version, int head = 404, Action<JsonObject>? change = null, bool text = false)
        {
            var manifest = Manifest(published);
            var responses = new JsonObject
            {
                ["Get " + Origin + "latest.json"] = Response(200, manifest, text),
                ["Get " + Origin + "latest.sig"] = Response(200, Encoding.UTF8.GetBytes(Sign(key, manifest) + "\n"), text),
                ["Head " + Origin + "releases/" + version + "/nexo-app.zip"] = Response(head),
            };
            change?.Invoke(responses);
            return new JsonObject { ["version"] = version, ["responses"] = responses };
        }
        var forged = Manifest("1.6.3");
        var cases = new JsonObject
        {
            ["nova"] = Published("1.6.3", "1.7.0"),
            ["nova-texto"] = Published("1.6.3", "1.7.0", text: true),
            ["menor"] = Published("1.6.3", "1.5.9"),
            ["igual"] = Published("1.6.3", "1.6.3"),
            ["numerica-maior"] = Published("1.9.0", "1.10.0"),
            ["numerica-menor"] = Published("1.10.0", "1.9.0"),
            ["adulterada"] = Published("1.6.3", "1.7.0", change: r => r["Get " + Origin + "latest.json"] = Response(200, Manifest("1.0.0"))),
            ["outra-chave"] = Published("1.6.3", "1.7.0", change: r => r["Get " + Origin + "latest.sig"] = Response(200, Encoding.UTF8.GetBytes(Sign(other, forged)))),
            ["assinatura-ilegivel"] = Published("1.6.3", "1.7.0", change: r => r["Get " + Origin + "latest.sig"] = Response(200, Encoding.UTF8.GetBytes("não é base64!"))),
            ["sem-manifesto"] = Published("1.6.3", "1.7.0", change: r => r["Get " + Origin + "latest.json"] = Response(404)),
            ["sem-assinatura"] = Published("1.6.3", "1.7.0", change: r => r["Get " + Origin + "latest.sig"] = Response(403)),
            ["rede"] = Published("1.6.3", "1.7.0", change: r => r["Get " + Origin + "latest.json"] = new JsonObject { ["throw"] = "conexão recusada" }),
            ["ja-publicada"] = Published("1.6.3", "1.7.0", head: 200),
            ["head-incerto"] = Published("1.6.3", "1.7.0", head: 500),
            ["head-rede"] = Published("1.6.3", "1.7.0", change: r => r["Head " + Origin + "releases/1.7.0/nexo-app.zip"] = new JsonObject { ["throw"] = "tempo esgotado" }),
            ["publicada-invalida"] = Published("1.6.3", "1.7.0", change: r =>
            {
                var bad = Manifest("1.7");
                r["Get " + Origin + "latest.json"] = Response(200, bad);
                r["Get " + Origin + "latest.sig"] = Response(200, Encoding.UTF8.GetBytes(Sign(key, bad)));
            }),
            ["projeto-invalido"] = Published("1.6.3", "1.7"),
        };

        var signed = Manifest("1.6.3");
        using var server = new LocalOrigin(new Dictionary<string, (int Status, string Type, byte[] Body)>
        {
            ["GET /latest.json"] = (200, "application/json", signed),
            ["GET /latest.sig"] = (200, "text/plain", Encoding.UTF8.GetBytes(Sign(key, signed))),
            ["GET /binario"] = (200, "application/octet-stream", [1, 2, 3, 250]),
            ["HEAD /releases/1.6.3/nexo-app.zip"] = (200, "application/zip", []),
            ["GET /erro"] = (500, "text/plain", Encoding.UTF8.GetBytes("falha")),
        });
        var closed = LocalOrigin.ClosedPrefix();
        var dir = Path.Combine(Path.GetTempPath(), "nexo-release-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            var harness = Path.Combine(dir, "harness.ps1");
            File.WriteAllText(harness, Harness, bom);
            File.WriteAllText(Path.Combine(dir, "public.pem"), key.ExportSubjectPublicKeyInfoPem());
            File.WriteAllText(Path.Combine(dir, "cases.json"), cases.ToJsonString(), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, "closed.txt"), closed, new UTF8Encoding(false));
            var start = new ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", harness, "-Guard", guard, "-Dir", dir, "-Local", server.Prefix })
                start.ArgumentList.Add(argument);
            Process process;
            try { process = Process.Start(start)!; }
            catch (Win32Exception) { Skip(check, ci); return; }
            using (process)
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                var exited = process.WaitForExit(60_000);
                if (!exited) { try { process.Kill(true); } catch (InvalidOperationException) { } }
                check(exited && process.ExitCode == 0 && stderr.Result.Trim().Length == 0 && File.Exists(Path.Combine(dir, "out.json")),
                    "Trava de publicação executada com a Oracle simulada termina sem erro do PowerShell " + stderr.Result.Trim() + stdout.Result.Trim());
            }

            using var output = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "out.json")));
            var result = output.RootElement.GetProperty("results");
            string Of(string name) => result.GetProperty(name).GetString() ?? "";
            check(Of("nova") == "OK 1.6.3" && Of("nova-texto") == "OK 1.6.3",
                "Trava de publicação: 1.7.0 sobre a 1.6.3 publicada e assinada passa, com o manifesto em bytes ou em texto");
            check(Of("menor").StartsWith("ERRO A versão 1.5.9 não é maior que a publicada (1.6.3)", StringComparison.Ordinal)
                && Of("igual").StartsWith("ERRO A versão 1.6.3 não é maior que a publicada (1.6.3)", StringComparison.Ordinal),
                "Trava de publicação: versão menor ou igual à publicada é recusada");
            check(Of("numerica-maior") == "OK 1.9.0" && Of("numerica-menor").StartsWith("ERRO A versão 1.9.0 não é maior que a publicada (1.10.0)", StringComparison.Ordinal),
                "Trava de publicação: a comparação é numérica (1.10.0 é maior que 1.9.0)");
            check(new[] { "adulterada", "outra-chave", "assinatura-ilegivel" }.All(name => Of(name) == "ERRO O latest.json publicado não tem assinatura válida; a publicação foi interrompida."),
                "Trava de publicação: latest.json adulterado, assinado por outra chave ou com assinatura ilegível é recusado");
            check(Of("sem-manifesto").StartsWith("ERRO Não foi possível ler o latest.json publicado (HTTP 404)", StringComparison.Ordinal)
                && Of("sem-assinatura").StartsWith("ERRO Não foi possível ler o latest.sig publicado (HTTP 403)", StringComparison.Ordinal)
                && Of("rede").StartsWith("ERRO Não foi possível consultar a versão publicada em " + Origin + "latest.json: conexão recusada", StringComparison.Ordinal),
                "Trava de publicação: sem manifesto, sem assinatura ou sem rede, a geração para em vez de seguir sem conferir");
            check(Of("ja-publicada").StartsWith("ERRO A versão 1.7.0 já está publicada em " + Origin + "releases/1.7.0/nexo-app.zip", StringComparison.Ordinal)
                && Of("head-incerto").StartsWith("ERRO Não foi possível confirmar se a versão 1.7.0 já foi publicada", StringComparison.Ordinal)
                && Of("head-rede").StartsWith("ERRO Não foi possível consultar a versão publicada em " + Origin + "releases/1.7.0/nexo-app.zip", StringComparison.Ordinal),
                "Trava de publicação: pacote da versão já presente na Oracle, ou presença não confirmada, é recusado");
            check(Of("publicada-invalida").StartsWith("ERRO O latest.json publicado traz uma versão inválida: 1.7", StringComparison.Ordinal)
                && Of("projeto-invalido").StartsWith("ERRO Versão do projeto inválida: 1.7", StringComparison.Ordinal),
                "Trava de publicação: versão fora de MAJOR.MINOR.PATCH, publicada ou do projeto, é recusada");
            var http = output.RootElement.GetProperty("http");
            string Http(string name) => http.GetProperty(name).GetString() ?? "";
            check(Http("manifesto") == "200 texto " + Encoding.UTF8.GetString(signed) && Http("binario") == "200 bytes 1,2,3,250"
                && Http("head-ausente") == "404" && Http("head-presente") == "200" && Http("erro") == "500 texto falha",
                "Trava de publicação: a leitura HTTP real devolve o código (404 no HEAD, 500) sem exceção e o conteúdo em texto ou bytes");
            check(Http("fetch-padrao") == "${function:Invoke-NexoHttp}",
                "Trava de publicação: sem -Fetch, a trava usa essa mesma leitura HTTP real");
            check(Http("fechada").StartsWith("ERRO ", StringComparison.Ordinal),
                "Trava de publicação: porta sem servidor vira erro de conexão, não um 404");
            check(Http("ponta-a-ponta") == "OK 1.6.3" && Http("ponta-a-ponta-publicada").StartsWith("ERRO A versão 1.6.3 não é maior", StringComparison.Ordinal)
                && Http("ponta-a-ponta-pacote").StartsWith("ERRO A versão 1.7.1 já está publicada", StringComparison.Ordinal),
                "Trava de publicação: com HTTP real em 127.0.0.1, manifesto assinado em texto passa e versão igual ou pacote presente são recusados");
            check(server.Requests.Contains("HEAD /releases/1.7.0/nexo-app.zip") && server.Requests.Contains("GET /latest.sig"),
                "Trava de publicação: o servidor local recebeu o HEAD do pacote e a leitura da assinatura");
            var parse = output.RootElement.GetProperty("parse");
            check(parse.GetProperty("Release-Guard.ps1").GetInt32() == 0 && parse.GetProperty("Build-CloudRelease.ps1").GetInt32() == 0,
                "Trava de publicação: Release-Guard.ps1 e Build-CloudRelease.ps1 são PowerShell válido");
            var requested = output.RootElement.GetProperty("requested").EnumerateArray().Select(r => r.GetString() ?? "").ToArray();
            check(requested.Length > 0 && requested.All(r => r.StartsWith("Get " + Origin, StringComparison.Ordinal) || r.StartsWith("Head " + Origin, StringComparison.Ordinal)),
                "Trava de publicação: só consulta a origem HTTPS fixa da Oracle, a mesma do instalador");
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    private static void Skip(Action<bool, string> check, bool ci)
    {
        if (ci) check(false, "Trava de publicação: no CI o PowerShell 7 (pwsh) precisa estar disponível");
        else Console.WriteLine("IGNORADO: trava de publicação sem PowerShell 7 (pwsh) neste computador");
    }

    // Origem HTTP local para a leitura real do Invoke-WebRequest: responde pelas rotas dadas e 404 no resto.
    private sealed class LocalOrigin : IDisposable
    {
        private readonly System.Net.HttpListener listener = new();
        private readonly Dictionary<string, (int Status, string Type, byte[] Body)> routes;
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Requests = new();
        public string Prefix { get; }

        public LocalOrigin(Dictionary<string, (int Status, string Type, byte[] Body)> routes)
        {
            this.routes = routes;
            Prefix = "http://127.0.0.1:" + FreePort() + "/";
            listener.Prefixes.Add(Prefix);
            listener.Start();
            _ = Task.Run(Serve);
        }

        public static string ClosedPrefix() => "http://127.0.0.1:" + FreePort() + "/";

        private static int FreePort()
        {
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            try { return ((System.Net.IPEndPoint)probe.LocalEndpoint).Port; } finally { probe.Stop(); }
        }

        private async Task Serve()
        {
            while (listener.IsListening)
            {
                System.Net.HttpListenerContext context;
                try { context = await listener.GetContextAsync(); }
                catch (Exception e) when (e is System.Net.HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
                var route = context.Request.HttpMethod + " " + context.Request.Url!.AbsolutePath;
                Requests.Enqueue(route);
                var (status, type, body) = routes.TryGetValue(route, out var found) ? found : (404, "text/plain", Array.Empty<byte>());
                try
                {
                    context.Response.StatusCode = status;
                    context.Response.ContentType = type;
                    if (context.Request.HttpMethod == "HEAD") context.Response.ContentLength64 = 0;
                    else { context.Response.ContentLength64 = body.Length; await context.Response.OutputStream.WriteAsync(body); }
                }
                finally { context.Response.Close(); }
            }
        }

        public void Dispose() { try { listener.Stop(); listener.Close(); } catch (ObjectDisposedException) { } }
    }

    private static string? Guard()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "scripts", "Release-Guard.ps1");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private const string Harness = """
        param([string]$Guard, [string]$Dir, [string]$Local)
        $ErrorActionPreference = 'Stop'
        . $Guard
        $cases = Get-Content -LiteralPath (Join-Path $Dir 'cases.json') -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable
        $requested = [System.Collections.Generic.List[string]]::new()
        $results = [ordered]@{}
        foreach ($name in $cases.Keys) {
            $responses = $cases[$name].responses
            $fetch = {
                param($Uri, $Method)
                $requested.Add("$Method $Uri")
                $answer = $responses["$Method $Uri"]
                if ($null -eq $answer) { return @{ Status = 404; Content = [byte[]]@() } }
                if ($answer.ContainsKey('throw')) { throw $answer.throw }
                $bytes = [Convert]::FromBase64String($answer.content)
                # Atribuição direta: um "if" como expressão desmancharia o byte[] em object[].
                $content = $bytes
                if ($answer.text) { $content = [System.Text.Encoding]::UTF8.GetString($bytes) }
                @{ Status = [int]$answer.status; Content = $content }
            }.GetNewClosure()
            try {
                $published = Assert-NexoNewRelease -Version $cases[$name].version -PublicKeyPath (Join-Path $Dir 'public.pem') -Fetch $fetch
                $results[$name] = "OK $($published.Version)"
            } catch { $results[$name] = "ERRO $($_.Exception.Message)" }
        }
        $http = [ordered]@{}
        function Describe($response) {
            if ($null -eq $response.Content -or ($response.Content -is [byte[]] -and $response.Content.Length -eq 0) -or ([string]$response.Content).Length -eq 0) { return "$($response.Status)" }
            if ($response.Content -is [byte[]]) { return "$($response.Status) bytes $($response.Content -join ',')" }
            return "$($response.Status) texto $($response.Content)"
        }
        foreach ($probe in @(@('manifesto', 'latest.json', 'Get'), @('binario', 'binario', 'Get'), @('head-ausente', 'releases/1.7.0/nexo-app.zip', 'Head'),
                @('head-presente', 'releases/1.6.3/nexo-app.zip', 'Head'), @('erro', 'erro', 'Get'))) {
            try { $http[$probe[0]] = Describe (Invoke-NexoHttp ($Local + $probe[1]) $probe[2]) } catch { $http[$probe[0]] = "ERRO $($_.Exception.Message)" }
        }
        $closed = Get-Content -LiteralPath (Join-Path $Dir 'closed.txt') -Raw
        try { $http['fechada'] = Describe (Invoke-NexoHttp ($closed + 'latest.json') 'Get') } catch { $http['fechada'] = "ERRO $($_.Exception.Message)" }
        # Ponta a ponta: a trava com a leitura HTTP real, só trocando a origem fixa pelo servidor local.
        $origin = $NexoPublishedOrigin
        # Nomes distintos de $Local: no PowerShell, variáveis não diferenciam maiúsculas.
        $viaLocal = { param($Uri, $Method) Invoke-NexoHttp ($Uri.Replace($origin, $Local)) $Method }.GetNewClosure()
        foreach ($run in @(@('ponta-a-ponta', '1.7.0'), @('ponta-a-ponta-publicada', '1.6.3'))) {
            try { $http[$run[0]] = "OK $((Assert-NexoNewRelease -Version $run[1] -PublicKeyPath (Join-Path $Dir 'public.pem') -Fetch $viaLocal).Version)" }
            catch { $http[$run[0]] = "ERRO $($_.Exception.Message)" }
        }
        $viaPresent = { param($Uri, $Method) Invoke-NexoHttp ($Uri.Replace($origin, $Local).Replace('releases/1.7.1/', 'releases/1.6.3/')) $Method }.GetNewClosure()
        try { $http['ponta-a-ponta-pacote'] = "OK $((Assert-NexoNewRelease -Version '1.7.1' -PublicKeyPath (Join-Path $Dir 'public.pem') -Fetch $viaPresent).Version)" }
        catch { $http['ponta-a-ponta-pacote'] = "ERRO $($_.Exception.Message)" }
        # Sem -Fetch, a trava usa a leitura HTTP real testada acima (não dá para chamá-la sem rede, então confere o padrão).
        $fetchParameter = (Get-Command Assert-NexoNewRelease).ScriptBlock.Ast.Body.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'Fetch' }
        $http['fetch-padrao'] = [string]$fetchParameter.DefaultValue.Extent.Text
        $parse = [ordered]@{}
        foreach ($script in @($Guard, (Join-Path (Split-Path -Parent $Guard) 'Build-CloudRelease.ps1'))) {
            $errors = $null
            [System.Management.Automation.Language.Parser]::ParseFile($script, [ref]$null, [ref]$errors) | Out-Null
            $parse[(Split-Path -Leaf $script)] = @($errors).Count
        }
        [ordered]@{ results = $results; requested = $requested; parse = $parse; http = $http } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Dir 'out.json') -Encoding utf8
        """;
}
