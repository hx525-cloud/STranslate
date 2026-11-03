using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace STranslate.Plugin.Translate.Doubao.Reader;

/// <summary>
/// 按「文档 + 目标语言 + 引擎」缓存每页译文，重复阅读或导出时不再消耗请求。
/// </summary>
/// <remarks>
/// 每份文档一个 JSON 文件：<c>{ "zh|1": { "3": ["译文1", "", ...] } }</c>，数组与该页段落一一对应。
/// </remarks>
internal sealed class ReaderCache
{
    private readonly string _filePath;
    private readonly Dictionary<string, Dictionary<int, string[]>> _data;

    private ReaderCache(string filePath, Dictionary<string, Dictionary<int, string[]>> data)
    {
        _filePath = filePath;
        _data = data;
    }

    /// <summary>
    /// 加载缓存；文档以路径、大小和修改时间区分，文件被修改后自动使用新缓存。
    /// </summary>
    public static ReaderCache Load(string cacheDirectory, string pdfPath)
    {
        var info = new FileInfo(pdfPath);
        var identity = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32];
        var filePath = Path.Combine(cacheDirectory, $"{key}.json");

        Dictionary<string, Dictionary<int, string[]>>? data = null;
        try
        {
            if (File.Exists(filePath))
                data = JsonSerializer.Deserialize<Dictionary<string, Dictionary<int, string[]>>>(File.ReadAllText(filePath));
        }
        catch (JsonException)
        {
            // 缓存损坏时忽略，重新翻译
        }
        return new ReaderCache(filePath, data ?? []);
    }

    public bool TryGet(string variant, int page, int paragraphCount, out string[] translations)
    {
        if (_data.TryGetValue(variant, out var pages) && pages.TryGetValue(page, out var cached) && cached.Length == paragraphCount)
        {
            translations = cached;
            return true;
        }
        translations = [];
        return false;
    }

    public void Set(string variant, int page, string[] translations)
    {
        if (!_data.TryGetValue(variant, out var pages))
            _data[variant] = pages = [];
        pages[page] = translations;
        Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_data));
        }
        catch (IOException)
        {
            // 写缓存失败不影响阅读
        }
    }
}
