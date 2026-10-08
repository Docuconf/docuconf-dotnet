using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Docuconf.Contract;

/// <summary>
/// An input's documentation from its XML doc comment (SPEC §14.7): the <c>&lt;summary&gt;</c> as the description,
/// when the property has no <c>[Description]</c>, and the <c>&lt;remarks&gt;</c> as the details, in CommonMark.
/// </summary>
/// <remarks>
/// Doc comments are not in the compiled assembly, so they are read from the XML documentation file the compiler
/// writes next to it when the project sets <c>GenerateDocumentationFile</c> (<c>Orders.Api.dll</c> and
/// <c>Orders.Api.xml</c>). Without that file an input has only its <c>[Description]</c>.
/// </remarks>
public static partial class XmlDocs
{
    /// <summary>The most characters (Unicode code points) details may have (SPEC §4.2).</summary>
    public const int MaxDetails = 4000;

    private static readonly ConcurrentDictionary<Assembly, IReadOnlyDictionary<string, XElement>?> Files = new();

    [GeneratedRegex(@"[ \t\r\n]+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();

    [GeneratedRegex(@"\r?\n[ \t]*\r?\n")]
    private static partial Regex ParagraphBreak();

    /// <summary>The summary (plain text, on one line, without a final period) and remarks (CommonMark) of a property.</summary>
    public static (string? Summary, string? Remarks) Of(PropertyInfo prop)
    {
        var docs = Files.GetOrAdd(prop.DeclaringType!.Assembly, Load);
        if (docs is null || !docs.TryGetValue(MemberId(prop), out var member))
        {
            return (null, null);
        }

        var summary = member.Element("summary") is { } s ? PlainText(s) : null;
        var remarks = member.Element("remarks") is { } r ? Markdown(r) : null;
        return (string.IsNullOrEmpty(summary) ? null : summary, string.IsNullOrEmpty(remarks) ? null : remarks);
    }

    /// <summary>Why <paramref name="details"/> is not valid in a contract, or null when it is.</summary>
    public static string? DetailsProblem(string details)
    {
        if (string.IsNullOrWhiteSpace(details))
        {
            return "details must not be blank";
        }

        int n = CodePoints(details);
        return n > MaxDetails ? $"details are {n} characters (Unicode code points); the most is {MaxDetails}" : null;
    }

    /// <summary>The length of <paramref name="s"/> in Unicode code points.</summary>
    internal static int CodePoints(string s)
    {
        int n = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                i++;
            }

            n++;
        }

        return n;
    }

    /// <summary>The XML doc file of <paramref name="assembly"/>, by member ID, or null when there is none.</summary>
    private static IReadOnlyDictionary<string, XElement>? Load(Assembly assembly)
    {
        var name = assembly.GetName().Name;
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(assembly.Location))
        {
            candidates.Add(Path.ChangeExtension(assembly.Location, ".xml"));
        }

        // Single-file apps have no assembly location; the doc file is published beside the executable.
        candidates.Add(Path.Combine(AppContext.BaseDirectory, name + ".xml"));
        foreach (var file in candidates.Where(File.Exists))
        {
            try
            {
                var doc = XDocument.Load(file, LoadOptions.PreserveWhitespace);
                return doc.Root?.Element("members")?.Elements("member")
                    .Where(m => m.Attribute("name") is not null)
                    .GroupBy(m => m.Attribute("name")!.Value, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>The documentation ID of a property: <c>P:Namespace.Outer.Inner.Name</c>.</summary>
    internal static string MemberId(PropertyInfo prop) => "P:" + TypeId(prop.DeclaringType!) + "." + prop.Name;

    private static string TypeId(Type t)
    {
        var type = t.IsGenericType ? t.GetGenericTypeDefinition() : t;
        var name = type.Name;
        return type.DeclaringType is { } outer ? TypeId(outer) + "." + name : (type.Namespace is { Length: > 0 } ns ? ns + "." + name : name);
    }

    /// <summary>An element's text as one line of plain text, without a final period, as descriptions read.</summary>
    internal static string PlainText(XElement element)
    {
        var sb = new StringBuilder();
        void Walk(XNode node)
        {
            switch (node)
            {
                case XText text:
                    sb.Append(text.Value);
                    break;
                case XElement e when e.Name.LocalName is "see" or "seealso" or "paramref" or "typeparamref":
                    sb.Append(e.IsEmpty || e.Value.Length == 0 ? CrefText(e) : e.Value);
                    break;
                case XElement e:
                    bool block = e.Name.LocalName is "para" or "list" or "item" or "code" or "br";
                    if (block) sb.Append(' ');
                    foreach (var child in e.Nodes())
                    {
                        Walk(child);
                    }

                    if (block) sb.Append(' ');
                    break;
            }
        }

        foreach (var node in element.Nodes())
        {
            Walk(node);
        }

        var line = Whitespace().Replace(sb.ToString(), " ").Trim();
        return line.EndsWith('.') ? line[..^1] : line;
    }

    /// <summary>
    /// An XML doc element as CommonMark: <c>&lt;para&gt;</c> starts a paragraph, <c>&lt;c&gt;</c>, <c>&lt;see cref&gt;</c>,
    /// <c>&lt;see langword&gt;</c> and <c>&lt;paramref&gt;</c> become code spans, <c>&lt;see href&gt;</c> a link,
    /// <c>&lt;code&gt;</c> a fenced block, <c>&lt;list&gt;</c> a bullet or numbered list, <c>&lt;b&gt;</c> and
    /// <c>&lt;i&gt;</c> emphasis. Other elements keep their text.
    /// </summary>
    internal static string Markdown(XElement element)
    {
        var blocks = new List<string>();
        var inline = new StringBuilder();

        void Flush()
        {
            var text = Whitespace().Replace(inline.ToString(), " ").Trim();
            // <br/> is a hard line break.
            text = text.Replace(" \u0001 ", "\u0001", StringComparison.Ordinal).Replace(" \u0001", "\u0001", StringComparison.Ordinal)
                .Replace("\u0001 ", "\u0001", StringComparison.Ordinal).Replace("\u0001", "\\\n", StringComparison.Ordinal);
            if (text.Length > 0)
            {
                blocks.Add(text);
            }

            inline.Clear();
        }

        void Inline(XNode node)
        {
            switch (node)
            {
                case XText text:
                    inline.Append(text.Value);
                    break;
                case XElement e:
                    switch (e.Name.LocalName)
                    {
                        case "para":
                            Flush();
                            foreach (var child in e.Nodes())
                            {
                                Inline(child);
                            }

                            Flush();
                            break;
                        case "code":
                            Flush();
                            blocks.Add(Fence(e.Value, e.Attribute("language")?.Value ?? e.Attribute("lang")?.Value));
                            break;
                        case "list":
                            Flush();
                            blocks.Add(List(e));
                            break;
                        case "br":
                            inline.Append('\u0001');
                            break;
                        case "c":
                            inline.Append(Code(Whitespace().Replace(e.Value, " ").Trim()));
                            break;
                        case "see" or "seealso" when e.Attribute("href") is { } href:
                            var label = Whitespace().Replace(e.Value, " ").Trim();
                            inline.Append(label.Length > 0 ? $"[{label}]({href.Value})" : $"<{href.Value}>");
                            break;
                        case "a" when e.Attribute("href") is { } a:
                            inline.Append($"[{Whitespace().Replace(e.Value, " ").Trim()}]({a.Value})");
                            break;
                        case "see" or "seealso" or "paramref" or "typeparamref":
                            var body = Whitespace().Replace(e.Value, " ").Trim();
                            inline.Append(body.Length > 0 ? body : Code(CrefText(e)));
                            break;
                        case "b" or "strong":
                            inline.Append("**" + Whitespace().Replace(e.Value, " ").Trim() + "**");
                            break;
                        case "i" or "em":
                            inline.Append('*' + Whitespace().Replace(e.Value, " ").Trim() + '*');
                            break;
                        default:
                            foreach (var child in e.Nodes())
                            {
                                Inline(child);
                            }

                            break;
                    }

                    break;
            }
        }

        foreach (var node in element.Nodes())
        {
            if (node is XText text)
            {
                // A blank line in the comment ends a paragraph, as it would in Markdown.
                var parts = ParagraphBreak().Split(text.Value);
                for (int i = 0; i < parts.Length; i++)
                {
                    if (i > 0)
                    {
                        Flush();
                    }

                    inline.Append(parts[i]);
                }
            }
            else
            {
                Inline(node);
            }
        }

        Flush();
        return BlankLines().Replace(string.Join("\n\n", blocks), "\n\n").Trim();
    }

    /// <summary>The name a <c>cref</c>, <c>langword</c> or <c>name</c> attribute refers to: <c>T:Ns.Type</c> is <c>Type</c>, <c>P:Ns.Type.Port</c> is <c>Type.Port</c>.</summary>
    private static string CrefText(XElement e)
    {
        if (e.Attribute("langword") is { } langword)
        {
            return langword.Value;
        }

        if (e.Attribute("name") is { } name)
        {
            return name.Value;
        }

        var cref = e.Attribute("cref")?.Value ?? "";
        var kind = cref.Length > 1 && cref[1] == ':' ? cref[0] : 'T';
        if (cref.Length > 1 && cref[1] == ':')
        {
            cref = cref[2..];
        }

        int paren = cref.IndexOf('(', StringComparison.Ordinal);
        if (paren >= 0)
        {
            cref = cref[..paren];
        }

        var parts = cref.Split('.');
        return kind == 'T' || parts.Length < 2 ? parts[^1] : parts[^2] + "." + parts[^1];
    }

    private static string Code(string s)
    {
        int longest = 0, run = 0;
        foreach (var ch in s)
        {
            run = ch == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        var fence = new string('`', longest + 1);
        return longest > 0 ? $"{fence} {s} {fence}" : fence + s + fence;
    }

    /// <summary>A fenced code block, with the comment's common indentation removed.</summary>
    private static string Fence(string code, string? language)
    {
        var lines = code.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        while (lines.Count > 0 && lines[0].Trim().Length == 0)
        {
            lines.RemoveAt(0);
        }

        while (lines.Count > 0 && lines[^1].Trim().Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        int indent = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        var body = string.Join("\n", lines.Select(l => l.Length >= indent ? l[indent..].TrimEnd() : l.Trim()));
        return $"```{language}\n{body}\n```";
    }

    private static string List(XElement list)
    {
        bool numbered = list.Attribute("type")?.Value == "number";
        var items = new List<string>();
        int i = 1;
        foreach (var item in list.Elements("item"))
        {
            var term = item.Element("term") is { } t ? Markdown(t) : null;
            var description = item.Element("description") is { } d ? Markdown(d) : (term is null ? Markdown(item) : null);
            var text = term is not null && description is not null ? $"**{term}**: {description}" : term ?? description ?? "";
            var marker = numbered ? $"{i++}. " : "- ";
            items.Add(marker + text.Replace("\n", "\n" + new string(' ', marker.Length), StringComparison.Ordinal));
        }

        return string.Join("\n", items);
    }
}
