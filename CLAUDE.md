# Vistoria

Vistoria cautelar para PCs usados. Um exe .NET Framework 4.7.2 que lê o histórico gravado no hardware e no Windows e gera um laudo HTML. Plano e princípios: `docs/PLANO.md`.

## Comandos

- Testes: `dotnet test`
- Build de release: `dotnet build src/Vistoria -c Release` gera `src/Vistoria/bin/Release/net472/Vistoria.exe`
- Rodar sem UAC (coleta parcial): `src/Vistoria/bin/Release/net472/Vistoria.exe --sem-admin --saida <pasta>`
- Release: crie e envie a tag `vX.Y.Z` (também suba `<Version>` no csproj). O CI anexa o exe e o SHA256SUMS à release.

## Regras do projeto

- **Somente leitura.** Nenhum coletor grava, apaga ou muda algo na máquina examinada, nem chama processos externos. Abrir o navegador ao terminar grava histórico nela, por isso só acontece com `--abrir`.
- **Coleta separada das regras.** Coletores (`Collectors/`) só preenchem `Evidence`. Toda conclusão nasce em `Rules.cs`, que é puro e testado em `tests/`.
- **Falha de leitura vira `Status.NotChecked`, nunca `Ok`.** Cada fonte roda em `Guard.Try`, que registra o motivo em `Evidence.Errors`.
- **Toda verificação cita a fonte** (log + ID, atributo SMART, chave de registro). Um teste garante isso.
- **Horários em UTC** na evidência. A conversão para o fuso local acontece só na exibição.
- **Sem dependências de runtime.** O exe precisa rodar sozinho, direto do pendrive.
- Nunca faça commit de `vistorias/` nem de laudos reais: eles contêm números de série e o histórico de máquinas de clientes.
