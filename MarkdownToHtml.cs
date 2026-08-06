using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DeepSeekUsageTray;

/// <summary>
/// 把 README 用到的 Markdown 子集转换成 B站专栏可用的 HTML。
/// 支持标题、粗体、行内代码、代码块、列表、引用、链接和本地图片上传替换。
/// </summary>
internal static partial class MarkdownToHtml
{
    private static readonly Regex ImagePattern = ImageRegex();
    private static readonly Regex LinkPattern = LinkRegex();
    private static readonly Regex BoldPattern = BoldRegex();
    private static readonly Regex InlineCodePattern = InlineCodeRegex();

    public static async Task<string> ConvertAsync(
        string markdown,
        Func<string, Task<string>> resolveImage)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        var inCode = false;
        var listTag = string.Empty;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (!inCode)
                {
                    CloseList(sb, ref listTag);
                    sb.Append("<pre>");
                    inCode = true;
                }
                else
                {
                    sb.Append("</pre>\n");
                    inCode = false;
                }
                continue;
            }

            if (inCode)
            {
                sb.Append(Escape(line)).Append('\n');
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                CloseList(sb, ref listTag);
                continue;
            }

            if (line.Trim() == "---" || line.Trim() == "***")
            {
                CloseList(sb, ref listTag);
                sb.Append("<hr/>\n");
                continue;
            }

            var trimmed = line.TrimStart();
            var indent = line.Length - trimmed.Length;

            if (trimmed.StartsWith("###### ", StringComparison.Ordinal))
            {
                CloseList(sb, ref listTag);
                sb.Append("<h6>").Append(await InlineAsync(trimmed[7..], resolveImage)).Append("</h6>\n");
                continue;
            }
            if (trimmed.StartsWith("##### ", StringComparison.Ordinal))
            {
                CloseList(sb, ref listTag);
                sb.Append("<h5>").Append(await InlineAsync(trimmed[6..], resolveImage)).Append("</h5>\n");
                continue;
            }
            if (trimmed.StartsWith("#### ", StringComparison.Ordinal))
            {
                CloseList(sb, ref listTag);
                sb.Append("<h4>").Append(await InlineAsync(trimmed[5..], resolveImage)).Append("</h4>\n");
                continue;
            }
            if (trimmed.StartsWith("### ", StringComparison.Ordinal))
            {
                CloseList(sb, ref listTag);
                sb.Append("<h3>").Append(await InlineAsync(trimmed[4..], resolveImage)).Append("</h3>\n");
                continue;
            }
            if (trimmed.StartsWith("## ", StringComparison.Ordinal))
            {
                CloseList(sb, ref listTag);
                sb.Append("<h2>").Append(await InlineAsync(trimmed[3..], resolveImage)).Append("</h2>\n");
                continue;
            }
            if (trimmed.StartsWith("# ", StringComparison.Ordinal))
            {
                CloseList(sb, ref listTag);
                sb.Append("<h1>").Append(await InlineAsync(trimmed[2..], resolveImage)).Append("</h1>\n");
                continue;
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal) ||
                trimmed.StartsWith("* ", StringComparison.Ordinal))
            {
                if (listTag != "ul")
                {
                    CloseList(sb, ref listTag);
                    sb.Append("<ul>\n");
                    listTag = "ul";
                }
                sb.Append("<li>").Append(await InlineAsync(trimmed[2..], resolveImage)).Append("</li>\n");
                continue;
            }

            if (Regex.IsMatch(trimmed, @"^\d+\.\s"))
            {
                if (listTag != "ol")
                {
                    CloseList(sb, ref listTag);
                    sb.Append("<ol>\n");
                    listTag = "ol";
                }
                sb.Append("<li>").Append(await InlineAsync(Regex.Replace(trimmed, @"^\d+\.\s", ""), resolveImage)).Append("</li>\n");
                continue;
            }

            if (trimmed.StartsWith("> ", StringComparison.Ordinal))
            {
                CloseList(sb, ref listTag);
                sb.Append("<blockquote>").Append(await InlineAsync(trimmed[2..], resolveImage)).Append("</blockquote>\n");
                continue;
            }

            if (indent == 0 && trimmed.StartsWith("![", StringComparison.Ordinal))
            {
                CloseList(sb, ref listTag);
                var img = await RenderImageAsync(trimmed, resolveImage);
                sb.Append("<p>").Append(img).Append("</p>\n");
                continue;
            }

            CloseList(sb, ref listTag);
            sb.Append("<p>").Append(await InlineAsync(trimmed, resolveImage)).Append("</p>\n");
        }

        CloseList(sb, ref listTag);
        if (inCode)
        {
            sb.Append("</pre>\n");
        }
        return sb.ToString();
    }

    private static async Task<string> InlineAsync(string text, Func<string, Task<string>> resolveImage)
    {
        var escaped = Escape(text);
        escaped = await ReplaceImagesAsync(escaped, resolveImage);
        escaped = InlineCodePattern.Replace(escaped, "<code>$1</code>");
        escaped = BoldPattern.Replace(escaped, "<b>$1</b>");
        escaped = LinkPattern.Replace(
            escaped,
            m => $"<a href=\"{Escape(m.Groups[2].Value)}\">{m.Groups[1].Value}</a>");
        return escaped;
    }

    private static async Task<string> ReplaceImagesAsync(string escaped, Func<string, Task<string>> resolveImage)
    {
        var matches = ImagePattern.Matches(escaped);
        if (matches.Count == 0)
        {
            return escaped;
        }
        var sb = new StringBuilder();
        var last = 0;
        foreach (Match match in matches)
        {
            sb.Append(escaped, last, match.Index - last);
            sb.Append(await RenderImageAsync(match, resolveImage));
            last = match.Index + match.Length;
        }
        sb.Append(escaped, last, escaped.Length - last);
        return sb.ToString();
    }

    private static async Task<string> RenderImageAsync(string line, Func<string, Task<string>> resolveImage)
    {
        var match = ImagePattern.Match(line);
        return match.Success
            ? await RenderImageAsync(match, resolveImage)
            : line;
    }

    private static async Task<string> RenderImageAsync(Match match, Func<string, Task<string>> resolveImage)
    {
        var alt = match.Groups[1].Value;
        var src = match.Groups[2].Value;
        var url = await resolveImage(src);
        return $"<img src=\"{Escape(url)}\" alt=\"{Escape(alt)}\"/>";
    }

    private static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static void CloseList(StringBuilder sb, ref string listTag)
    {
        if (!string.IsNullOrEmpty(listTag))
        {
            sb.Append(listTag == "ul" ? "</ul>\n" : "</ol>\n");
            listTag = string.Empty;
        }
    }

    [GeneratedRegex(@"!\[([^\]]*)\]\(([^)]+)\)")]
    private static partial Regex ImageRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)]+)\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"\*\*([^*]+)\*\*")]
    private static partial Regex BoldRegex();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex InlineCodeRegex();
}
