using System.Text.Json;
using System.Text.Json.Nodes;

namespace STranslate.Plugin.Translate.Doubao;

/// <summary>
/// 豆包业务错误，<see cref="Code"/> 为服务端返回的原始错误码。
/// </summary>
internal class DoubaoException(int? code, string message) : Exception(message)
{
    public int? Code { get; } = code;
}

/// <summary>
/// 封装豆包网页版翻译接口的请求与响应解析。
/// </summary>
internal static class DoubaoClient
{
    /// <summary>
    /// 规整 Cookie 串：去掉 <c>Cookie:</c> 前缀与换行，并剔除无法放进 HTTP 头的非 Latin-1 字符项。
    /// </summary>
    internal static string NormalizeCookie(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var text = raw.Trim();
        if (text.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
            text = text["Cookie:".Length..];

        var pairs = text
            .Split([';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.Contains('=') && p.All(c => c <= 0xff));
        return string.Join("; ", pairs);
    }

    /// <summary>
    /// 判断 Cookie 是否包含登录所需的最小字段集合。
    /// </summary>
    internal static bool HasLoginCookies(string cookie)
    {
        var names = cookie
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split('=', 2)[0])
            .ToHashSet(StringComparer.Ordinal);
        return Constant.LoginCookieNames.All(names.Contains);
    }

    internal static Options BuildOptions(string cookie) => new()
    {
        Headers = new Dictionary<string, string>
        {
            { "Cookie", cookie },
            { "User-Agent", Constant.UserAgent },
            { "Accept", "*/*" },
            { "Origin", Constant.Origin },
            { "Referer", Constant.LoginUrl },
        }
    };

    internal static object BuildRequestBody(IReadOnlyList<string> texts, string targetLang, DoubaoEngine engine) => new Dictionary<string, object>
    {
        ["raw_text"] = texts,
        ["target_lang"] = targetLang,
        // translate_service 必须是字符串，scene 必须是数字
        ["translate_service"] = ((int)engine).ToString(),
        ["scene"] = Constant.Scene,
        ["frontend_source"] = Constant.FrontendSource,
    };

    /// <summary>
    /// 按段数与字符数上限切分批次，返回每批在原数组中的起始下标与段数。
    /// </summary>
    internal static IEnumerable<(int Start, int Count)> SplitBatches(IReadOnlyList<string> texts)
    {
        var start = 0;
        while (start < texts.Count)
        {
            var count = 0;
            var chars = 0;
            while (start + count < texts.Count && count < Constant.BatchMaxItems)
            {
                var length = texts[start + count].Length;
                if (count > 0 && chars + length > Constant.BatchMaxChars)
                    break;
                chars += length;
                count++;
            }
            yield return (start, count);
            start += count;
        }
    }

    /// <summary>
    /// 解析翻译接口响应，返回批内下标到译文的映射。
    /// </summary>
    /// <remarks>
    /// 正常响应为 SSE：<c>event: json</c> 帧携带 <c>data.items[]</c>，<c>event: done</c> 结束，
    /// <c>event: err</c> 表示整批失败。参数非法或段数超限时服务端直接返回 JSON 错误体。
    /// </remarks>
    internal static Dictionary<int, string> ParseTranslateResponse(string response)
    {
        var body = response.Replace("\r\n", "\n").Replace('\r', '\n').TrimStart();
        if (body.StartsWith('{'))
        {
            ThrowIfError(TryParseObject(body));
            throw new DoubaoException(null, body);
        }

        var results = new Dictionary<int, string>();
        foreach (var block in body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var eventName = "message";
            var dataLines = new List<string>();
            foreach (var line in block.Split('\n'))
            {
                if (line.StartsWith("event:"))
                    eventName = line[6..].Trim();
                else if (line.StartsWith("data:"))
                    dataLines.Add(line[5..].TrimStart(' '));
            }

            var frame = TryParseObject(string.Join('\n', dataLines));
            switch (eventName)
            {
                case "err":
                    ThrowIfError(frame);
                    throw new DoubaoException(null, string.Join('\n', dataLines));
                case "done":
                    ThrowIfError(frame);
                    break;
                case "json":
                case "message":
                    ThrowIfError(frame);
                    foreach (var item in frame?["data"]?["items"]?.AsArray() ?? [])
                    {
                        if (item?["index"]?.GetValueKind() != JsonValueKind.Number || item["res"]?.GetValueKind() != JsonValueKind.String)
                            continue;
                        results.TryAdd(item["index"]!.GetValue<int>(), item["res"]!.GetValue<string>());
                    }
                    break;
            }
        }
        return results;
    }

    /// <summary>
    /// 解析登录态探针响应，返回服务端错误码。
    /// </summary>
    internal static int? ParseProbeCode(string response)
    {
        var node = TryParseObject(response);
        return node?["code"]?.GetValueKind() == JsonValueKind.Number ? node["code"]!.GetValue<int>() : null;
    }

    private static JsonObject? TryParseObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void ThrowIfError(JsonObject? frame)
    {
        if (frame?["code"]?.GetValueKind() != JsonValueKind.Number)
            return;
        var code = frame["code"]!.GetValue<int>();
        if (code != Constant.CodeOk)
            throw new DoubaoException(code, frame["msg"]?.ToString() ?? string.Empty);
    }
}
