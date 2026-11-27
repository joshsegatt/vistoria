# Design do laudo

Superfície única: o laudo HTML (`src/Vistoria/Report.cs`). Modo **Read/Operate**: o leitor precisa entender um veredito e conferir evidências. Precisão e sobriedade, não expressão.

## Cenário de uso

Ele é aberto na bancada logo após a vistoria, impresso ou salvo em PDF e entregue ao cliente. Por isso usa fundo claro (papel), não tem script nem rede, e tem CSS de impressão.

## Paleta (8 cores funcionais)

| Papel | Valor |
|---|---|
| Tinta | `#101828` |
| Texto secundário | `#475467` |
| Linha | `#e4e7ec` |
| Papel | `#ffffff` |
| Fundo | `#f5f6f8` |
| Crítico | `#b42318` |
| Atenção | `#a15c07` |
| OK | `#067647` |

O status nunca depende só da cor: a pílula sempre traz o texto ("Crítico", "Atenção", "OK", "Info", "Não verificado"). "Não verificado" usa borda tracejada.

## Tipografia

Segoe UI Variable / Segoe UI, a fonte nativa do Windows, onde o laudo é gerado e aberto. Cascadia Mono / Consolas só para dados: números de série, hashes, códigos de bugcheck e caminhos. Números de tabela em `tabular-nums`.

## Espaçamento e forma

Raio de 10px nos blocos e de 999px nas pílulas. Seções separadas por 48px, linhas de lista por 14px. Largura máxima de 980px; o texto corrido para em 75ch.

## Movimento

Nenhum. É um documento.
