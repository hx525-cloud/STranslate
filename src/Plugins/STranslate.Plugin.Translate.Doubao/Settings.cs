namespace STranslate.Plugin.Translate.Doubao;

/// <summary>
/// 保存豆包翻译插件的登录 Cookie 与翻译引擎配置。
/// </summary>
public class Settings
{
    /// <summary>
    /// 豆包网页版登录 Cookie（<c>name=value; name=value</c>）。
    /// </summary>
    public string Cookie { get; set; } = string.Empty;

    public DoubaoEngine Engine { get; set; } = DoubaoEngine.DoubaoAi;

    /// <summary>
    /// 文档翻译上次使用的目标语言。
    /// </summary>
    public LangEnum DocumentTargetLang { get; set; } = LangEnum.ChineseSimplified;
}

/// <summary>
/// 豆包翻译引擎，数值对应接口字段 <c>translate_service</c>。
/// </summary>
public enum DoubaoEngine
{
    /// <summary>
    /// 火山引擎机器翻译，速度最快。
    /// </summary>
    Volcengine = 0,

    /// <summary>
    /// 豆包大模型翻译，长文与专业文本质量更好。
    /// </summary>
    DoubaoAi = 1,

    /// <summary>
    /// 微软翻译。
    /// </summary>
    Microsoft = 3
}
