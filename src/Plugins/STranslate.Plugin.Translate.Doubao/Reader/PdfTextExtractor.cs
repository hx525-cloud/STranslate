using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace STranslate.Plugin.Translate.Doubao.Reader;

/// <summary>
/// PDF 页面中的一个文字段落，坐标为 PDF 用户空间（左下角为原点，单位 pt）。
/// </summary>
/// <param name="Text">段落文字（块内折行已合并）。</param>
/// <param name="FontSize">段落字号中位数（pt），镜像页据此设定译文初始字号。</param>
/// <param name="NeedsTranslation">是否为需要翻译的正文；代码、表格、图中标注等为 <see langword="false"/>。</param>
internal record PdfParagraph(string Text, double Left, double Bottom, double Right, double Top, double FontSize, bool NeedsTranslation);

/// <summary>
/// 按页提取 PDF 段落及其位置；PdfPig 文档对象非线程安全，所有读取串行执行。
/// </summary>
internal sealed partial class PdfTextExtractor : IDisposable
{
    /// <summary>
    /// 段落识别规则版本；规则变化后段落切分不同，缓存需按版本区分。
    /// </summary>
    internal const int Version = 3;

    private static readonly string[] MonospaceFontHints = ["Courier", "Mono", "Consolas", "Menlo", "Code", "Typewriter", "CMTT"];
    private static readonly char[] TerminalPunctuation = ['.', '!', '?', ':', ';', '。', '！', '？', '：', '；'];

    private readonly PdfDocument _document;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<int, IReadOnlyList<PdfParagraph>> _cache = [];

    public PdfTextExtractor(string path)
    {
        _document = PdfDocument.Open(path);
        PageCount = _document.NumberOfPages;
    }

    public int PageCount { get; }

    /// <summary>
    /// 获取指定页（从 1 开始）的段落，结果按阅读顺序排列。
    /// </summary>
    public async Task<IReadOnlyList<PdfParagraph>> GetParagraphsAsync(int pageNumber, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue(pageNumber, out var cached))
                return cached;

            var paragraphs = await Task.Run(() => Extract(_document.GetPage(pageNumber)), cancellationToken);
            _cache[pageNumber] = paragraphs;
            return paragraphs;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static List<PdfParagraph> Extract(Page page)
    {
        var words = page.GetWords(NearestNeighbourWordExtractor.Instance).ToList();
        if (words.Count == 0)
            return [];

        var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
        var ordered = UnsupervisedReadingOrderDetector.Instance.Get(blocks).Select(Block.From).ToList();
        var merged = MergeWrappedBlocks(ordered).Where(b => !string.IsNullOrWhiteSpace(b.Text)).ToList();

        var bodySize = BodyFontSize(merged);
        return merged
            .Select(b => new PdfParagraph(b.Text, b.Left, b.Bottom, b.Right, b.Top, b.FontSize, b.IsProse && !b.IsSmallLabel(bodySize)))
            .ToList();
    }

    /// <summary>
    /// 正文字号：按文字量加权的字号中位数，用于识别图中的小字号标注。
    /// </summary>
    private static double BodyFontSize(List<Block> blocks)
    {
        var half = blocks.Sum(b => b.Text.Length) / 2.0;
        var accumulated = 0;
        foreach (var block in blocks.OrderBy(b => b.FontSize))
        {
            accumulated += block.Text.Length;
            if (accumulated >= half)
                return block.FontSize;
        }
        return 0;
    }

    /// <summary>
    /// 合并被错误切开的块：折行的大字号标题，以及与标题分离的章节编号（如「6.5」「Calling External Functions」）。
    /// </summary>
    private static List<Block> MergeWrappedBlocks(List<Block> blocks)
    {
        var result = new List<Block>();
        foreach (var block in blocks)
        {
            if (result.Count > 0 && (IsContinuation(result[^1], block) || IsSectionNumberOf(result[^1], block)))
                result[^1] = result[^1].Merge(block);
            else
                result.Add(block);
        }
        return result;
    }

    private static bool IsContinuation(Block upper, Block lower)
    {
        var size = Math.Max(upper.FontSize, lower.FontSize);
        if (size <= 0 || Math.Abs(upper.FontSize - lower.FontSize) > size * 0.1)
            return false;

        var overlap = Math.Min(upper.Right, lower.Right) - Math.Max(upper.Left, lower.Left);
        if (overlap < Math.Min(upper.Right - upper.Left, lower.Right - lower.Left) * 0.5)
            return false;

        var gap = upper.Bottom - lower.Top;
        if (gap < -size * 0.5 || gap > size * 1.5)
            return false;

        var width = Math.Max(upper.Right, lower.Right) - Math.Min(upper.Left, lower.Left);
        return upper.LastLineWidth >= width * 0.75 && !upper.Text.TrimEnd().EndsWith(TerminalPunctuation);
    }

    /// <summary>
    /// 章节编号与其右侧同一行的标题被切成两块。
    /// </summary>
    private static bool IsSectionNumberOf(Block number, Block heading)
    {
        if (!SectionNumber().IsMatch(number.Text.Trim()))
            return false;
        var lineHeight = Math.Max(number.Top - number.Bottom, heading.Top - heading.Bottom);
        var sameLine = Math.Min(number.Top, heading.Top) - Math.Max(number.Bottom, heading.Bottom) > lineHeight * 0.3;
        var gap = heading.Left - number.Right;
        return sameLine && gap >= -1 && gap < Math.Max(number.FontSize, heading.FontSize) * 3;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(\d+\.)*\d+\.?$|^[A-Z]\.$")]
    private static partial System.Text.RegularExpressions.Regex SectionNumber();

    /// <summary>
    /// 合并块内折行：英文等以空格连接并处理连字符断词，中日韩文字直接相连。
    /// </summary>
    internal static string JoinLines(IEnumerable<string> lines)
    {
        var sb = new StringBuilder();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            if (sb.Length > 0)
            {
                var last = sb[^1];
                if (last == '-' && sb.Length > 1 && char.IsLetter(sb[^2]) && char.IsLower(line[0]))
                    sb.Length--;
                else if (!IsCjk(last) && !IsCjk(line[0]))
                    sb.Append(' ');
            }
            sb.Append(line);
        }
        return sb.ToString();
    }

    private static bool IsCjk(char c) =>
        c is >= '⺀' and <= '鿿' or >= '가' and <= '힯' or >= '豈' and <= '﫿' or >= '＀' and <= '￯';

    public void Dispose()
    {
        _document.Dispose();
        _lock.Dispose();
    }

    /// <summary>
    /// 识别过程中的文字块。
    /// </summary>
    private record Block(string Text, double Left, double Bottom, double Right, double Top, double FontSize, double LastLineWidth, bool IsMonospace)
    {
        public static Block From(TextBlock block)
        {
            var letters = block.TextLines.SelectMany(l => l.Words).SelectMany(w => w.Letters).ToList();
            var sizes = letters.Select(l => l.PointSize).Where(s => s > 0).Order().ToList();
            var monospace = letters.Count(l => MonospaceFontHints.Any(h => l.FontName?.Contains(h, StringComparison.OrdinalIgnoreCase) == true));
            var lastLine = block.TextLines[^1].BoundingBox;

            return new Block(
                JoinLines(block.TextLines.Select(l => l.Text)),
                block.BoundingBox.Left,
                block.BoundingBox.Bottom,
                block.BoundingBox.Right,
                block.BoundingBox.Top,
                sizes.Count > 0 ? sizes[sizes.Count / 2] : 0,
                lastLine.Width,
                letters.Count > 0 && monospace > letters.Count / 2);
        }

        public Block Merge(Block lower) => this with
        {
            Text = JoinLines([Text, lower.Text]),
            Left = Math.Min(Left, lower.Left),
            Bottom = Math.Min(Bottom, lower.Bottom),
            Right = Math.Max(Right, lower.Right),
            Top = Math.Max(Top, lower.Top),
            LastLineWidth = lower.LastLineWidth,
            IsMonospace = IsMonospace && lower.IsMonospace
        };

        /// <summary>
        /// 图中的小字号短标注（坐标轴、图例等），通常无需翻译。
        /// </summary>
        public bool IsSmallLabel(double bodySize) => bodySize > 0 && FontSize < bodySize * 0.75 && Text.Length < 40;

        /// <summary>
        /// 是否为正文：排除等宽字体的代码、字母占比低的表格/公式/乱码，以及只有一两个字母的图中标注。
        /// </summary>
        public bool IsProse
        {
            get
            {
                if (IsMonospace)
                    return false;
                var visible = Text.Count(c => !char.IsWhiteSpace(c));
                var letters = Text.Count(char.IsLetter);
                return letters >= 2 && visible > 0 && letters >= visible * 0.5;
            }
        }
    }
}
