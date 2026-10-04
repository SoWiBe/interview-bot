using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace InterviewBot.Infrastructure.Telegram;

/// <summary>
/// Telegram HTML parse mode: экранируем всё, что пришло от модели, и сами расставляем разрешённые теги.
/// HTML выбран вместо MarkdownV2: там пришлось бы экранировать половину символов кода.
/// </summary>
public static partial class TelegramHtml
{
    /// <summary>Лимит Telegram — 4096 символов; оставляем запас.</summary>
    public const int MessageLimit = 4000;

    /// <summary>Разделитель блоков в сохранённом HTML: по нему упаковщик может резать сообщение, не ломая теги.</summary>
    public const char BlockSeparator = '\u001e';

    public static string Escape(string text) => WebUtility.HtmlEncode(text);

    public static string Bold(string text) => $"<b>{Escape(text)}</b>";

    public static string Italic(string text) => $"<i>{Escape(text)}</i>";

    public static string Link(string url, string text) => $"<a href=\"{Escape(url)}\">{Escape(text)}</a>";

    public static string CodeBlock(string code, string? language = null)
    {
        var languageClass = string.IsNullOrEmpty(language) ? "" : $" class=\"language-{Escape(language)}\"";
        return $"<pre><code{languageClass}>{Escape(code.Trim('\n'))}</code></pre>";
    }

    /// <summary>
    /// Текст модели → HTML-блоки (по абзацам): `код` → code, **жирный** → b, ```блоки``` → pre.
    /// Блоки потом пакуются в сообщения по лимиту, не разрываясь посередине.
    /// </summary>
    public static IEnumerable<string> FromMarkdownLite(string text)
    {
        var position = 0;
        foreach (Match fence in FenceRegex().Matches(text))
        {
            foreach (var paragraph in Paragraphs(text[position..fence.Index]))
            {
                yield return paragraph;
            }

            yield return CodeBlock(fence.Groups["code"].Value, fence.Groups["lang"].Value);
            position = fence.Index + fence.Length;
        }

        foreach (var paragraph in Paragraphs(text[position..]))
        {
            yield return paragraph;
        }
    }

    public static string InlineMarkdown(string text)
    {
        var html = Escape(text);
        html = InlineCodeRegex().Replace(html, "<code>$1</code>");
        html = BoldRegex().Replace(html, "<b>$1</b>");
        return html;
    }

    /// <summary>Склеивает блоки в сообщения не длиннее лимита.</summary>
    public static IReadOnlyList<string> Pack(IEnumerable<string> blocks)
    {
        var messages = new List<string>();
        var current = new StringBuilder();

        foreach (var block in blocks.Where(b => !string.IsNullOrWhiteSpace(b)))
        {
            foreach (var piece in block.Length > MessageLimit ? SplitOversized(block) : [block])
            {
                if (current.Length > 0 && current.Length + 2 + piece.Length > MessageLimit)
                {
                    messages.Add(current.ToString());
                    current.Clear();
                }

                if (current.Length > 0)
                {
                    current.Append("\n\n");
                }

                current.Append(piece);
            }
        }

        if (current.Length > 0)
        {
            messages.Add(current.ToString());
        }

        return messages;
    }

    private static IEnumerable<string> Paragraphs(string text) =>
        text.ReplaceLineEndings("\n")
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(InlineMarkdown);

    /// <summary>Редкий случай: один блок больше лимита. Режем по строкам, код — на несколько pre-блоков.</summary>
    private static IEnumerable<string> SplitOversized(string block)
    {
        var codeMatch = PreRegex().Match(block);
        var isCode = codeMatch.Success && codeMatch.Length == block.Length;
        var raw = isCode ? WebUtility.HtmlDecode(codeMatch.Groups["code"].Value) : block;
        var language = isCode ? codeMatch.Groups["lang"].Value : null;

        var chunk = new StringBuilder();
        foreach (var line in raw.Split('\n'))
        {
            var budget = isCode ? MessageLimit - 100 - Escape(chunk.ToString()).Length : MessageLimit - chunk.Length;
            if (chunk.Length > 0 && (isCode ? Escape(line).Length : line.Length) + 1 > budget)
            {
                yield return isCode ? CodeBlock(chunk.ToString(), language) : chunk.ToString();
                chunk.Clear();
            }

            // одна строка длиннее лимита — режем жёстко
            var safeLine = line.Length > MessageLimit / 2 ? line[..(MessageLimit / 2)] + "…" : line;
            chunk.Append(safeLine).Append('\n');
        }

        if (chunk.Length > 0)
        {
            yield return isCode ? CodeBlock(chunk.ToString(), language) : chunk.ToString().TrimEnd();
        }
    }

    [GeneratedRegex(@"```(?<lang>[\w#+-]*)[^\n]*\n(?<code>.*?)```", RegexOptions.Singleline)]
    private static partial Regex FenceRegex();

    [GeneratedRegex(@"`([^`\n]+)`")]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex BoldRegex();

    [GeneratedRegex(@"^<pre><code(?: class=""language-(?<lang>[^""]*)"")?>(?<code>.*)</code></pre>$", RegexOptions.Singleline)]
    private static partial Regex PreRegex();
}
