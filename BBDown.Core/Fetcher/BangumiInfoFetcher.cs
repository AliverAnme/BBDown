using BBDown.Core.Entity;
using System.Text.Json;
using static BBDown.Core.Entity.Entity;
using static BBDown.Core.Util.HTTPUtil;

namespace BBDown.Core.Fetcher;

public class BangumiInfoFetcher : IFetcher
{
    public async Task<VInfo> FetchAsync(string id)
    {
        id = id[3..];
        string index = "";
        string api = $"https://{Config.EPHOST}/pgc/view/web/season?ep_id={id}";
        string json = await GetWebSourceAsync(api);
        using var infoJson = JsonDocument.Parse(json);
        if (!infoJson.RootElement.TryGetProperty("result", out var result))
            throw new KeyNotFoundException("Bangumi API response missing 'result' node");
        string cover = result.GetProperty("cover").ToString();
        string title = result.GetProperty("title").ToString();
        string desc = result.GetProperty("evaluate").ToString();
        string pubTimeStr = result.GetProperty("publish").GetProperty("pub_time").ToString();
        long pubTime = string.IsNullOrEmpty(pubTimeStr) ? 0 : DateTimeOffset.ParseExact(pubTimeStr, "yyyy-MM-dd HH:mm:ss", null).ToUnixTimeSeconds();
        var pages = result.GetProperty("episodes").EnumerateArray();
        List<Page> pagesInfo = new();
        int i = 1;

        //episodes为空; 或者未包含对应epid，番外/花絮什么的
        bool foundEp = false;
        foreach (var ep in pages)
        {
            if (ep.TryGetProperty("id", out var eid) && eid.ToString() == id)
            {
                foundEp = true;
                break;
            }
        }
        if (!foundEp)
        {
            if (result.TryGetProperty("section", out JsonElement sections))
            {
                foreach (var section in sections.EnumerateArray())
                {
                    bool inSection = false;
                    foreach (var ep in section.GetProperty("episodes").EnumerateArray())
                    {
                        if (ep.TryGetProperty("id", out var eid) && eid.ToString() == id)
                        {
                            inSection = true;
                            break;
                        }
                    }
                    if (inSection)
                    {
                        if (section.TryGetProperty("title", out var secTitle))
                            title += "[" + secTitle.ToString() + "]";
                        if (section.TryGetProperty("episodes", out var secEps))
                            pages = secEps.EnumerateArray();
                        break;
                    }
                }
            }
        }

        foreach (var page in pages)
        {
            //跳过预告
            if (page.TryGetProperty("badge", out JsonElement badge) && badge.ToString() == "预告") continue;
            string res = "";
            if (page.TryGetProperty("dimension", out var dim) &&
                dim.TryGetProperty("width", out var w) &&
                dim.TryGetProperty("height", out var h))
            {
                res = $"{w}x{h}";
            }
            string _title = page.GetProperty("title").ToString();
            if (page.TryGetProperty("long_title", out var lt) && lt.ValueKind != JsonValueKind.Null)
                _title += " " + lt.ToString();
            _title = _title.Trim();
            Page p = new(i++,
                page.GetProperty("aid").ToString(),
                page.GetProperty("cid").ToString(),
                page.GetProperty("id").ToString(),
                _title,
                0, res,
                page.GetProperty("pub_time").GetInt64());
            if (p.epid == id) index = p.index.ToString();
            pagesInfo.Add(p);
        }


        var info = new VInfo
        {
            Title = title.Trim(),
            Desc = desc.Trim(),
            Pic = cover,
            PubTime = pubTime,
            PagesInfo = pagesInfo,
            IsBangumi = true,
            IsCheese = false,
            Index = index
        };

        return info;
    }
}