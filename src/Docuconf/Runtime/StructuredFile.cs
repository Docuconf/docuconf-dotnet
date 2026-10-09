using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Docuconf.Runtime;

/// <summary>
/// Reads a config file or overlay in <c>json</c>, <c>yaml</c> or <c>toml</c> (SPEC §4.6, §4.7) into JSON, so every
/// format is checked the same way: against the file's JSON Schema, or by binding the app's type with
/// System.Text.Json. YAML is read with the YAML 1.2 core schema (<c>yes</c> is a string, <c>010</c> is ten).
/// </summary>
internal static partial class StructuredFile
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The format a path's extension implies: <c>yaml</c>, <c>toml</c>, or <c>json</c>.</summary>
    public static string FormatOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".yaml" or ".yml" => "yaml",
        ".toml" => "toml",
        _ => "json",
    };

    /// <summary>
    /// Parses <paramref name="data"/> into a JSON node (null for an empty YAML document or JSON <c>null</c>). On
    /// failure returns false with what is wrong in <paramref name="error"/> and the parser's own message, which may
    /// quote the file, in <paramref name="detail"/>.
    /// </summary>
    public static bool TryParse(string format, byte[] data, out JsonNode? node, out string error, out string? detail)
    {
        node = null;
        detail = null;
        var name = format switch { "yaml" => "YAML", "toml" => "TOML", _ => "JSON" };
        error = $"is not valid {name}";
        string text;
        try
        {
            text = StrictUtf8.GetString(data);
        }
        catch (DecoderFallbackException)
        {
            error = "is not UTF-8 text";
            return false;
        }

        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        try
        {
            switch (format)
            {
                case "yaml":
                    node = FromYaml(text);
                    break;
                case "toml":
                    node = FromToml(text);
                    break;
                default:
                    node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
                    // A duplicate key only throws when the object is read.
                    _ = node?.ToJsonString();
                    break;
            }

            return true;
        }
        catch (Exception ex) when (ex is JsonException or YamlException or TomlException or FormatException or InvalidOperationException or ArgumentException)
        {
            detail = ex.Message;
            return false;
        }
    }

    private static JsonNode? FromYaml(string text)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        if (stream.Documents.Count > 1)
        {
            throw new FormatException("holds more than one YAML document");
        }

        return stream.Documents.Count == 0 ? null : Yaml(stream.Documents[0].RootNode, 0);
    }

    private static JsonNode? Yaml(YamlNode node, int depth)
    {
        if (depth > 64)
        {
            throw new FormatException("is nested too deeply");
        }

        switch (node)
        {
            case YamlMappingNode mapping:
                var obj = new JsonObject();
                foreach (var (key, value) in mapping.Children)
                {
                    if (key is not YamlScalarNode { Value: { } name })
                    {
                        throw new FormatException("has a mapping key that is not a scalar");
                    }

                    if (obj.ContainsKey(name))
                    {
                        throw new FormatException($"has the key {name} twice");
                    }

                    obj[name] = Yaml(value, depth + 1);
                }

                return obj;
            case YamlSequenceNode sequence:
                var array = new JsonArray();
                foreach (var item in sequence.Children)
                {
                    array.Add(Yaml(item, depth + 1));
                }

                return array;
            case YamlScalarNode scalar:
                return YamlScalar(scalar);
            default:
                throw new FormatException("holds a YAML node that is not a mapping, sequence or scalar");
        }
    }

    [GeneratedRegex(@"^[-+]?[0-9]+\z")]
    private static partial Regex YamlInt();

    [GeneratedRegex(@"^0o[0-7]+\z")]
    private static partial Regex YamlOctal();

    [GeneratedRegex(@"^0x[0-9a-fA-F]+\z")]
    private static partial Regex YamlHex();

    [GeneratedRegex(@"^[-+]?(?:\.[0-9]+|[0-9]+(?:\.[0-9]*)?)(?:[eE][-+]?[0-9]+)?\z")]
    private static partial Regex YamlFloat();

    /// <summary>A scalar under the YAML 1.2 core schema: quoted scalars are strings, plain ones are resolved.</summary>
    private static JsonNode? YamlScalar(YamlScalarNode scalar)
    {
        var value = scalar.Value ?? "";
        var tag = scalar.Tag.IsEmpty ? null : scalar.Tag.Value;
        if (tag is "tag:yaml.org,2002:str" || (tag is null && scalar.Style is not ScalarStyle.Plain))
        {
            return JsonValue.Create(value);
        }

        if (tag is "tag:yaml.org,2002:null" || (tag is null && value is "" or "~" or "null" or "Null" or "NULL"))
        {
            return null;
        }

        if (value is "true" or "True" or "TRUE")
        {
            return JsonValue.Create(true);
        }

        if (value is "false" or "False" or "FALSE")
        {
            return JsonValue.Create(false);
        }

        if (YamlInt().IsMatch(value))
        {
            return Integer(BigInteger.Parse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture));
        }

        if (YamlOctal().IsMatch(value))
        {
            return Integer(value[2..].Aggregate(BigInteger.Zero, (n, c) => n * 8 + (c - '0')));
        }

        if (YamlHex().IsMatch(value))
        {
            return Integer(BigInteger.Parse("0" + value[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
        }

        if (YamlFloat().IsMatch(value))
        {
            return Float(double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture));
        }

        if (value is ".inf" or ".Inf" or ".INF" or "+.inf" or "+.Inf" or "+.INF" or "-.inf" or "-.Inf" or "-.INF" or ".nan" or ".NaN" or ".NAN")
        {
            throw new FormatException("holds a number JSON cannot represent (infinity or NaN)");
        }

        if (tag is "tag:yaml.org,2002:int" or "tag:yaml.org,2002:float" or "tag:yaml.org,2002:bool")
        {
            throw new FormatException($"holds a value that is not a valid {tag[(tag.LastIndexOf(':') + 1)..]}");
        }

        return JsonValue.Create(value);
    }

    private static JsonNode? FromToml(string text)
    {
        var table = Toml.ToModel(text);
        return TomlValue(table, 0);
    }

    private static JsonNode? TomlValue(object? value, int depth)
    {
        if (depth > 64)
        {
            throw new FormatException("is nested too deeply");
        }

        switch (value)
        {
            case null:
                return null;
            case TomlTable table:
                var obj = new JsonObject();
                foreach (var (key, item) in table)
                {
                    obj[key] = TomlValue(item, depth + 1);
                }

                return obj;
            case TomlTableArray tables:
                var tableArray = new JsonArray();
                foreach (var item in tables)
                {
                    tableArray.Add(TomlValue(item, depth + 1));
                }

                return tableArray;
            case TomlArray items:
                var array = new JsonArray();
                foreach (var item in items)
                {
                    array.Add(TomlValue(item, depth + 1));
                }

                return array;
            case string s:
                return JsonValue.Create(s);
            case bool b:
                return JsonValue.Create(b);
            case long l:
                return JsonValue.Create(l);
            case double d:
                return Float(d);
            case TomlDateTime dt:
                return JsonValue.Create(dt.ToString());
            default:
                return JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture));
        }
    }

    // The number's own text, so an integer beyond 64 bits stays exact.
    private static JsonNode Integer(BigInteger n) => JsonNode.Parse(n.ToString(CultureInfo.InvariantCulture))!;

    private static JsonNode Float(double d) =>
        double.IsFinite(d) ? JsonValue.Create(d) : throw new FormatException("holds a number JSON cannot represent (infinity or NaN)");
}
