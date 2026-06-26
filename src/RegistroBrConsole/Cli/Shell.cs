using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RegistroBrConsole.Api;
using RegistroBrConsole.Configuration;
using RegistroBrConsole.Dns;
using RegistroBrConsole.Security;

namespace RegistroBrConsole.Cli;

/// <summary>
/// Shell interativo — porta de <c>shell.py</c> (cmd.Cmd).
///
/// Diferença em relação ao original: o shell Python acumulava estados
/// (Add/Delete) mas nunca chamava add_records/remove_records (não "commitava").
/// Aqui os comandos add_* / del aplicam a mudança imediatamente, deixando o
/// shell de fato funcional.
/// </summary>
internal static class Shell
{
    private static RegistroBrApi _api;
    private static List<Domain> _domains;
    private static string _currentFqdn; // último domínio usado (para add/update sem domínio)

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("Bem-vindo ao shell do registro.br. Digite 'help' ou '?' para os comandos.\n");

        while (true)
        {
            Console.Write("(registro.br) ");
            var line = Console.ReadLine();
            if (line is null) break; // EOF
            line = line.Trim();
            if (line.Length > 0 && line[0] == (char)0xFEFF) // remove BOM de entrada canalizada
                line = line.Substring(1).Trim();
            if (line.Length == 0) continue;

            var split = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var cmd = split[0].ToLowerInvariant();
            var arg = split.Length > 1 ? split[1].Trim() : string.Empty;

            try
            {
                switch (cmd)
                {
                    case "help" or "?":
                        PrintHelp();
                        break;
                    case "otp":
                        ShowOtp(arg);
                        break;
                    case "login":
                        await LoginAsync();
                        break;
                    case "domains":
                        await DomainsAsync();
                        break;
                    case "zone" or "zone_info":
                        await ZoneInfoAsync(arg);
                        break;
                    case "zone_raw":
                        await ZoneRawAsync(arg);
                        break;
                    case "use":
                        await UseAsync(arg);
                        break;
                    case "add" or "update":
                        await ApplyFileAsync(arg);
                        break;
                    case "add_a":
                        await AddAAsync(arg);
                        break;
                    case "add_cname":
                        await AddCnameAsync(arg);
                        break;
                    case "add_mx":
                        await AddMxAsync(arg);
                        break;
                    case "add_txt" or "new_txt_record":
                        await AddTxtAsync(arg);
                        break;
                    case "del" or "delete_record":
                        await DeleteAsync(arg);
                        break;
                    case "logout":
                        await LogoutAsync();
                        break;
                    case "exit" or "quit":
                        await LogoutAsync();
                        return 0;
                    default:
                        Console.WriteLine($"Comando desconhecido: {cmd}. Digite 'help'.");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Erro: {ex.Message}");
            }
        }

        await LogoutAsync();
        return 0;
    }

    private static void PrintHelp() => Console.WriteLine("""
        Comandos:
          login                         Autentica no registro.br
          otp [seed]                    Mostra o código TOTP atual (2FA)
          domains                       Lista os domínios da conta
          zone_info <dominio>           Lista os registros da zona
          zone_raw <dominio>            Mostra o JSON cru da zona (diagnóstico)
          use <dominio>                 Define o domínio atual (usado por add/update)
          add [dominio] <arquivo>       Aplica um arquivo de zona (+ adiciona, - remove)
          update [dominio] <arquivo>    Igual a 'add'
          add_a <dominio>               Adiciona registro A
          add_cname <dominio>           Adiciona registro CNAME
          add_mx <dominio>              Adiciona registro MX
          add_txt <dominio>             Adiciona registro TXT
          del <dominio>                 Remove registros (escolha por índice)
          logout                        Encerra a sessão
          exit                          Sai do shell
        """);

    private static void ShowOtp(string arg)
    {
        var seed = string.IsNullOrWhiteSpace(arg) ? Settings.Load().OtpSeed : arg.Trim();
        if (string.IsNullOrWhiteSpace(seed))
        {
            Console.WriteLine("Nenhuma seed. Passe 'otp <seed>' ou configure RegistroBr:OtpSeed no local.settings.json.");
            return;
        }
        try
        {
            var code = TotpGenerator.Generate(seed);
            Console.WriteLine($"OTP: {code}   (expira em {TotpGenerator.SecondsRemaining()}s)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Seed inválida: {ex.Message}");
        }
    }

    // -------------------------------------------------------------------------
    private static async Task LoginAsync()
    {
        var settings = Settings.Load();

        var user = settings.User;
        if (string.IsNullOrWhiteSpace(user))
        {
            Console.Write("user: ");
            user = Console.ReadLine() ?? string.Empty;
        }

        var password = settings.Password;
        if (string.IsNullOrWhiteSpace(password))
            password = Input.ReadHidden("password: ");

        // Com seed no local.settings.json, o OTP é gerado sozinho; senão pergunta.
        string otp = null;
        if (string.IsNullOrWhiteSpace(settings.OtpSeed))
        {
            Console.Write("otp (enter se não usar 2FA): ");
            var typed = Console.ReadLine();
            otp = string.IsNullOrWhiteSpace(typed) ? null : typed;
        }

        _api?.Dispose();
        _api = new RegistroBrApi(user, password, otp, settings.OtpSeed);
        await _api.LoginAsync();
        _domains = null;
        Console.WriteLine("Logado.");
    }

    private static async Task DomainsAsync()
    {
        var domains = await LoadDomainsAsync();
        foreach (var d in domains) Console.WriteLine(d.FQDN);
    }

    private static async Task ZoneInfoAsync(string fqdn)
    {
        var domain = await ResolveDomainAsync(fqdn);
        if (domain is null) return;

        var raw = await _api.GetZoneRawAsync(domain.FQDN);
        if (RegistroBrApi.TryParseZone(raw, out var records))
        {
            Console.WriteLine($"==== {domain.FQDN} ({records.Count} registro(s)) ====");
            if (records.Count == 0) Console.WriteLine("  (zona vazia)");
            foreach (var r in records) Console.WriteLine("  " + r);
        }
        else
        {
            Console.WriteLine("Não consegui mapear os registros automaticamente. JSON bruto da zona:");
            Console.WriteLine(raw.Length > 4000 ? raw[..4000] + "…" : raw);
            Console.WriteLine("\n(Cole esse JSON aqui para o parser ser finalizado.)");
        }
    }

    private static async Task ZoneRawAsync(string fqdn)
    {
        var domain = await ResolveDomainAsync(fqdn);
        if (domain is null) return;
        Console.WriteLine(await _api.GetZoneRawAsync(domain.FQDN));
    }

    private static async Task UseAsync(string fqdn)
    {
        var domain = await ResolveDomainAsync(fqdn);
        if (domain is not null) Console.WriteLine($"Domínio atual: {domain.FQDN}");
    }

    // add/update <arquivo>  ou  add/update <dominio> <arquivo>
    private static async Task ApplyFileAsync(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            Console.WriteLine("Uso: add [dominio] <arquivo>");
            return;
        }

        // Decide quem é domínio e quem é arquivo.
        string fqdn, file;
        if (File.Exists(arg))
        {
            fqdn = _currentFqdn ?? string.Empty;
            file = arg;
        }
        else
        {
            var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2) { fqdn = parts[0]; file = parts[1]; }
            else { fqdn = _currentFqdn ?? string.Empty; file = arg; }
        }

        if (!File.Exists(file))
        {
            Console.WriteLine($"Arquivo não encontrado: {file}");
            return;
        }

        var domain = await ResolveDomainAsync(fqdn);
        if (domain is null) return;

        var dirs = ZoneFile.ParseFile(file);
        if (dirs.Count == 0)
        {
            Console.WriteLine("Nenhuma linha válida no arquivo.");
            return;
        }

        var raw = await _api.GetZoneRawAsync(domain.FQDN);
        var plan = RegistroBrApi.ComputePlan(raw, dirs);

        Console.WriteLine($"== Prévia para {domain.FQDN} ==");
        Console.WriteLine($"A remover ({plan.ToRemove.Count}):");
        foreach (var r in plan.ToRemove) Console.WriteLine("  - " + r);
        Console.WriteLine($"A adicionar ({plan.ToAdd.Count}):");
        foreach (var a in plan.ToAdd) Console.WriteLine("  + " + a);

        if (plan.IsEmpty)
        {
            Console.WriteLine("Nada a alterar (a zona já está como o arquivo).");
            return;
        }

        Console.Write("\nAplicar no registro.br? (s/N): ");
        var ans = (Console.ReadLine() ?? string.Empty).Trim().ToLowerInvariant();
        if (ans is not ("s" or "sim" or "y" or "yes"))
        {
            Console.WriteLine("Cancelado.");
            return;
        }

        await _api.ApplyPlanAsync(domain.FQDN, plan);
        Console.WriteLine($"Enviado (remoções e depois adições). Confira com 'zone_info {domain.FQDN}'.");
    }

    private static async Task AddAAsync(string fqdn)
    {
        var domain = await ResolveDomainAsync(fqdn);
        if (domain is null) return;
        var owner = Ask("ownername (vazio = @): ");
        var ip = Ask("ip: ");
        await ApplyAdd(domain, RegistroBrApi.CreateA(owner, ip));
    }

    private static async Task AddCnameAsync(string fqdn)
    {
        var domain = await ResolveDomainAsync(fqdn);
        if (domain is null) return;
        var owner = Ask("ownername: ");
        var server = Ask("server (destino): ");
        await ApplyAdd(domain, RegistroBrApi.CreateCname(owner, server));
    }

    private static async Task AddMxAsync(string fqdn)
    {
        var domain = await ResolveDomainAsync(fqdn);
        if (domain is null) return;
        var owner = Ask("ownername (vazio = @): ");
        var priorityText = Ask("prioridade: ");
        if (!int.TryParse(priorityText, out var priority))
        {
            Console.WriteLine("Prioridade inválida.");
            return;
        }
        var server = Ask("servidor de e-mail: ");
        await ApplyAdd(domain, RegistroBrApi.CreateMx(owner, priority, server));
    }

    private static async Task AddTxtAsync(string fqdn)
    {
        var domain = await ResolveDomainAsync(fqdn);
        if (domain is null) return;
        var owner = Ask("ownername: ");
        var value = Ask("value: ");
        await ApplyAdd(domain, RegistroBrApi.CreateTxt(owner, value));
    }

    private static async Task DeleteAsync(string fqdn)
    {
        var domain = await ResolveDomainAsync(fqdn);
        if (domain is null) return;

        var records = await _api.ZoneInfoAsync(domain);
        if (records.Count == 0)
        {
            Console.WriteLine("Nenhum registro na zona.");
            return;
        }

        for (int i = 0; i < records.Count; i++)
            Console.WriteLine($"  {i} - {records[i]}");

        var chosen = Ask("quais remover (índices separados por vírgula)? ");
        var toRemove = chosen
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var n) ? n : -1)
            .Where(n => n >= 0 && n < records.Count)
            .Select(n => records[n])
            .ToList();

        if (toRemove.Count == 0)
        {
            Console.WriteLine("Nada selecionado.");
            return;
        }

        await _api.RemoveRecordsAsync(domain, toRemove);
        Console.WriteLine($"{toRemove.Count} registro(s) enviado(s) para remoção. Confira com 'zone_info {domain.FQDN}'.");
    }

    private static async Task LogoutAsync()
    {
        if (_api is { IsLogged: true })
        {
            Console.WriteLine("Encerrando a sessão do registro.br.");
            await _api.LogoutAsync();
        }
    }

    // -------------------------------------------------------------------------
    private static async Task ApplyAdd(Domain domain, DnsRecord record)
    {
        await _api.AddRecordsAsync(domain, new[] { record });
        Console.WriteLine($"Adicionado: {record}. Confira com 'zone_info {domain.FQDN}'.");
    }

    private static async Task<List<Domain>> LoadDomainsAsync()
    {
        RequireLogin();
        return _domains ??= await _api.DomainsAsync();
    }

    private static async Task<Domain> ResolveDomainAsync(string fqdn)
    {
        if (string.IsNullOrWhiteSpace(fqdn)) fqdn = _currentFqdn ?? string.Empty;
        if (string.IsNullOrWhiteSpace(fqdn))
        {
            Console.WriteLine("Informe o domínio (ou rode 'use <dominio>' / 'zone_info <dominio>' antes).");
            return null;
        }
        var domain = (await LoadDomainsAsync()).FirstOrDefault(d => d.FQDN == fqdn);
        if (domain is null)
        {
            Console.WriteLine($"Domínio '{fqdn}' não encontrado na conta.");
            return null;
        }
        _currentFqdn = domain.FQDN; // lembra para os próximos comandos
        return domain;
    }

    private static void RequireLogin()
    {
        if (_api is not { IsLogged: true })
            throw new InvalidOperationException("Faça 'login' primeiro.");
    }

    private static string Ask(string prompt)
    {
        Console.Write(prompt);
        return Console.ReadLine() ?? string.Empty;
    }
}
