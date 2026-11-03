using STranslate.Plugin.Translate.Doubao.View;
using STranslate.Plugin.Translate.Doubao.ViewModel;
using System.Windows.Controls;

namespace STranslate.Plugin.Translate.Doubao;

/// <summary>
/// 使用豆包网页版账号登录态调用豆包翻译，无需开通火山方舟 API。
/// </summary>
public class Main : TranslatePluginBase
{
    private Control? _settingUi;
    private SettingsViewModel? _viewModel;
    private Settings Settings { get; set; } = null!;
    private IPluginContext Context { get; set; } = null!;

    /// <summary>
    /// 获取豆包翻译插件设置界面。
    /// </summary>
    /// <returns>绑定当前插件配置的设置控件。</returns>
    public override Control GetSettingUI()
    {
        _viewModel ??= new SettingsViewModel(Context, Settings);
        _settingUi ??= new SettingsView { DataContext = _viewModel };
        return _settingUi;
    }

    /// <summary>
    /// 豆包翻译接口没有源语言参数，由服务端自动识别。
    /// </summary>
    /// <param name="langEnum">STranslate 语言枚举。</param>
    /// <returns>始终返回 <c>auto</c>。</returns>
    public override string? GetSourceLanguage(LangEnum langEnum) => "auto";

    /// <summary>
    /// 将 STranslate 目标语言转换为豆包语言代码。
    /// </summary>
    /// <param name="langEnum">STranslate 语言枚举。</param>
    /// <returns>豆包语言代码；不支持时返回 <see langword="null"/>。</returns>
    public override string? GetTargetLanguage(LangEnum langEnum) => DoubaoTranslator.ToDoubaoLang(langEnum);

    /// <summary>
    /// 初始化插件上下文并加载设置。
    /// </summary>
    /// <param name="context">当前插件服务的运行时上下文。</param>
    public override void Init(IPluginContext context)
    {
        Context = context;
        Settings = context.LoadSettingStorage<Settings>();
    }

    /// <summary>
    /// 释放设置视图模型订阅的事件。
    /// </summary>
    public override void Dispose() => _viewModel?.Dispose();

    /// <summary>
    /// 按行切分文本后分批调用豆包翻译接口，再按原有行结构拼回译文。
    /// </summary>
    /// <param name="request">翻译文本及源、目标语言。</param>
    /// <param name="result">用于写入翻译结果或错误信息的结果对象。</param>
    /// <param name="cancellationToken">用于取消 HTTP 请求的令牌。</param>
    /// <returns>表示异步翻译操作的任务。</returns>
    public override async Task TranslateAsync(TranslateRequest request, TranslateResult result, CancellationToken cancellationToken = default)
    {
        if (GetTargetLanguage(request.TargetLang) is not string targetStr)
        {
            result.Fail(Context.GetTranslation("UnsupportedTargetLang"));
            return;
        }

        var cookie = DoubaoClient.NormalizeCookie(Settings.Cookie);
        if (!DoubaoClient.HasLoginCookies(cookie))
        {
            result.Fail(Context.GetTranslation("STranslate_Plugin_Translate_Doubao_NotLoggedIn"));
            return;
        }

        // 空行不发给服务端，只在拼回时保留位置
        var lines = request.Text.Replace("\r\n", "\n").Split('\n');
        var segments = lines.Select((text, index) => (text, index)).Where(x => !string.IsNullOrWhiteSpace(x.text)).ToList();
        if (segments.Count == 0)
        {
            result.Success(request.Text);
            return;
        }

        string[] translated;
        try
        {
            var translator = new DoubaoTranslator(Context.HttpService, cookie, Settings.Engine);
            translated = await translator.TranslateAsync(segments.Select(x => x.text).ToList(), targetStr, cancellationToken: cancellationToken);
        }
        catch (DoubaoException ex)
        {
            result.Fail(DescribeError(Context, ex));
            return;
        }

        for (var i = 0; i < segments.Count; i++)
            lines[segments[i].index] = translated[i];

        result.Success(string.Join(Environment.NewLine, lines));
    }

    /// <summary>
    /// 将豆包业务错误转换为本地化提示文本。
    /// </summary>
    internal static string DescribeError(IPluginContext context, DoubaoException ex) => ex.Code switch
    {
        Constant.CodeLoginExpired => context.GetTranslation("STranslate_Plugin_Translate_Doubao_LoginExpired"),
        Constant.CodeRateLimited => context.GetTranslation("STranslate_Plugin_Translate_Doubao_RateLimited"),
        int code => $"{context.GetTranslation("STranslate_Plugin_Translate_Doubao_RequestFailed")} ({code}) {ex.Message}".Trim(),
        null => string.IsNullOrEmpty(ex.Message)
            ? context.GetTranslation("STranslate_Plugin_Translate_Doubao_EmptyResult")
            : $"{context.GetTranslation("STranslate_Plugin_Translate_Doubao_RequestFailed")} {ex.Message}".Trim()
    };
}
