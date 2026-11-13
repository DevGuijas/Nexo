# Assinatura de código do Nexo (#22)

## Objetivo

Assinar `Nexo.exe` antes de compactá-lo e `NexoSetup.exe` antes da distribuição. A publicação deve falhar sem certificado Authenticode confiável, carimbo de tempo e verificação válida. A chave privada fica fora do repositório, no armazenamento do Windows ou em provedor de assinatura compatível.

## Passos

- [x] Adicionar teste de rejeição de binário sem assinatura e de certificado ausente.
- [x] Criar script para assinar e conferir executáveis, com certificado de Code Signing, validade, cadeia confiável e carimbo de tempo.
- [x] Integrar assinatura do app ao empacotamento; verificar também ZIP reutilizado e exigir instalador assinado.
- [x] Documentar publicação e assinatura do instalador, incluindo emissor, validade e hash.
- [ ] Rodar testes, revisão e CI; manter #22 aberta até assinar artefatos reais com certificado confiável.
