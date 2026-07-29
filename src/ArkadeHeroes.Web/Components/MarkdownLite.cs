using System.Text;
using System.Text.RegularExpressions;

namespace ArkadeHeroes.Web.Components;

/// <summary>
/// A deliberately tiny Markdown-to-HTML renderer for the Terms of Use document, so the terms read as prose
/// inside the acceptance UI instead of as raw source. Not a general Markdown implementation and not trying
/// to be one — pulling a Markdown package into the WASM bundle to render one document is a poor trade.
///
/// The load-bearing invariant is that IT NEVER DROPS A LINE. This renders a document a player is being asked
/// to agree to, so a construct the renderer doesn't recognise falls through as plain text rather than
/// disappearing: the worst outcome is a line that looks unstyled, never a clause that isn't shown.
///
/// Everything is HTML-escaped before any markup is added, and link targets are restricted to http(s),
/// mailto and in-page anchors — the document is repo-controlled, but a renderer that emits raw HTML from
/// text is a habit worth not forming.
/// </summary>
public static class MarkdownLite
{
    public static string ToHtml(string markdown)
    {
        var html = new StringBuilder();
        var inList = false;
        var inCodeBlock = false;

        foreach (var raw in markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.TrimEnd();

            // Fenced code: emitted verbatim (escaped), never parsed for markup.
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                CloseList(html, ref inList);
                html.Append(inCodeBlock ? "</pre>" : "<pre class=\"md-code\">");
                inCodeBlock = !inCodeBlock;
                continue;
            }
            if (inCodeBlock)
            {
                html.Append(Escape(line)).Append('\n');
                continue;
            }

            if (line.Length == 0) { CloseList(html, ref inList); continue; }

            var trimmed = line.TrimStart();

            // A horizontal rule, in any of Markdown's spellings.
            if (trimmed is "---" or "***" or "___")
            {
                CloseList(html, ref inList);
                html.Append("<hr />");
                continue;
            }

            // Headings. The page supplies the h1, so the document's own levels start one below it.
            if (trimmed.StartsWith('#'))
            {
                var hashes = trimmed.Length - trimmed.TrimStart('#').Length;
                var text = trimmed[hashes..].TrimStart();
                if (hashes <= 6 && text.Length > 0)
                {
                    CloseList(html, ref inList);
                    var level = Math.Min(hashes + 1, 6);
                    html.Append($"<h{level}>").Append(Inline(text)).Append($"</h{level}>");
                    continue;
                }
            }

            // Bullets ("- ", "* ") and ordered items ("1. "), both flattened to one list level: the terms
            // are a flat document, and nesting is the kind of thing a half-renderer gets wrong.
            var bullet = (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
                ? trimmed[2..]
                : OrderedItem(trimmed);
            if (bullet is not null)
            {
                if (!inList) { html.Append("<ul class=\"md-list\">"); inList = true; }
                html.Append("<li>").Append(Inline(bullet)).Append("</li>");
                continue;
            }

            if (trimmed.StartsWith("> ", StringComparison.Ordinal))
            {
                CloseList(html, ref inList);
                html.Append("<p class=\"md-quote\">").Append(Inline(trimmed[2..])).Append("</p>");
                continue;
            }

            // Anything else — including a table row or any construct not handled above — is shown as prose.
            CloseList(html, ref inList);
            html.Append("<p>").Append(Inline(trimmed)).Append("</p>");
        }

        CloseList(html, ref inList);
        if (inCodeBlock) html.Append("</pre>");
        return html.ToString();
    }

    /// <summary>"1. text" → "text"; anything else → null.</summary>
    private static string? OrderedItem(string trimmed)
    {
        var match = Regex.Match(trimmed, @"^\d+\.\s+(.*)$");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static void CloseList(StringBuilder html, ref bool inList)
    {
        if (!inList) return;
        html.Append("</ul>");
        inList = false;
    }

    /// <summary>Escape first, then re-introduce only the inline markup we understand.</summary>
    private static string Inline(string text)
    {
        var s = Escape(text);
        s = Regex.Replace(s, @"\*\*(.+?)\*\*", "<strong>$1</strong>");
        s = Regex.Replace(s, @"(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?![\w*])", "<em>$1</em>");
        s = Regex.Replace(s, @"`([^`]+)`", "<code>$1</code>");
        s = Regex.Replace(s, @"\[([^\]]+)\]\(([^)\s]+)\)", m =>
            SafeHref(m.Groups[2].Value) is { } href
                ? $"<a href=\"{href}\" target=\"_blank\" rel=\"noopener noreferrer\">{m.Groups[1].Value}</a>"
                : m.Groups[1].Value);   // an unsafe scheme keeps its text and loses only the link
        return s;
    }

    /// <summary>Only http(s), mailto and in-page anchors survive as links.</summary>
    private static string? SafeHref(string href) =>
        href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        href.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
        href.StartsWith('#')
            ? href
            : null;

    private static string Escape(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
