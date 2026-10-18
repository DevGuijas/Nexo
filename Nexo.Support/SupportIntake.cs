using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Nexo.Support;

// A read-only starting observation, not a fixed repair plan.
public static class SupportIntake
{
    public static HashSet<string> IncidentTopics(string request)
    {
        var text = new string(request.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).Select(char.ToLowerInvariant).ToArray());
        var topics = new HashSet<string>();
        if (Regex.IsMatch(text, @"\b(internet|rede|conexao|network|wireless|wifi|wi-fi|ethernet|dns|dhcp|ipv4|gateway|adaptador de rede|cache dns|wlan)\b")) topics.Add("network");
        if (Regex.IsMatch(text, @"\b(camera|webcam|imagem da camera)\b")) topics.Add("camera");
        if (Regex.IsMatch(text, @"\b(audio|media|sound|som|microfone|fone|alto.falante|audiosrv)\b")) topics.Add("audio");
        if (Regex.IsMatch(text, @"\b(bluetooth|bthserv)\b")) topics.Add("bluetooth");
        if (Regex.IsMatch(text, @"\b(impressoras?|imprimir|impressao|printer|print queue|fila de impressao|spooler)\b")) topics.Add("printer");
        if (Regex.IsMatch(text, @"\b(lentidao|desempenho|memoria|disco|processos)\b")) topics.Add("system");
        return topics;
    }

    public static string? InitialTopic(string request)
    {
        var text = new string(request.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).Select(char.ToLowerInvariant).ToArray());
        if (!Regex.IsMatch(text, @"\b(nao\s+(funciona|aparece|conecta|imprime|consigo|liga)|sem\s+(som|audio|imagem|camera|microfone|bluetooth)|sumiu|desapareceu|preso|presos|presa|travou|travada|problema|falha|parou|para quando|desconectar e conectar)\b")) return null;
        if (Regex.IsMatch(text, @"\b(camera|webcam)\b")) return "camera";
        if (Regex.IsMatch(text, @"\b(impressoras?|imprimir|impressao)\b")) return "printer";
        if (Regex.IsMatch(text, @"\b(som|audio|microfone|fone)\b")) return "audio";
        if (text.Contains("bluetooth")) return "bluetooth";
        return null;
    }
}
