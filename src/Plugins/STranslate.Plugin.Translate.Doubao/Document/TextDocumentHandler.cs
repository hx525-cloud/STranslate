using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace STranslate.Plugin.Translate.Doubao.Document;

/// <summary>
/// 纯文本与 Markdown 的双语对照翻译：译文紧跟在原文之后。
/// 纯文本逐行翻译（与主窗口一致）；Markdown 按空行分段并合并软换行。
/// </summary>
internal partial class TextDocumentHandler(bool isMarkdown) : IDocumentHandler
{
    private readonly List<Block> _blocks = [];
    private Encoding _encoding = new UTF8Encoding(false);

    public string OutputExtension => isMarkdown ? ".md" : ".txt";

    public int Load(string inputPath)
    {
        var bytes = File.ReadAllBytes(inputPath);
        _encoding = DetectEncoding(bytes, out var preambleLength);
        Parse(_encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength));
        return _blocks.Count(b => b.Translatable);
    }

    public async Task TranslateAsync(string outputPath, TranslateSegments translate, CancellationToken cancellationToken)
    {
        var translatable = _blocks.Where(b => b.Translatable).ToList();
        var translated = await translate(translatable.Select(b => b.Content).ToList(), cancellationToken);

        var sb = new StringBuilder();
        var index = 0;
        // Markdown 原文与译文之间需空行，否则会被渲染成同一段
        var pairSeparator = isMarkdown ? Environment.NewLine + Environment.NewLine : Environment.NewLine;
        foreach (var block in _blocks)
        {
            if (sb.Length > 0)
                sb.Append(Environment.NewLine);
            if (sb.Length > 0 && block.NewParagraph)
                sb.Append(Environment.NewLine);
            sb.Append(block.Original);

            if (block.Translatable)
                sb.Append(pairSeparator).Append(block.Prefix).Append(translated[index++]);
        }

        await File.WriteAllTextAsync(outputPath, sb.ToString(), _encoding, cancellationToken);
    }

    private void Parse(string text)
    {
        var current = new List<string>();
        var inCodeFence = false;

        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (isMarkdown && line.TrimStart().StartsWith("```"))
                inCodeFence = !inCodeFence;

            if (!inCodeFence && string.IsNullOrWhiteSpace(line))
            {
                Flush(current, isCode: false);
                continue;
            }
            current.Add(line);
        }
        Flush(current, inCodeFence);
    }

    private void Flush(List<string> lines, bool isCode)
    {
        if (lines.Count == 0)
            return;

        var original = string.Join(Environment.NewLine, lines);
        lines.Clear();

        // Markdown 代码块、表格原样保留
        if (isMarkdown && (original.TrimStart().StartsWith("```") || original.TrimStart().StartsWith('|')) || isCode)
        {
            _blocks.Add(new Block(original, string.Empty, string.Empty, false, true));
            return;
        }

        if (!isMarkdown)
        {
            var newParagraph = true;
            foreach (var line in original.Split(Environment.NewLine))
            {
                _blocks.Add(new Block(line, string.Empty, line.Trim(), DocumentHandlers.NeedsTranslation(line), newParagraph));
                newParagraph = false;
            }
            return;
        }

        // 保留标题、引用、列表标记，只翻译其后文字；软换行合并为空格
        var prefix = MarkdownPrefix().Match(original).Value;
        var content = string.Join(' ', original[prefix.Length..].Split(Environment.NewLine).Select(l => l.Trim()));
        _blocks.Add(new Block(original, prefix, content, DocumentHandlers.NeedsTranslation(content), true));
    }

    /// <summary>
    /// 识别编码并保持写出时一致：有 BOM 按 BOM，否则优先严格 UTF-8，失败回退 GB18030（兼容 GBK 旧文本）。
    /// </summary>
    private static Encoding DetectEncoding(byte[] bytes, out int preambleLength)
    {
        Encoding[] bomEncodings = [new UTF8Encoding(true), Encoding.Unicode, Encoding.BigEndianUnicode];
        foreach (var encoding in bomEncodings)
        {
            var preamble = encoding.GetPreamble();
            if (bytes.AsSpan().StartsWith(preamble))
            {
                preambleLength = preamble.Length;
                return encoding;
            }
        }

        preambleLength = 0;
        try
        {
            var strictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
            strictUtf8.GetString(bytes);
            return new UTF8Encoding(false);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding("GB18030");
        }
    }

    [GeneratedRegex(@"^\s*(#{1,6}\s+|>\s*|[-*+]\s+|\d+[.)]\s+)")]
    private static partial Regex MarkdownPrefix();

    private record Block(string Original, string Prefix, string Content, bool Translatable, bool NewParagraph);
}
