using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RegistroBrConsole.Configuration;

/// <summary>Configuração lida de <c>local.settings.json</c>.</summary>
public sealed record AppSettings(string User, string Password, string OtpSeed);

/// <summary>
/// Carrega <c>local.settings.json</c> (procura no diretório atual e no diretório
/// do executável). Aceita o formato do Azure Functions (<c>{"Values":{...}}</c>)
/// e também chaves na raiz, tanto planas (<c>"RegistroBr:OtpSeed"</c>) quanto
/// aninhadas (<c>"RegistroBr": { "OtpSeed": "..." }</c>).
/// </summary>
public static class Settings
{
    private const string FileName = "local.settings.json";

    public static AppSettings Load()
    {
        var path = FindFile();
        if (path is null) return new AppSettings(null, null, null);

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            // Azure Functions guarda tudo sob "Values".
            var container = root.TryGetProperty("Values", out var vals) && vals.ValueKind == JsonValueKind.Object
                ? vals
                : root;

            return new AppSettings(
                Get(container, "User"),
                Get(container, "Password"),
                Get(container, "OtpSeed"));
        }
        catch
        {
            return new AppSettings(null, null, null);
        }
    }

    private static string Get(JsonElement container, string field)
    {
        // 1) chave plana: "RegistroBr:Campo"
        if (container.TryGetProperty($"RegistroBr:{field}", out var flat) &&
            flat.ValueKind == JsonValueKind.String)
            return NullIfBlank(flat.GetString());

        // 2) objeto aninhado: "RegistroBr": { "Campo": "..." }
        if (container.TryGetProperty("RegistroBr", out var rb) && rb.ValueKind == JsonValueKind.Object &&
            rb.TryGetProperty(field, out var nested) && nested.ValueKind == JsonValueKind.String)
            return NullIfBlank(nested.GetString());

        // 3) chave solta na raiz: "Campo"
        if (container.TryGetProperty(field, out var loose) && loose.ValueKind == JsonValueKind.String)
            return NullIfBlank(loose.GetString());

        return null;
    }

    private static string NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string FindFile()
    {
        // Procura no diretório atual e no do executável, subindo até 6 níveis em
        // cada (assim acha o arquivo na raiz do projeto mesmo rodando de bin/...).
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = start;
            for (int up = 0; up < 6 && !string.IsNullOrEmpty(dir); up++)
            {
                if (seen.Add(dir))
                {
                    var p = Path.Combine(dir, FileName);
                    if (File.Exists(p)) return p;
                }
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
        }
        return null;
    }
}
