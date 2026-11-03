using System.IO;

namespace STranslate.Plugin.Translate.Doubao.Document;

/// <summary>
/// 翻译一组段落，返回与输入等长、同序的译文。
/// </summary>
internal delegate Task<string[]> TranslateSegments(IReadOnlyList<string> segments, CancellationToken cancellationToken);

/// <summary>
/// 某一种文档格式的双语翻译处理器。
/// </summary>
internal interface IDocumentHandler
{
    /// <summary>
    /// 输出文件扩展名（含点）。
    /// </summary>
    string OutputExtension { get; }

    /// <summary>
    /// 读取输入文件并提取待翻译段落数，用于展示进度总数。
    /// </summary>
    int Load(string inputPath);

    /// <summary>
    /// 翻译已加载的段落并写出双语对照文件。
    /// </summary>
    Task TranslateAsync(string outputPath, TranslateSegments translate, CancellationToken cancellationToken);
}

/// <summary>
/// 根据扩展名选择文档处理器；PDF 由阅读器单独处理。
/// </summary>
internal static class DocumentHandlers
{
    internal static IDocumentHandler? Create(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".docx" => new DocxDocumentHandler(),
        ".txt" => new TextDocumentHandler(isMarkdown: false),
        ".md" => new TextDocumentHandler(isMarkdown: true),
        _ => null
    };

    /// <summary>
    /// 生成不覆盖已有文件的输出路径，例如 <c>report.双语.docx</c>、<c>report.双语(2).docx</c>。
    /// </summary>
    internal static string BuildOutputPath(string inputPath, string extension, string suffix)
    {
        var directory = Path.GetDirectoryName(inputPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(inputPath);
        var path = Path.Combine(directory, $"{name}.{suffix}{extension}");
        for (var i = 2; File.Exists(path); i++)
            path = Path.Combine(directory, $"{name}.{suffix}({i}){extension}");
        return path;
    }

    /// <summary>
    /// 只含数字、符号的段落（页码、编号等）无需翻译。
    /// </summary>
    internal static bool NeedsTranslation(string text) => text.Any(char.IsLetter);
}
