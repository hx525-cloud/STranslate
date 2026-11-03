using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace STranslate.Plugin.Translate.Doubao.View;

/// <summary>
/// 内嵌浏览器登录豆包网页版，检测到登录 Cookie 后自动关闭并返回 Cookie 串。
/// </summary>
internal class LoginWindow : Window
{
    private readonly WebView2 _webView = new();
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly string _userDataFolder;
    private bool _checking;

    /// <summary>
    /// 登录成功后采集到的 Cookie 串；未登录关闭时为空。
    /// </summary>
    public string Cookie { get; private set; } = string.Empty;

    /// <summary>
    /// 初始化登录窗口。
    /// </summary>
    /// <param name="title">窗口标题。</param>
    /// <param name="userDataFolder">WebView2 用户数据目录，保留后下次打开可免重复登录。</param>
    public LoginWindow(string title, string userDataFolder)
    {
        Title = title;
        Width = 1000;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = _webView;
        _userDataFolder = userDataFolder;

        _pollTimer.Tick += async (_, _) => await TryCollectCookieAsync();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_userDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: _userDataFolder);
            await _webView.EnsureCoreWebView2Async(environment);
            _webView.CoreWebView2.Navigate(Constant.LoginUrl);
            _pollTimer.Start();
        }
        catch (Exception ex)
        {
            // 常见原因是系统未安装 WebView2 Runtime
            InitializeFailed?.Invoke(ex);
            Close();
        }
    }

    /// <summary>
    /// WebView2 初始化失败时触发。
    /// </summary>
    public event Action<Exception>? InitializeFailed;

    private async Task TryCollectCookieAsync()
    {
        if (_checking || _webView.CoreWebView2 is null)
            return;

        _checking = true;
        try
        {
            var cookies = await _webView.CoreWebView2.CookieManager.GetCookiesAsync(Constant.Origin);
            var cookie = string.Join("; ", cookies
                .Where(c => c.Domain.TrimStart('.').EndsWith(Constant.CookieDomain, StringComparison.OrdinalIgnoreCase))
                .Select(c => $"{c.Name}={c.Value}"));
            cookie = DoubaoClient.NormalizeCookie(cookie);

            if (DoubaoClient.HasLoginCookies(cookie))
            {
                Cookie = cookie;
                _pollTimer.Stop();
                DialogResult = true;
            }
        }
        catch (Exception)
        {
            // 页面跳转过程中读取可能失败，下次轮询重试
        }
        finally
        {
            _checking = false;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _pollTimer.Stop();
        _webView.Dispose();
    }
}
