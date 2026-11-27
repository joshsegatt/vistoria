# Armadilhas do projeto

- `MSFT_PhysicalDisk.DeviceId` do primeiro disco é `"0"`: nunca trate `"0"` como valor vazio em `Wmi.Clean` (já sumiu o disco do sistema por isso).
- Mensagens de exceção vêm localizadas (pt-BR): detecte "acesso negado" pelo código (`Guard.IsDenied`), nunca pelo texto.
- `root\wmi` responde `NotSupported` quando a classe não existe na máquina (sem ATA, sem bateria). Isso é "nada para ler", não erro.
- Edge headless não desce abaixo de ~500 px de largura: print de "celular" feito por ele mostra um estouro falso. Confira o layout mobile no painel do navegador (preset mobile).
- Laudos reais têm números de série e o nome do usuário. Use `vistorias/` (ignorado pelo git) e anonimize antes de virar imagem de README.
