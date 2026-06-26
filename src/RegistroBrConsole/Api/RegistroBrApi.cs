using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RegistroBrConsole.Dns;
using RegistroBrConsole.Security;

namespace RegistroBrConsole.Api;

/// <summary>
/// Cliente da API interna do painel registro.br (base <c>/v2/ajax/</c>),
/// reconstruída a partir de captura real (cURL) do fluxo de login.
///
/// Pontos-chave da autenticação:
///  - Cabeçalho <c>X-XSRF-TOKEN</c> = valor do cookie <c>XSRF-TOKEN</c> (rotaciona
///    a cada chamada). Injetado automaticamente por <see cref="XsrfHandler"/>.
///  - O corpo do login carrega o <c>token</c> do <b>Turnstile</b>, obtido em
///    <c>GET /checklogin?v=2</c> (<c>{"service":"turnstile","token":"…","captcharequired":false}</c>).
///  - Login em 2 etapas (contas com 2FA): <c>POST /user/login/{user}</c> com
///    <c>OTP</c> vazio e depois <c>POST /user/otp/login/{user}</c> com o código.
///
/// Ainda a confirmar (marcado com <c>// CONFIRMAR</c>): as RESPOSTAS de
/// <c>/user/login</c>, <c>/user/otp/login</c> e <c>/domains</c> (para detectar
/// sucesso/erro/OTP e mapear os campos dos domínios). DNS continua pendente.
/// </summary>
public sealed class RegistroBrApi : IDisposable
{
    private const string Origin = "https://registro.br";
    private const string ApiBase = "https://registro.br/v2/ajax";

    private readonly HttpClient _http;
    private readonly CookieContainer _cookies;
    private readonly string _user;
    private readonly string _password;
    private readonly string _otpSeed;
    private string _otp;

    public bool IsLogged { get; private set; }

    /// <param name="otp">Código OTP fixo (opcional). Tem prioridade sobre a seed.</param>
    /// <param name="otpSeed">Seed Base32 para gerar o OTP automaticamente (2FA).</param>
    public RegistroBrApi(string user, string password = null, string otp = null, string otpSeed = null)
    {
        _user = user;
        _password = password;
        _otp = otp;
        _otpSeed = otpSeed;

        _cookies = new CookieContainer();
        var inner = new HttpClientHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
            AllowAutoRedirect = true,
        };

        _http = new HttpClient(new XsrfHandler(_cookies, inner));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) RegistroBrConsole/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        _http.DefaultRequestHeaders.Referrer = new Uri($"{Origin}/painel/");
        _http.DefaultRequestHeaders.Add("Origin", Origin);
    }

    // -------------------------------------------------------------------------
    // Login (fluxo /v2/ajax com XSRF + Turnstile + 2FA)
    // -------------------------------------------------------------------------
    public async Task LoginAsync()
    {
        // 0) Semeia cookies de sessão (XSRF-TOKEN) como o navegador faz.
        await TryGetAsync($"{Origin}/login/");
        await TryGetAsync($"{ApiBase}/ping");

        // 1) checklogin → token do Turnstile + flag de captcha.
        var (turnstileToken, captchaRequired) = await CheckLoginAsync();
        if (captchaRequired)
            throw new InvalidOperationException(
                "O registro.br está exigindo CAPTCHA (Cloudflare Turnstile) para este login, " +
                "o que não dá para automatizar. Faça o login pelo navegador.");

        // 2) 1ª etapa: senha com OTP vazio.
        var first = await PostLoginAsync($"{ApiBase}/user/login/{Uri.EscapeDataString(_user)}",
            turnstileToken, otp: string.Empty);

        switch (DetectLogin(first))
        {
            case LoginState.LoggedIn:
                IsLogged = true;
                return;
            case LoginState.Failed:
                throw new InvalidOperationException($"Falha ao logar: {ExtractMessage(first)}");
            case LoginState.OtpRequired:
            default:
                break;
        }

        // 3) 2ª etapa: OTP (2FA). Gera da seed, se configurada; senão pergunta.
        if (string.IsNullOrEmpty(_otp))
        {
            if (!string.IsNullOrWhiteSpace(_otpSeed))
            {
                _otp = TotpGenerator.Generate(_otpSeed);
                Console.WriteLine($"OTP gerado automaticamente da seed: {_otp}");
            }
            else
            {
                Console.Write("OTP: ");
                _otp = Console.ReadLine();
            }
        }
        await TryGetAsync($"{ApiBase}/user/hotp/{Uri.EscapeDataString(_user)}");

        var second = await PostLoginAsync($"{ApiBase}/user/otp/login/{Uri.EscapeDataString(_user)}",
            turnstileToken, otp: _otp ?? string.Empty);

        if (DetectLogin(second) != LoginState.LoggedIn)
            throw new InvalidOperationException($"Falha ao logar (OTP): {ExtractMessage(second)}");

        IsLogged = true;
    }

    private async Task<(string token, bool captcha)> CheckLoginAsync()
    {
        var body = await _http.GetStringAsync($"{ApiBase}/checklogin?v=2");
        string token = string.Empty;
        bool captcha = false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String)
                token = t.GetString() ?? string.Empty;
            if (root.TryGetProperty("captcharequired", out var c))
                captcha = c.ValueKind == JsonValueKind.True;
        }
        catch { /* corpo inesperado */ }
        return (token, captcha);
    }

    private async Task<string> PostLoginAsync(string url, string turnstileToken, string otp)
    {
        // Campos EXATOS observados no DevTools (atenção à caixa: Password, OTP).
        var payload = new
        {
            Password = _password ?? string.Empty,
            OTP = otp,
            challenge = string.Empty,
            recaptcha = string.Empty,
            service = "turnstile",
            token = turnstileToken,
        };
        var resp = await _http.PostAsync(url, JsonContent(payload));
        return await resp.Content.ReadAsStringAsync();
    }

    private enum LoginState { LoggedIn, OtpRequired, Failed }

    // CONFIRMAR com as respostas reais de /user/login e /user/otp/login.
    private static LoginState DetectLogin(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return LoginState.LoggedIn;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // Erros chegam como {"messages":[{"code":...,"message":...}]}.
            if (root.TryGetProperty("messages", out var msgs) &&
                msgs.ValueKind == JsonValueKind.Array && msgs.GetArrayLength() > 0)
            {
                var text = msgs.GetRawText().ToLowerInvariant();
                if (text.Contains("otp") || text.Contains("hotp") || text.Contains("2fa") ||
                    text.Contains("token de acesso") || text.Contains("segundo fator"))
                    return LoginState.OtpRequired;
                return LoginState.Failed;
            }

            if (root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.False)
                return LoginState.Failed;

            foreach (var n in new[] { "otp", "hotp", "needotp", "otprequired" })
                if (root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True)
                    return LoginState.OtpRequired;

            return LoginState.LoggedIn;
        }
        catch
        {
            return LoginState.LoggedIn;
        }
    }

    private static string ExtractMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("messages", out var msgs) &&
                msgs.ValueKind == JsonValueKind.Array && msgs.GetArrayLength() > 0)
            {
                var first = msgs[0];
                if (first.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                    return m.GetString() ?? Trim(body);
            }
        }
        catch { }
        return Trim(body);
    }

    // -------------------------------------------------------------------------
    // Domínios — GET /v2/ajax/domains
    // -------------------------------------------------------------------------
    public async Task<List<Domain>> DomainsAsync()
    {
        EnsureLogged();
        var body = await _http.GetStringAsync($"{ApiBase}/domains");
        using var doc = JsonDocument.Parse(body);

        // CONFIRMAR shape: aceita array direto OU objeto {domains:[...]}.
        JsonElement arr;
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
            arr = doc.RootElement;
        else if (doc.RootElement.TryGetProperty("domains", out var d) && d.ValueKind == JsonValueKind.Array)
            arr = d;
        else
            return new List<Domain>();

        var result = new List<Domain>();
        foreach (var el in arr.EnumerateArray())
        {
            result.Add(new Domain(
                ReadInt(el, "Id", "id"),
                ReadString(el, "FQDN", "fqdn", "domain"),
                ReadString(el, "ExpirationDate", "expiration_date", "expiration", "expiry"),
                ReadString(el, "Status", "status"),
                ReadNullableString(el, "Contact", "contact"),
                ReadNullableString(el, "PayLink", "paylink", "pay_link"),
                ReadBool(el, "Auctionable", "auctionable")));
        }
        return result;
    }

    // -------------------------------------------------------------------------
    // DNS (leitura) — GET /v2/ajax/domain/{fqdn}/freedns-advanced
    // (a "Edição de Zona / Modo Avançado" do painel).
    // -------------------------------------------------------------------------
    public async Task<List<DnsRecord>> ZoneInfoAsync(Domain domain)
        => ParseZone(await GetZoneRawAsync(domain.FQDN));

    /// <summary>JSON cru da zona — útil para diagnóstico/ajuste do parser.</summary>
    public async Task<string> GetZoneRawAsync(string fqdn)
    {
        EnsureLogged();
        return await _http.GetStringAsync($"{ApiBase}/domain/{Uri.EscapeDataString(fqdn)}/freedns-advanced");
    }

    /// <summary>
    /// Parse da resposta de <c>freedns-advanced</c>:
    /// <code>{ "FQDN": "...", "RRs": [ { "Ownername","TTL","Type","Class","Rdata" }, ... ] }</code>
    /// O <c>Rdata</c> reusa o <see cref="DnsRecordParser"/> (que trata MX/TLSA) e o TTL é preservado.
    /// </summary>
    public static List<DnsRecord> ParseZone(string json)
    {
        TryParseZone(json, out var list);
        return list;
    }

    /// <summary>
    /// Tenta interpretar a resposta de zona. Retorna <c>true</c> se a estrutura
    /// foi reconhecida (tem o array <c>RRs</c>), mesmo que vazia — assim dá para
    /// distinguir "zona vazia" de "JSON inesperado".
    /// </summary>
    public static bool TryParseZone(string json, out List<DnsRecord> records)
    {
        records = new List<DnsRecord>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("RRs", out var rrs) || rrs.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var el in rrs.EnumerateArray())
            {
                var type = el.TryGetProperty("Type", out var t) ? (t.GetString() ?? string.Empty) : string.Empty;
                if (string.IsNullOrEmpty(type)) continue;

                var owner = el.TryGetProperty("Ownername", out var o) ? (o.GetString() ?? string.Empty) : string.Empty;
                var rdata = el.TryGetProperty("Rdata", out var d) ? (d.GetString() ?? string.Empty) : string.Empty;
                var ttl = el.TryGetProperty("TTL", out var ttlEl) && ttlEl.ValueKind == JsonValueKind.Number
                    ? ttlEl.GetInt32()
                    : 3600;

                records.Add(DnsRecordParser.Parse($"{owner}|{type}|{rdata}") with { Ttl = ttl });
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // DNS (escrita) — POST /v2/ajax/domain/{fqdn}/freedns-advanced
    // Corpo (delta, confirmado por captura):
    //   { "AddRR": [ {Ownername,Type,Rdata[,TTL]} ], "RemoveRR": [ {Ownername,Type,Rdata} ] }
    // As remoções vão ANTES das adições (requisições separadas), o que evita
    // conflito de CNAME duplicado ao "atualizar" um registro.
    // -------------------------------------------------------------------------

    public Task<ZonePlan> AddRecordsAsync(Domain domain, IEnumerable<DnsRecord> records)
        => ApplyAndSaveAsync(domain.FQDN,
            records.Select(r => new ZoneDirective(false, r.OwnerName, r.Type, r.Data, r.Ttl)).ToList());

    public Task<ZonePlan> RemoveRecordsAsync(Domain domain, IEnumerable<DnsRecord> records)
        => ApplyAndSaveAsync(domain.FQDN,
            records.Select(r => new ZoneDirective(true, r.OwnerName, r.Type, r.Data, r.Ttl)).ToList());

    /// <summary>Lê a zona, calcula o plano e o aplica (se houver mudança).</summary>
    public async Task<ZonePlan> ApplyAndSaveAsync(string fqdn, IReadOnlyList<ZoneDirective> directives)
    {
        var plan = ComputePlan(await GetZoneRawAsync(fqdn), directives);
        if (!plan.IsEmpty) await ApplyPlanAsync(fqdn, plan);
        return plan;
    }

    /// <summary>
    /// Calcula o que adicionar/remover (puro, sem rede), comparando a zona atual
    /// com as diretivas. Semântica DECLARATIVA por (nome, tipo): o conjunto do
    /// arquivo substitui o existente daquele par — atende a unicidade de CNAME e
    /// mantém múltiplos A/TXT quando vários são listados.
    /// </summary>
    public static ZonePlan ComputePlan(string rawZoneJson, IReadOnlyList<ZoneDirective> directives)
    {
        var toAdd = new List<RrDelta>();
        var toRemove = new List<RrDelta>();
        var remaining = ParseCurrentRrs(rawZoneJson);

        static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        // 1) Remoções explícitas (linhas com '-'). Valor vazio = todos do nome+tipo.
        foreach (var d in directives.Where(x => x.Remove))
        {
            var hits = remaining.Where(r => Eq(r.Owner, d.Owner) && Eq(r.Type, d.Type)
                        && (string.IsNullOrEmpty(d.Rdata) || Eq(r.Rdata, d.Rdata))).ToList();
            foreach (var h in hits) { toRemove.Add(h); remaining.Remove(h); }
        }

        // 2) Adições declarativas por (nome, tipo).
        var groups = directives
            .Where(x => !x.Remove && !string.IsNullOrEmpty(x.Rdata))
            .GroupBy(x => (Owner: x.Owner.ToLowerInvariant(), Type: x.Type.ToUpperInvariant()));

        foreach (var g in groups)
        {
            var owner = g.Key.Owner;
            var type = g.Key.Type;
            var desired = g.Select(x => x.Rdata).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // remove do mesmo (nome, tipo) o que NÃO está no conjunto desejado
            foreach (var r in remaining.Where(r => Eq(r.Owner, owner) && Eq(r.Type, type)).ToList())
            {
                if (!desired.Contains(r.Rdata)) { toRemove.Add(r); remaining.Remove(r); }
            }

            // adiciona o que ainda não existe
            foreach (var d in g)
            {
                if (remaining.Any(r => Eq(r.Owner, owner) && Eq(r.Type, type) && Eq(r.Rdata, d.Rdata)))
                    continue;
                var rr = new RrDelta(owner, type, d.Rdata, d.Ttl);
                toAdd.Add(rr);
                remaining.Add(rr); // evita duplicar caso a mesma linha apareça 2x
            }
        }

        return new ZonePlan(toRemove, toAdd);
    }

    /// <summary>Envia o plano: PRIMEIRO as remoções, DEPOIS as adições.</summary>
    public async Task ApplyPlanAsync(string fqdn, ZonePlan plan)
    {
        EnsureLogged();
        var url = $"{ApiBase}/domain/{Uri.EscapeDataString(fqdn)}/freedns-advanced";

        if (plan.ToRemove.Count > 0)
            await PostFreednsAsync(url, Array.Empty<RrDelta>(), plan.ToRemove);
        if (plan.ToAdd.Count > 0)
            await PostFreednsAsync(url, plan.ToAdd, Array.Empty<RrDelta>());
    }

    private async Task PostFreednsAsync(string url, IReadOnlyList<RrDelta> adds, IReadOnlyList<RrDelta> removes)
    {
        var json = BuildFreednsPayload(adds, removes);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync(url, content);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Falha ao salvar (HTTP {(int)resp.StatusCode}): {Trim(body)}");
    }

    /// <summary>
    /// Monta o corpo { AddRR:[...], RemoveRR:[...] } (também usado offline).
    /// Campos iguais aos da captura: Ownername, Type, Rdata (sem TTL — o painel
    /// não o envia; o free DNS usa TTL padrão).
    /// </summary>
    public static string BuildFreednsPayload(IReadOnlyList<RrDelta> adds, IReadOnlyList<RrDelta> removes)
    {
        static object Item(RrDelta r) => new { Ownername = r.Owner, Type = r.Type, Rdata = r.Rdata };
        var payload = new
        {
            AddRR = adds.Select(Item).ToArray(),
            RemoveRR = removes.Select(Item).ToArray(),
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    private static List<RrDelta> ParseCurrentRrs(string rawZoneJson)
    {
        var list = new List<RrDelta>();
        using var doc = JsonDocument.Parse(rawZoneJson);
        if (!doc.RootElement.TryGetProperty("RRs", out var rrs) || rrs.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var el in rrs.EnumerateArray())
        {
            var type = el.TryGetProperty("Type", out var t) ? (t.GetString() ?? string.Empty) : string.Empty;
            if (string.IsNullOrEmpty(type)) continue;
            var owner = el.TryGetProperty("Ownername", out var o) ? (o.GetString() ?? string.Empty) : string.Empty;
            var rdata = el.TryGetProperty("Rdata", out var d) ? (d.GetString() ?? string.Empty) : string.Empty;
            var ttl = el.TryGetProperty("TTL", out var tt) && tt.ValueKind == JsonValueKind.Number ? tt.GetInt32() : 3600;
            list.Add(new RrDelta(owner, type, rdata, ttl));
        }
        return list;
    }

    // -------------------------------------------------------------------------
    // Logout — POST /v2/ajax/user/logout  (corpo {})
    // -------------------------------------------------------------------------
    public async Task LogoutAsync()
    {
        if (!IsLogged) return;
        try { await _http.PostAsync($"{ApiBase}/user/logout", JsonContent(new { })); } catch { }
        IsLogged = false;
    }

    // -------------------------------------------------------------------------
    // Fábricas estáticas (porta dos create_*_record)
    // -------------------------------------------------------------------------
    public static ARecord CreateA(string owner, string ip) => new(owner, ip);
    public static AaaaRecord CreateAaaa(string owner, string ipv6) => new(owner, ipv6);
    public static CnameRecord CreateCname(string owner, string server) => new(owner, server);
    public static MxRecord CreateMx(string owner, int priority, string emailServer) => new(owner, priority, emailServer);
    public static TxtRecord CreateTxt(string owner, string data) => new(owner, data);

    // -------------------------------------------------------------------------
    // Auxiliares
    // -------------------------------------------------------------------------
    private async Task TryGetAsync(string url)
    {
        try { using var _ = await _http.GetAsync(url); } catch { }
    }

    private static StringContent JsonContent(object payload)
        => new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private static string Trim(string s) => s.Length > 300 ? s[..300] + "…" : s;

    private static string ReadString(JsonElement el, params string[] names)
    {
        foreach (var n in names)
            if (el.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? string.Empty;
        return string.Empty;
    }

    private static string ReadNullableString(JsonElement el, params string[] names)
    {
        foreach (var n in names)
            if (el.TryGetProperty(n, out var v))
                return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
        return null;
    }

    private static int ReadInt(JsonElement el, params string[] names)
    {
        foreach (var n in names)
            if (el.TryGetProperty(n, out var v))
                return v.ValueKind switch
                {
                    JsonValueKind.Number => v.GetInt32(),
                    JsonValueKind.String => int.TryParse(v.GetString(), out var x) ? x : 0,
                    _ => 0,
                };
        return 0;
    }

    private static bool ReadBool(JsonElement el, params string[] names)
    {
        foreach (var n in names)
            if (el.TryGetProperty(n, out var v))
                return v.ValueKind == JsonValueKind.True;
        return false;
    }

    private void EnsureLogged()
    {
        if (!IsLogged)
            throw new InvalidOperationException("Não autenticado. Chame LoginAsync() antes.");
    }

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// Injeta, a cada requisição, o cabeçalho <c>X-XSRF-TOKEN</c> com o valor
    /// atual do cookie <c>XSRF-TOKEN</c> (que o servidor rotaciona ao longo do
    /// fluxo). É assim que o painel evita o erro "X-XSRF-TOKEN Header inválido".
    /// </summary>
    private sealed class XsrfHandler : DelegatingHandler
    {
        private readonly CookieContainer _cookies;

        public XsrfHandler(CookieContainer cookies, HttpMessageHandler inner) : base(inner)
            => _cookies = cookies;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var token = _cookies.GetCookies(new Uri(Origin))["XSRF-TOKEN"]?.Value;
            if (!string.IsNullOrEmpty(token))
            {
                request.Headers.Remove("X-XSRF-TOKEN");
                request.Headers.TryAddWithoutValidation("X-XSRF-TOKEN", token);
            }
            return base.SendAsync(request, cancellationToken);
        }
    }
}
