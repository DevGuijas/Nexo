using System.Text.Json;

namespace Nexo.Support;

public static class ToolResultPresentation
{
    public static (string State, string Detail, bool Warning) Describe(string label, string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ("Resultado indisponível", "A ferramenta não retornou um resultado válido.", true);
            if (root.TryGetProperty("denied", out var denied) && denied.ValueKind == JsonValueKind.True)
                return ("Recusado", "Você recusou a alteração. O Nexo interrompeu esta etapa.", true);
            if (!root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
                return ("Não concluído", Text(root, "error", "Não foi possível confirmar o resultado desta etapa."), true);

            if (label.Contains("hardware e drivers", StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("data", out var inventory) && inventory.ValueKind == JsonValueKind.Object)
            {
                var devices = inventory.TryGetProperty("Devices", out var found) && found.ValueKind == JsonValueKind.Array ? found.EnumerateArray().ToArray() : [];
                if (devices.Length == 0) return ("Nenhum dispositivo encontrado", "O Windows não encontrou um dispositivo presente nessa categoria. Nenhuma alteração foi feita.", false);
                var lines = devices.Take(8).Select(device =>
                {
                    var name = Text(device, "Name", "Dispositivo sem nome");
                    var version = Text(device, "DriverVersion", "não informada");
                    var provider = Text(device, "DriverProvider", "não informado");
                    return $"• {name}\n  Driver: {version} · Fabricante: {provider}";
                });
                var suffix = inventory.TryGetProperty("Truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True ? "\nA lista foi limitada; escolha o dispositivo antes de alterar drivers." : "";
                return (devices.Length == 1 ? "1 dispositivo identificado" : $"{devices.Length} dispositivos identificados", string.Join("\n", lines) + suffix, false);
            }

            if (label.Contains("Buscando drivers", StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("data", out var scan) && scan.ValueKind == JsonValueKind.String)
            {
                using var updates = JsonDocument.Parse(scan.GetString() ?? "[]");
                if (updates.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException();
                var offers = updates.RootElement.EnumerateArray().ToArray();
                if (offers.Length == 0) return ("0 ofertas compatíveis", "A fonte de atualizações configurada no Windows não ofereceu um driver compatível para o hardware identificado. Nenhum arquivo foi baixado e nenhum driver foi instalado.", false);
                var names = offers.Take(8).Select(offer => "• " + Text(offer, "Title", "Driver sem título") + " — " + Text(offer, "Target", "alvo não informado"));
                return (offers.Length == 1 ? "1 oferta compatível" : $"{offers.Length} ofertas compatíveis", "Ofertas encontradas para o hardware observado:\n" + string.Join("\n", names), false);
            }

            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                if (label.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) && Text(data, "Signature", "") == "Valid")
                    return ("Download verificado", "O instalador do NVIDIA App foi baixado do domínio oficial e sua assinatura digital foi validada. O driver ainda precisa ser escolhido, instalado e testado.", false);
                var verification = Text(data, "Verification", "");
                if (verification.Length > 0)
                {
                    var after = Text(data, "AfterVersion", "não informada");
                    return verification switch
                    {
                        "version_changed" => ("Driver instalado; teste funcional pendente", $"O Windows confirmou a nova versão {after}. Abra o aplicativo que apresentou o problema e teste o dispositivo.", false),
                        "pending_restart" => ("Reinicialização necessária", "O Windows pediu reinicialização. A correção ainda precisa ser confirmada após reiniciar e testar o dispositivo.", false),
                        _ => ("Verificação incompleta", "A instalação não comprovou uma nova versão do driver. Confira o dispositivo antes de considerar o problema resolvido.", true)
                    };
                }
            }
            return ("Executado", "A consulta ou operação retornou com sucesso. O resultado foi considerado nas próximas etapas do atendimento.", false);
        }
        catch (JsonException)
        {
            return ("Resultado indisponível", "Não foi possível interpretar o retorno desta etapa. Consulte o registro de ações para investigação técnica.", true);
        }
    }

    private static string Text(JsonElement value, string key, string fallback) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(property.GetString()) ? property.GetString()! : fallback;
}
