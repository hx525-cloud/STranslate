using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System.IO;

namespace STranslate.Plugin.Translate.Doubao.Document;

/// <summary>
/// 在 Word 文档副本中每个段落之后插入沿用原段落格式的译文段落。
/// </summary>
internal class DocxDocumentHandler : IDocumentHandler
{
    private string _inputPath = string.Empty;
    private int _count;

    public string OutputExtension => ".docx";

    public int Load(string inputPath)
    {
        _inputPath = inputPath;
        using var document = WordprocessingDocument.Open(inputPath, false);
        _count = GetParagraphs(document).Count;
        return _count;
    }

    public async Task TranslateAsync(string outputPath, TranslateSegments translate, CancellationToken cancellationToken)
    {
        File.Copy(_inputPath, outputPath, overwrite: false);
        try
        {
            using var document = WordprocessingDocument.Open(outputPath, true);
            var paragraphs = GetParagraphs(document);
            var translated = await translate(paragraphs.Select(p => p.InnerText).ToList(), cancellationToken);

            for (var i = 0; i < paragraphs.Count; i++)
                paragraphs[i].InsertAfterSelf(CreateTranslationParagraph(paragraphs[i], translated[i]));

            document.MainDocumentPart?.Document?.Save();
        }
        catch
        {
            // 失败时不留下半成品
            File.Delete(outputPath);
            throw;
        }
    }

    private static List<Paragraph> GetParagraphs(WordprocessingDocument document) =>
        document.MainDocumentPart?.Document?.Body?
            .Descendants<Paragraph>()
            // 文本框会把段落嵌套在段落里，只处理最内层段落，避免重复翻译
            .Where(p => !p.Descendants<Paragraph>().Any() && DocumentHandlers.NeedsTranslation(p.InnerText))
            .ToList() ?? [];

    private static Paragraph CreateTranslationParagraph(Paragraph original, string text)
    {
        var paragraph = new Paragraph();

        if (original.ParagraphProperties?.CloneNode(true) is ParagraphProperties properties)
        {
            // 去掉列表编号与分页设置，避免译文段落多出一个编号或另起一页
            properties.NumberingProperties?.Remove();
            properties.PageBreakBefore?.Remove();
            properties.SectionProperties?.Remove();
            paragraph.Append(properties);
        }

        var runProperties = original.Descendants<Run>().FirstOrDefault()?.RunProperties?.CloneNode(true) as RunProperties ?? new RunProperties();
        runProperties.Color = new Color { Val = BilingualDocxWriter.TranslationColor };

        paragraph.Append(new Run(runProperties, new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
        return paragraph;
    }
}
