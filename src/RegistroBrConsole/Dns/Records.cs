using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace RegistroBrConsole.Dns;

/// <summary>
/// Domínio retornado pelo endpoint <c>user_domains</c>.
/// Porta da <c>namedtuple('Domain', ...)</c> do Python.
/// </summary>
public sealed record Domain(
    int Id,
    string FQDN,
    string ExpirationDate,
    string Status,
    string Contact,
    string PayLink,
    bool Auctionable);

/// <summary>
/// Base de um registro DNS. O formato "de fio" do registro.br é
/// <c>ownername|TIPO|dado</c> (é assim que a zona é lida e gravada).
/// </summary>
public abstract record DnsRecord(string OwnerName, string Type)
{
    /// <summary>Tempo de vida (segundos). Padrão do painel: 3600.</summary>
    public int Ttl { get; init; } = 3600;

    /// <summary>Parte "dado" no formato de fio (varia por tipo).</summary>
    public abstract string Data { get; }

    /// <summary>Serializa para <c>ownername|TIPO|dado</c>.</summary>
    public string ToWire() => $"{OwnerName}|{Type}|{Data}";

    // 'sealed' impede que cada record derivado gere o próprio ToString e
    // sobreponha este (senão a saída sai no formato automático do record).
    public sealed override string ToString()
    {
        var name = string.IsNullOrEmpty(OwnerName) ? "@" : OwnerName;
        return $"{Type,-6} {name,-28} TTL={Ttl,-5} {Data}";
    }
}

public sealed record ARecord(string OwnerName, string Ip)
    : DnsRecord(OwnerName, "A") { public override string Data => Ip; }

public sealed record AaaaRecord(string OwnerName, string IpV6)
    : DnsRecord(OwnerName, "AAAA") { public override string Data => IpV6; }

public sealed record CnameRecord(string OwnerName, string Server)
    : DnsRecord(OwnerName, "CNAME") { public override string Data => Server; }

public sealed record TxtRecord(string OwnerName, string Text)
    : DnsRecord(OwnerName, "TXT") { public override string Data => Text; }

public sealed record MxRecord(string OwnerName, int Priority, string EmailServer)
    : DnsRecord(OwnerName, "MX") { public override string Data => $"{Priority} {EmailServer}"; }

/// <summary>
/// Registro TLSA. Mantém os pares (código, descrição) como no Python original,
/// mas o formato de fio usa apenas os códigos numéricos.
/// </summary>
public sealed record TlsaRecord(
    string OwnerName,
    (int Code, string Description) Usage,
    (int Code, string Description) Selector,
    (int Code, string Description) Matching,
    string CertData) : DnsRecord(OwnerName, "TLSA")
{
    public override string Data => $"{Usage.Code} {Selector.Code} {Matching.Code} {CertData}";
}

/// <summary>Tipo desconhecido — equivalente ao namedtuple genérico do Python.</summary>
public sealed record GenericRecord(string OwnerName, string TypeName, string RawData)
    : DnsRecord(OwnerName, TypeName) { public override string Data => RawData; }

/// <summary>Um registro do delta de gravação (AddRR/RemoveRR do freedns-advanced).</summary>
public sealed record RrDelta(string Owner, string Type, string Rdata, int Ttl)
{
    public override string ToString()
    {
        var name = string.IsNullOrEmpty(Owner) ? "@" : Owner;
        return $"{Type,-6} {name,-28} TTL={Ttl,-5} {Rdata}";
    }
}

/// <summary>Plano de alteração da zona: o que remover e o que adicionar.</summary>
public sealed record ZonePlan(IReadOnlyList<RrDelta> ToRemove, IReadOnlyList<RrDelta> ToAdd)
{
    public bool IsEmpty => ToRemove.Count == 0 && ToAdd.Count == 0;
}

/// <summary>
/// Faz o parse de uma linha <c>ownername|TIPO|dado</c> vinda da zona,
/// equivalente ao <c>__parse_records</c> do Python.
/// </summary>
public static class DnsRecordParser
{
    private static readonly Dictionary<int, string> UsageType = new()
    {
        [0] = "CA",
        [1] = "Service certificate",
        [2] = "Trust Anchor",
        [3] = "Domain-issued certificate",
    };

    private static readonly Dictionary<int, string> SelectorType = new()
    {
        [0] = "Subject Public Key",
        [1] = "Subject Public Key",
    };

    private static readonly Dictionary<int, string> MatchingType = new()
    {
        [1] = "SHA-256",
        [2] = "SHA-512",
    };

    public static DnsRecord Parse(string raw)
    {
        var parts = raw.Split('|', 3);
        if (parts.Length < 3)
            throw new FormatException($"Registro inválido (esperado owner|tipo|dado): '{raw}'");

        var owner = parts[0];
        var type = parts[1];
        var data = parts[2];

        return type switch
        {
            "A" => new ARecord(owner, data),
            "AAAA" => new AaaaRecord(owner, data),
            "CNAME" => new CnameRecord(owner, data),
            "TXT" => new TxtRecord(owner, data),
            "MX" => ParseMx(owner, data),
            "TLSA" => ParseTlsa(owner, data),
            _ => new GenericRecord(owner, type, data),
        };
    }

    // Porta de __parse_mx: "prioridade servidor".
    private static MxRecord ParseMx(string owner, string data)
    {
        var t = data.Trim().Split(' ', 2);
        var priority = int.Parse(t[0]);
        return new MxRecord(owner, priority, t.Length > 1 ? t[1] : string.Empty);
    }

    // Porta de __parse_tlsa: "usage selector matching dado".
    private static TlsaRecord ParseTlsa(string owner, string data)
    {
        var t = Regex.Split(data.Trim(), @"\s+");
        int usage = int.Parse(t[0]);
        int selector = int.Parse(t[1]);
        int matching = int.Parse(t[2]);
        var cert = t.Length > 3 ? t[3] : string.Empty;

        return new TlsaRecord(
            owner,
            (usage, UsageType.GetValueOrDefault(usage, "?")),
            (selector, SelectorType.GetValueOrDefault(selector, "?")),
            (matching, MatchingType.GetValueOrDefault(matching, "?")),
            cert);
    }
}
