using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using RegistroBrConsole.Api;
using RegistroBrConsole.Cli;
using RegistroBrConsole.Configuration;
using RegistroBrConsole.Dns;

namespace RegistroBrConsole;

/// <summary>
/// Front-end console. Reúne o comportamento de <c>cli.py</c> (modo argumentos)
/// e de <c>shell.py</c> (modo interativo).
///
/// Uso:
///   registrobr &lt;usuario&gt; [-p senha] [-o otp] domains
///   registrobr &lt;usuario&gt; [-p senha] [-o otp] zone_info &lt;dominio&gt;
///   registrobr shell      (ou sem argumentos)  → shell interativo
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            if (args.Length == 0 ||
                args[0].Equals("shell", StringComparison.OrdinalIgnoreCase))
            {
                return await Shell.RunAsync();
            }

            if (args[0] is "-h" or "--help" or "/?")
            {
                PrintUsage();
                return 0;
            }

            // Comando offline: gera/valida OTP sem precisar logar.
            if (args[0].Equals("otp", StringComparison.OrdinalIgnoreCase))
                return OtpCommand.Run(args[1..], Settings.Load());

            // Comando offline: testa o parser de zona a partir de um JSON salvo.
            if (args[0].Equals("zone_parse", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length < 2)
                {
                    Console.Error.WriteLine("Uso: registrobr zone_parse <arquivo.json>");
                    return 2;
                }
                if (RegistroBrApi.TryParseZone(File.ReadAllText(args[1]), out var recs))
                {
                    Console.WriteLine($"{recs.Count} registro(s):");
                    foreach (var r in recs) Console.WriteLine("  " + r);
                }
                else
                {
                    Console.WriteLine("JSON não reconhecido como zona (sem array 'RRs').");
                }
                return 0;
            }

            // Comando offline: testa a aplicação de um arquivo de zona sobre um JSON salvo.
            //   registrobr zone_apply <zona.json> <arquivo.txt>
            if (args[0].Equals("zone_apply", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length < 3)
                {
                    Console.Error.WriteLine("Uso: registrobr zone_apply <zona.json> <arquivo.txt>");
                    return 2;
                }
                var dirs = ZoneFile.ParseFile(args[2]);
                var plan = RegistroBrApi.ComputePlan(File.ReadAllText(args[1]), dirs);
                Console.WriteLine($"A remover ({plan.ToRemove.Count}):");
                foreach (var r in plan.ToRemove) Console.WriteLine("  - " + r);
                Console.WriteLine($"A adicionar ({plan.ToAdd.Count}):");
                foreach (var a in plan.ToAdd) Console.WriteLine("  + " + a);
                Console.WriteLine("\n--- POST 1 (remoções primeiro) ---");
                Console.WriteLine(RegistroBrApi.BuildFreednsPayload(Array.Empty<RrDelta>(), plan.ToRemove));
                Console.WriteLine("\n--- POST 2 (adições) ---");
                Console.WriteLine(RegistroBrApi.BuildFreednsPayload(plan.ToAdd, Array.Empty<RrDelta>()));
                return 0;
            }

            return await RunCliAsync(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Erro: {ex.Message}");
            return 1;
        }
    }

    // -------------------------------------------------------------------------
    // Modo argumentos (porta de cli.py)
    // -------------------------------------------------------------------------
    private static async Task<int> RunCliAsync(string[] args)
    {
        var settings = Settings.Load();

        // Se o 1º token for um comando, o usuário vem do local.settings.json.
        int start = 0;
        string user = null;
        if (!IsCommand(args[0]))
        {
            user = args[0];
            start = 1;
        }
        user ??= settings.User;

        string password = null, otp = null, command = null, domain = null;

        for (int i = start; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-p":
                case "--password":
                    password = Next(args, ref i);
                    break;
                case "-o":
                case "--otp":
                    otp = Next(args, ref i);
                    break;
                case "domains":
                    command = "domains";
                    break;
                case "zone":
                case "zone_info":
                    command = "zone_info";
                    domain = Next(args, ref i);
                    break;
                default:
                    Console.Error.WriteLine($"Argumento desconhecido: {args[i]}");
                    PrintUsage();
                    return 2;
            }
        }

        if (command is null)
        {
            PrintUsage();
            return 2;
        }

        if (string.IsNullOrWhiteSpace(user))
        {
            Console.Error.WriteLine("Informe o usuário (argumento ou RegistroBr:User no local.settings.json).");
            return 2;
        }

        password ??= settings.Password;
        password ??= Input.ReadHidden("Senha: ");

        using var api = new RegistroBrApi(user, password, otp, settings.OtpSeed);
        Console.WriteLine("Autenticando…");
        await api.LoginAsync();
        Console.WriteLine("Login OK.\n");

        switch (command)
        {
            case "domains":
                foreach (var d in await api.DomainsAsync())
                    Console.WriteLine($"Domínio {d.FQDN} | status {d.Status} | expira em {d.ExpirationDate}");
                break;

            case "zone_info":
                var dom = (await api.DomainsAsync()).FirstOrDefault(d => d.FQDN == domain);
                if (dom is null)
                {
                    Console.Error.WriteLine($"Domínio '{domain}' não encontrado na conta.");
                    await api.LogoutAsync();
                    return 3;
                }
                var raw = await api.GetZoneRawAsync(dom.FQDN);
                if (RegistroBrApi.TryParseZone(raw, out var records))
                {
                    Console.WriteLine($"Zona de {dom.FQDN} ({records.Count} registro(s)):");
                    if (records.Count == 0) Console.WriteLine("  (zona vazia)");
                    foreach (var r in records) Console.WriteLine("  " + r);
                }
                else
                {
                    Console.WriteLine("Não consegui mapear os registros. JSON bruto da zona:");
                    Console.WriteLine(raw.Length > 4000 ? raw[..4000] + "…" : raw);
                }
                break;
        }

        await api.LogoutAsync();
        return 0;
    }

    private static string Next(string[] args, ref int i)
        => (i + 1 < args.Length) ? args[++i] : null;

    private static bool IsCommand(string a)
        => a is "domains" or "zone" or "zone_info";

    private static void PrintUsage()
    {
        Console.WriteLine("""
            registro.br console

            Uso:
              registrobr [usuario] [-p|--password <senha>] [-o|--otp <otp>] domains
              registrobr [usuario] [-p|--password <senha>] [-o|--otp <otp>] zone_info <dominio>
              registrobr otp [seed]   Mostra o código TOTP atual (--selftest valida a lib)
              registrobr shell        Shell interativo (login, domains, add/del, ...)
              registrobr               (sem argumentos) abre o shell interativo

            Observações:
              - usuario/senha/seed podem vir de local.settings.json (RegistroBr:User,
                RegistroBr:Password, RegistroBr:OtpSeed). Argumentos têm prioridade.
              - Se a conta tiver 2FA e houver seed configurada, o OTP é gerado sozinho.
              - A senha é solicitada de forma oculta se não vier por -p nem pelo settings.
            """);
    }
}

/// <summary>Leitura de senha com mascaramento, com fallback para entrada redirecionada.</summary>
internal static class Input
{
    public static string ReadHidden(string prompt)
    {
        Console.Write(prompt);

        // Sem console interativo (entrada redirecionada): lê normal.
        if (Console.IsInputRedirected)
            return Console.ReadLine() ?? string.Empty;

        var sb = new StringBuilder();
        ConsoleKeyInfo key;
        while ((key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                {
                    sb.Length--;
                    Console.Write("\b \b");
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                sb.Append(key.KeyChar);
                Console.Write('*');
            }
        }
        Console.WriteLine();
        return sb.ToString();
    }
}
