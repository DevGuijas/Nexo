# Monitoramento em segundo plano (#111)

## Escopo

O Nexo pode continuar coletando a saúde do PC com a janela oculta na bandeja do Windows. A opção começa desligada e fica nas preferências de monitoramento. O menu da bandeja oferece abrir e encerrar. Ao sair, o monitor e o ícone são liberados; nenhuma correção automática é iniciada por esse modo.

## Plano

1. Testar persistência e compatibilidade das preferências antigas; conferir a opção na prévia da interface.
2. Adicionar a opção e o ciclo de vida do ícone, com fechamento seguro quando há operação em andamento.
3. Verificar build, regressões e prévia; revisar o comportamento de abrir/fechar em Windows.
4. Criar PR para `development`, esperar CI e integrar, mantendo a issue aberta se a validação real da bandeja não estiver completa.
