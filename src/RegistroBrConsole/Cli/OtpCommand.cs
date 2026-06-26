using System;
using RegistroBrConsole.Configuration;
using RegistroBrConsole.Security;

namespace RegistroBrConsole.Cli;

/// <summary>
/// Comando <c>otp</c>: imprime o código TOTP atual.
///   registrobr otp                 → usa a seed do local.settings.json
///   registrobr otp &lt;seed-base32&gt;     → usa a seed informada
///   registrobr otp --selftest      → valida contra os vetores do RFC 6238
/// </summary>
internal static class OtpCommand
{
    public static int Run(string[] args, AppSettings settings)
    {
        if (args.Length > 0 && (args[0] is "--selftest" or "selftest" or "test"))
        {
            var ok = TotpGenerator.SelfTest(out var report);
            Console.WriteLine("Self-test TOTP (RFC 6238):");
            Console.Write(report);
            Console.WriteLine(ok ? "Resultado: PASSOU" : "Resultado: FALHOU");
            return ok ? 0 : 1;
        }

        var seed = args.Length > 0 ? args[0] : settings.OtpSeed;
        if (string.IsNullOrWhiteSpace(seed))
        {
            Console.Error.WriteLine(
                "Nenhuma seed. Passe como argumento (registrobr otp <seed>) ou configure " +
                "RegistroBr:OtpSeed no local.settings.json.");
            return 2;
        }

        try
        {
            var code = TotpGenerator.Generate(seed);
            var remaining = TotpGenerator.SecondsRemaining();
            Console.WriteLine($"{code}   (expira em {remaining}s)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Seed inválida: {ex.Message}");
            return 1;
        }
    }
}
