namespace STranslate.Plugin.Translate.Doubao;

/// <summary>
/// 豆包网页版翻译接口常量（与豆包浏览器翻译插件使用同一组非公开接口）。
/// </summary>
internal static class Constant
{
    internal const string Origin = "https://www.doubao.com";

    /// <summary>
    /// 登录页面，在内嵌浏览器中打开以获取登录 Cookie。
    /// </summary>
    internal const string LoginUrl = Origin + "/chat/";

    /// <summary>
    /// 唯一可用的翻译接口，响应为 SSE 流，支持一次传入多段文本。
    /// </summary>
    internal const string TranslateUrl = Origin + "/samantha/plugin/stream_article_translate";

    /// <summary>
    /// 登录态探针，返回 <c>code == 0</c> 表示登录有效。
    /// </summary>
    internal const string UserSettingsUrl = Origin + "/samantha/plugin/user_settings/get?frontend_source=1";

    internal const string CookieDomain = "doubao.com";

    internal const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36 Edg/140.0.0.0";

    /// <summary>
    /// 判定登录态成立所需的最小 Cookie 集合。
    /// </summary>
    internal static readonly string[] LoginCookieNames = ["sessionid", "sid_tt", "uid_tt"];

    /// <summary>
    /// 场景号必须是数字，传字符串会返回 710010202；2 表示 AI 阅读器。
    /// </summary>
    internal const int Scene = 2;

    internal const int FrontendSource = 1;

    // 单次请求最多 50 段且不超过 10000 字符
    internal const int BatchMaxItems = 50;
    internal const int BatchMaxChars = 10000;

    internal const int CodeOk = 0;
    internal const int CodeLoginExpired = 710012001;
    internal const int CodeSystemError = 710010202;
    internal const int CodeRateLimited = 710012005;
}
