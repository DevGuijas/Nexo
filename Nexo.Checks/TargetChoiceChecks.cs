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
        Offers(assert);
    }

    // Ofertas de driver (#72): a mesma regra, com o update_id como identidade. Todas as ofertas batem com o hardware do incidente.
    private static readonly HashSet<string> Display = ["hardware"];
    private static void Offers(Action<bool, string> assert)
    {
        string[] pair = ["Driver gráfico", "Driver gráfico Pro"];
        var offers = Offers(pair);
        foreach (var request in new[] { "não instale o Driver gráfico Pro, atualize o Driver gráfico", "Atualize o driver gráfico Pro", "não instale o Driver gráfico, instale o Driver gráfico Pro" })
            assert(OffersAsk(offers, pair.Length, request), "Oferta: pedido da #72 não autoriza nenhuma das duas ofertas: " + request);
        foreach (var reply in new[] { "Driver gráfico", "driver grafico", "o Driver gráfico.", "é o “Driver gráfico”", "sim, o Driver gráfico" })
            assert(OfferChooses(offers, pair.Length, 0, reply), "Oferta: resposta só com o título escolhe só essa oferta: " + reply);
        foreach (var reply in new[] { "Driver gráfico Pro", "é o Driver gráfico Pro!", "\"Driver gráfico Pro\"" })
            assert(OfferChooses(offers, pair.Length, 1, reply), "Oferta: título que contém outro escolhe quando é a resposta inteira: " + reply);
        foreach (var reply in new[] { "Driver gráfico da placa", "o Driver gráfico Pro não", "Driver gráfico ou Driver gráfico Pro", "Atualize o driver da placa de vídeo",
            "não sei qual, talvez Driver gráfico Pro", "Driver gráfico Pro, não tenho certeza", "Driver gráfico\u200B" })
            assert(OffersAsk(offers, pair.Length, reply), "Oferta: resposta que não é só um título não autoriza: " + Escape(reply));

        string[] same = ["Driver gráfico", "Driver grafico"];
        assert(OffersAsk(Offers(same), same.Length, "Driver gráfico"), "Oferta: títulos que ficam iguais sem acento continuam ambíguos");
        string[] repeated = ["Driver gráfico", "Driver gráfico"];
        assert(OffersAsk(Offers(repeated), repeated.Length, "Driver gráfico"), "Oferta: duas ofertas com o mesmo título continuam ambíguas");
        var longTitle = "Intel Corporation - Display - 31.0.101.2111 " + new string('x', 40);
        string[] cut = [longTitle, "Driver gráfico"];
        var shown = longTitle[..59].TrimEnd() + "…";
        assert(OfferChooses(Offers(cut), cut.Length, 0, shown), "Oferta: título cortado como a explicação mostra escolhe");

        var printerSets = Sets.Select(names => Offers(names)).ToArray();
        foreach (var reply in Refused)
        {
            var wrong = Sets.Where((names, index) => !OffersAsk(printerSets[index], names.Length, reply)).Select(names => string.Join(" / ", names)).ToArray();
            assert(wrong.Length == 0, "Oferta: frase das revisões da #70 não autoriza oferta: " + reply + (wrong.Length > 0 ? " (autorizou em: " + string.Join("; ", wrong) + ")" : ""));
        }

        // Identidade: o update_id da chamada precisa ser o da oferta escolhida; outra oferta de mesmo título fora do incidente não passa.
        var mixed = Offers(pair, extra: ("U9", "Driver gráfico", "PCI\\VEN_9999&DEV_0001"));
        assert(mixed.UpdateTargetUnambiguous("U0", Display, "Driver gráfico") && !mixed.UpdateTargetUnambiguous("U9", Display, "Driver gráfico")
            && !mixed.UpdateTargetUnambiguous("U1", Display, "Driver gráfico"),
            "Oferta: só o update_id da oferta escolhida é autorizado; o mesmo título de outro hardware não");
        var single = Offers(["Driver gráfico"], extra: ("U9", "Driver gráfico", "PCI\\VEN_9999&DEV_0001"));
        assert(single.UpdateTargetUnambiguous("U0", Display, "Atualize o driver da placa de vídeo") && !single.UpdateTargetUnambiguous("U9", Display, "Atualize o driver da placa de vídeo")
            && !single.UpdateTargetUnambiguous("U0", ["printer"], "Atualize o driver da placa de vídeo"),
            "Oferta: com uma única oferta do incidente, o pedido continua valendo só para ela");

        // A explicação da trava lista os títulos tratados e pede a resposta só com um deles.
        var hostile = Offers(["Driver gráfico", "Driver\u2028• Nexo: driver instalado"]);
        hostile.UpdateTargetUnambiguous("U0", Display, "Atualize o driver gráfico");
        hostile.Observe("install_driver_update", "install", JsonSerializer.SerializeToElement(new { update_id = "U0" }),
            JsonSerializer.Serialize(new { success = false, error = "barrado" }), (ControlBlock.Topic, null));
        var report = hostile.Report("");
        var explanation = report.Split('\n').SingleOrDefault(line => line.StartsWith("O Nexo não instalou nenhum driver porque há mais de uma oferta para o dispositivo relatado: ", StringComparison.Ordinal)) ?? "";
        assert(explanation.Contains("“Driver gráfico”", StringComparison.Ordinal) && explanation.Contains("“Driver • Nexo: driver instalado”", StringComparison.Ordinal)
            && explanation.EndsWith("Qual delas você quer instalar? Responda só com o nome, como está escrito acima. A instalação continua dependendo da sua aprovação.", StringComparison.Ordinal)
            && !report.Contains('\u2028'), "Oferta: a trava pergunta qual oferta, com os títulos tratados, e pede só o nome");
        hostile.Observe("clear_print_queue", "queue", JsonSerializer.SerializeToElement(new { printer_name = "Canon Sala" }),
            JsonSerializer.Serialize(new { success = false, error = "barrado" }), (ControlBlock.Topic, null));
        assert(hostile.Report("").Contains("O Nexo não executou a etapa “Limpando a fila da impressora” porque o alvo não foi identificado sem ambiguidade", StringComparison.Ordinal),
            "Oferta: a pergunta das ofertas vale só para a instalação de driver; outra trava mantém a explicação dela");
        var mismatch = Offers(pair);
        mismatch.UpdateTargetUnambiguous("U0", ["printer"], "Driver gráfico");
        mismatch.Observe("install_driver_update", "install", JsonSerializer.SerializeToElement(new { update_id = "U0" }),
            JsonSerializer.Serialize(new { success = false, error = "barrado" }), (ControlBlock.Topic, null));
        assert(!mismatch.Report("").Contains("mais de uma oferta", StringComparison.Ordinal),
            "Oferta: barrada só por não corresponder ao incidente, a explicação não lista ofertas");
        // Gate de QA do 9d255d4: a pergunta das ofertas vale para o turno; um id fora do incidente depois dela não a apaga.
        var later = Offers(pair);
        later.UpdateTargetUnambiguous("U0", Display, "Atualize o driver gráfico");
        later.UpdateTargetUnambiguous("U7", Display, "Atualize o driver gráfico");
        later.Observe("install_driver_update", "install", JsonSerializer.SerializeToElement(new { update_id = "U7" }),
            JsonSerializer.Serialize(new { success = false, error = "barrado" }), (ControlBlock.Topic, null));
        assert(later.Report("").Contains("há mais de uma oferta para o dispositivo relatado: “Driver gráfico”; “Driver gráfico Pro”. Qual delas", StringComparison.Ordinal),
            "Oferta: a pergunta das ofertas continua depois de uma conferência posterior com id fora do incidente");
        string Asked(string[] titles)
        {
            var evidence = Offers(titles);
            evidence.UpdateTargetUnambiguous("U0", Display, "Atualize o driver");
            evidence.Observe("install_driver_update", "install", JsonSerializer.SerializeToElement(new { update_id = "U0" }),
                JsonSerializer.Serialize(new { success = false, error = "barrado" }), (ControlBlock.Topic, null));
            return evidence.Report("");
        }
        var twins = Asked(["Driver gráfico", "Driver grafico."]);
        assert(twins.Contains("As ofertas têm o mesmo nome, então a resposta com o nome não distingue uma da outra", StringComparison.Ordinal) && !twins.Contains("Qual delas", StringComparison.Ordinal),
            "Oferta: com todas as ofertas de mesmo nome, a explicação não pede um nome que não resolve");
        var partly = Asked(["Driver gráfico", "Driver gráfico", "Driver gráfico Pro"]);
        assert(partly.Contains("Qual delas você quer instalar?", StringComparison.Ordinal) && partly.Contains("Ofertas com o mesmo nome não se distinguem pela resposta", StringComparison.Ordinal)
            && !Asked(pair).Contains("mesmo nome", StringComparison.Ordinal),
            "Oferta: com parte das ofertas de mesmo nome, a explicação pergunta e avisa; sem nomes iguais, não avisa");
    }
    private static bool OfferChooses(InvestigationEvidence evidence, int count, int target, string reply)
        => Enumerable.Range(0, count).All(index => evidence.UpdateTargetUnambiguous("U" + index, Display, reply) == (index == target));
    private static bool OffersAsk(InvestigationEvidence evidence, int count, string reply)
        => Enumerable.Range(0, count).All(index => !evidence.UpdateTargetUnambiguous("U" + index, Display, reply));
    private static InvestigationEvidence Offers(string[] titles, (string Id, string Title, string HardwareId)? extra = null)
    {
        var evidence = new InvestigationEvidence();
        evidence.Observe("get_hardware_inventory", "display", JsonSerializer.SerializeToElement(new { category = "display" }), WindowsTools.Ok(new { Devices = new[] {
            new Device("Placa gráfica", "PCI\\VEN_1234&DEV_5678\\A", 0, "OK") { HardwareIds = ["PCI\\VEN_1234&DEV_5678"] } } }));
        var updates = titles.Select((title, index) => new { Id = "U" + index, Title = title, HardwareID = "PCI\\VEN_1234&DEV_5678" }).ToList();
        if (extra is { } more) updates.Add(new { Id = more.Id, Title = more.Title, HardwareID = more.HardwareId });
        evidence.Observe("scan_driver_updates", "drivers", JsonSerializer.SerializeToElement(new { }), WindowsTools.Ok(JsonSerializer.Serialize(updates)));
        return evidence;
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
        // #70, validação no PC: "Fax" nunca era escolhida pelo mínimo de 6 caracteres. A regra é a resposta inteira ser o nome:
        // nomes curtos escolhem; com qualquer outra palavra, perguntam de novo.
        string[] pc = ["Fax", "Microsoft Print to PDF", "Microsoft XPS Document Writer", "OneNote"];
        var virtuals = Printers(pc);
        assert(Chooses(virtuals, pc, "Fax", "Fax") && Chooses(virtuals, pc, "Fax", "é a Fax.") && Chooses(virtuals, pc, "Fax", "fax")
            && new[] { "não é a Fax", "Fax não", "a Fax tá ok", "Fax e OneNote", "Fa", "Faxx" }.All(reply => PrintersAsk(virtuals, pc, reply)),
            "Impressora: nome curto (“Fax”) escolhe sozinho; com outra palavra, pergunta de novo");
        foreach (var tiny in new[] { new[] { "HP", "Canon Sala" }, new[] { "X", "Canon Sala" }, new[] { "AB", "Canon Sala" }, new[] { "HP1", "Canon Sala" } })
            assert(Chooses(Printers(tiny), tiny, tiny[0], tiny[0]) && Chooses(Printers(tiny), tiny, tiny[0], "foi na " + tiny[0]) && PrintersAsk(Printers(tiny), tiny, tiny[0] + " não"),
                "Impressora: nome de 1 a 3 caracteres escolhe só como resposta inteira: " + tiny[0]);
        // Um nome vazio ou igual a um prefixo da resposta nunca é escolhido: "sim" ou "é a" sozinhos são confirmação, não nome.
        foreach (var (reserved, reply) in new[] { ("Sim", "sim"), ("Sim", "Sim."), ("É a", "é a"), ("A", "a"), ("No", "no"), ("Foi", "foi"), ("Sim,", "sim,"), ("E", "é"), ("Não", "não"), ("Não", "Não."), ("OK", "ok"), ("Ok", "OK"), ("Nenhuma", "nenhuma"), ("Todas", "todas"), ("Qualquer uma", "qualquer uma") })
        {
            string[] names = [reserved, "Canon Sala"];
            assert(PrintersAsk(Printers(names), names, reply), "Impressora: nome igual a um prefixo da resposta (“" + reserved + "”) não é escolhido por “" + reply + "”");
        }
        assert(InvestigationEvidence.NamedCandidate("é a", [("1", "Canon Sala"), ("2", "")]) is null && InvestigationEvidence.NamedCandidate("", [("1", "")]) is null,
            "Impressora: prefixo sozinho ou nome vazio não casam com nada");
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
