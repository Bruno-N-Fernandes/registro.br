using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RegistroBrConsole.Dns;

/// <summary>
/// Uma diretiva lida de um arquivo de zona: adicionar (padrão) ou remover (linha
/// iniciada por '-'). <see cref="Rdata"/> é exatamente o dado do registro.br
/// (ex.: "52.167.15.22", "alvo.azure.net", "10 mail.dominio.com").
/// </summary>
public sealed record ZoneDirective(bool Remove, string Owner, string Type, string Rdata, int Ttl);

/// <summary>
/// Lê arquivos de zona no estilo BIND/zone-file, um registro por linha:
/// <code>nome tipo rdata [ttl]</code> (aceita também a ordem <c>tipo nome rdata</c>).
/// - <c>@</c> ou nome vazio = raiz; <c>#</c>/<c>;</c> = comentário.
/// - Linha começando com <c>-</c> = remover; com <c>+</c> (ou nada) = adicionar.
/// - O TTL é o último token, se for um inteiro.
/// - Valores com espaços (TXT, MX) podem usar aspas: <c>@ TXT "v=spf1 ..."</c>.
/// </summary>
public static class ZoneFile
{
    // Tipos conhecidos — usados para detectar a ordem (nome-primeiro x tipo-primeiro).
    private static readonly HashSet<string> KnownTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "A", "AAAA", "CNAME", "MX", "TXT", "NS", "SRV", "CAA", "TLSA",
        "PTR", "DS", "DNSKEY", "SSHFP", "NAPTR", "ALIAS",
    };

    public static List<ZoneDirective> ParseFile(string path)
        => Parse(File.ReadAllLines(path));

    public static List<ZoneDirective> Parse(IEnumerable<string> lines)
    {
        var list = new List<ZoneDirective>();
        foreach (var line in lines)
        {
            var d = ParseLine(line);
            if (d is not null) list.Add(d);
        }
        return list;
    }

    public static ZoneDirective ParseLine(string raw)
    {
        var line = (raw ?? string.Empty).Trim();
        if (line.Length == 0 || line[0] == '#' || line[0] == ';') return null;

        bool remove = false;
        if (line[0] == '-') { remove = true; line = line[1..].TrimStart(); }
        else if (line[0] == '+') { line = line[1..].TrimStart(); }

        var tok = Tokenize(line);
        if (tok.Count < 2) return null; // precisa ao menos de nome + tipo

        // Detecta a ordem: se o 1º token é um tipo e o 2º não, é "tipo nome ...";
        // caso contrário assume o padrão zone-file "nome tipo ..." (desempate).
        string name, type;
        if (KnownTypes.Contains(tok[0]) && !KnownTypes.Contains(tok[1]))
        {
            type = tok[0].ToUpperInvariant();
            name = tok[1];
        }
        else
        {
            name = tok[0];
            type = tok[1].ToUpperInvariant();
        }

        var owner = name == "@" ? string.Empty : name;
        var rest = tok.GetRange(2, tok.Count - 2);
        int ttl = 3600;
        // TTL = último token, se inteiro, desde que haja outro token de rdata antes.
        if (rest.Count >= 2 && IsInt(rest[^1]))
        {
            ttl = int.Parse(rest[^1]);
            rest.RemoveAt(rest.Count - 1);
        }

        var rdata = string.Join(' ', rest).Trim();
        return new ZoneDirective(remove, owner, type, rdata, ttl);
    }

    // Quebra por espaços, mas mantém trechos entre aspas duplas (com as aspas).
    private static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        int i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            if (i >= line.Length) break;

            int start = i;
            if (line[i] == '"')
            {
                i++;
                while (i < line.Length && line[i] != '"') i++;
                if (i < line.Length) i++; // inclui a aspa de fechamento
            }
            else
            {
                while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
            }
            tokens.Add(line[start..i]);
        }
        return tokens;
    }

    private static bool IsInt(string s) => s.Length > 0 && s.All(char.IsDigit);
}
