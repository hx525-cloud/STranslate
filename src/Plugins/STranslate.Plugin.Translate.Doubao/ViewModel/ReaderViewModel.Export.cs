using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using STranslate.Plugin.Translate.Doubao.Document;
using System.Diagnostics;
using System.IO;

namespace STranslate.Plugin.Translate.Doubao.ViewModel;

/// <summary>
/// 阅读器的导出功能：PDF 导出双语 Word，Word/TXT/Markdown 直接生成双语文件。
/// </summary>
public partial class ReaderViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutput))]
    public partial string OutputPath { get; set; } = string.Empty;

    public bool HasOutput => !string.IsNullOrEmpty(OutputPath);

    [ObservableProperty] public partial bool IsExporting { get; set; }
    [ObservableProperty] public partial double ExportProgress { get; set; }

    private bool CanExport() => IsPdfLoaded;

    /// <summary>
    /// 导出整份 PDF 的双语 Word；已读页面直接使用缓存。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExport), IncludeCancelCommand = true)]
    private async Task ExportAsync(CancellationToken cancellationToken)
    {
        if (_extractor is null)
            return;

        var output = DocumentHandlers.BuildOutputPath(FilePath, ".docx", Context.GetTranslation("STranslate_Plugin_Translate_Doubao_Doc_Suffix"));
        OutputPath = string.Empty;
        ExportProgress = 0;
        IsExporting = true;

        try
        {
            using (var writer = new BilingualDocxWriter(output))
            {
                for (var page = 1; page <= _extractor.PageCount; page++)
                {
                    StatusText = Format("STranslate_Plugin_Translate_Doubao_Reader_Exporting", page, _extractor.PageCount);
                    var paragraphs = await _extractor.GetParagraphsAsync(page, cancellationToken);
                    var translations = await GetTranslationsAsync(page, cancellationToken);

                    if (page > 1)
                        writer.AddPageBreak();
                    for (var i = 0; i < paragraphs.Count; i++)
                        writer.AddPair(paragraphs[i].Text, translations[i]);

                    ExportProgress = page * 100.0 / _extractor.PageCount;
                }
            }
            OutputPath = output;
            StatusText = string.Format(Context.GetTranslation("STranslate_Plugin_Translate_Doubao_Doc_Done"), Path.GetFileName(output));
        }
        catch (Exception ex)
        {
            TryDelete(output);
            ReportExportError(ex);
        }
        finally
        {
            IsExporting = false;
        }
    }

    /// <summary>
    /// Word/TXT/Markdown 暂不提供阅读视图，直接生成双语对照文件。
    /// </summary>
    private async Task ExportDocumentAsync(string path)
    {
        if (DocumentHandlers.Create(path) is not { } handler)
        {
            StatusText = Context.GetTranslation("STranslate_Plugin_Translate_Doubao_Doc_Unsupported");
            return;
        }

        var cookie = DoubaoClient.NormalizeCookie(Settings.Cookie);
        if (!DoubaoClient.HasLoginCookies(cookie))
        {
            StatusText = Context.GetTranslation("STranslate_Plugin_Translate_Doubao_NotLoggedIn");
            return;
        }

        var output = DocumentHandlers.BuildOutputPath(path, handler.OutputExtension, Context.GetTranslation("STranslate_Plugin_Translate_Doubao_Doc_Suffix"));
        var targetLang = TargetLangCode;
        var translator = new DoubaoTranslator(Context.HttpService, cookie, Settings.Engine);
        OutputPath = string.Empty;
        ExportProgress = 0;
        IsExporting = true;

        try
        {
            StatusText = Context.GetTranslation("STranslate_Plugin_Translate_Doubao_Doc_Reading");
            var total = await Task.Run(() => handler.Load(path));
            if (total == 0)
            {
                StatusText = Context.GetTranslation("STranslate_Plugin_Translate_Doubao_Doc_NoText");
                return;
            }

            var progress = new Progress<int>(done =>
            {
                ExportProgress = done * 100.0 / total;
                StatusText = string.Format(Context.GetTranslation("STranslate_Plugin_Translate_Doubao_Doc_Progress"), done, total);
            });
            await handler.TranslateAsync(output, (segments, ct) => translator.TranslateAsync(segments, targetLang, progress, ct), _lifetime.Token);

            ExportProgress = 100;
            OutputPath = output;
            StatusText = string.Format(Context.GetTranslation("STranslate_Plugin_Translate_Doubao_Doc_Done"), Path.GetFileName(output));
        }
        catch (Exception ex)
        {
            ReportExportError(ex);
        }
        finally
        {
            IsExporting = false;
        }
    }

    private void ReportExportError(Exception ex)
    {
        StatusText = ex switch
        {
            OperationCanceledException => Context.GetTranslation("STranslate_Plugin_Translate_Doubao_Doc_Canceled"),
            DoubaoException doubao => Main.DescribeError(Context, doubao),
            _ => $"{Context.GetTranslation("STranslate_Plugin_Translate_Doubao_Doc_Failed")} {ex.Message}"
        };
        if (ex is not OperationCanceledException and not DoubaoException)
            Context.Logger.LogError(ex, "Doubao document export failed");
    }

    [RelayCommand]
    private void OpenOutput()
    {
        if (File.Exists(OutputPath))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{OutputPath}\"") { UseShellExecute = true });
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // ignored
        }
    }
}
