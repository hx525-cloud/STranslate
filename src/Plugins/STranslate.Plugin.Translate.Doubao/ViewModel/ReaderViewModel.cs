using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using STranslate.Plugin.Translate.Doubao.Reader;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace STranslate.Plugin.Translate.Doubao.ViewModel;

/// <summary>
/// 阅读器宿主视图需要提供的能力。
/// </summary>
internal interface IReaderView
{
    /// <summary>
    /// 在阅读器中打开 PDF。
    /// </summary>
    void OpenPdf(string path);

    /// <summary>
    /// 在 pdf.js 页面中执行脚本。
    /// </summary>
    Task RunScriptAsync(string script);
}

/// <summary>
/// 目标语言选项。
/// </summary>
public record LanguageOption(LangEnum Value, string Name);

/// <summary>
/// PDF 阅读翻译：翻译屏幕上可见的页面并预取下一页，译文显示在 pdf.js 右侧的镜像对照页中；
/// Word/TXT/Markdown 直接导出双语文件。
/// </summary>
public partial class ReaderViewModel : ObservableObject, IDisposable
{
    private const string OpenFilter = "PDF|*.pdf|Word / TXT / Markdown|*.docx;*.txt;*.md";

    // 滚动停顿多久后才开始翻译，快速滚动时只翻译最终停下时可见的页面
    private static readonly TimeSpan VisibleDebounce = TimeSpan.FromMilliseconds(400);

    private readonly IPluginContext Context;
    private readonly Settings Settings;
    private readonly SemaphoreSlim _translateGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    private IReaderView? _view;
    private PdfTextExtractor? _extractor;
    private ReaderCache? _cache;
    private CancellationTokenSource? _visibleCts;

    /// <summary>
    /// 初始化阅读器视图模型。
    /// </summary>
    /// <param name="context">插件运行时上下文。</param>
    /// <param name="settings">当前服务的持久化设置。</param>
    public ReaderViewModel(IPluginContext context, Settings settings)
    {
        Context = context;
        Settings = settings;

        Languages = Enum.GetValues<LangEnum>()
            .Where(l => DoubaoTranslator.ToDoubaoLang(l) is not null && l != LangEnum.Cantonese && l != LangEnum.PortugueseBrazil)
            .Select(l => new LanguageOption(l, Context.GetTranslation($"LangEnum{l}")))
            .ToList();
        TargetLanguage = Languages.FirstOrDefault(l => l.Value == Settings.DocumentTargetLang) ?? Languages[0];
        StatusText = Context.GetTranslation("STranslate_Plugin_Translate_Doubao_Reader_Hint");
    }

    public IReadOnlyList<LanguageOption> Languages { get; }

    [ObservableProperty] public partial LanguageOption TargetLanguage { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; }
    [ObservableProperty] public partial string Title { get; set; } = string.Empty;
    [ObservableProperty] public partial string FilePath { get; set; } = string.Empty;
    [ObservableProperty] public partial bool ShowMirror { get; set; } = true;
    [ObservableProperty] public partial bool IsTranslating { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    public partial bool IsPdfLoaded { get; set; }

    public int CurrentPage { get; private set; }

    private string TargetLangCode => DoubaoTranslator.ToDoubaoLang(TargetLanguage.Value)!;

    // 段落识别规则、目标语言、引擎任一变化都使用独立缓存
    private string Variant => $"v{PdfTextExtractor.Version}|{TargetLangCode}|{(int)Settings.Engine}";

    internal void Attach(IReaderView view) => _view = view;

    partial void OnTargetLanguageChanged(LanguageOption value)
    {
        Settings.DocumentTargetLang = value.Value;
        Context.SaveSettingStorage<Settings>();
        // 清空镜像页后 pdf.js 会重新上报可见页，按新语言翻译
        if (IsPdfLoaded)
            _ = RunScriptAsync("window.doubaoReader?.reset()");
    }

    partial void OnShowMirrorChanged(bool value) =>
        _ = RunScriptAsync($"window.doubaoReader?.setMirror({(value ? "true" : "false")})");

    [RelayCommand]
    private async Task OpenAsync()
    {
        var dialog = new OpenFileDialog { Filter = OpenFilter };
        if (dialog.ShowDialog() != true)
            return;

        if (Path.GetExtension(dialog.FileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            OpenPdf(dialog.FileName);
        else
            await ExportDocumentAsync(dialog.FileName);
    }

    /// <summary>
    /// 打开 PDF：建立段落提取器与缓存，并让视图加载文档。
    /// </summary>
    internal void OpenPdf(string path)
    {
        _visibleCts?.Cancel();
        _extractor?.Dispose();
        OutputPath = string.Empty;
        IsPdfLoaded = false;
        CurrentPage = 0;

        try
        {
            _extractor = new PdfTextExtractor(path);
            _cache = ReaderCache.Load(Path.Combine(Context.MetaData.PluginCacheDirectoryPath, "reader"), path);
        }
        catch (Exception ex)
        {
            Context.Logger.LogError(ex, "Failed to open pdf: {File}", path);
            StatusText = $"{Context.GetTranslation("STranslate_Plugin_Translate_Doubao_Reader_OpenFailed")} {ex.Message}";
            return;
        }

        FilePath = path;
        Title = Path.GetFileName(path);
        IsPdfLoaded = true;
        StatusText = Context.GetTranslation("STranslate_Plugin_Translate_Doubao_Reader_Loading");
        _view?.OpenPdf(path);
    }

    /// <summary>
    /// pdf.js 文档加载完成：下发界面文案与对照显示状态。
    /// </summary>
    internal void OnDocumentLoaded()
    {
        var labels = JsonSerializer.Serialize(new { pending = Context.GetTranslation("STranslate_Plugin_Translate_Doubao_Reader_PagePending") });
        _ = RunScriptAsync($"window.doubaoReader?.setLabels({labels})");
        if (!ShowMirror)
            OnShowMirrorChanged(false);
    }

    /// <summary>
    /// 屏幕上可见的页面变化：当前页优先，其次其它可见页，最后预取下一页。
    /// </summary>
    internal async void OnVisiblePagesChanged(int current, IReadOnlyList<int> pages)
    {
        if (_extractor is null || current < 1 || current > _extractor.PageCount)
            return;

        CurrentPage = current;
        _visibleCts?.Cancel();
        var cts = _visibleCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);

        try
        {
            await Task.Delay(VisibleDebounce, cts.Token);

            var ordered = pages.Where(p => p >= 1 && p <= _extractor.PageCount)
                .OrderBy(p => p == current ? 0 : 1)
                .ThenBy(p => p)
                .ToList();
            if (ordered.Count == 0)
                ordered.Add(current);

            foreach (var page in ordered)
                await ShowPageAsync(page, page == current, cts.Token);

            var next = ordered.Max() + 1;
            if (next <= _extractor.PageCount)
                await ShowPageAsync(next, false, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 可见页已变化，或窗口已关闭
        }
        catch (DoubaoException ex)
        {
            if (!cts.IsCancellationRequested)
                StatusText = Main.DescribeError(Context, ex);
        }
        catch (Exception ex)
        {
            Context.Logger.LogError(ex, "Doubao reader failed on page {Page}", current);
            if (!cts.IsCancellationRequested)
                StatusText = $"{Context.GetTranslation("STranslate_Plugin_Translate_Doubao_RequestFailed")} {ex.Message}";
        }
        finally
        {
            if (_visibleCts == cts)
                IsTranslating = false;
        }
    }

    /// <summary>
    /// 翻译（或读取缓存）一页并推送到镜像页；<paramref name="isCurrent"/> 为当前页时更新状态栏。
    /// </summary>
    private async Task ShowPageAsync(int page, bool isCurrent, CancellationToken cancellationToken)
    {
        var paragraphs = await _extractor!.GetParagraphsAsync(page, cancellationToken);
        if (!paragraphs.Any(p => p.NeedsTranslation))
        {
            if (isCurrent)
                StatusText = Format("STranslate_Plugin_Translate_Doubao_Reader_NoText", page, _extractor.PageCount);
            return;
        }

        if (!_cache!.TryGet(Variant, page, paragraphs.Count, out _))
        {
            if (isCurrent)
            {
                IsTranslating = true;
                StatusText = Format("STranslate_Plugin_Translate_Doubao_Reader_Translating", page, _extractor.PageCount);
            }
            await RunScriptAsync($"window.doubaoReader?.setPending({page})");
        }

        var translations = await GetTranslationsAsync(page, cancellationToken);
        var payload = JsonSerializer.Serialize(paragraphs
            .Select((p, i) => (p, tr: translations[i]))
            .Where(x => x.p.NeedsTranslation && !string.IsNullOrEmpty(x.tr))
            .Select(x => new { l = x.p.Left, b = x.p.Bottom, r = x.p.Right, t = x.p.Top, fs = x.p.FontSize, x.tr }));
        await RunScriptAsync($"window.doubaoReader?.setPage({page},{payload})");

        if (isCurrent)
        {
            IsTranslating = false;
            StatusText = Format("STranslate_Plugin_Translate_Doubao_Reader_PageDone", page, _extractor.PageCount);
        }
    }

    /// <summary>
    /// 获取某页译文：优先读缓存，否则调用豆包翻译并写入缓存。
    /// </summary>
    /// <remarks>
    /// 所有请求串行，避免触发限流；请求一旦发出就会完成并写入缓存（只随窗口关闭取消），
    /// 滚动离开不会浪费已发出的请求。
    /// </remarks>
    private async Task<string[]> GetTranslationsAsync(int page, CancellationToken cancellationToken)
    {
        var paragraphs = await _extractor!.GetParagraphsAsync(page, cancellationToken);
        var variant = Variant;
        if (_cache!.TryGet(variant, page, paragraphs.Count, out var cached))
            return cached;

        await _translateGate.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGet(variant, page, paragraphs.Count, out cached))
                return cached;

            var cookie = DoubaoClient.NormalizeCookie(Settings.Cookie);
            if (!DoubaoClient.HasLoginCookies(cookie))
                throw new DoubaoException(Constant.CodeLoginExpired, string.Empty);

            var targets = paragraphs.Where(p => p.NeedsTranslation).Select(p => p.Text).ToList();
            var results = new string[paragraphs.Count];
            Array.Fill(results, string.Empty);
            if (targets.Count > 0)
            {
                var translator = new DoubaoTranslator(Context.HttpService, cookie, Settings.Engine);
                var translated = await translator.TranslateAsync(targets, TargetLangCode, cancellationToken: _lifetime.Token);
                for (int i = 0, j = 0; i < paragraphs.Count; i++)
                {
                    if (paragraphs[i].NeedsTranslation)
                        results[i] = translated[j++];
                }
            }

            _cache.Set(variant, page, results);
            return results;
        }
        finally
        {
            _translateGate.Release();
        }
    }

    private async Task RunScriptAsync(string script)
    {
        try
        {
            if (_view is not null)
                await _view.RunScriptAsync(script);
        }
        catch (Exception ex)
        {
            Context.Logger.LogDebug(ex, "Reader script failed");
        }
    }

    private string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, Context.GetTranslation(key), args);

    /// <summary>
    /// 取消进行中的翻译并释放 PDF 文档。
    /// </summary>
    public void Dispose()
    {
        _lifetime.Cancel();
        if (ExportCommand.IsRunning)
            ExportCancelCommand.Execute(null);
        _extractor?.Dispose();
    }
}
