using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using System.Web.Script.Serialization;

namespace AfkHub;

sealed class RobloxAuthException : Exception
{
    public RobloxAuthException(string message) : base(message) { }
}

public sealed class GameTarget
{
    public long PlaceId { get; set; }
    public string JobId { get; set; }
    public string LinkCode { get; set; }
    public string AccessCode { get; set; }
    public string ShareCode { get; set; }
    public string ShareType { get; set; }
    /// <summary>Join whatever server this user is in (Roblox's "Join" button on a friend).</summary>
    public long FollowUserId { get; set; }
    public string FollowName { get; set; }

    public bool IsPrivate => !string.IsNullOrEmpty(LinkCode) || !string.IsNullOrEmpty(AccessCode);

    public string Describe()
    {
        if (FollowUserId != 0) return $"Joining {FollowName ?? "a friend"}'s server";
        if (PlaceId == 0 && ShareCode != null) return $"Share link ({ShareType}) — resolved when you launch";
        if (IsPrivate) return $"Private server · place {PlaceId}";
        if (!string.IsNullOrEmpty(JobId)) return $"Specific server · place {PlaceId}";
        return $"Public game · place {PlaceId}";
    }

    public GameTarget Clone() => (GameTarget)MemberwiseClone();
}

static class GameLink
{
    public static GameTarget Parse(string input)
    {
        input = (input ?? "").Trim();
        if (input.Length == 0) throw new FormatException("Paste a game link, private server link, or place ID.");
        if (Regex.IsMatch(input, @"^\d+$")) return new GameTarget { PlaceId = long.Parse(input) };

        if (!input.Contains("://")) input = "https://" + input;
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || !uri.Host.EndsWith("roblox.com", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("That doesn't look like a roblox.com link.");

        var q = HttpUtility.ParseQueryString(uri.Query);
        var t = new GameTarget();

        var m = Regex.Match(uri.AbsolutePath, @"/games/(\d+)", RegexOptions.IgnoreCase);
        if (m.Success) t.PlaceId = long.Parse(m.Groups[1].Value);
        if (long.TryParse(q["placeId"], out var pid)) t.PlaceId = pid;

        t.LinkCode = NullIfEmpty(q["privateServerLinkCode"]) ?? NullIfEmpty(q["linkCode"]);
        t.AccessCode = NullIfEmpty(q["accessCode"]);
        t.JobId = NullIfEmpty(q["gameInstanceId"]) ?? NullIfEmpty(q["jobId"]);

        if (uri.AbsolutePath.StartsWith("/share", StringComparison.OrdinalIgnoreCase) && NullIfEmpty(q["code"]) != null)
        {
            t.ShareCode = q["code"];
            t.ShareType = NullIfEmpty(q["type"]) ?? "Server";
        }

        if (t.PlaceId == 0 && t.ShareCode == null) throw new FormatException("Couldn't find a game in that link.");
        return t;
    }

    static string NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

static class RobloxApi
{
    const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";
    static readonly HttpClient Http = new(new HttpClientHandler
    {
        UseCookies = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
    }) { Timeout = TimeSpan.FromSeconds(25) };
    static readonly JavaScriptSerializer Json = new();
    static readonly Random Rng = new();

    static HttpRequestMessage Request(HttpMethod method, string url, string cookie, string csrf = null, string json = null, string referer = null)
    {
        // A login cookie is full access to the account: it only ever goes to Roblox itself, over HTTPS.
        var uri = new Uri(url);
        if (uri.Scheme != "https" || !(uri.Host == "roblox.com" || uri.Host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Refusing to send a Roblox login to " + uri.Host);
        var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("Referer", referer ?? "https://www.roblox.com/");
        req.Headers.TryAddWithoutValidation("Origin", "https://www.roblox.com");
        if (csrf != null) req.Headers.TryAddWithoutValidation("x-csrf-token", csrf);
        if (method == HttpMethod.Post) req.Content = new StringContent(json ?? "{}", Encoding.UTF8, "application/json");
        return req;
    }

    /// <summary>Roblox answers the first POST with 403 + a CSRF token; repeat the request with it.</summary>
    static async Task<HttpResponseMessage> SendWithCsrf(Func<string, HttpRequestMessage> make)
    {
        var resp = await Http.SendAsync(make(null));
        if (resp.StatusCode == HttpStatusCode.Forbidden && resp.Headers.TryGetValues("x-csrf-token", out var tokens))
        {
            resp.Dispose();
            resp = await Http.SendAsync(make(tokens.First()));
        }
        return resp;
    }

    public static string CleanCookie(string raw)
    {
        raw = (raw ?? "").Trim().Trim('"');
        const string prefix = ".ROBLOSECURITY=";
        if (raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) raw = raw.Substring(prefix.Length);
        int semi = raw.IndexOf(';');
        return semi >= 0 ? raw.Substring(0, semi).Trim() : raw;
    }

    public static async Task<(long Id, string Name, string DisplayName)> GetUser(string cookie)
    {
        using var resp = await Http.SendAsync(Request(HttpMethod.Get, "https://users.roblox.com/v1/users/authenticated", cookie));
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new RobloxAuthException("This login has expired or was logged out. Remove the account and add it again.");
        var body = await RobloxApi.ReadText(resp);
        if (!resp.IsSuccessStatusCode) throw new Exception($"Roblox returned HTTP {(int)resp.StatusCode} when checking the login.");
        var d = Json.Deserialize<Dictionary<string, object>>(body);
        return (Convert.ToInt64(d["id"]), d["name"] as string, d.TryGetValue("displayName", out var dn) ? dn as string : null);
    }

    public static async Task<string> GetAuthTicket(string cookie, long placeId)
    {
        string referer = placeId > 0 ? $"https://www.roblox.com/games/{placeId}/" : "https://www.roblox.com/";
        using var resp = await SendWithCsrf(csrf =>
            Request(HttpMethod.Post, "https://auth.roblox.com/v1/authentication-ticket", cookie, csrf, "{}", referer));
        if (resp.Headers.TryGetValues("rbx-authentication-ticket", out var t)) return t.First();
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new RobloxAuthException("This login has expired or was logged out. Remove the account and add it again.");
        if ((int)resp.StatusCode == 429)
            throw new Exception("Roblox is rate-limiting launches. Wait a minute and try again.");
        throw new Exception($"Roblox didn't return a join ticket (HTTP {(int)resp.StatusCode}).");
    }

    /// <summary>Response body as UTF-8 (Roblox doesn't always send a charset, and .NET would guess Latin-1).</summary>
    public static async Task<string> ReadText(HttpResponseMessage resp) =>
        Encoding.UTF8.GetString(await resp.Content.ReadAsByteArrayAsync());

    /// <summary>A logged-in GET; returns the response body.</summary>
    public static async Task<string> GetWithCookie(string cookie, string url)
    {
        using var resp = await Http.SendAsync(Request(HttpMethod.Get, url, cookie));
        if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new RobloxAuthException("This login has expired.");
        resp.EnsureSuccessStatusCode();
        return await RobloxApi.ReadText(resp);
    }

    /// <summary>A logged-in POST (with Roblox's CSRF handshake); returns the response body.</summary>
    public static async Task<string> PostWithCsrf(string cookie, string url, string json)
    {
        using var resp = await SendWithCsrf(csrf => Request(HttpMethod.Post, url, cookie, csrf, json));
        if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new RobloxAuthException("This login has expired.");
        resp.EnsureSuccessStatusCode();
        return await RobloxApi.ReadText(resp);
    }

    /// <summary>Avatar headshot image URLs (public API, no login needed).</summary>
    public static async Task<Dictionary<long, string>> GetHeadshots(IEnumerable<long> userIds)
    {
        var result = new Dictionary<long, string>();
        var ids = string.Join(",", userIds.Distinct());
        if (ids.Length == 0) return result;
        var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds={ids}&size=150x150&format=Png&isCircular=false");
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        using var resp = await Http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return result;
        var body = Json.Deserialize<Dictionary<string, object>>(await RobloxApi.ReadText(resp));
        if (body.TryGetValue("data", out var data) && data is System.Collections.ArrayList list)
            foreach (Dictionary<string, object> item in list)
                if (item.TryGetValue("imageUrl", out var url) && url is string u && u.Length > 0)
                    result[Convert.ToInt64(item["targetId"])] = u;
        return result;
    }

    /// <summary>Turns a roblox.com/share?code=… link into a place ID and private server link code.</summary>
    public static async Task ResolveShareLink(string cookie, GameTarget target)
    {
        var payload = Json.Serialize(new { linkId = target.ShareCode, linkType = target.ShareType });
        using var resp = await SendWithCsrf(csrf =>
            Request(HttpMethod.Post, "https://apis.roblox.com/sharelinks/v1/resolve-link", cookie, csrf, payload));
        var body = await RobloxApi.ReadText(resp);
        if (!resp.IsSuccessStatusCode) throw new Exception($"Couldn't open that share link (HTTP {(int)resp.StatusCode}).");

        var place = Regex.Match(body, @"""placeId""\s*:\s*""?(\d+)");
        if (!place.Success) throw new Exception("That share link didn't point to a game Roblox would let this account join.");
        target.PlaceId = long.Parse(place.Groups[1].Value);
        var link = Regex.Match(body, @"""linkCode""\s*:\s*""([^""]+)""");
        if (link.Success) target.LinkCode ??= link.Groups[1].Value;
        var instance = Regex.Match(body, @"""instanceId""\s*:\s*""([0-9a-fA-F-]{36})""");
        if (instance.Success) target.JobId ??= instance.Groups[1].Value;
    }

    /// <summary>Best effort: older private-server joins also wanted an access code scraped from the game page.</summary>
    public static async Task<string> TryGetAccessCode(string cookie, GameTarget target)
    {
        try
        {
            var url = $"https://www.roblox.com/games/{target.PlaceId}/?privateServerLinkCode={Uri.EscapeDataString(target.LinkCode)}";
            using var resp = await Http.SendAsync(Request(HttpMethod.Get, url, cookie));
            var html = await RobloxApi.ReadText(resp);
            var m = Regex.Match(html, @"joinPrivateGame\(\d+\s*,\s*'([0-9a-fA-F-]{36})'");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch { return null; }
    }

    public static string BuildLaunchUri(string ticket, GameTarget t)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string tracker = Rng.Next(100000, 130000).ToString() + Rng.Next(100000, 900000).ToString();
        const string launcher = "https://assetgame.roblox.com/game/PlaceLauncher.ashx";

        string placeLauncher;
        if (t.FollowUserId != 0)
            placeLauncher = $"{launcher}?request=RequestFollowUser&userId={t.FollowUserId}";
        else if (t.IsPrivate)
        {
            placeLauncher = $"{launcher}?request=RequestPrivateGame&placeId={t.PlaceId}";
            if (!string.IsNullOrEmpty(t.AccessCode)) placeLauncher += "&accessCode=" + t.AccessCode;
            if (!string.IsNullOrEmpty(t.LinkCode)) placeLauncher += "&linkCode=" + t.LinkCode;
        }
        else if (!string.IsNullOrEmpty(t.JobId))
            placeLauncher = $"{launcher}?request=RequestGameJob&browserTrackerId={tracker}&placeId={t.PlaceId}&gameId={t.JobId}&isPlayTogetherGame=false";
        else
            placeLauncher = $"{launcher}?request=RequestGame&browserTrackerId={tracker}&placeId={t.PlaceId}&isPlayTogetherGame=false";

        return $"roblox-player:1+launchmode:play+gameinfo:{ticket}+launchtime:{now}+placelauncherurl:{Uri.EscapeDataString(placeLauncher)}" +
               $"+browsertrackerid:{tracker}+robloxLocale:en_us+gameLocale:en_us+channel:+LaunchExp:InApp";
    }
}
