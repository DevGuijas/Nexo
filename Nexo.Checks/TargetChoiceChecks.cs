using System.Text.Json;
using Nexo.Support;

// Escolha do alvo quando há mais de uma impressora ou dispositivo (#70). Só escolhe a resposta que é apenas o nome de um
// candidato, inteiro ou cortado como a pergunta o mostra, com no máximo um prefixo curto ("é a", "foi na", "sim,"), aspas e
// ponto final. Qualquer outra palavra faz o Nexo perguntar de novo: uma lista de recusas não fecha (quatro revisões do @qa).
internal static class TargetChoiceChecks
{
    private static readonly string[] Two = ["Canon Sala", "HP LaserJet Recepção"];
    private static readonly string[][] Sets = [Two, ["HP LaserJet", "HP LaserJet Recepção"], ["Fone de ouvido USB", "Microfone Fifine USB"],
        ["Microfone (Realtek(R) Audio)", "Microfone (Realtek(R) Audio) - Painel frontal"], ["Canon (Sala)", "Canon (Sala) 2º andar"], ["Canon Sala 2", "Canon Sala 20"],
        ["Canon Sala", "Canon Sala (Cópia 1)"], ["Impressora Recepção", "Impressora Recepção Fiscal"], ["HP LaserJet", "HP LaserJet Recepção", "Canon Recepção"]];

    // As 201 frases que, nas revisões do @qa (qa70r3 e qa70r4), autorizaram um alvo que o usuário recusou ou não escolheu,
    // na base, no b569616 ou no d9b4832.
    private static readonly string[] Refused = [
        "Canon Sala (não)", "Canon Sala - não", "Canon Sala jamais",
        "Canon Sala jamais HP LaserJet Recepção talvez", "Canon Sala nem pensar", "Canon Sala nunca",
        "Canon Sala não HP LaserJet Recepção sim", "Canon Sala – não, a outra", "Canon Sala — não",
        "Canon Sala, nem a pau", "Canon Sala? Negativo", "Canon Sala? Nunca usei",
        "Canon Sala? Não a escolhi", "HP LaserJet Recepção não HP LaserJet sim", "Não apague ainda, a Canon Sala tem documentos importantes",
        "Não limpe, por favor, a fila da Canon Sala", "Não mexa, por favor, na Canon Sala", "Não mexe, a Canon Sala",
        "a Canon Sala de jeito nenhum", "a Canon Sala está funcionando, é a outra", "a Canon Sala está perfeita",
        "a Canon Sala eu nem uso", "a Canon Sala eu não usei", "a Canon Sala funciona, a outra não",
        "a Canon Sala imprime de boa", "a Canon Sala imprime normal, o problema é a outra", "a Canon Sala imprime, o problema é a outra",
        "a Canon Sala imprimiu certinho, o problema é a outra", "a Canon Sala já arrumei", "a Canon Sala já foi resolvida",
        "a Canon Sala já resolvi, falta a outra", "a Canon Sala já voltou", "a Canon Sala não a HP LaserJet Recepção",
        "a Canon Sala não está com problema", "a Canon Sala não mesmo", "a Canon Sala não tem nada a ver",
        "a Canon Sala não tem problema", "a Canon Sala tá 100%", "a Canon Sala tá de boa, é a outra",
        "a Canon Sala tá ok", "a Canon Sala tá ok, é a Canon Sala", "a Canon Sala tá tranquila",
        "a Canon Sala é outra história", "a Canon Sala, aquela do corredor, não", "a Canon Sala, essa não",
        "a Canon Sala, não a limpe", "a Canon Sala, não a reinicie", "a Canon Sala, não a toque",
        "a Canon Sala, não a use", "a Canon Sala, não o faça", "a Canon Sala. A do corredor. Não",
        "a Canon Sala? nem a pau", "a Canon Sala? nunca a usei", "a Canon Sala? não a conheço",
        "a Canon Sala? não a uso", "a Canon Sala? não na minha frente", "a Canon Sala? que nada",
        "a HP LaserJet Recepção - não", "a HP LaserJet Recepção jamais", "a HP LaserJet Recepção já resolvi",
        "a HP LaserJet Recepção tá de boa", "a HP LaserJet Recepção, por favor, não", "a HP LaserJet Recepção? nem a pau",
        "a HP LaserJet Recepção? não a use", "a HP LaserJet Recepção? que nada", "a da recepção. HP LaserJet",
        "deixa a Canon Sala em paz, é a outra", "deixa a Canon Sala quieta", "dispensa a Canon Sala",
        "esquece a Canon Sala, é a outra", "esquece a HP LaserJet Recepção", "esquece o Microfone (Realtek(R) Audio) - Painel frontal",
        "foi na Canon (Sala) (2º andar)", "foi na Canon (Sala) 2o andar", "foi na Canon (Sala) 2° andar",
        "foi na Canon (Sala) do 2º andar", "foi na Canon (Sala), 2º andar", "foi na Canon (Sala), a do 2º andar",
        "foi na Canon (Sala), no segundo andar", "foi na Canon Sala 2 0", "foi na Canon Sala cópia",
        "foi na Canon Sala, a cópia", "foi na HP LaserJet 'Recepção'", "foi na HP LaserJet / a da recepção",
        "foi na HP LaserJet — a da recepção", "foi na HP LaserJet! a da recepção", "foi na HP LaserJet, a impressora da recepção",
        "foi na HP LaserJet, a lá da recepção", "foi na HP LaserJet, a recepção", "foi na HP LaserJet, aquela da recepção",
        "foi na HP LaserJet, da recepção", "foi na HP LaserJet, esta da recepção", "foi na HP LaserJet, na recepção",
        "foi na HP LaserJet, perto da recepção", "foi na HP LaserJet, que fica na recepção", "foi na HP LaserJet/recepção",
        "foi na HP LaserJet: a da recepção", "foi na HP LaserJet; a da recepção", "foi na HP LaserJet? a da recepção",
        "foi na HP LaserJet… da recepção", "foi na Impressora Recepção, a fiscal", "foi na Impressora Recepção, a fiscal não",
        "foi na da recepção, a HP LaserJet", "ignora a Canon Sala", "jamais a Canon Sala",
        "na Canon Sala eu não imprimi", "na Canon Sala não mandei nada", "na Canon Sala, não a mexa",
        "na Canon Sala, por favor, não", "nada de Canon Sala", "nem mexi na Canon Sala",
        "nem é a Canon Sala", "não Canon Sala HP LaserJet Recepção", "não abre a fila da Canon Sala",
        "não abre o Microfone Fifine USB", "não apague a fila da Canon Sala", "não ative o Microfone (Realtek(R) Audio)",
        "não cancele nada na Canon Sala", "não consigo achar defeito na Canon Sala", "não consigo ver problema na Canon Sala",
        "não deve ser a Canon Sala", "não enviei pra Canon Sala", "não escolhi a Canon Sala",
        "não estava imprimindo na Canon Sala", "não falei da Canon Sala", "não foi não na Canon Sala",
        "não habilite o Microfone (Realtek(R) Audio)", "não imprimi na Canon Sala, foi na outra", "não imprimi nada na Canon Sala",
        "não liga a Canon Sala", "não liga o Microfone (Realtek(R) Audio) - Painel frontal", "não liga o Microfone Fifine USB",
        "não liga o Microfone Fifine USB, ele chia", "não liga para a Canon Sala", "não liga pra Canon Sala, é a outra",
        "não mandei imprimir na Canon Sala", "não mandei nada pra Canon Sala", "não mandei o documento pra Canon Sala",
        "não mexi na Canon Sala", "não mexi na HP LaserJet Recepção", "não não é a Canon Sala",
        "não pedi a Canon Sala", "não pode ser a Canon Sala", "não precisa mexer na Canon Sala",
        "não quero que limpe a fila da Canon Sala", "não quero que mexa na Canon Sala", "não reinicie a Canon Sala",
        "não sou eu que uso a Canon Sala", "não tem nada a ver com a Canon Sala, é a outra", "não tentei imprimir na Canon Sala",
        "não toquei na Canon Sala", "não use a Canon Sala", "não utilize a Canon Sala",
        "não é a (Canon Sala)", "não é a *Canon Sala*", "não é a Canon Recepção",
        "não é a Canon Sala 2", "não é a Canon Sala de jeito nenhum", "não é a [Canon Sala]",
        "não é a `Canon Sala`", "não é a fila da impressora Canon Sala", "não é a tal da Canon Sala",
        "não é a «Canon Sala»", "não é bem a Canon Sala", "não é exatamente a Canon Sala",
        "não é nem a Canon Sala", "não é pra mexer na Canon Sala", "não, não, não, a Canon Sala não",
        "o Microfone (Realtek(R) Audio) - Painel frontal - não", "o Microfone (Realtek(R) Audio) - Painel frontal tá de boa", "o Microfone (Realtek(R) Audio) - Painel frontal tá funcionando",
        "o Microfone (Realtek(R) Audio) - Painel frontal? não o use", "o da frente, é o Microfone (Realtek(R) Audio)", "o problema não está na Canon Sala",
        "o problema não é com a Canon Sala", "pode pular a Canon Sala", "por favor não limpe a fila da Canon Sala",
        "pula a Canon Sala", "pula a HP LaserJet Recepção", "pula o Microfone (Realtek(R) Audio) - Painel frontal",
        "salvo a Canon Sala", "sem mexer na Canon Sala, por favor", "sem tocar na Canon Sala",
        "tira da cabeça a Canon Sala", "tira da jogada a Canon Sala", "tira da lista a Canon Sala",
        "tira de lado a Canon Sala", "tira essa Canon Sala", "tira essa Canon Sala daí",
        "é o Microfone (Realtek(R) Audio) / o da frente", "é o Microfone (Realtek(R) Audio) [painel frontal]", "é o Microfone (Realtek(R) Audio)! O da frente",
        "é o Microfone (Realtek(R) Audio), da frente", "é o Microfone (Realtek(R) Audio), este da frente", "é o Microfone (Realtek(R) Audio), na frente",
        "é o Microfone (Realtek(R) Audio), o frontal", "é o Microfone (Realtek(R) Audio), o lá da frente", "é o Microfone (Realtek(R) Audio), o microfone da frente",
        "é o Microfone (Realtek(R) Audio), um da frente", "é o Microfone (Realtek(R) Audio)? o da frente", "é o Microfone (Realtek(R) Audio)… o da frente"
    ];

    internal static void Run(Action<bool, string> assert)
    {
        Accepted(assert);
        ShownLongName(assert);
        ContainedName(assert);
        StillAsks(assert);
        StrangeCharacters(assert);
        Identity(assert);
        PreviousFailures(assert);
    }

    private static void Accepted(Action<bool, string> assert)
    {
        var printers = Printers(Two);
        string[] canon = ["Canon Sala", "canon sala", "CANON SALA.", "Canon Sala!", "  Canon  Sala  ", "é a Canon Sala", "e a canon sala", "é na Canon Sala", "foi na Canon Sala",
            "foi a Canon Sala", "na Canon Sala", "a Canon Sala", "sim, a Canon Sala", "sim a Canon Sala", "sim, foi na Canon Sala.", "\"Canon Sala\"", "“Canon Sala”", "é a “Canon Sala”.", "'Canon Sala'"];
        foreach (var reply in canon)
            assert(Chooses(printers, Two, "Canon Sala", reply), "Impressora: resposta só com o nome escolhe: " + reply);
        foreach (var reply in new[] { "HP LaserJet Recepcao", "é a hp laserjet recepção", "foi na HP LASERJET RECEPÇÃO." })
            assert(Chooses(printers, Two, "HP LaserJet Recepção", reply), "Impressora: caixa e acento não mudam a escolha: " + reply);
        assert(printers.AmbiguityQuestion("printer", "Canon Sala") is null && printers.AmbiguityQuestion("printer", "a impressora não imprime") is { } question
            && question.EndsWith("Qual delas recebeu o documento que falhou? Responda só com o nome, como está escrito acima.", StringComparison.Ordinal),
            "Impressora: a pergunta pede a resposta só com o nome e some quando o nome chega");

        string[] mics = ["Fone de ouvido USB", "Microfone Fifine USB"];
        var audio = Audio(mics);
        foreach (var reply in new[] { "Microfone Fifine USB", "é o Microfone Fifine USB", "o microfone fifine usb.", "no Microfone Fifine USB" })
            assert(AudioChooses(audio, 2, 1, reply), "Áudio com código 22: resposta só com o nome escolhe: " + reply);
        assert(audio.AmbiguityQuestion("audio", "o microfone não funciona")?.EndsWith("Qual deles apresenta a falha no aplicativo? Responda só com o nome, como está escrito acima.", StringComparison.Ordinal) == true,
            "Áudio: a pergunta pede a resposta só com o nome");

        // O mesmo caso de SecurityChecks, que no Mac não roda: com duas câmeras, a frase que cita o nome pergunta; o nome sozinho escolhe.
        var cameras = new InvestigationEvidence();
        cameras.Observe("diagnose_device", "c", JsonSerializer.SerializeToElement(new { topic = "camera" }), WindowsTools.Ok(new { count = 2, devices = new[] {
            new Device("Câmera interna", "PCI\\CAMERA_TEST", 22, "Error"), new Device("Webcam USB", "USB\\WEBCAM_TEST", 22, "Error") } }));
        assert(!cameras.DeviceTargetUnambiguous("USB\\WEBCAM_TEST", "A Webcam USB não aparece no Teams.", true) && cameras.AmbiguityQuestion("camera", "A Webcam USB não aparece no Teams.") is not null
            && cameras.DeviceTargetUnambiguous("USB\\WEBCAM_TEST", "Webcam USB", true) && !cameras.DeviceTargetUnambiguous("PCI\\CAMERA_TEST", "Webcam USB", true)
            && cameras.DeviceTargetUnambiguous("PCI\\CAMERA_TEST", "é a câmera interna.", true) && cameras.AmbiguityQuestion("camera", "Webcam USB") is null,
            "Câmera: com duas câmeras, só a resposta com o nome escolhe; a frase que cita o nome pergunta de novo");

        string[] article = ["A Impressora Sala", "Impressora Sala"];
        var withArticle = Printers(article);
        assert(PrintersAsk(withArticle, article, "A Impressora Sala") && Chooses(withArticle, article, "A Impressora Sala", "“A Impressora Sala”")
            && Chooses(withArticle, article, "Impressora Sala", "Impressora Sala"),
            "Impressora: nome que começa com artigo e também é outro nome sem o artigo pergunta de novo; entre aspas, escolhe");
    }

    private static void ShownLongName(Action<bool, string> assert)
    {
        var longName = "Impressora do segundo andar " + new string('x', 50);
        string[] names = [longName, "Canon Sala"];
        var printers = Printers(names);
        var question = printers.AmbiguityQuestion("printer", "a impressora não imprime") ?? "";
        var shown = question[(question.IndexOf("“Impressora", StringComparison.Ordinal) + 1)..question.IndexOf('…')];
        foreach (var reply in new[] { shown + "…", "foi na " + shown + "…", shown, "é a " + shown + "...", longName })
            assert(Chooses(printers, names, longName, reply), "Impressora: nome cortado como a pergunta mostra escolhe: " + reply);
        assert(PrintersAsk(printers, names, shown[..^5]) && PrintersAsk(printers, names, "foi na " + shown + "… do segundo andar"),
            "Impressora: pedaço menor do nome cortado ou palavra depois dele faz perguntar de novo");
        string[] twins = [longName + " A", longName + " B"];
        var pair = Printers(twins);
        assert(PrintersAsk(pair, twins, "foi na " + shown + "…") && Chooses(pair, twins, longName + " A", longName + " A"),
            "Impressora: nome cortado que serve para duas continua ambíguo; o nome inteiro escolhe");
    }

    private static void ContainedName(Action<bool, string> assert)
    {
        string[] prefix = ["HP LaserJet", "HP LaserJet Recepção"];
        var printers = Printers(prefix);
        assert(Chooses(printers, prefix, "HP LaserJet", "HP LaserJet") && Chooses(printers, prefix, "HP LaserJet Recepção", "foi na HP LaserJet Recepção"),
            "Impressora: nome contido em outro escolhe só quando é a resposta inteira");
        foreach (var reply in new[] { "HP LaserJet da recepção", "a HP LaserJet não imprime", "HP LaserJet Recepção não", "foi na HP LaserJet, na recepção", "HP LaserJet Recepção 2" })
            assert(PrintersAsk(printers, prefix, reply), "Impressora: nome contido com outras palavras pergunta de novo: " + reply);
        string[] devices = ["Microfone (Realtek(R) Audio)", "Microfone (Realtek(R) Audio) - Painel frontal"];
        var audio = Audio(devices);
        assert(AudioChooses(audio, 2, 0, "Microfone (Realtek(R) Audio)") && AudioChooses(audio, 2, 1, "é o “Microfone (Realtek(R) Audio) - Painel frontal”"),
            "Áudio: nome com parênteses escolhe quando é a resposta inteira");
        assert(AudioAsks(audio, 2, "é o Microfone (Realtek(R) Audio) da frente") && AudioAsks(audio, 2, "Microfone (Realtek(R) Audio) - Painel"),
            "Áudio: nome contido com outras palavras pergunta de novo");
    }

    private static void StillAsks(Action<bool, string> assert)
    {
        var printers = Printers(Two);
        string[] replies = ["", "   ", "sim", "não", "é a", "foi na", "a outra", "essa", "Canon Sala?", "Canon Sala e HP LaserJet Recepção", "Canon Sala, HP LaserJet Recepção",
            "Canon Sala ou HP LaserJet Recepção", "não, a Canon Sala", "a Canon Sala não", "não é a Canon Sala", "é a Canon Sala, não a HP LaserJet Recepção", "acho que é a Canon Sala",
            "Canon Sala acho", "a Canon Sala imprime normal", "foi na Canon Sala ontem", "Canon", "Sala", "Canon Sal", "Canon Salas", "CanonSala", "a a Canon Sala", "é é a Canon Sala"];
        foreach (var reply in replies)
            assert(PrintersAsk(printers, Two, reply), "Impressora: resposta que não é só um nome pergunta de novo: " + reply);
        string[] same = ["Impressora Recepção", "Impressora Recepcao"];
        assert(PrintersAsk(Printers(same), same, "Impressora Recepção") && PrintersAsk(Printers(same), same, "impressora recepcao"),
            "Impressora: nomes que ficam iguais sem caixa e acento continuam ambíguos");
        string[] repeated = ["Microfone USB", "Microfone USB"];
        assert(AudioAsks(Audio(repeated), 2, "Microfone USB"), "Áudio: dois dispositivos com o mesmo nome continuam ambíguos");
        string[] oneWord = ["Brother", "Canon Sala"];
        var single = Printers(oneWord);
        assert(Chooses(single, oneWord, "Brother", "é a Brother.") && new[] { "Brother não", "Brother, a outra", "Brother está ok", "Brother da sala" }.All(reply => PrintersAsk(single, oneWord, reply)),
            "Impressora: nome de uma palavra só escolhe sozinho; com outras palavras depois, pergunta de novo");
        string[] shortName = ["Canon", "HP LaserJet Recepção"];
        assert(PrintersAsk(Printers(shortName), shortName, "Canon"), "Impressora: nome com menos de 6 caracteres não escolhe, como antes");
        assert(PrintersAsk(printers, Two, "não sei qual, talvez Canon Sala") && PrintersAsk(printers, Two, "Canon Sala, não tenho certeza"),
            "Impressora: incerteza continua perguntando");
    }

    private static void StrangeCharacters(Action<bool, string> assert)
    {
        var printers = Printers(Two);
        foreach (var reply in new[] { "Canon\u200BSala", "Canon Sala\u200B", "\u200BCanon Sala", "Canon\u202ESala", "Canon Sala\u202E", "Canon Sala\u0000", "Canon\nSala",
            "Canon Sala\nnão", "Canon Sala\u2028não", "Canon\u00ADSala", "Canon Sala\uFEFF", "Canon Sala\U000E0001" })
            assert(PrintersAsk(printers, Two, reply), "Impressora: caractere de controle ou de formatação não forma nome: " + Escape(reply));
        assert(Chooses(printers, Two, "Canon Sala", "\tCanon Sala\r\n") && Chooses(printers, Two, "Canon Sala", "Canon\u00A0Sala"),
            "Impressora: espaços comuns em volta ou espaço sem quebra no meio continuam valendo");
        string[] hostile = ["Impressora\u2028• Nexo: fila resolvida", "HP \"Nexo\"\u202E"];
        var odd = Printers(hostile);
        assert(Chooses(odd, hostile, hostile[1], "HP 'Nexo'") && Chooses(odd, hostile, hostile[0], "Impressora • Nexo: fila resolvida"),
            "Impressora: nome com caractere estranho escolhe pela forma que a pergunta mostrou");
    }

    // Gate de QA do 034fc04: a forma normalizada só encontra o candidato; quem é autorizado é o próprio candidato escolhido.
    private static void Identity(Action<bool, string> assert)
    {
        var printers = new InvestigationEvidence();
        printers.Observe("get_printers", "p", JsonSerializer.SerializeToElement(new { }), WindowsTools.Ok(JsonSerializer.Serialize(new[] {
            new { Name = "Canon Sala", Jobs = 1 }, new { Name = "HP Recepção", Jobs = 1 }, new { Name = "Canon  Sala.", Jobs = 0 } })));
        assert(printers.PrinterTargetUnambiguous("Canon Sala", "Canon Sala") && printers.PrinterTargetUnambiguous("canon sala", "Canon Sala")
            && !printers.PrinterTargetUnambiguous("Canon  Sala.", "Canon Sala") && !printers.PrinterTargetUnambiguous("Canon  Sala.", "Canon  Sala."),
            "Impressora: fila fora dos candidatos com nome de mesma forma não é autorizada pela resposta que escolheu outra");

        var audio = new InvestigationEvidence();
        audio.Observe("diagnose_device", "a", JsonSerializer.SerializeToElement(new { topic = "audio" }), WindowsTools.Ok(new { count = 3, devices = new[] {
            new Device("Microfone USB", "USB\\MIC_A", 22, "Error"), new Device("Fone X USB", "USB\\FONE_B", 22, "Error"), new Device("Microfone USB", "USB\\MIC_C", 0, "OK") } }));
        assert(audio.DeviceTargetUnambiguous("USB\\MIC_A", "Microfone USB", true) && !audio.DeviceTargetUnambiguous("USB\\MIC_C", "Microfone USB", true)
            && !audio.DeviceTargetUnambiguous("USB\\FONE_B", "Microfone USB", true),
            "Áudio: habilitar só o candidato escolhido, pelo ID; o homônimo de código 0 não é autorizado");
        assert(!audio.DeviceTargetUnambiguous("USB\\MIC_A", "Microfone USB", false) && !audio.DeviceTargetUnambiguous("USB\\MIC_C", "Microfone USB", false)
            && audio.DeviceTargetUnambiguous("USB\\FONE_B", "Fone X USB", false),
            "Áudio: para reiniciar, dois dispositivos com o mesmo nome continuam ambíguos; o nome único escolhe");

        var single = new InvestigationEvidence();
        single.Observe("diagnose_device", "a", JsonSerializer.SerializeToElement(new { topic = "audio" }), WindowsTools.Ok(new { count = 2, devices = new[] {
            new Device("Microfone USB", "USB\\MIC_A", 22, "Error"), new Device("Microfone USB", "USB\\MIC_C", 0, "OK") } }));
        assert(single.DeviceTargetUnambiguous("USB\\MIC_A", "o microfone não funciona", true) && !single.DeviceTargetUnambiguous("USB\\MIC_C", "o microfone não funciona", true),
            "Áudio: com um único candidato desabilitado, só ele pode ser habilitado; o homônimo não");
    }

    private static void PreviousFailures(Action<bool, string> assert)
    {
        var printerSets = Sets.Select(Printers).ToArray();
        var audioSets = Sets.Select(Audio).ToArray();
        foreach (var reply in Refused)
        {
            var wrong = Sets.Select((names, index) => PrintersAsk(printerSets[index], names, reply) && AudioAsks(audioSets[index], names.Length, reply) ? null : string.Join(" / ", names))
                .Where(set => set != null).ToArray();
            assert(wrong.Length == 0, "Frase das revisões anteriores pergunta de novo em todos os pares: " + reply + (wrong.Length > 0 ? " (escolheu em: " + string.Join("; ", wrong) + ")" : ""));
        }
    }

    private static bool Chooses(InvestigationEvidence evidence, string[] names, string target, string reply)
        => evidence.PrinterTargetUnambiguous(target, reply) && names.Where(name => name != target).All(name => !evidence.PrinterTargetUnambiguous(name, reply))
            && evidence.AmbiguityQuestion("printer", reply) is null;
    private static bool PrintersAsk(InvestigationEvidence evidence, string[] names, string reply)
        => names.All(name => !evidence.PrinterTargetUnambiguous(name, reply)) && evidence.AmbiguityQuestion("printer", reply) is not null;
    private static bool AudioChooses(InvestigationEvidence evidence, int count, int target, string reply)
        => Enumerable.Range(0, count).All(index => evidence.DeviceTargetUnambiguous("USB\\AUDIO" + index, reply, true) == (index == target))
            && evidence.AmbiguityQuestion("audio", reply) is null;
    private static bool AudioAsks(InvestigationEvidence evidence, int count, string reply)
        => Enumerable.Range(0, count).All(index => !evidence.DeviceTargetUnambiguous("USB\\AUDIO" + index, reply, true)) && evidence.AmbiguityQuestion("audio", reply) is not null;
    private static string Escape(string text) => string.Concat(text.Select(c => c < ' ' || c is >= '\u007F' and <= '\u00A0' or >= ' ' and <= '\u206F' or '\uFEFF' or '\u00AD' || char.IsSurrogate(c) ? "\\u" + ((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture) : c.ToString()));

    private static InvestigationEvidence Printers(string[] names)
    {
        var evidence = new InvestigationEvidence();
        evidence.Observe("get_printers", "p", JsonSerializer.SerializeToElement(new { }),
            WindowsTools.Ok(JsonSerializer.Serialize(names.Select(name => new { Name = name, Jobs = 1 }).ToArray())));
        return evidence;
    }

    private static InvestigationEvidence Audio(string[] names)
    {
        var evidence = new InvestigationEvidence();
        evidence.Observe("diagnose_device", "a", JsonSerializer.SerializeToElement(new { topic = "audio" }), WindowsTools.Ok(new { count = names.Length,
            devices = names.Select((name, index) => new Device(name, "USB\\AUDIO" + index, 22, "Error")).ToArray() }));
        return evidence;
    }
}
