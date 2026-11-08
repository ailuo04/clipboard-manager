using System.Net;
using System.Net.Http;

namespace ClipboardManager.Services;

internal static class GitHubHttp
{
    internal static bool IsReleaseDownloadUrl(string repository, string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return false;
        return uri.AbsolutePath.StartsWith($"/{repository}/releases/download/", StringComparison.OrdinalIgnoreCase);
    }

    internal static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new HttpRequestException("GitHub 未找到可公开访问的正式发行版或附件（HTTP 404）。请确认仓库公开可访问，并且已发布正式版本。", null, response.StatusCode);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new HttpRequestException("公开更新源拒绝了匿名请求（HTTP 401）。更新器不使用个人访问令牌；请检查仓库可见性或网络代理设置。", null, response.StatusCode);
        if ((int)response.StatusCode == 429 || response.StatusCode == HttpStatusCode.Forbidden)
        {
            bool rateLimited = (int)response.StatusCode == 429 || response.Headers.RetryAfter is not null
                || (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Any(value => value == "0"));
            if (rateLimited)
                throw new HttpRequestException($"GitHub API 请求频率已达上限（HTTP {(int)response.StatusCode}）。{GetRateLimitRetryHint(response)}", null, response.StatusCode);
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new HttpRequestException("GitHub 拒绝了公开更新请求（HTTP 403）。请确认仓库可公开访问；若仓库已公开，可能是 GitHub 临时限制或网络代理拦截，请稍后重试。", null, response.StatusCode);
        }
        response.EnsureSuccessStatusCode();
    }

    private static string GetRateLimitRetryHint(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is TimeSpan delay)
            return $"请在约 {Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds))} 秒后重试。";
        if (retryAfter?.Date is DateTimeOffset retryAt)
            return $"请于 {retryAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} 后重试。";
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues)
            && long.TryParse(resetValues.FirstOrDefault(), out long resetSeconds))
        {
            try { return $"请于 {DateTimeOffset.FromUnixTimeSeconds(resetSeconds).ToLocalTime():yyyy-MM-dd HH:mm:ss} 后重试。"; }
            catch (ArgumentOutOfRangeException) { }
        }
        return "请稍后重试。";
    }
}
