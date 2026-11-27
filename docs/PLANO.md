# Plano do Vistoria

Vistoria cautelar para PCs usados: um `.exe` que roda do pendrive, lê o histórico
que o próprio hardware e o Windows guardam, cruza essas fontes atrás de contradições
e entrega um laudo HTML com cada conclusão apontando a evidência de onde veio.

## User stories

- Quando eu plugar o pendrive e abrir `Vistoria.exe` num PC que vou comprar,
  quero um laudo em menos de um minuto com veredito: Aprovado, Aprovado com
  apontamentos ou Reprovado.
- Quando o disco tiver setores ruins, desgaste alto ou muitos desligamentos
  forçados, quero ver isso como apontamento, com o número lido do SMART.
- Quando o Windows tiver sido reinstalado num disco com muito uso, quero ver a
  contradição: "o disco tem 18.000 h ligado, o Windows tem 3 dias".
- Quando alguém tiver apagado os logs ou voltado o relógio, quero ver isso como
  sinal de adulteração.
- Quando uma CPU, placa de vídeo ou disco já tiver passado pela máquina e não
  estiver mais lá, quero ver qual peça e as datas em que ela aparece.
- Quando a bateria do notebook estiver gasta, quero ver a capacidade que sobrou
  e os ciclos de carga.
- Quando o app não conseguir ler alguma coisa (sem admin, disco USB, máquina sem
  bateria), quero ver "não verificado" com o motivo. Nunca um falso "OK".
- Quando eu entregar o laudo ao cliente, quero o nome da loja e do técnico no
  laudo e um hash SHA-256 que prove que as evidências não foram editadas depois.

## Fora do escopo (de propósito)

- **Arquivos pessoais e atividade do usuário** (fotos, documentos, histórico de
  navegação, programas abertos): é dado pessoal (LGPD), e não diz nada sobre a
  saúde da máquina.
- **Recuperar arquivos apagados:** em SSD com TRIM não funciona, e é dado pessoal.
- **Teste de estresse:** existem ferramentas melhores (OCCT, MemTest86+). O
  Vistoria lê histórico; ele não força o hardware.

## Princípios forenses

1. **Somente leitura.** Nada é gravado na máquina examinada; a saída vai para
   o pendrive. Não chama processos externos. Abrir o laudo no navegador grava
   histórico na máquina, então só acontece com `--abrir`. Os discos são abertos sem acesso de
   escrita, só para consultas de status (IOCTL).
2. **Toda conclusão cita a fonte**: log e ID do evento, atributo SMART, chave de
   registro, arquivo de dump.
3. **Ausência de evidência não é evidência de ausência.** Se uma fonte não foi
   lida, a verificação sai como `Não verificado`, com o motivo. A janela que o
   histórico cobre aparece no laudo.
4. **Cadeia de custódia.** `evidencias.json` guarda tudo o que foi coletado, em
   bruto. `manifesto.txt` traz o SHA-256 de cada arquivo, horários em UTC, versão
   da ferramenta e o operador.
5. **Determinístico e testável.** A coleta fica separada das regras. As regras
   são funções puras sobre o modelo de evidência e têm testes com fixtures.

## Arquitetura

```
Program.cs        argumentos, elevação (UAC), orquestração, escrita da saída
Evidence.cs       modelo: o que foi coletado + erros de coleta
Collectors/
  Machine.cs      WMI (BIOS, placa, CPU, RAM, GPU, SO) + registro (datas de instalação)
  Disks.cs        MSFT_PhysicalDisk, contadores de confiabilidade, SMART ATA, log de saúde NVMe
  Battery.cs      root\wmi: capacidade de projeto, carga total atual, ciclos
  Events.cs       logs System/Security via EventLogReader (XPath)
  Devices.cs      SetupAPI: dispositivos presentes e já removidos, com datas
  Dumps.cs        Minidump, MEMORY.DMP, LiveKernelReports + código do bugcheck
Rules.cs          verificações puras → status por item + veredito
Report.cs         laudo HTML autocontido (sem JS, imprimível)
Json.cs           serializador JSON mínimo (sem dependência)
```

Alvo: **.NET Framework 4.7.2**, que já vem no Windows 10 (1803+) e no Windows 11.
Resultado: um `.exe` pequeno, sem instalador e sem DLL ao lado. AnyCPU, então o
mesmo binário roda em 32 e 64 bits.

## Fontes e o que cada uma prova

| Fonte | O que dá | Sobrevive a formatação? |
|---|---|---|
| SMART / log NVMe | horas ligado, ciclos, desligamentos inseguros, TB gravados, setores ruins | Sim |
| Bateria (ACPI via WMI) | capacidade de projeto x atual, ciclos | Sim |
| `Source OS` no registro | datas de instalações anteriores do Windows (upgrades) | Não |
| Logs System/Security | telas azuis, WHEA, desligamentos, erros de disco, logs apagados, relógio | Não |
| SetupAPI (propriedades de dispositivo) | peças que já passaram pela máquina, com datas | Não |
| Dumps do kernel | código do bugcheck de cada tela azul | Não |

Cruzamento principal: **horas de uso do disco x idade da instalação do
Windows**. Disco muito mais velho que o sistema significa que o sistema foi
reinstalado, e o histórico de antes disso se perdeu.

## Verificação

- `dotnet test`: regras, parsers (SMART, NVMe, cabeçalho de dump, evento de
  bugcheck) e JSON.
- Execução real nesta máquina sem admin: o laudo precisa sair coerente, com os
  itens que exigem admin marcados como `Não verificado`.
- CI no GitHub Actions (windows-latest): build e testes a cada push; em tag `v*`,
  o `.exe` vai para a release.
