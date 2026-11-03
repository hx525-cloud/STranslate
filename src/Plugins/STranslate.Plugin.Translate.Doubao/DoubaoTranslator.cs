namespace STranslate.Plugin.Translate.Doubao;

/// <summary>
/// 分批调用豆包翻译接口，供文本翻译与文档翻译共用。
/// </summary>
internal class DoubaoTranslator(IHttpService httpService, string cookie, DoubaoEngine engine)
{
    // 超长段落按句切分，避免单段超过接口字符上限被截断
    private const int MaxSegmentChars = 3000;

    // 批次之间稍作停顿，降低触发限流的概率
    private static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(300);

    private static readonly char[] SentenceEnds = ['。', '！', '？', '；', '.', '!', '?', ';', '\n'];

    /// <summary>
    /// 将 STranslate 语言转换为豆包语言代码；不支持时返回 <see langword="null"/>。
    /// </summary>
    internal static string? ToDoubaoLang(LangEnum langEnum) => langEnum switch
    {
        LangEnum.ChineseSimplified => "zh",
        LangEnum.ChineseTraditional => "zh-Hant",
        LangEnum.Cantonese => "zh-Hant",
        LangEnum.English => "en",
        LangEnum.Japanese => "ja",
        LangEnum.Korean => "ko",
        LangEnum.French => "fr",
        LangEnum.Spanish => "es",
        LangEnum.Russian => "ru",
        LangEnum.German => "de",
        LangEnum.Italian => "it",
        LangEnum.PortuguesePortugal => "pt",
        LangEnum.PortugueseBrazil => "pt",
        LangEnum.Vietnamese => "vi",
        LangEnum.Indonesian => "id",
        LangEnum.Thai => "th",
        LangEnum.Malay => "ms",
        LangEnum.Arabic => "ar",
        LangEnum.Uzbek => "uz",
        _ => null
    };

    /// <summary>
    /// 翻译多段文本，返回与输入等长、同序的译文；某段缺失译文时保留原文。
    /// </summary>
    /// <param name="texts">待翻译段落。</param>
    /// <param name="targetLang">豆包目标语言代码。</param>
    /// <param name="progress">已完成段数进度回调。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="DoubaoException">服务端返回业务错误。</exception>
    internal async Task<string[]> TranslateAsync(
        IReadOnlyList<string> texts,
        string targetLang,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // 把超长段落拆成若干片，记录每片属于哪一段
        var pieces = new List<string>();
        var owners = new List<int>();
        for (var i = 0; i < texts.Count; i++)
        {
            foreach (var piece in SplitLongText(texts[i]))
            {
                pieces.Add(piece);
                owners.Add(i);
            }
        }

        var translatedPieces = new string[pieces.Count];
        var option = DoubaoClient.BuildOptions(cookie);
        var first = true;

        foreach (var (start, count) in DoubaoClient.SplitBatches(pieces))
        {
            if (!first)
                await Task.Delay(BatchInterval, cancellationToken);
            first = false;

            var batch = pieces.GetRange(start, count);
            var body = DoubaoClient.BuildRequestBody(batch, targetLang, engine);
            var response = await httpService.PostAsync(Constant.TranslateUrl, body, option, cancellationToken);
            var items = DoubaoClient.ParseTranslateResponse(response);
            if (items.Count == 0)
                throw new DoubaoException(null, string.Empty);

            for (var i = 0; i < count; i++)
                translatedPieces[start + i] = items.TryGetValue(i, out var res) ? res : batch[i];

            progress?.Report(owners[start + count - 1] + 1);
        }

        var results = new string[texts.Count];
        for (var i = 0; i < pieces.Count; i++)
            results[owners[i]] += translatedPieces[i];
        return results;
    }

    private static IEnumerable<string> SplitLongText(string text)
    {
        var start = 0;
        while (text.Length - start > MaxSegmentChars)
        {
            var cut = text.LastIndexOfAny(SentenceEnds, start + MaxSegmentChars - 1, MaxSegmentChars);
            var end = cut <= start ? start + MaxSegmentChars : cut + 1;
            yield return text[start..end];
            start = end;
        }
        if (start < text.Length)
            yield return text[start..];
    }
}
