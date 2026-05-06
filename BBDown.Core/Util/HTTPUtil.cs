using System.Net;
using System.Net.Http.Headers;
using static BBDown.Core.Logger;

namespace BBDown.Core.Util;

public static class HTTPUtil
{

    private static HttpClient CreateAppHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
        };
        if (Config.SKIP_SSL_CHECK)
        {
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
            LogDebug("SSL 证书验证已禁用");
        }
        return new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };
    }

    private static HttpClient? _appHttpClient;
    public static HttpClient AppHttpClient
    {
        get
        {
            _appHttpClient ??= CreateAppHttpClient();
            return _appHttpClient;
        }
    }

    private static readonly string[] platforms = { "Windows NT 10.0; Win64", "Macintosh; Intel Mac OS X 10_15", "X11; Linux x86_64" };

    private static string RandomVersion(int min, int max)
    {
        double version = Random.Shared.NextDouble() * (max - min) + min;
        return version.ToString("F3");
    }

    private static string GetRandomUserAgent()
    {
        string[] browsers = { $"AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{RandomVersion(80, 110)} Safari/537.36", $"Gecko/20100101 Firefox/{RandomVersion(80, 110)}" };
        return $"Mozilla/5.0 ({platforms[Random.Shared.Next(platforms.Length)]}) {browsers[Random.Shared.Next(browsers.Length)]}";
    }

    public static string UserAgent { get; set; } = GetRandomUserAgent();

    public static async Task<string> GetWebSourceAsync(string url, string? userAgent = null)
    {
        using var webRequest = new HttpRequestMessage(HttpMethod.Get, url);
        webRequest.Headers.TryAddWithoutValidation("User-Agent", userAgent ?? UserAgent);
        webRequest.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate");
        webRequest.Headers.TryAddWithoutValidation("Cookie", (url.Contains("/ep") || url.Contains("/ss")) ? Config.COOKIE + ";CURRENT_FNVAL=4048;" : Config.COOKIE);
        if (url.Contains("api.bilibili.com"))
            webRequest.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
        if (url.Contains("api.bilibili.tv"))
            webRequest.Headers.TryAddWithoutValidation("sec-ch-ua", "\"Google Chrome\";v=\"131\", \"Chromium\";v=\"131\", \"Not_A Brand\";v=\"24\"");
        webRequest.Headers.CacheControl = CacheControlHeaderValue.Parse("no-cache");
        webRequest.Headers.Connection.Clear();

        LogDebug("获取网页内容: Url: {0}, Headers: {1}", url, webRequest.Headers);
        var webResponse = (await AppHttpClient.SendAsync(webRequest, HttpCompletionOption.ResponseHeadersRead)).EnsureSuccessStatusCode();

        string htmlCode = await webResponse.Content.ReadAsStringAsync();
        LogDebug("Response: {0}", htmlCode);
        return htmlCode;
    }

    // 重写重定向处理, 自动跟随多次重定向
    public static async Task<string> GetWebLocationAsync(string url)
    {
        // 先尝试 HEAD，部分服务器不支持则 fallback 到 GET
        foreach (var method in new[] { HttpMethod.Head, HttpMethod.Get })
        {
            try
            {
                using var webRequest = new HttpRequestMessage(method, url);
                webRequest.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                webRequest.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate");
                webRequest.Headers.CacheControl = CacheControlHeaderValue.Parse("no-cache");
                webRequest.Headers.Connection.Clear();

                LogDebug("获取网页重定向地址(method={1}): Url: {0}", url, method);
                var webResponse = (await AppHttpClient.SendAsync(webRequest, HttpCompletionOption.ResponseHeadersRead)).EnsureSuccessStatusCode();
                string location = webResponse.RequestMessage?.RequestUri?.AbsoluteUri ?? url;
                LogDebug("Location: {0}", location);
                return location;
            }
            catch (HttpRequestException) when (method == HttpMethod.Head)
            {
                // HEAD 不被支持，回退到 GET
                LogDebug("HEAD 请求失败，尝试 GET");
            }
        }
        return url; // fallback: return original URL
    }

    public static async Task<byte[]> GetPostResponseAsync(string Url, byte[] postData, Dictionary<string, string>? headers = null)
    {
        LogDebug("Post to: {0}, data: {1}", Url, Convert.ToBase64String(postData));

        ByteArrayContent content = new(postData);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/grpc");

        HttpRequestMessage request = new()
        {
            RequestUri = new Uri(Url),
            Method = HttpMethod.Post,
            Content = content,
            //Version = HttpVersion.Version20
        };

        if (headers != null)
        {
            foreach (KeyValuePair<string, string> header in headers)
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        else
        {
            request.Headers.TryAddWithoutValidation("User-Agent", "Dalvik/2.1.0 (Linux; U; Android 6.0.1; oneplus a5010 Build/V417IR) 6.10.0 os/android model/oneplus a5010 mobi_app/android build/6100500 channel/bili innerVer/6100500 osVer/6.0.1 network/2");
            request.Headers.TryAddWithoutValidation("grpc-encoding", "gzip");
        }

        HttpResponseMessage response = await AppHttpClient.SendAsync(request);
        byte[] bytes = await response.Content.ReadAsByteArrayAsync();

        return bytes;
    }
}