using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace STranslate.Plugin.Translate.Doubao.Document;

/// <summary>
/// 生成新的双语对照 Word 文档：原文段落后紧跟译文段落。
/// </summary>
internal sealed class BilingualDocxWriter : IDisposable
{
    internal const string TranslationColor = "1F4E79";

    private readonly WordprocessingDocument _document;
    private readonly Body _body;

    internal BilingualDocxWriter(string path)
    {
        _document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var mainPart = _document.AddMainDocumentPart();
        mainPart.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(new Body());
        _body = mainPart.Document.Body!;
    }

    internal void AddPair(string original, string translation)
    {
        // 无需翻译的段落（页码等）只保留原文
        if (string.IsNullOrEmpty(translation))
        {
            _body.Append(CreateParagraph(original, color: null, spaceAfter: "240"));
            return;
        }
        _body.Append(CreateParagraph(original, color: null, spaceAfter: "60"));
        _body.Append(CreateParagraph(translation, TranslationColor, spaceAfter: "240"));
    }

    internal void AddPageBreak() =>
        _body.Append(new Paragraph(new Run(new Break { Type = BreakValues.Page })));

    private static Paragraph CreateParagraph(string text, string? color, string spaceAfter)
    {
        var runProperties = new RunProperties();
        if (color is not null)
            runProperties.Append(new Color { Val = color });

        return new Paragraph(
            new ParagraphProperties(new SpacingBetweenLines { After = spaceAfter }),
            new Run(runProperties, new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
    }

    public void Dispose()
    {
        _document.MainDocumentPart?.Document?.Save();
        _document.Dispose();
    }
}
