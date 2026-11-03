using Microsoft.Web.WebView2.Core;
using STranslate.Plugin.Translate.Doubao.ViewModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace STranslate.Plugin.Translate.Doubao.View;

/// <summary>
/// PDF 阅读翻译窗口：pdf.js 阅读器左侧显示原文，右侧显示同版式的镜像对照页。
/// </summary>
/// <remarks>
/// pdf.js 与文档都通过虚拟域名 <see cref="Host"/> 由本窗口直接响应，无需本地 Web 服务，也规避了 pdf.js 的跨域校验。
/// </remarks>
public partial class ReaderWindow : IReaderView
{
    private const string Host = "https://doubao-reader.local";
    private const string DocumentPrefix = "/document/";

    private readonly ReaderViewModel _viewModel;
    private readonly string _pdfjsRoot;
    private readonly string _userDataFolder;
    private readonly Task _initialization;
    private string? _documentPath;

    /// <summary>
    /// 初始化阅读器窗口。
    /// </summary>
    /// <param name="viewModel">阅读器视图模型。</param>
    /// <param name="pluginDirectory">插件安装目录，pdf.js 位于其 <c>pdfjs</c> 子目录。</param>
    /// <param name="userDataFolder">WebView2 用户数据目录。</param>
    public ReaderWindow(ReaderViewModel viewModel, string pluginDirectory, string userDataFolder)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _pdfjsRoot = Path.GetFullPath(Path.Combine(pluginDirectory, "pdfjs")) + Path.DirectorySeparatorChar;
        _userDataFolder = userDataFolder;
        DataContext = viewModel;
        viewModel.Attach(this);
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateTitle();

        _initialization = InitializeWebViewAsync(Path.Combine(pluginDirectory, "Reader", "bridge.js"));
    }

    /// <summary>
    /// WebView2 初始化失败时触发，常见原因是系统未安装 WebView2 Runtime。
    /// </summary>
    public event Action<Exception>? InitializeFailed;

    private async Task InitializeWebViewAsync(string bridgeScriptPath)
    {
        try
        {
            Directory.CreateDirectory(_userDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: _userDataFolder);
            await WebView.EnsureCoreWebView2Async(environment);

            var core = WebView.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(await File.ReadAllTextAsync(bridgeScriptPath));
            core.AddWebResourceRequestedFilter($"{Host}/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnWebResourceRequested;
            core.WebMessageReceived += OnWebMessageReceived;
        }
        catch (Exception ex)
        {
            InitializeFailed?.Invoke(ex);
            Close();
        }
    }

    /// <inheritdoc />
    public async void OpenPdf(string path)
    {
        await _initialization;
        if (WebView.CoreWebView2 is null)
            return;

        _documentPath = path;
        EmptyHint.Visibility = Visibility.Collapsed;
        // 每次打开使用不同地址，避免 pdf.js 复用上一份文档
        var documentUrl = Uri.EscapeDataString($"{DocumentPrefix}{Guid.NewGuid():N}.pdf");
        WebView.CoreWebView2.Navigate($"{Host}/web/viewer.html?file={documentUrl}");
    }

    /// <inheritdoc />
    public async Task RunScriptAsync(string script)
    {
        await _initialization;
        if (WebView.CoreWebView2 is not null)
            await WebView.CoreWebView2.ExecuteScriptAsync(script);
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var path = Uri.UnescapeDataString(new Uri(e.Request.Uri).AbsolutePath);
        var filePath = path.StartsWith(DocumentPrefix, StringComparison.Ordinal)
            ? _documentPath
            : Path.GetFullPath(Path.Combine(_pdfjsRoot, path.TrimStart('/')));

        // 只允许访问当前文档与 pdf.js 目录，防止路径穿越
        var allowed = filePath is not null
            && (filePath == _documentPath || filePath.StartsWith(_pdfjsRoot, StringComparison.OrdinalIgnoreCase))
            && File.Exists(filePath);

        var environment = WebView.CoreWebView2.Environment;
        if (!allowed)
        {
            e.Response = environment.CreateWebResourceResponse(null, 404, "Not Found", string.Empty);
            return;
        }

        var stream = new FileStream(filePath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        e.Response = environment.CreateWebResourceResponse(stream, 200, "OK", $"Content-Type: {GetContentType(filePath!)}");
    }

    private static string GetContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".mjs" or ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".ftl" => "text/plain; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".wasm" => "application/wasm",
        ".pdf" => "application/pdf",
        _ => "application/octet-stream"
    };

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var json = JsonDocument.Parse(e.WebMessageAsJson);
            var root = json.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "loaded":
                    _viewModel.OnDocumentLoaded();
                    break;
                case "visible":
                    var pages = root.GetProperty("pages").EnumerateArray().Select(p => p.GetInt32()).ToList();
                    _viewModel.OnVisiblePagesChanged(root.GetProperty("page").GetInt32(), pages);
                    break;
            }
        }
        catch (Exception)
        {
            // 忽略格式不符的消息
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReaderViewModel.Title))
            UpdateTitle();
    }

    private void UpdateTitle()
    {
        var baseTitle = TryFindResource("STranslate_Plugin_Translate_Doubao_Doc_Title") as string ?? "Doubao";
        Title = string.IsNullOrEmpty(_viewModel.Title) ? baseTitle : $"{_viewModel.Title} - {baseTitle}";
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Dispose();
        WebView.Dispose();
        base.OnClosed(e);
    }
}
