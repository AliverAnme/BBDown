using BBDown.Core.Entity;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using static BBDown.Core.Entity.Entity;
using static BBDown.Core.Util.HTTPUtil;

namespace BBDown.Core.Fetcher;

public partial class NormalInfoFetcher : IFetcher
{
    public async Task<VInfo> FetchAsync(string id)
    {
        string api = $"https://api.bilibili.com/x/web-interface/view?aid={id}";
        string json = await GetWebSourceAsync(api);
        using var infoJson = JsonDocument.Parse(json);
        var data = infoJson.RootElement.GetProperty("data");
        string title = data.GetProperty("title").ToString();
        string desc = data.GetProperty("desc").ToString();
        string pic = data.GetProperty("pic").ToString();
        var owner = data.GetProperty("owner");
        string ownerMid = owner.GetProperty("mid").ToString();
        string ownerName = owner.GetProperty("name").ToString();
        long pubTime = data.GetProperty("pubdate").GetInt64();
        bool bangumi = false;
        var bvid = data.GetProperty("bvid").ToString();
        var cid = data.GetProperty("cid").GetInt64();

        // 互动视频 1:是 0:否
        var isSteinGate = data.TryGetProperty("rights", out var rights) && rights.TryGetProperty("is_stein_gate", out var sg) ? sg.GetInt16() : (short)0;

        // 分p信息
        List<Page> pagesInfo = new();
        var pages = data.GetProperty("pages").EnumerateArray().ToList();
        foreach (var page in pages)
        {
            Page p = new(page.GetProperty("page").GetInt32(),
                id,
                page.GetProperty("cid").ToString(),
                "", //epid
                page.GetProperty("part").ToString().Trim(),
                page.GetProperty("duration").GetInt32(),
                page.TryGetProperty("dimension", out var dim) && dim.TryGetProperty("width", out var w) && dim.TryGetProperty("height", out var h) ? $"{w}x{h}" : "",
                pubTime, //分p视频没有发布时间
                "",
                "",
                ownerName,
                ownerMid
            );
            pagesInfo.Add(p);
        }

        if (isSteinGate == 1) // 互动视频获取分P信息
        {
            var playerSoApi = $"https://api.bilibili.com/x/player.so?bvid={bvid}&id=cid:{cid}";
            var playerSoText = await GetWebSourceAsync(playerSoApi);
            var playerSoXml = new XmlDocument();
            playerSoXml.LoadXml($"<root>{playerSoText}</root>");
                
            var interactionNode = playerSoXml.SelectSingleNode("//interaction");

            if (interactionNode is { InnerText.Length: > 0 })
            {
                using var graphDoc = JsonDocument.Parse(interactionNode.InnerText);
                var graphVersion = graphDoc.RootElement.GetProperty("graph_version").GetInt64();
                var edgeInfoApi = $"https://api.bilibili.com/x/stein/edgeinfo_v2?graph_version={graphVersion}&bvid={bvid}";
                var edgeInfoJson = await GetWebSourceAsync(edgeInfoApi);
                using var edgeDoc = JsonDocument.Parse(edgeInfoJson);
                var edgeInfoData = edgeDoc.RootElement.GetProperty("data");
                var questions = edgeInfoData.GetProperty("edges").GetProperty("questions").EnumerateArray()
                    .ToList();
                var index = 2; // 互动视频分P索引从2开始
                foreach (var question in questions)
                {
                    var choices = question.GetProperty("choices").EnumerateArray().ToList();
                    foreach (var page in choices)
                    {
                        Page p = new(index++,
                            id,
                            page.GetProperty("cid").ToString(),
                            "", //epid
                            page.GetProperty("option").ToString().Trim(),
                            0,
                            "",
                            pubTime, //分p视频没有发布时间
                            "",
                            "",
                            ownerName,
                            ownerMid
                        );
                        pagesInfo.Add(p);
                    }
                }
            }
            else
            {
                throw new Exception("互动视频获取分P信息失败");
            }
        }

        if (data.TryGetProperty("redirect_url", out var redirectUrl) && redirectUrl.ToString().Contains("bangumi"))
        {
            bangumi = true;
            string epId = EpIdRegex().Match(redirectUrl.ToString()).Groups[1].Value;
            //番剧内容通常不会有分P，如果有分P则不需要epId参数
            if (pages.Count == 1)
            {
                pagesInfo.ForEach(p => p.epid = epId);
            }
        }

        var info = new VInfo
        {
            Title = title.Trim(),
            Desc = desc.Trim(),
            Pic = pic,
            PubTime = pubTime,
            PagesInfo = pagesInfo,
            IsBangumi = bangumi,
            IsSteinGate = isSteinGate == 1
        };

        return info;
    }

    [GeneratedRegex("ep(\\d+)")]
    private static partial Regex EpIdRegex();
}