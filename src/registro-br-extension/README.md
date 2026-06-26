# Registro.br DNS Helper

Extensão do Chrome (Manifest V3) que automatiza a criação e configuração de
**registros DNS e subdomínios** no painel do **registro.br**, usando a sua
própria sessão já autenticada — nenhuma senha é pedida ou armazenada.

## Por que automação do painel (e não API)

O registro.br **não disponibiliza API pública de DNS** para usuários comuns —
o acesso programático oficial é via protocolo **EPP**, exclusivo para provedores
credenciados (`registro.br/provedor/`). Por isso esta extensão age como um
"piloto automático" do próprio painel: ela preenche o formulário de
**Edição de Zona → Modo Avançado** por você, dentro da sua sessão logada.

## Instalação (modo desenvolvedor)

1. Abra o Chrome em `chrome://extensions`.
2. Ative o **Modo do desenvolvedor** (canto superior direito).
3. Clique em **Carregar sem compactação** e selecione a pasta
   `registro-br-dns-extension`.
4. O ícone `.br` aparece na barra de extensões.

## Como usar

1. Faça login em `https://registro.br/`.
2. Clique no ícone da extensão, digite o **domínio** e clique em **Abrir DNS**.
   A extensão abre `https://registro.br/painel/dominios/?dominio=<domínio>` **e
   já entra no editor de zona** (clica em Configurar zona DNS → Editar zona →
   Modo Avançado conforme necessário).
3. Preencha as linhas de registro (ou use **importar texto** / **Terraform**) e
   clique em **Aplicar no registro.br** — ele começa direto em "Nova entrada",
   preenche os campos e clica em **Adicionar** para cada registro.
4. Clique em **Salvar alterações** para gravar no painel (ou marque
   **Salvar automaticamente após aplicar** para que o "Aplicar" já salve).

### Campo de domínio e botões

- **Abrir DNS** — abre a página do domínio e já entra no editor de zona
  (Configurar zona DNS → Editar zona → Modo Avançado). Se você já estiver numa
  página do registro.br, o domínio é detectado e preenchido sozinho.
- **Salvar alterações** — clica no "Salvar" do painel a qualquer momento.
- **Salvar automaticamente após aplicar** — quando marcado, o "Aplicar" salva
  sozinho ao terminar; quando desmarcado, você salva no botão dedicado.
- **Manter janela aberta (destacar à direita)** — abre o painel numa **janela
  própria**, colada na borda direita da tela, que **não fecha** ao clicar em
  outro lugar do navegador (o popup ancorado normal fecha ao perder o foco).
  Desmarque para fechá-la. *Obs.: o Chrome não expõe "always-on-top" real
  (flutuar sobre outros aplicativos); a janela fica aberta e fixa à direita.*

### Campos por linha

| Campo | Descrição |
|-------|-----------|
| **Tipo** | A, AAAA, CNAME, MX, TXT, NS, SRV |
| **Nome / subdomínio** | Ex.: `www`, `api`. Vazio = raiz (`@`) |
| **Valor** | IP, hostname de destino, conteúdo TXT etc. |
| **Prio.** | Só para MX/SRV |

> O registro.br não usa TTL na edição de zona, então esse campo foi removido da
> interface.

### Importação por texto

Formato: `nome tipo valor [ttl] [prioridade]` — uma entrada por linha
(`#` ou `;` = comentário):

```
www   CNAME  meusite.com.br
@     A      203.0.113.10
api   A      203.0.113.20   3600
mail  MX     mx.provedor.com 3600 10
@     TXT    "v=spf1 include:_spf.provedor.com ~all"
```

### Importação do Terraform

Clique em **Terraform ▾** (ao lado de "Registros a criar"), cole a saída do
output `acs_custom_domain_dns_records` e escolha:

- **Substituir pelos do Terraform** — limpa a lista e usa só os do Terraform.
- **+ Adicionar do Terraform** — mantém os registros já preenchidos e acrescenta
  os novos.

A extensão lê os campos `name` / `ttl` / `type` / `value` (e `priority`/
`preference`, se houver), ignora listas vazias como `"dmarc" = tolist([])` e
preenche a tabela.

- O parser lê os pares `"chave" = valor` diretamente, então funciona mesmo se as
  chaves `{ }` se perderem no copiar/colar.
- O `name` é tornado **relativo ao domínio** do campo de cima: por exemplo, com o
  domínio `meudominio.com.br`, o name `hmail.meudominio.com.br` (blocos `domain`/
  `spf`) vira `hmail`.
- Os registros **DKIM** (`dkim`/`dkim2`) vêm relativos ao subdomínio de e-mail,
  então recebem esse subdomínio como **sufixo**: `selector1..._domainkey` vira
  `selector1..._domainkey.hmail` (onde `hmail` é extraído do bloco `domain`).

Exemplo de entrada:

```hcl
acs_custom_domain_dns_records = tolist([
  {
    "dkim" = tolist([
      {
        "name" = "selector1-azurecomm-prod-net._domainkey"
        "ttl"  = 3600
        "type" = "CNAME"
        "value" = "selector1-azurecomm-prod-net._domainkey.azurecomm.net"
      },
    ])
    "dmarc" = tolist([])
    "spf" = tolist([
      {
        "name" = "email.meudominio.com.br"
        "ttl"  = 3600
        "type" = "TXT"
        "value" = "v=spf1 include:spf.protection.outlook.com -all"
      },
    ])
  },
])
```

## Botão "Diagnosticar"

O painel do registro.br pode mudar de layout. Se a aplicação não encontrar os
botões/campos, clique em **Diagnosticar**: ele lista os botões, campos e selects
detectados na página atual. Com essa saída dá para ajustar as palavras-chave em
[`content/content.js`](content/content.js) (constantes `NAME_KEYS`, `VALUE_KEYS`,
`TTL_KEYS`, `PRIORITY_KEYS` e as listas de texto em `findClickableByText`).

## Como funciona (técnico)

- `content/content.js` roda nas páginas `*.registro.br`. Localiza botões e
  campos **por texto/rótulo** (sem acento, caixa-baixa) em vez de seletores
  fixos, o que o torna resiliente a mudanças de classe/id. Para SPAs
  (React/Vue), os valores são definidos via `setNativeValue` + disparo de
  eventos `input`/`change`, garantindo que o framework reconheça a digitação.
- `popup/*` é a interface: monta a lista de registros, persiste em
  `chrome.storage.local` e envia as ordens ao content script, mostrando o
  andamento em tempo real.
- `background/background.js` é um service worker mínimo (exigido pelo MV3).

## Privacidade e segurança

- Não coleta nem envia dados a terceiros. Tudo roda localmente no seu navegador.
- Não pede nem guarda credenciais — usa apenas a sessão que **você** já abriu.
- Permissões: `storage` (salvar suas linhas), `scripting`/`activeTab` e acesso
  somente a `registro.br`.

## Limitações

- Depende do layout do painel estar acessível (Modo Avançado aberto). Se mudar
  muito, ajuste os seletores conforme o "Diagnosticar".
- **Revise sempre** o resultado no painel antes de confirmar mudanças críticas
  de DNS (MX, NS), pois afetam e-mail e resolução do domínio.
