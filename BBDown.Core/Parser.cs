using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using static BBDown.Core.Logger;
using static BBDown.Core.Util.HTTPUtil;
using static BBDown.Core.Entity.Entity;
using System.Security.Cryptography;
using BBDown.Core.Entity;

namespace BBDown.Core;

public static partial class Parser
{
    public static string WbiSign(string api)
    {
        return $"{api}&w_rid=" + Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(api + Config.WBI)));
    }

    private static async Task<string> GetPlayJsonAsync(string encoding, string aidOri, string aid, string cid, string epId, bool tvApi, bool intl, bool appApi, bool wantDrm, string qn = "0")
    {
        LogDebug("aid={0},cid={1},epId={2},tvApi={3},IntlApi={4},appApi={5},qn={6}", aid, cid, epId, tvApi, intl, appApi, qn);

        if (intl) return await GetPlayJsonAsync(aid, cid, epId, qn);


        bool cheese = aidOri.StartsWith("cheese:");
        bool bangumi = cheese || aidOri.StartsWith("ep:");
        LogDebug("bangumi={0},cheese={1}", bangumi, cheese);

        if (appApi) return await AppHelper.DoReqAsync(aid, cid, epId, qn, bangumi, encoding, Config.TOKEN);

        string prefix = tvApi ? bangumi ? $"{Config.TVHOST}/pgc/player/api/playurltv" : $"{Config.TVHOST}/x/tv/playurl"
            : bangumi ? $"{Config.HOST}/pgc/player/web/v2/playurl" : "api.bilibili.com/x/player/wbi/playurl";
        prefix = $"https://{prefix}?";

        string api;
        if (tvApi)
        {
            StringBuilder apiBuilder = new();
            if (Config.TOKEN != "") apiBuilder.Append($"access_key={Config.TOKEN}&");
            apiBuilder.Append($"appkey=4409e2ce8ffd12b8&build=106500&cid={cid}&device=android");
            if (bangumi) apiBuilder.Append($"&ep_id={epId}&expire=0");
            apiBuilder.Append($"&fnval=4048&fnver=0&fourk=1&mid=0&mobi_app=android_tv_yst");
            apiBuilder.Append($"&object_id={aid}&platform=android&playurl_type=1&qn={qn}&ts={GetTimeStamp(true)}");
            api = $"{prefix}{apiBuilder}&sign={GetSign(apiBuilder.ToString(), false)}";
        }
        else
        {
            // 尝试提高可读性
            StringBuilder apiBuilder = new();
            apiBuilder.Append($"support_multi_audio=true&from_client=BROWSER&avid={aid}&cid={cid}&fnval=4048&fnver=0&fourk=1");
            if (Config.AREA != "") apiBuilder.Append($"&access_key={Config.TOKEN}&area={Config.AREA}");
            apiBuilder.Append($"&otype=json&qn={qn}");
            if (bangumi) apiBuilder.Append($"&module=bangumi&ep_id={epId}&session=");
            if (Config.COOKIE == "" && !wantDrm) apiBuilder.Append("&try_look=1");
            if (wantDrm) apiBuilder.Append("&drm_tech_type=2");
            apiBuilder.Append($"&wts={GetTimeStamp(true)}");
            api = prefix + (bangumi ? apiBuilder.ToString() : WbiSign(apiBuilder.ToString()));
        }

        //课程接口
        if (cheese) api = api.Replace("/pgc/player/web/v2/playurl", "/pugv/player/web/playurl");

        //Console.WriteLine(api);
        string webJson = await GetWebSourceAsync(api);
        //以下情况从网页源代码尝试解析
        if (webJson.Contains("\"大会员专享限制\""))
        {
            Log("此视频需要大会员，您大概率需要登录一个有大会员的账号才可以下载，尝试从网页源码解析");
            string webUrl = "https://www.bilibili.com/bangumi/play/ep" + epId;
            string webSource = await GetWebSourceAsync(webUrl);
            webJson = PlayerJsonRegex().Match(webSource).Groups[1].Value;
        }
        return webJson;
    }

    private static async Task<string> GetPlayJsonAsync(string aid, string cid, string epId, string qn, string code = "0")
    {
        bool isBiliPlus = Config.HOST != "api.bilibili.com";
        string api = $"https://{(isBiliPlus ? Config.HOST : "api.biliintl.com")}/intl/gateway/v2/ogv/playurl?";

        StringBuilder paramBuilder = new();
        if (Config.TOKEN != "") paramBuilder.Append($"access_key={Config.TOKEN}&");
        paramBuilder.Append($"aid={aid}");
        if (isBiliPlus) paramBuilder.Append($"&appkey=7d089525d3611b1c&area={(Config.AREA == "" ? "th" : Config.AREA)}");
        paramBuilder.Append($"&cid={cid}&ep_id={epId}&platform=android&prefer_code_type={code}&qn={qn}");
        if (isBiliPlus) paramBuilder.Append($"&ts={GetTimeStamp(true)}");

        paramBuilder.Append("&s_locale=zh_SG");
        string param = paramBuilder.ToString();
        api += (isBiliPlus ? $"{param}&sign={GetSign(param, true)}" : param);

        string webJson = await GetWebSourceAsync(api);
        return webJson;
    }

    public static async Task<ParsedResult> ExtractTracksAsync(string aidOri, string aid, string cid, string epId, bool tvApi, bool intlApi, bool appApi, string encoding, bool wantDrm = false, string qn = "0")
    {
        ParsedResult parsedResult = new();

        //调用解析
        parsedResult.WebJsonString = await GetPlayJsonAsync(encoding, aidOri, aid, cid, epId, tvApi, intlApi, appApi, wantDrm, qn);

        LogDebug(parsedResult.WebJsonString);

        //intl接口需要两次请求(code=0和code=1)
        if (intlApi)
        {
            foreach (var code in new[] { "0", "1" })
            {
                if (code == "1")
                    parsedResult.WebJsonString = await GetPlayJsonAsync(aid, cid, epId, qn, code);

                using var intlJson = JsonDocument.Parse(parsedResult.WebJsonString);
                var streamList = intlJson.RootElement.GetProperty("data").GetProperty("video_info").GetProperty("stream_list");
                int pDur = intlJson.RootElement.GetProperty("data").GetProperty("video_info").GetProperty("timelength").GetInt32() / 1000;
                var audioElements = intlJson.RootElement.GetProperty("data").GetProperty("video_info").GetProperty("dash_audio").EnumerateArray().ToList();

                foreach (var stream in streamList.EnumerateArray())
                {
                    if (stream.TryGetProperty("dash_video", out JsonElement dashVideo))
                    {
                        if (dashVideo.GetProperty("base_url").ToString() != "")
                        {
                            var videoId = stream.GetProperty("stream_info").GetProperty("quality").ToString();
                            var urlList = new List<string>() { dashVideo.GetProperty("base_url").ToString() };
                            urlList.AddRange(dashVideo.GetProperty("backup_url").EnumerateArray().Select(i => i.ToString()));
                            Video v = new()
                            {
                                dur = pDur,
                                id = videoId,
                                dfn = Config.qualitys[videoId],
                                bandwidth = Convert.ToInt64(dashVideo.GetProperty("bandwidth").ToString()) / 1000,
                                baseUrl = urlList.FirstOrDefault(i => !BaseUrlRegex().IsMatch(i), urlList.First()),
                                codecs = GetVideoCodec(dashVideo.GetProperty("codecid").ToString()),
                                size = dashVideo.TryGetProperty("size", out var sizeNode) ? Convert.ToDouble(sizeNode.ToString()) : 0
                            };
                            if (!parsedResult.VideoTracks.Contains(v)) parsedResult.VideoTracks.Add(v);
                        }
                    }
                }

                foreach (var node in audioElements)
                {
                    var urlList = new List<string>() { node.GetProperty("base_url").ToString() };
                    urlList.AddRange(node.GetProperty("backup_url").EnumerateArray().Select(i => i.ToString()));
                    Audio a = new()
                    {
                        id = node.GetProperty("id").ToString(),
                        dfn = node.GetProperty("id").ToString(),
                        dur = pDur,
                        bandwidth = Convert.ToInt64(node.GetProperty("bandwidth").ToString()) / 1000,
                        baseUrl = urlList.FirstOrDefault(i => !BaseUrlRegex().IsMatch(i), urlList.First()),
                        codecs = "M4A"
                    };
                    if (!parsedResult.AudioTracks.Contains(a)) parsedResult.AudioTracks.Add(a);
                }
            }
            return parsedResult;
        }

        var respJson = JsonDocument.Parse(parsedResult.WebJsonString);
        var data = respJson.RootElement;
        // 根据API版本自动定位数据节点
        JsonElement root;
        if (data.TryGetProperty("result", out var resultElem))
        {
            root = resultElem.TryGetProperty("video_info", out var vi) ? vi : resultElem;
        }
        else if (data.TryGetProperty("data", out var dataElem))
        {
            root = dataElem;
        }
        else
        {
            root = data;
        }

        bool bangumi = aidOri.StartsWith("ep:");

        if (root.TryGetProperty("dash", out _)) //dash
        {
            List<JsonElement>? audio = null;
            List<JsonElement>? video = null;
            List<JsonElement>? backgroundAudio = null;
            List<JsonElement>? roleAudio = null;
            int pDur = 0;

            if (root.TryGetProperty("dash", out var dashElem) && dashElem.TryGetProperty("duration", out var durElem))
                pDur = durElem.GetInt32();
            else if (root.TryGetProperty("timelength", out var tlElem))
                pDur = tlElem.GetInt32() / 1000;

            // DRM metadata
            if (root.TryGetProperty("is_drm", out var isDrmElem))
                parsedResult.IsDrm = isDrmElem.GetBoolean();
            if (root.TryGetProperty("drm_tech_type", out var techElem))
                parsedResult.DrmTechType = techElem.GetInt32();
            if (root.TryGetProperty("drm_type", out var typeElem))
                parsedResult.DrmType = typeElem.GetString() ?? "";
            if (parsedResult.IsDrm) LogDebug("DRM detected: type={0}, tech={1}", parsedResult.DrmType, parsedResult.DrmTechType);

            //免二压视频需要重新请求
            for (int reparsePass = 0; reparsePass < 2; reparsePass++)
            {
            if (reparsePass == 1)
            {
                if (appApi) break; //只有非APP接口需要免二压
                parsedResult.WebJsonString = await GetPlayJsonAsync(encoding, aidOri, aid, cid, epId, tvApi, intlApi, appApi, wantDrm, GetMaxQn());
                respJson.Dispose();
                respJson = JsonDocument.Parse(parsedResult.WebJsonString);
                var newRoot = respJson.RootElement;
                root = newRoot.TryGetProperty("result", out var rr) && rr.TryGetProperty("video_info", out var vvii) ? vvii :
                       newRoot.TryGetProperty("result", out var rr2) ? rr2 :
                       newRoot.TryGetProperty("data", out var dd) ? dd : newRoot;
            }
            if (root.TryGetProperty("dash", out var dash) && dash.TryGetProperty("video", out var vidArr))
                video = vidArr.EnumerateArray().ToList();
            if (root.TryGetProperty("dash", out dash) && dash.TryGetProperty("audio", out var audArr))
                audio = audArr.EnumerateArray().ToList();

            if (appApi && bangumi)
            {
                if (data.TryGetProperty("dubbing_info", out var dub) && dub.TryGetProperty("background_audio", out var bgArr))
                    backgroundAudio = bgArr.EnumerateArray().ToList();
                if (data.TryGetProperty("dubbing_info", out dub) && dub.TryGetProperty("role_audio_list", out var roleArr))
                    roleAudio = roleArr.EnumerateArray().ToList();
            }
            //处理杜比音频
            try
            {
                if (audio != null)
                {
                    if (!tvApi && root.GetProperty("dash").TryGetProperty("dolby", out JsonElement dolby))
                    {
                        if (dolby.TryGetProperty("audio", out JsonElement db))
                        {
                            audio.AddRange(db.EnumerateArray());
                        }
                    }
                }
            }
            catch (Exception) { /* non-critical metadata, ignore */ }

            //处理Hi-Res无损
            try
            {
                if (audio != null)
                {
                    if (!tvApi && root.GetProperty("dash").TryGetProperty("flac", out JsonElement hiRes))
                    {
                        if (hiRes.TryGetProperty("audio", out JsonElement db))
                        {
                            if (db.ValueKind != JsonValueKind.Null)
                                audio.Add(db);
                        }
                    }
                }
            }
            catch (Exception) { /* non-critical metadata, ignore */ }

            if (video != null)
            {
                foreach (var node in video)
                {
                    var urlList = new List<string>() { node.GetProperty("base_url").ToString() };
                    if (node.TryGetProperty("backup_url", out JsonElement element) && element.ValueKind != JsonValueKind.Null)
                    {
                        urlList.AddRange(element.EnumerateArray().Select(i => i.ToString()));
                    }
                    var videoId = node.GetProperty("id").ToString();
                    Video v = new()
                    {
                        dur = pDur,
                        id = videoId,
                        dfn = Config.qualitys[videoId],
                        bandwidth = Convert.ToInt64(node.GetProperty("bandwidth").ToString()) / 1000,
                        baseUrl = urlList.FirstOrDefault(i => !BaseUrlRegex().IsMatch(i), urlList.First()),
                        codecs = GetVideoCodec(node.GetProperty("codecid").ToString()),
                        size = node.TryGetProperty("size", out var sizeNode) ? Convert.ToDouble(sizeNode.ToString()) : 0
                    };
                    if (!tvApi && !appApi)
                    {
                        v.res = node.GetProperty("width").ToString() + "x" + node.GetProperty("height").ToString();
                        v.fps = node.GetProperty("frame_rate").ToString();
                    }
                    if (!parsedResult.VideoTracks.Contains(v)) parsedResult.VideoTracks.Add(v);
                }

                if (parsedResult.IsDrm && string.IsNullOrEmpty(parsedResult.KidHex))
                {
                    try
                    {
                        var firstVideo = video.First();
                        if (firstVideo.TryGetProperty("bilidrm_uri", out var drmUri))
                        {
                            var uri = drmUri.GetString() ?? "";
                            var lastSlash = uri.LastIndexOf("//", StringComparison.Ordinal);
                            if (lastSlash >= 0)
                                parsedResult.KidHex = uri[(lastSlash + 2)..];
                        }
                        if (firstVideo.TryGetProperty("widevine_pssh", out var pssh) && pssh.GetString() is string ps && ps.Length > 0)
                            parsedResult.PsshBase64 = ps;
                    }
                    catch (Exception ex) { LogDebug("DRM license info extraction error: {0}", ex.Message); }
                }
            }

            } // end for reparsePass

            if (audio != null)
            {
                foreach (var node in audio)
                {
                    var urlList = new List<string>() { node.GetProperty("base_url").ToString() };
                    if (node.TryGetProperty("backup_url", out JsonElement element) && element.ValueKind != JsonValueKind.Null)
                    {
                        urlList.AddRange(element.EnumerateArray().Select(i => i.ToString()));
                    }
                    var audioId = node.GetProperty("id").ToString();
                    var codecs = node.GetProperty("codecs").ToString();
                    codecs = codecs switch
                    {
                        "mp4a.40.2" => "M4A",
                        "mp4a.40.5" => "M4A",
                        "ec-3" => "E-AC-3",
                        "fLaC" => "FLAC",
                        _ => codecs
                    };

                    parsedResult.AudioTracks.Add(new Audio()
                    {
                        id = audioId,
                        dfn = audioId,
                        dur = pDur,
                        bandwidth = Convert.ToInt64(node.GetProperty("bandwidth").ToString()) / 1000,
                        baseUrl = urlList.FirstOrDefault(i => !BaseUrlRegex().IsMatch(i), urlList.First()),
                        codecs = codecs
                    });
                }
            }

            if (backgroundAudio != null && roleAudio != null)
            {
                foreach (var node in backgroundAudio)
                {
                    var audioId = node.GetProperty("id").ToString();
                    var urlList = new List<string> { node.GetProperty("base_url").ToString() };
                    urlList.AddRange(node.GetProperty("backup_url").EnumerateArray().Select(i => i.ToString()));
                    parsedResult.BackgroundAudioTracks.Add(new Audio()
                    {
                        id = audioId,
                        dfn = audioId,
                        dur = pDur,
                        bandwidth = Convert.ToInt64(node.GetProperty("bandwidth").ToString()) / 1000,
                        baseUrl = urlList.FirstOrDefault(i => !BaseUrlRegex().IsMatch(i), urlList.First()),
                        codecs = node.GetProperty("codecs").ToString()
                    });
                }

                foreach (var role in roleAudio)
                {
                    var roleAudioTracks = new List<Audio>();
                    foreach (var node in role.GetProperty("audio").EnumerateArray())
                    {
                        var audioId = node.GetProperty("id").ToString();
                        var urlList = new List<string> { node.GetProperty("base_url").ToString() };
                        urlList.AddRange(node.GetProperty("backup_url").EnumerateArray().Select(i => i.ToString()));
                        roleAudioTracks.Add(new Audio()
                        {
                            id = audioId,
                            dfn = audioId,
                            dur = pDur,
                            bandwidth = Convert.ToInt64(node.GetProperty("bandwidth").ToString()) / 1000,
                            baseUrl = urlList.FirstOrDefault(i => !BaseUrlRegex().IsMatch(i), urlList.First()),
                            codecs = node.GetProperty("codecs").ToString()
                        });
                    }
                    parsedResult.RoleAudioList.Add(new AudioMaterialInfo()
                    {
                        title = role.GetProperty("title").ToString(),
                        personName = role.GetProperty("person_name").ToString(),
                        path = $"{aid}/{aid}.{cid}.{role.GetProperty("audio_id").ToString()}.m4a",
                        audio = roleAudioTracks
                    });
                }
            }
        }
        else if (root.TryGetProperty("durl", out _)) //flv
        {
            //默认以最高清晰度解析
            parsedResult.WebJsonString = await GetPlayJsonAsync(encoding, aidOri, aid, cid, epId, tvApi, intlApi, appApi, wantDrm, GetMaxQn());
            using var newDoc = JsonDocument.Parse(parsedResult.WebJsonString);
            var newData = newDoc.RootElement;
            if (newData.TryGetProperty("result", out var r))
                root = r.TryGetProperty("video_info", out var vi) ? vi : r;
            else if (newData.TryGetProperty("data", out var d))
                root = d;
            string quality = "";
            string videoCodecid = "";
            string url = "";
            double size = 0;
            double length = 0;

            quality = root.GetProperty("quality").ToString();
            videoCodecid = root.GetProperty("video_codecid").ToString();
            //获取所有分段
            foreach (var node in root.GetProperty("durl").EnumerateArray())
            {
                parsedResult.Clips.Add(node.GetProperty("url").ToString());
                size += node.GetProperty("size").GetDouble();
                length += node.GetProperty("length").GetDouble();
            }
            //TV模式可用清晰度
            if (root.TryGetProperty("qn_extras", out JsonElement qnExtras))
            {
                parsedResult.Dfns.AddRange(qnExtras.EnumerateArray().Select(node => node.GetProperty("qn").ToString()));
            }
            else if (root.TryGetProperty("accept_quality", out JsonElement acceptQuality)) //非tv模式可用清晰度
            {
                parsedResult.Dfns.AddRange(acceptQuality.EnumerateArray()
                    .Select(node => node.ToString())
                    .Where(_qn => !string.IsNullOrEmpty(_qn)));
            }

            Video v = new()
            {
                id = quality,
                dfn = Config.qualitys[quality],
                baseUrl = url,
                codecs = GetVideoCodec(videoCodecid),
                dur = (int)length / 1000,
                size = size
            };
            if (!parsedResult.VideoTracks.Contains(v)) parsedResult.VideoTracks.Add(v);
        }

        // 番剧片头片尾转分段信息, 预计效果: 正片? -> 片头 -> 正片 -> 片尾
        if (bangumi)
        {
            if (root.TryGetProperty("clip_info_list", out JsonElement clipList))
            {
                parsedResult.ExtraPoints.AddRange(clipList.EnumerateArray().Select(clip => new ViewPoint()
                    {
                        title = clip.GetProperty("toastText").ToString().Replace("即将跳过", ""),
                        start = clip.GetProperty("start").GetInt32(),
                        end = clip.GetProperty("end").GetInt32()
                    })
                );
                parsedResult.ExtraPoints.Sort((p1, p2) => p1.start.CompareTo(p2.start));
                var newPoints = new List<ViewPoint>();
                int lastEnd = 0;
                foreach (var point in parsedResult.ExtraPoints)
                {
                    if (lastEnd < point.start)
                        newPoints.Add(new ViewPoint() { title = "正片", start = lastEnd, end = point.start });
                    newPoints.Add(point);
                    lastEnd = point.end;
                }
                parsedResult.ExtraPoints = newPoints;
            }

        }

        respJson.Dispose();
        return parsedResult;
    }

    /// <summary>
    /// 编码转换
    /// </summary>
    /// <param name="code"></param>
    /// <returns></returns>
    private static string GetVideoCodec(string code)
    {
        return code switch
        {
            "13" => "AV1",
            "12" => "HEVC",
            "7" => "AVC",
            _ => "UNKNOWN"
        };
    }

    private static string GetMaxQn()
    {
        return Config.qualitys.Keys.Select(int.Parse).Max().ToString();
    }

    private static string GetTimeStamp(bool bflag)
    {
        DateTimeOffset ts = DateTimeOffset.Now;
        return bflag ? ts.ToUnixTimeSeconds().ToString() : ts.ToUnixTimeMilliseconds().ToString();
    }

    private static string GetSign(string parameters, bool isBiliPlus)
    {
        string toEncode = parameters + (isBiliPlus ? "acd495b248ec528c2eed1e862d393126" : "59b43e04ad6965f34319062b478f83dd");
        return Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(toEncode)));
    }

    [GeneratedRegex("window.__playinfo__=([\\s\\S]*?)<\\/script>")]
    private static partial Regex PlayerJsonRegex();
    [GeneratedRegex("http.*:\\d+")]
    private static partial Regex BaseUrlRegex();
}