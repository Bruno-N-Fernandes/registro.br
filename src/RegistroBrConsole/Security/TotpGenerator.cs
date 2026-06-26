using System;
using System.Linq;
using System.Text;
using OtpNet;

namespace RegistroBrConsole.Security;

/// <summary>
/// Gerador de OTP (TOTP, 6 dígitos, SHA-1, janela de 30s — o padrão dos
/// autenticadores tipo Google Authenticator/Authy, que é o usado pelo
/// registro.br). A seed (segredo em Base32) é configurada em local.settings.json.
/// </summary>
internal static class TotpGenerator
{
    /// <summary>Código TOTP atual para a seed Base32 informada.</summary>
    public static string Generate(string base32Seed)
    {
        var totp = new Totp(DecodeSeed(base32Seed));
        return totp.ComputeTotp();
    }

    /// <summary>Segundos restantes até o código atual expirar.</summary>
    public static int SecondsRemaining() => new Totp(new byte[10]).RemainingSeconds();

    private static byte[] DecodeSeed(string seed)
    {
        // Aceita seed com espaços/hífens (como costuma ser exibida) e minúsculas.
        var clean = new string((seed ?? string.Empty)
            .Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray())
            .ToUpperInvariant();
        if (clean.Length == 0)
            throw new ArgumentException("Seed vazia.");
        return Base32Encoding.ToBytes(clean);
    }

    /// <summary>
    /// Verifica a implementação contra os vetores de teste do RFC 6238
    /// (segredo ASCII "12345678901234567890"). Útil para confirmar que a seed e
    /// a lib estão corretas, sem depender de rede.
    /// </summary>
    public static bool SelfTest(out string report)
    {
        var key = Base32Encoding.ToBytes("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ");
        var totp = new Totp(key); // SHA-1, 6 dígitos, 30s

        // (unix time, 6 dígitos esperados = últimos 6 do vetor de 8 dígitos do RFC)
        var cases = new (long Unix, string Expect)[]
        {
            (59L,          "287082"),
            (1111111109L,  "081804"),
            (1111111111L,  "050471"),
            (1234567890L,  "005924"),
            (2000000000L,  "279037"),
            (20000000000L, "353130"),
        };

        var sb = new StringBuilder();
        bool all = true;
        foreach (var (unix, expect) in cases)
        {
            var when = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
            var got = totp.ComputeTotp(when);
            bool ok = got == expect;
            all &= ok;
            sb.AppendLine($"  t={unix,-12} esperado={expect} obtido={got} {(ok ? "OK" : "ERRO")}");
        }
        report = sb.ToString();
        return all;
    }
}
