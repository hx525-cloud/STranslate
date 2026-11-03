using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using STranslate.Plugin.Translate.Doubao.View;
using System.ComponentModel;
using System.IO;
using System.Windows;

namespace STranslate.Plugin.Translate.Doubao.ViewModel;

/// <summary>
/// 管理豆包翻译插件的登录态与引擎设置，修改后立即持久化。
/// </summary>
public partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly IPluginContext Context;
    private readonly Settings Settings;

    /// <summary>
    /// 初始化豆包翻译设置视图模型。
    /// </summary>
    /// <param name="context">插件运行时上下文。</param>
    /// <param name="settings">当前服务的持久化设置。</param>
    public SettingsViewModel(IPluginContext context, Settings settings)
    {
        Context = context;
        Settings = settings;

        Cookie = Settings.Cookie;
        Engine = Settings.Engine;
        UpdateLoginStatus();

        PropertyChanged += PropertyChangedHandler;
    }

    private void PropertyChangedHandler(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Cookie):
                Settings.Cookie = DoubaoClient.NormalizeCookie(Cookie);
                UpdateLoginStatus();
                break;
            case nameof(Engine):
                Settings.Engine = Engine;
                break;
            default:
                return;
        }
        Context.SaveSettingStorage<Settings>();
    }

    /// <summary>
    /// 取消设置变更事件订阅。
    /// </summary>
    public void Dispose() => PropertyChanged -= PropertyChangedHandler;

    [ObservableProperty] public partial string Cookie { get; set; }
    [ObservableProperty] public partial DoubaoEngine Engine { get; set; }
    [ObservableProperty] public partial string LoginStatus { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsLoggedIn { get; set; }

    private string WebViewDataFolder => Path.Combine(Context.MetaData.PluginCacheDirectoryPath, "WebView2");

    private void UpdateLoginStatus()
    {
        IsLoggedIn = DoubaoClient.HasLoginCookies(DoubaoClient.NormalizeCookie(Cookie));
        LoginStatus = Context.GetTranslation(IsLoggedIn
            ? "STranslate_Plugin_Translate_Doubao_Status_LoggedIn"
            : "STranslate_Plugin_Translate_Doubao_Status_NotLoggedIn");
    }

    [RelayCommand]
    private void Login()
    {
        var window = new LoginWindow(Context.GetTranslation("STranslate_Plugin_Translate_Doubao_LoginWindowTitle"), WebViewDataFolder)
        {
            Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
        };
        window.InitializeFailed += ex =>
        {
            Context.Logger.LogError(ex, "Doubao login window initialize failed");
            Context.Snackbar.ShowError(Context.GetTranslation("STranslate_Plugin_Translate_Doubao_WebView2Missing"));
        };
        Context.ApplyTheme(window);

        if (window.ShowDialog() == true && !string.IsNullOrEmpty(window.Cookie))
        {
            Cookie = window.Cookie;
            Context.Snackbar.ShowSuccess(Context.GetTranslation("STranslate_Plugin_Translate_Doubao_LoginSuccess"));
        }
    }

    [RelayCommand]
    private void OpenDocumentTranslate()
    {
        var window = new ReaderWindow(
            new ReaderViewModel(Context, Settings),
            Context.MetaData.PluginDirectory,
            Path.Combine(Context.MetaData.PluginCacheDirectoryPath, "WebView2Reader"));
        window.InitializeFailed += ex =>
        {
            Context.Logger.LogError(ex, "Doubao reader initialize failed");
            Context.Snackbar.ShowError(Context.GetTranslation("STranslate_Plugin_Translate_Doubao_WebView2Missing"));
        };
        Context.ApplyTheme(window);
        window.Show();
    }

    [RelayCommand]
    private void Logout()
    {
        Cookie = string.Empty;
        try
        {
            // 清掉内嵌浏览器的登录数据，下次登录可切换账号
            if (Directory.Exists(WebViewDataFolder))
                Directory.Delete(WebViewDataFolder, true);
        }
        catch (Exception ex)
        {
            Context.Logger.LogWarning(ex, "Failed to clear Doubao WebView2 data");
        }
    }

    [RelayCommand]
    private async Task CheckLoginAsync(CancellationToken cancellationToken)
    {
        var cookie = DoubaoClient.NormalizeCookie(Cookie);
        if (!DoubaoClient.HasLoginCookies(cookie))
        {
            Context.Snackbar.ShowError(Context.GetTranslation("STranslate_Plugin_Translate_Doubao_NotLoggedIn"));
            return;
        }

        try
        {
            var response = await Context.HttpService.GetAsync(Constant.UserSettingsUrl, DoubaoClient.BuildOptions(cookie), cancellationToken);
            switch (DoubaoClient.ParseProbeCode(response))
            {
                case Constant.CodeOk:
                    Context.Snackbar.ShowSuccess(Context.GetTranslation("STranslate_Plugin_Translate_Doubao_CheckSuccess"));
                    break;
                case Constant.CodeLoginExpired:
                    Context.Snackbar.ShowError(Context.GetTranslation("STranslate_Plugin_Translate_Doubao_LoginExpired"));
                    break;
                case var code:
                    Context.Snackbar.ShowError($"{Context.GetTranslation("STranslate_Plugin_Translate_Doubao_RequestFailed")} ({code})");
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
        catch (Exception ex)
        {
            Context.Snackbar.ShowError(Context.GetTranslation("STranslate_Plugin_Translate_Doubao_RequestFailed"));
            Context.Logger.LogError(ex, "Doubao login check failed");
        }
    }
}
