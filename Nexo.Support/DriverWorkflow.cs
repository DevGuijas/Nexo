using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nexo.Support;

// A bounded, evidence-first path for explicit driver requests. The model never
// selects an arbitrary installer or guesses which device an update belongs to.
public static class DriverWorkflow
{
    private static string Normalize(string value) => AgentProtocol.Normalize(value);
    public static bool Matches(string request)
    {
        if (SupportIntake.AsksRestartResume(request)) return false;
        var text = Normalize(request);
        return Regex.IsMatch(text, @"\b(drivers?|controladores?)\b")
            && Regex.IsMatch(text, @"\b(atualiz\w*|instal\w*|busc\w*|procur\w*|verific\w*|verifiq\w*)\b");
    }

    internal static string? Category(string request)
    {
        var text = Normalize(request);
        var matches = new List<string>();
        if (Regex.IsMatch(text, @"\b(video|grafica|gpu|display|tela)\b")) matches.Add("display");
        if (Regex.IsMatch(text, @"\b(audio|som|microfone|alto.falante)\b")) matches.Add("audio");
        if (Regex.IsMatch(text, @"\b(rede|wifi|wi-fi|ethernet|internet)\b")) matches.Add("network");
        if (Regex.IsMatch(text, @"\b(camera|webcam)\b")) matches.Add("camera");
        if (Regex.IsMatch(text, @"\b(bluetooth)\b")) matches.Add("bluetooth");
        if (Regex.IsMatch(text, @"\b(impressora|printer)\b")) matches.Add("printer");
        if (Regex.IsMatch(text, @"\b(armazenamento|disco|ssd|nvme)\b")) matches.Add("storage");
        if (Regex.IsMatch(text, @"\b(usb)\b")) matches.Add("usb");
        return matches.Count == 1 ? matches[0] : null;
    }

    internal static readonly TimeSpan DeviceQuestionLifetime = TimeSpan.FromMinutes(30);
    // O pedido de driver que a última mensagem do usuário retoma (#74), ou null. Retoma só quando a mensagem é apenas o nome
    // de um dos dispositivos mostrados na pergunta (regra da #70), a pergunta é a última resposta do assistente e foi feita
    // há no máximo 30 minutos. Qualquer outra mensagem ("deixa pra lá", "oi", uma dúvida, outro pedido) segue o caminho
    // normal. Retomar não escolhe o alvo: Run consulta o inventário de novo e confere a escolha pelo ID.
    // Usado pelo AgentService e pela MainWindow, que assim não exige o Ollama numa retomada.
    public static string? ResumedRequest(Conversation conversation, DateTime nowUtc)
    {
        if (conversation.PendingDriverChoice is not { Question.Length: > 0, Request.Length: > 0, Names.Count: > 0 } pending) return null;
        var age = nowUtc - pending.AskedUtc;
        if (age < TimeSpan.Zero || age > DeviceQuestionLifetime) return null;
        var lastUser = conversation.Entries.FindLastIndex(entry => entry.Role == "user");
        if (lastUser < 1) return null;
        var lastAssistant = conversation.Entries.FindLastIndex(lastUser - 1, entry => entry.Role == "assistant");
        if (lastAssistant < 0 || !string.Equals(conversation.Entries[lastAssistant].Text, pending.Question, StringComparison.Ordinal)) return null;
        return InvestigationEvidence.NamedCandidate(conversation.Entries[lastUser].Text, pending.Names.Select(name => (name, name))) != null ? pending.Request : null;
    }

    // choice: a resposta do usuário à pergunta de dispositivo (#74). Com ela, o alvo é o dispositivo do inventário
    // desta consulta que a resposta nomeia pela regra da #70 (InvestigationEvidence.NamedCandidate), identificado pelo ID;
    // nada da consulta anterior é reaproveitado. askedForDevice recebe os nomes mostrados quando a resposta é a pergunta.
    public static async Task<string> Run(string request, bool readOnly, Func<string, object, Task<JsonElement>> execute, CancellationToken token,
        string? choice = null, Action<IReadOnlyList<string>>? askedForDevice = null)
    {
        var category = Category(request);
        if (category == null) return "Para procurar um driver com segurança, diga qual componente apresenta o problema (por exemplo, placa de vídeo, áudio ou Bluetooth). Não alterei nenhum driver.";
        var allowInstall = !readOnly && Regex.IsMatch(Normalize(request), @"\b(atualiz\w*|instal\w*)\b");

        var inventory = await execute("get_hardware_inventory", new { category });
        if (!Succeeded(inventory)) return "Não consegui concluir o inventário desse hardware: " + Error(inventory) + " Nenhum driver foi alterado; encaminhe o resultado à TI se a falha persistir.";
        if (!inventory.TryGetProperty("data", out var inventoryData) || inventoryData.ValueKind != JsonValueKind.Object
            || !inventoryData.TryGetProperty("Devices", out var devices) || devices.ValueKind != JsonValueKind.Array)
            return "O inventário do Windows veio em formato inválido. Interrompi a busca; nenhum driver foi alterado.";
        if (inventoryData.TryGetProperty("Truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True)
            return "O inventário dessa categoria foi truncado. Não consigo escolher um alvo com segurança; informe o nome exato do dispositivo ou encaminhe à TI. Nenhum driver foi alterado.";
        if (devices.GetArrayLength() == 0) return "O Windows não encontrou um dispositivo presente dessa categoria. Não há alvo identificado para atualizar; nenhum driver foi alterado.";
        var target = devices[0];
        if (devices.GetArrayLength() > 1 || choice != null)
        {
            var candidates = devices.EnumerateArray().Select(device => (Key: Field(device, "Id"), Name: Field(device, "Name")))
                .Where(candidate => candidate.Key.Length > 0 && candidate.Name.Length > 0).ToArray();
            var chosenId = choice == null ? null : InvestigationEvidence.NamedCandidate(choice, candidates);
            var chosen = chosenId == null ? [] : devices.EnumerateArray().Where(device => Field(device, "Id") == chosenId).ToArray();
            if (chosen.Length == 1) target = chosen[0];
            else if (devices.GetArrayLength() == 1)
                // O dispositivo indicado sumiu entre os turnos: o que restou não foi escolhido pelo usuário.
                return "O dispositivo que você indicou não aparece mais nesta consulta; agora o Windows lista só " + InvestigationEvidence.Quoted(Field(target, "Name"))
                    + ". Se o problema é nele, peça de novo a atualização do driver. Nenhum driver foi alterado.";
            else if (candidates.Length == 0)
                return "Encontrei mais de um dispositivo possível, mas o Windows não informou nome e identificação deles. Não consigo escolher um alvo com segurança; encaminhe o caso à TI. Nenhum driver foi alterado.";
            else
            {
                var shown = candidates.Select(candidate => candidate.Name).Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
                askedForDevice?.Invoke(shown.Take(6).ToArray());
                return "Encontrei mais de um dispositivo possível: " + string.Join("; ", shown.Take(6).Select(name => InvestigationEvidence.Quoted(name)))
                    + (shown.Length > 6 ? " e mais " + (shown.Length - 6) : "") + ". Qual deles apresenta o problema? "
                    + InvestigationEvidence.ReplyWithName + " Nenhum driver foi alterado.";
            }
        }

        var targetName = Field(target, "Name");
        var targetId = Field(target, "Id");
        var version = Field(target, "DriverVersion");
        var provider = Field(target, "DriverProvider");
        var ids = Ids(target, "HardwareIds").Concat(Ids(target, "CompatibleIds")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (targetId.Length == 0 || ids.Count == 0)
            return "Identifiquei " + (targetName.Length > 0 ? InvestigationEvidence.Quoted(targetName) : "o dispositivo") + ", mas o Windows não forneceu IDs suficientes para associar uma oferta de driver com segurança. Nenhum driver foi alterado; encaminhe o caso à TI.";

        var observed = "Identifiquei " + InvestigationEvidence.Quoted(targetName) + (version.Length > 0 ? " (driver " + version + (provider.Length > 0 ? ", provedor " + provider : "") + ")" : "");
        var inspectPackage = Regex.IsMatch(Normalize(request), @"\b(inf|pacote|repositorio)\b");
        if (inspectPackage)
        {
            try
            {
                var installed = await execute("get_driver_store", new { device_id = targetId });
                var data = installed.TryGetProperty("data", out var payload) ? payload : default;
                var package = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("package", out var found) ? found : default;
                var inf = package.ValueKind == JsonValueKind.Object ? Field(package, "InfName") : "";
                observed += inf.Length > 0 ? "; INF instalado: " + inf : "; INF instalado não confirmado";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { observed += "; consulta do INF indisponível"; }
        }
        if (inspectPackage && !Regex.IsMatch(Normalize(request), @"\b(atualiz\w*|instal\w*|busc\w*|procur\w*|ofertas?|versao nova)\b"))
            return observed + ". Consulta local concluída; não procurei ofertas online nem alterei drivers.";
        var scan = await execute("scan_driver_updates", new { });
        if (!Succeeded(scan)) return observed + ". A busca online não terminou: " + Error(scan) + " Nenhum driver foi alterado; tente novamente mais tarde ou encaminhe à TI.";
        if (!scan.TryGetProperty("data", out var raw) || raw.ValueKind != JsonValueKind.String)
            return observed + ". A lista de ofertas veio em formato inválido. Nenhum driver foi alterado.";
        JsonDocument offersDocument;
        try { offersDocument = JsonDocument.Parse(raw.GetString() ?? ""); }
        catch (JsonException) { return observed + ". A lista de ofertas veio em formato inválido. Nenhum driver foi alterado."; }
        using (offersDocument)
        {
            if (offersDocument.RootElement.ValueKind != JsonValueKind.Array)
                return observed + ". A lista de ofertas veio em formato inválido. Nenhum driver foi alterado.";
            var offers = offersDocument.RootElement.EnumerateArray().ToArray();
            if (offers.Any(offer => offer.ValueKind != JsonValueKind.Object || !ids.Contains(Field(offer, "HardwareID"))
                || !string.Equals(Field(offer, "Target"), targetName, StringComparison.OrdinalIgnoreCase)))
                return observed + ". Uma oferta não corresponde exatamente ao dispositivo observado. Interrompi a instalação; nenhum driver foi alterado.";
            if (offers.Length == 0)
            {
                if (category == "display" && targetId.StartsWith("PCI\\VEN_10DE&", StringComparison.OrdinalIgnoreCase)
                    && ids.Any(id => id.StartsWith("PCI\\VEN_10DE&", StringComparison.OrdinalIgnoreCase)))
                {
                    token.ThrowIfCancellationRequested();
                    var vendor = await execute("find_official_nvidia_driver", new { });
                    if (!Succeeded(vendor))
                        return observed + ". O Windows não ofereceu driver compatível. A consulta oficial da NVIDIA falhou: " + Error(vendor) + " Nenhum driver foi alterado; encaminhe à TI se precisar de ajuda.";
                    if (!vendor.TryGetProperty("data", out var vendorData) || vendorData.ValueKind != JsonValueKind.Object
                        || !vendorData.TryGetProperty("Available", out var available) || available.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        return observed + ". A consulta da NVIDIA retornou dados inválidos. Nenhum driver foi alterado.";
                    if (available.ValueKind == JsonValueKind.False)
                        return observed + ". Nem a fonte configurada no Windows nem a NVIDIA ofereceram uma versão mais nova compatível agora. Nenhum driver foi alterado.";
                    var driverId = Field(vendorData, "DriverId");
                    var offeredVersion = Field(vendorData, "Version");
                    if (!Regex.IsMatch(driverId, @"^\d{4,8}$") || !Regex.IsMatch(offeredVersion, @"^\d{3}\.\d{2}$")
                        || !string.Equals(Field(vendorData, "Target"), targetName, StringComparison.OrdinalIgnoreCase))
                        return observed + ". A oferta NVIDIA não corresponde ao dispositivo observado. Nenhum driver foi alterado.";
                    if (!allowInstall)
                        return observed + ". Encontrei o driver NVIDIA " + offeredVersion + " no catálogo oficial. Fiz apenas a consulta; nenhum driver foi alterado.";
                    token.ThrowIfCancellationRequested();
                    var installedVendor = await execute("install_official_nvidia_driver", new { driver_id = driverId });
                    if (installedVendor.TryGetProperty("denied", out var refused) && refused.ValueKind == JsonValueKind.True)
                        return observed + ". Você recusou o download e a instalação do driver NVIDIA " + offeredVersion + ". Nenhum driver foi alterado.";
                    // Com a instalação relatada e não confirmada, o erro já orienta a conferir o Gerenciador de Dispositivos;
                    // a nota de áudio, quando existe, já termina em "encaminhe à TI".
                    if (!Succeeded(installedVendor) && installedVendor.TryGetProperty("installationReported", out var reported) && reported.ValueKind == JsonValueKind.True)
                    {
                        var reportedError = Error(installedVendor);
                        return observed + ". A instalação do driver NVIDIA " + offeredVersion + " não foi confirmada: " + reportedError
                            + (reportedError.Contains("encaminhe à TI", StringComparison.Ordinal) ? "" : " Se o problema continuar, encaminhe à TI.");
                    }
                    if (!Succeeded(installedVendor))
                        return observed + ". A instalação do driver NVIDIA " + offeredVersion + " não foi confirmada: " + Error(installedVendor) + " Confira o Gerenciador de Dispositivos ou encaminhe à TI.";
                    if (!installedVendor.TryGetProperty("data", out var installedData) || installedData.ValueKind != JsonValueKind.Object)
                        return observed + ". O instalador NVIDIA não retornou verificação suficiente. Não considero a correção concluída.";
                    var audioNote = new[] { "warning", "audioTest", "audioInfo" }.Select(key => Field(installedVendor, key)).FirstOrDefault(note => note.Length > 0) ?? "";
                    // Só a versão que corresponde à oferta é dada como instalada; versão diferente chega como erro com installationReported.
                    var installerWarning = Field(installedVendor, "installerWarning");
                    return Field(installedData, "Verification") switch
                    {
                        "expected_version_confirmed" => observed + ". Instalei o driver NVIDIA " + offeredVersion + " e o Windows agora informa a versão " + Field(installedData, "AfterVersion") + ", que corresponde a essa oferta. Teste a placa de vídeo no aplicativo em que percebeu a falha; a resolução funcional ainda precisa da sua confirmação.",
                        "pending_restart" => observed + (Field(installedData, "RestartRequestedBy") == "windows"
                            ? ". O Windows pediu reinicialização para concluir a instalação do driver NVIDIA " + offeredVersion + " (a placa está com código 14)."
                            : ". O instalador NVIDIA solicitou reinicialização.") + " Salve seu trabalho, reinicie manualmente e teste a placa de vídeo. A correção ainda não está confirmada.",
                        _ => observed + ". O instalador NVIDIA terminou, mas o Windows não confirmou a versão oferecida. Não considero a correção concluída; confira o Gerenciador de Dispositivos ou encaminhe à TI."
                    } + (installerWarning.Length == 0 ? "" : " " + installerWarning) + (audioNote.Length == 0 ? "" : " " + audioNote);
                }
                return observed + ". A fonte de atualizações configurada no Windows não ofereceu driver compatível agora. Nenhuma alteração foi feita. Se o problema continuar, descreva o sintoma ou encaminhe este atendimento à TI.";
            }
            if (offers.Length > 1)
                return observed + ". Encontrei " + offers.Length + " ofertas compatíveis; não escolhi uma versão por tentativa. Nenhum driver foi alterado. Encaminhe as ofertas à TI para avaliação.";
            var offer = offers[0];
            var updateId = Field(offer, "Id");
            if (!Guid.TryParse(updateId, out _)) return observed + ". A oferta não tem identificação válida. Nenhum driver foi alterado.";
            var title = Field(offer, "Title");
            if (!allowInstall)
                return observed + ". Encontrei a oferta " + InvestigationEvidence.Quoted(title) + " para esse hardware na fonte configurada no Windows. Fiz apenas a consulta; nenhum driver foi alterado.";

            token.ThrowIfCancellationRequested();
            var installed = await execute("install_driver_update", new { update_id = updateId });
            if (installed.TryGetProperty("denied", out var denied) && denied.ValueKind == JsonValueKind.True)
                return observed + ". Você recusou a instalação de " + InvestigationEvidence.Quoted(title) + ". Nenhum driver foi instalado nesta etapa.";
            if (!Succeeded(installed))
                return observed + ". A instalação de " + InvestigationEvidence.Quoted(title) + " não foi confirmada: " + Error(installed) + " Confira o Gerenciador de Dispositivos antes de tentar novamente ou encaminhe à TI.";
            if (!installed.TryGetProperty("data", out var result) || result.ValueKind != JsonValueKind.Object)
                return observed + ". O Windows não retornou verificação suficiente após a instalação. Não considero o problema resolvido; confira o dispositivo e encaminhe à TI.";
            var verification = Field(result, "Verification");
            var afterVersion = Field(result, "AfterVersion");
            if (verification == "pending_restart")
                return observed + ". O Windows relatou a instalação de " + InvestigationEvidence.Quoted(title) + ", mas requer reinicialização. Salve seus arquivos, reinicie manualmente e depois teste o dispositivo. A correção ainda não está confirmada.";
            if (verification == "version_changed")
                return observed + ". O Windows instalou " + InvestigationEvidence.Quoted(title) + " e a versão observada passou para " + afterVersion + ". Teste o dispositivo no aplicativo em que percebeu a falha e me diga se funcionou; a resolução funcional ainda não está confirmada.";
            return observed + ". O Windows relatou a instalação, mas a nova versão não foi confirmada. Não considero o problema resolvido; confira o Gerenciador de Dispositivos e encaminhe à TI se necessário.";
        }
    }

    private static bool Succeeded(JsonElement result) => result.ValueKind == JsonValueKind.Object
        && result.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True;
    private static string Error(JsonElement result) => result.ValueKind == JsonValueKind.Object
        && result.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
        ? error.GetString() ?? "resultado não confirmado." : "resultado não confirmado.";
    private static string Field(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
    private static IEnumerable<string> Ids(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(key, out var items) && items.ValueKind == JsonValueKind.Array
        ? items.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString() ?? "").Where(item => item.Length > 0)
        : [];
}
