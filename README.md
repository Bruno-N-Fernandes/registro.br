# RegistroBrConsole

Aplicativo **console C# (.NET 10)** que é a migração do projeto Python
[`fcrespo82/registro.br`](https://github.com/fcrespo82/registro.br) — um cliente
da API interna do painel do registro.br para listar domínios e ler/alterar a
zona de DNS.

> ⚠️ Usa endpoints **internos** (não oficiais) do painel, sujeitos a mudança
> pelo registro.br. Para automação suportada/oficial, o caminho é o protocolo
> EPP (apenas provedores credenciados).

## Mapeamento Python → C#

| Python | C# | Papel |
|--------|-----|-------|
| `registrobr/main.py` → `RegistroBrAPI` | [`Api/RegistroBrApi.cs`](Api/RegistroBrApi.cs) | Cliente HTTP da API (login/OTP, domínios, zona, add/remove) |
| namedtuples de registro + `__parse_*` | [`Dns/Records.cs`](Dns/Records.cs) | `DnsRecord` e derivados (A/AAAA/CNAME/TXT/MX/TLSA) + parser |
| `cli.py` (argparse) | [`Program.cs`](Program.cs) | Entry point + comandos de linha |
| `shell.py` (cmd.Cmd) | [`Cli/Shell.cs`](Cli/Shell.cs) | Shell interativo |
| BeautifulSoup + lxml | `System.Text.Json` | a API atual é JSON (não há mais HTML) |
| `requests.session()` | `HttpClient` + `CookieContainer` | Sessão com cookies |

### Rotas (atualizadas via captura do DevTools)

O painel migrou para a base **`/v2/ajax/`**. As rotas antigas do Python
(`/2/...`, `/ajax/...`, `/cgi-bin/nicbr/...`) **não funcionam mais**.

| Função | Rota antiga (Python) | Rota atual | Status |
|--------|----------------------|------------|--------|
| Sessão/token | — | `GET /v2/ajax/ping`, `GET /v2/ajax/checklogin?v=2` | ✅ confirmada |
| Checar 2FA | — | `GET /v2/ajax/user/hotp/{user}` | ✅ confirmada |
| Login (1ª etapa) | `POST /ajax/login` | `POST /v2/ajax/user/login/{user}` | ✅ confirmada¹ |
| Login (OTP) | `POST /ajax/token` | `POST /v2/ajax/user/otp/login/{user}` | ✅ confirmada¹ |
| Listar domínios | `GET /cgi-bin/nicbr/user_domains` | `GET /v2/ajax/domains` | ✅ confirmada¹ |
| Ler zona DNS | `GET /2/freedns` | `GET /v2/ajax/domain/{fqdn}/freedns-advanced` | ✅ confirmada² |
| Gravar zona DNS | `POST /2/freedns` | `POST /v2/ajax/domain/{fqdn}/freedns-advanced` (delta) | ✅ confirmada³ |
| Logout | `GET /cgi-bin/nicbr/logout` | `POST /v2/ajax/user/logout` (`{}`) | ✅ confirmada |

¹ Confirmada por captura e **testada** (login funcionando). Corpo do login:
`{"Password","OTP","challenge":"","recaptcha":"","service":"turnstile","token":"<turnstile>"}`.

² Confirmada e implementada. A resposta é
`{ "FQDN": "...", "RRs": [ { "Ownername", "TTL", "Type", "Class", "Rdata" }, ... ] }`;
o parser lê `RRs` e preserva o TTL. No shell, `zone_raw <dominio>` mostra o JSON
cru e `registrobr zone_parse <arquivo.json>` testa o parser offline.

³ Confirmada por captura. O corpo é um **delta**:
`{ "AddRR": [ {Ownername,Type,Rdata} ], "RemoveRR": [ {Ownername,Type,Rdata} ] }`.
As **remoções são enviadas antes das adições** (requisições separadas), o que
evita conflito de CNAME duplicado ao atualizar. `add`/`update` mostram uma prévia
e pedem confirmação antes de gravar.

**Autenticação (reconstruída via cURL):**
- **`X-XSRF-TOKEN`** (cabeçalho) = valor do **cookie `XSRF-TOKEN`**, que o
  servidor **rotaciona** a cada chamada. Um `DelegatingHandler` lê o cookie atual
  e injeta o header em toda requisição (resolve o erro "Header inválido").
- **Turnstile**: o `GET /checklogin?v=2` retorna
  `{"service":"turnstile","token":"…","captcharequired":false}`. Esse `token` vai
  no **corpo** do login. Se `captcharequired` for `true`, é CAPTCHA interativo e
  não dá para automatizar — use o navegador.
- **Corpo do login** (campos exatos, atenção à caixa):
  `{"Password":"…","OTP":"…","challenge":"","recaptcha":"","service":"turnstile","token":"<turnstile>"}`
- **2 etapas (2FA)**: `POST /user/login/{user}` com `OTP` vazio →
  `POST /user/otp/login/{user}` com o código. Contas sem 2FA encerram na 1ª etapa.
- **Logout**: `POST /user/logout` com corpo `{}`.

Fluxo completo observado: `user/logout` → `ping` → `checklogin` →
`user/login` (OTP vazio) → `user/hotp` → `user/otp/login` (com OTP) →
`domains` → `user/resources`.

### Preservado do original
- O formato de fio dos registros: `ownername|TIPO|dado` e o parse de MX/TLSA
  ([`Dns/Records.cs`](Dns/Records.cs)) — reaproveitado pelo parser da zona.

### O que foi corrigido/melhorado
- **Rotas atualizadas** para a base `/v2/ajax/` do painel atual (as do Python
  estavam mortas).
- **OTP sob demanda**: o `cli.py` sempre pedia OTP; aqui ele só é solicitado se
  o servidor indicar (via `/user/hotp/{user}`) que a conta tem 2FA.
- **Formato de fio correto**: o `add_records` do Python tinha um bug (contador
  nunca incrementado + envio do objeto em vez de `owner|TIPO|dado`). O
  `ToWire()`/`DnsRecordParser` já tratam isso para quando o endpoint de DNS for
  ligado.

### Estado atual
- **Login (com 2FA), listar domínios, ler zona e gravar zona** (add/remove):
  implementados e confirmados por captura. O login foi testado de ponta a ponta.
- A gravação foi validada no nível do **payload** (formato idêntico ao do painel);
  a aplicação de ponta a ponta depende da sua sessão/conta.

## Como compilar e executar

Pré-requisito: **.NET SDK 10** (`dotnet --version`).

```powershell
# compilar
dotnet build .\RegistroBrConsole.csproj -c Release

# ou rodar direto
dotnet run --project .\RegistroBrConsole.csproj -- <usuario> domains
```

### Modo linha de comando (porta do cli.py)

```powershell
# listar domínios (senha é pedida de forma oculta)
registrobr 000.000.000-00 domains

# ver a zona de um domínio
registrobr 000.000.000-00 -p MinhaSenha zone_info meudominio.com.br

# com OTP (2FA)
registrobr 000.000.000-00 -p MinhaSenha -o 123456 domains
```

`<usuario>` é o mesmo login do site (CPF, CNPJ, ID-Handle ou domínio).

### Configuração via `local.settings.json` + OTP automático (2FA)

Copie `local.settings.example.json` para `local.settings.json` e preencha:

```json
{
  "IsEncrypted": false,
  "Values": {
    "RegistroBr:User": "User",
    "RegistroBr:Password": "minha-senha",
    "RegistroBr:OtpSeed": "JBSWY3DPEHPK3PXP"
  }
}
```

> A `OtpSeed` é o **segredo Base32** do autenticador (o mesmo do QR Code, em
> texto). Com ela configurada, o app **gera o OTP sozinho** a cada login — não
> precisa mais digitar o código de 6 dígitos. Argumentos de linha de comando
> (`-p`, `-o`) têm prioridade sobre o arquivo.

O `local.settings.json` está no `.gitignore` (contém segredos). Também aceita o
formato aninhado `"RegistroBr": { "User": ..., "OtpSeed": ... }`.

Com tudo no arquivo, basta:

```powershell
registrobr domains                       # usuário/senha/otp vêm do settings
registrobr zone_info meudominio.com.br
```

### Comando `otp` (gerar/validar TOTP)

```powershell
registrobr otp               # código atual a partir da seed do local.settings.json
registrobr otp JBSWY3DPEHPK3PXP   # código de uma seed específica
registrobr otp --selftest    # valida a lib contra os vetores do RFC 6238
```

Implementado com o pacote **Otp.NET** (TOTP de 6 dígitos / SHA-1 / 30s, padrão do
Google Authenticator). O `--selftest` passa nos 6 vetores oficiais do RFC 6238.

### Shell interativo (porta do shell.py)

```powershell
registrobr            # ou: registrobr shell
```

```
(registro.br) login
(registro.br) domains
(registro.br) zone_info meudominio.com.br
(registro.br) add_cname meudominio.com.br
(registro.br) add_a meudominio.com.br
(registro.br) del meudominio.com.br
(registro.br) exit
```

### Aplicar registros por arquivo (`add` / `update`)

No shell, aplique um arquivo de zona (estilo BIND), uma entrada por linha:

```
(registro.br) use tantagrana.com.br      # define o domínio atual (opcional)
(registro.br) add registros.txt          # ou: add tantagrana.com.br registros.txt
(registro.br) update registros.txt       # 'update' é sinônimo de 'add'
```

Antes de gravar, é mostrada uma **prévia** (o que será adicionado/removido) e
pedida confirmação.

**Formato do arquivo** — `nome tipo rdata [ttl]` (também aceita a ordem
`tipo nome rdata`; o `rdata` é exatamente o que o registro.br guarda):

```
# adicionar (linha normal ou com '+')
@       A      203.0.113.10
www     CNAME  meusite.azurewebsites.net
api     CNAME  meusite.azurewebsites.net   3600
@       MX     10 mail.meudominio.com.br
@       TXT    "v=spf1 include:spf.protection.outlook.com -all"

# remover (linha começando com '-')
- antigo   CNAME                         # remove todos os CNAME de 'antigo'
- www      CNAME  alvo-errado.net        # remove só o registro exato
```

Regras:
- `@` ou nome vazio = raiz; `#` ou `;` **no início da linha** = comentário.
- Ordem flexível: `nome tipo rdata` ou `tipo nome rdata` (detecta qual token é o
  tipo). No empate, vale a ordem padrão de zone-file (`nome tipo`).
- TTL é o último token, se for inteiro (senão usa 3600).
- Valores com espaço (TXT/MX) podem ir entre aspas.

**Semântica declarativa (importante):** para cada **nome+tipo** que aparece no
arquivo, o conjunto do arquivo **substitui** o que existe na zona para aquele par:
- `xyz CNAME b` com um `xyz CNAME a` existente → **troca** (remove o `a`, põe o
  `b`). É a forma de **atualizar** um registro.
- Listar dois `A` para o mesmo nome mantém os dois (o conjunto declarado).
- Registro idêntico ao que já existe é **ignorado** (não duplica).
- Pares **nome+tipo não citados** no arquivo ficam **intocados**.
- Linhas com `-` removem explicitamente (com valor = só o exato; sem valor =
  todos daquele nome+tipo).

A prévia mostra cada `+` (adição) e `-` (remoção/substituição) antes de confirmar.

Teste offline (sem rede): `registrobr zone_apply <zona.json> <arquivo.txt>`
mostra a prévia e o JSON resultante.

## Uso como biblioteca

```csharp
using var api = new RegistroBrApi("000.000.000-00", "senha", otp: null);
await api.LoginAsync();

var dominios = await api.DomainsAsync();
var dom = dominios.First(d => d.FQDN == "meudominio.com.br");

var registros = await api.ZoneInfoAsync(dom);

await api.AddRecordsAsync(dom, new[]
{
    RegistroBrApi.CreateCname("www", "meusite.github.io"),
    RegistroBrApi.CreateA("api", "203.0.113.20"),
});

await api.LogoutAsync();
```

## Estrutura

```
RegistroBrConsole/
├── Program.cs                    # entry point + comandos otp/zone_apply/zone_parse
├── RegistroBrConsole.csproj      # net10.0, Otp.NET, global usings
├── Api/
│   └── RegistroBrApi.cs          # cliente da API /v2/ajax (main.py)
├── Dns/
│   ├── Records.cs                # Domain, DnsRecord*, parser, RrDelta, ZonePlan
│   └── ZoneFile.cs               # parser do arquivo de zona (+ adiciona / - remove)
├── Cli/
│   ├── Shell.cs                  # shell interativo (shell.py)
│   └── OtpCommand.cs             # comando "otp"
├── Configuration/
│   └── Settings.cs               # leitura do local.settings.json
├── Security/
│   └── TotpGenerator.cs          # gerador de OTP (TOTP / Otp.NET)
├── exemplos/                     # arquivos de zona de teste (seed/update/remove-dup)
├── local.settings.example.json   # modelo p/ copiar como local.settings.json
└── README.md
```

## Avisos

- Credenciais **não** são armazenadas — ficam só em memória durante a execução.
- Alterações de DNS (especialmente MX/NS) afetam e-mail e resolução do domínio:
  **revise no painel** após aplicar.
