using System.Globalization;
using System.Text;

namespace Nexo.Support;

/// <summary>Item da paleta Ctrl+K (#143): uma tela ("page:…") ou uma ação que já existe no Nexo ("action:…").</summary>
public sealed record PaletteCommand(string Id, string Title, string Kind, string Icon, string Keywords = "");

/// <summary>
/// Catálogo, filtro e teclado da paleta Ctrl+K (#143), sem dependência do WPF para o Nexo.Checks. A paleta só navega entre as
/// telas e dispara ações que já existem (novo atendimento, troca de tema, barra lateral); não cria comportamento novo.
/// </summary>
public static class CommandCatalog
{
    public static IReadOnlyList<PaletteCommand> Build(string currentTheme, bool sidebarCollapsed)
    {
        var other = ThemeCatalog.Normalize(currentTheme) == ThemeCatalog.Onyx ? ThemeCatalog.Glacial : ThemeCatalog.Onyx;
        return
        [
            new("page:home", "Início", "Tela", "home", "inicio resumo estado computador atalhos"),
            new("page:chat", "Assistente", "Tela", "chat", "conversa chat suporte ajuda"),
            new("page:health", "Saúde do PC", "Tela", "health", "monitor cpu memoria disco rede ocorrencias eventos temperatura"),
            new("page:history", "Atendimentos", "Tela", "history", "historico conversas"),
            new("page:tickets", "Encaminhamentos", "Tela", "tickets", "relatorio ti exportar"),
            new("page:tools", "Ferramentas", "Tela", "tools", "office otimizar limpeza temporarios"),
            new("page:settings", "Configurações", "Tela", "settings", "ollama modelo agente monitoramento aparencia preferencias"),
            new("page:audit", "Registro de ações", "Tela", "audit", "auditoria log metricas"),
            new("action:new-chat", "Novo atendimento", "Ação", "plus", "nova conversa novo chat"),
            new("action:theme", "Trocar o tema para " + ThemeCatalog.DisplayName(other), "Ação", "settings", "tema aparencia claro escuro onix glacial"),
            new("action:sidebar", sidebarCollapsed ? "Expandir a barra lateral" : "Recolher a barra lateral", "Ação", "sidebar", "menu lateral navegacao")
        ];
    }

    /// <summary>Minúsculas e sem acento: "Saúde" e "saude" encontram o mesmo item.</summary>
    public static string Fold(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text.Trim().Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) builder.Append(char.ToLowerInvariant(ch));
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Todos os termos precisam aparecer no título ou nas palavras-chave; o título que começa com o termo vem primeiro.</summary>
    public static IReadOnlyList<PaletteCommand> Filter(IReadOnlyList<PaletteCommand> items, string? query)
    {
        var terms = Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return items;
        return items.Select((item, index) => (item, index, title: Fold(item.Title), all: Fold(item.Title + " " + item.Kind + " " + item.Keywords)))
            .Where(entry => terms.All(entry.all.Contains))
            .OrderBy(entry => entry.title.StartsWith(terms[0], StringComparison.Ordinal) ? 0 : entry.title.Contains(terms[0], StringComparison.Ordinal) ? 1 : 2)
            .ThenBy(entry => entry.index)
            .Select(entry => entry.item).ToList();
    }

    /// <summary>
    /// Teclado da paleta: setas mudam a seleção (circular), Home e End vão às pontas, Enter executa o item selecionado e Esc fecha.
    /// Devolve a nova seleção (-1 sem itens), o item a executar e se a paleta fecha.
    /// </summary>
    public static (int Selected, string? Execute, bool Close) HandleKey(string key, int selected, IReadOnlyList<PaletteCommand> visible)
    {
        var count = visible.Count;
        var current = count == 0 ? -1 : Math.Clamp(selected, 0, count - 1);
        return key switch
        {
            "Down" => (count == 0 ? -1 : (current + 1) % count, null, false),
            "Up" => (count == 0 ? -1 : (current - 1 + count) % count, null, false),
            "Home" => (count == 0 ? -1 : 0, null, false),
            "End" => (count - 1, null, false),
            "Enter" => (current, current >= 0 ? visible[current].Id : null, current >= 0),
            "Escape" => (current, null, true),
            _ => (current, null, false)
        };
    }
}
