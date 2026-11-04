using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Zipora.Services;

/// <summary>A small, text-only Markdown renderer. Never loads HTML, XAML or remote images.</summary>
public static partial class ReleaseNotesMarkdown
{
    public const int MaximumLength = 100_000;

    public static string Limit(string text)
    {
        if (text.Length <= MaximumLength) return text;
        int end = char.IsHighSurrogate(text[MaximumLength - 1]) ? MaximumLength - 1 : MaximumLength;
        return text[..end] + "\n\n更新日志过长，完整内容请查看 GitHub 发行说明。";
    }

    public static FlowDocument Render(string markdown)
    {
        var document = new FlowDocument { FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 13,
            PagePadding = new Thickness(0), ColumnWidth = double.PositiveInfinity, LineHeight = 22 };
        document.SetResourceReference(TextElement.ForegroundProperty, "ZiporaTextPrimary");
        string[] lines = Limit(markdown).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length;)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) { i++; continue; }
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                char marker = trimmed[0]; int count = trimmed.TakeWhile(c => c == marker).Count();
                var code = new StringBuilder(); i++;
                while (i < lines.Length && !IsClosingFence(lines[i], marker, count))
                { if (code.Length > 0) code.Append('\n'); code.Append(lines[i++]); }
                if (i < lines.Length) i++;
                var block = new Paragraph(new Run(code.ToString())) { FontFamily = new FontFamily("Consolas"), FontSize = 12,
                    Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 4, 0, 12) };
                block.SetResourceReference(TextElement.BackgroundProperty, "ZiporaOverlayBackground");
                document.Blocks.Add(block); continue;
            }
            var heading = Heading().Match(line);
            if (heading.Success)
            {
                var block = Paragraph(heading.Groups[2].Value);
                int level = heading.Groups[1].Length;
                block.FontSize = level == 1 ? 20 : level == 2 ? 17 : 14;
                block.FontWeight = FontWeights.SemiBold; block.Margin = new Thickness(0, 10, 0, 7);
                document.Blocks.Add(block); i++; continue;
            }
            if (IsRule(line))
            {
                var rule = new Paragraph { BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 5, 0, 12) };
                rule.SetResourceReference(Block.BorderBrushProperty, "ZiporaDividerBrush"); document.Blocks.Add(rule); i++; continue;
            }
            var item = ListItem().Match(line);
            if (item.Success)
            {
                bool numbered = char.IsDigit(item.Groups[1].Value[0]);
                var list = new System.Windows.Documents.List { MarkerStyle = numbered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Padding = new Thickness(20, 0, 0, 0), Margin = new Thickness(0, 3, 0, 10) };
                if (numbered && int.TryParse(item.Groups[1].Value.TrimEnd('.', ')'), out int start)) list.StartIndex = Math.Max(1, start);
                while (i < lines.Length)
                {
                    item = ListItem().Match(lines[i]);
                    if (!item.Success || char.IsDigit(item.Groups[1].Value[0]) != numbered) break;
                    var content = new StringBuilder(item.Groups[2].Value); i++;
                    while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]) &&
                        char.IsWhiteSpace(lines[i][0]) && !IsBlockStart(lines[i]))
                        content.Append(' ').Append(lines[i++].Trim());
                    var paragraph = Paragraph(content.ToString()); paragraph.Margin = new Thickness(0, 0, 0, 5);
                    list.ListItems.Add(new System.Windows.Documents.ListItem(paragraph));
                }
                document.Blocks.Add(list); continue;
            }
            if (trimmed.StartsWith('>'))
            {
                var quote = Paragraph(trimmed[1..].TrimStart()); quote.Padding = new Thickness(10, 2, 0, 2);
                quote.BorderThickness = new Thickness(3, 0, 0, 0);
                quote.SetResourceReference(Block.BorderBrushProperty, "ZiporaCardBorder");
                quote.SetResourceReference(TextElement.ForegroundProperty, "ZiporaTextSecondary");
                document.Blocks.Add(quote); i++; continue;
            }
            var text = new StringBuilder(line); i++;
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]) && !IsBlockStart(lines[i]))
                text.Append(' ').Append(lines[i++].Trim());
            document.Blocks.Add(Paragraph(text.ToString()));
        }
        if (document.Blocks.Count == 0) document.Blocks.Add(Paragraph("此版本未提供更新日志。"));
        return document;
    }

    private static Paragraph Paragraph(string text)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
        AddInlines(paragraph.Inlines, text, 0); return paragraph;
    }

    private static void AddInlines(InlineCollection output, string text, int depth)
    {
        if (depth >= 8) { output.Add(new Run(text)); return; }
        var literal = new StringBuilder();
        bool couldHaveLink = text.Contains("](", StringComparison.Ordinal);
        void Flush() { if (literal.Length > 0) { output.Add(new Run(literal.ToString())); literal.Clear(); } }
        for (int i = 0; i < text.Length;)
        {
            if (text[i] == '\\' && i + 1 < text.Length && "\\`*_[]()~!#".Contains(text[i + 1]))
            { literal.Append(text[i + 1]); i += 2; continue; }
            if (text[i] == '`')
            {
                int count = 1; while (i + count < text.Length && text[i + count] == '`') count++;
                string token = new('`', count);
                int end = text.IndexOf(token, i + count, StringComparison.Ordinal);
                if (end >= 0)
                {
                    Flush(); var code = new Run(text[(i + count)..end]) { FontFamily = new FontFamily("Consolas"), FontSize = 12 };
                    code.SetResourceReference(TextElement.BackgroundProperty, "ZiporaOverlayBackground"); output.Add(code); i = end + count; continue;
                }
                literal.Append(token); i += count; continue;
            }
            bool image = text[i] == '!' && i + 1 < text.Length && text[i + 1] == '[';
            int labelStart = image ? i + 1 : i;
            if (couldHaveLink && text[labelStart] == '[')
            {
                int close = text.IndexOf("](", labelStart, StringComparison.Ordinal);
                if (close > labelStart)
                {
                    int end = close + 2, balance = 1;
                    for (; end < text.Length; end++)
                    { if (text[end] == '(') balance++; else if (text[end] == ')' && --balance == 0) break; }
                    if (balance == 0)
                    {
                        Flush(); string label = text[(labelStart + 1)..close]; string target = text[(close + 2)..end].Trim();
                        if (!image && IsSafeLink(target, out var uri))
                        {
                            var link = new Hyperlink { NavigateUri = uri, ToolTip = uri!.AbsoluteUri };
                            link.SetResourceReference(TextElement.ForegroundProperty, "ZiporaAccentBrush");
                            AddInlines(link.Inlines, label, depth + 1);
                            link.RequestNavigate += (_, e) => { OpenLink(e.Uri); e.Handled = true; };
                            output.Add(link);
                        }
                        else if (image) output.Add(new Run(string.IsNullOrEmpty(label) ? "[图片]" : $"[图片：{label}]"));
                        else output.Add(new Run(label));
                        i = end + 1; continue;
                    }
                    literal.Append(text[i..]); break;
                }
            }
            string? marker = i + 1 < text.Length && (text.AsSpan(i, 2).SequenceEqual("**") ||
                text.AsSpan(i, 2).SequenceEqual("__") || text.AsSpan(i, 2).SequenceEqual("~~")) ? text.Substring(i, 2)
                : text[i] is '*' or '_' && (i == 0 || !char.IsLetterOrDigit(text[i - 1])) ? text[i].ToString() : null;
            if (marker != null)
            {
                int end = text.IndexOf(marker, i + marker.Length, StringComparison.Ordinal);
                if (end > i + marker.Length)
                {
                    Flush(); Span span = marker == "~~" ? new Span { TextDecorations = TextDecorations.Strikethrough }
                        : marker.Length == 2 ? new Bold() : new Italic();
                    AddInlines(span.Inlines, text[(i + marker.Length)..end], depth + 1); output.Add(span); i = end + marker.Length; continue;
                }
            }
            literal.Append(text[i++]);
        }
        Flush();
    }

    public static bool IsSafeLink(string target, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(target, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https")) return false;
        uri = parsed; return true;
    }

    private static void OpenLink(Uri uri)
    {
        if (!IsSafeLink(uri.AbsoluteUri, out _)) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
    }

    private static bool IsClosingFence(string line, char marker, int count)
    { string value = line.Trim(); return value.Length >= count && value.All(c => c == marker); }
    private static bool IsBlockStart(string line) => Heading().IsMatch(line) || ListItem().IsMatch(line) ||
        line.TrimStart().StartsWith('>') || line.TrimStart().StartsWith("```") || line.TrimStart().StartsWith("~~~") || IsRule(line);
    private static bool IsRule(string line)
    { string value = line.Replace(" ", "").Replace("\t", ""); return value.Length >= 3 && value[0] is '-' or '*' or '_' && value.All(c => c == value[0]); }
    [GeneratedRegex(@"^ {0,3}(#{1,6})[ \t]+(.+?)(?:[ \t]+#+[ \t]*)?$")]
    private static partial Regex Heading();
    [GeneratedRegex(@"^\s*([-+*]|\d{1,9}[.)])[ \t]+(.+)$")]
    private static partial Regex ListItem();
}
