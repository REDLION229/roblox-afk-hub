using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace AfkHub;

/// <summary>
/// One-click updates from this app's GitHub Releases. The newest release's zip is downloaded, checked against the
/// fingerprints published with it (SHA256SUMS.txt), and the exe is swapped in place: Windows lets a running exe be
/// renamed, so the old one becomes "… .exe.old" and the new one takes its name. Pins and shortcuts keep working,
/// and settings, accounts and macros live outside the exe, so nothing else changes.
/// </summary>
sealed class Updater
{
    public const string Repo = "REDLION229/roblox-afk-hub";
    const string ExeName = "Roblox AFK Hub.exe";
    static readonly JavaScriptSerializer Json = new();
    static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) };

    public sealed record Release(string Version, string Notes, string ZipName, string ZipUrl, string SumsUrl, string Page);

    public Release Available { get; private set; }
    public string Status { get; private set; } = "";
    public bool Busy { get; private set; }
    public DateTime LastCheckUtc { get; private set; }
    /// <summary>true = checked and on the newest version, false = a newer one is out, null = not checked / couldn't tell.</summary>
    public bool? UpToDate { get; private set; }

    public static string ExePath => System.Windows.Forms.Application.ExecutablePath;

    /// <summary>The old exe left behind by the last update; removed once the new one is running.</summary>
    public static void CleanUpOld()
    {
        var old = ExePath + ".old";
        if (!File.Exists(old)) return;
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 20 && File.Exists(old); i++)
            {
                try { File.Delete(old); } catch { await Task.Delay(500); }
            }
        });
    }

    // ---------- talking to GitHub ----------

    static HttpRequestMessage Request(string url, bool file)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, url);
        r.Headers.UserAgent.ParseAdd("RobloxAfkHub/" + Program.Version);
        r.Headers.Accept.ParseAdd(file ? "application/octet-stream" : "application/vnd.github+json");
        return r;
    }

    /// <summary>GETs a GitHub URL; downloads redirect to a signed link, which is followed without any login attached.</summary>
    static async Task<HttpResponseMessage> Get(string url, bool file)
    {
        for (int hop = 0; hop < 5; hop++)
        {
            var resp = await Http.SendAsync(Request(url, file));
            if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } loc)
            {
                url = loc.IsAbsoluteUri ? loc.ToString() : new Uri(new Uri(url), loc).ToString();
                resp.Dispose();
                if (!url.StartsWith("https://")) throw new InvalidOperationException("Refusing a non-HTTPS download.");
                continue;
            }
            return resp;
        }
        throw new InvalidOperationException("Too many redirects.");
    }

    static async Task<byte[]> Download(string url)
    {
        using var resp = await Get(url, file: true);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"GitHub answered HTTP {(int)resp.StatusCode}.");
        return await resp.Content.ReadAsByteArrayAsync();
    }

    public static Version ParseVersion(string v)
    {
        v = (v ?? "").Trim().TrimStart('v', 'V');
        if (!v.Contains('.')) v += ".0";
        return Version.TryParse(v, out var x) ? x : null;
    }

    // ---------- checking ----------

    /// <summary>Looks for a newer release. Quiet unless something is found (or it's a manual check).</summary>
    public async Task CheckAsync()
    {
        if (Busy) return;
        Busy = true;
        LastCheckUtc = DateTime.UtcNow;
        Status = "Checking…";
        try
        {
            using var resp = await Get($"https://api.github.com/repos/{Repo}/releases/latest", file: false);
            UpToDate = null;
            if (resp.StatusCode == HttpStatusCode.NotFound) { Available = null; Status = "No releases published yet."; return; }
            if (!resp.IsSuccessStatusCode) { Status = $"Couldn't check (GitHub answered HTTP {(int)resp.StatusCode})."; return; }
            var r = Json.Deserialize<Dictionary<string, object>>(await resp.Content.ReadAsStringAsync());
            string tag = r["tag_name"] as string;
            var assets = (r["assets"] as System.Collections.ArrayList)?.Cast<Dictionary<string, object>>().ToList() ?? new();
            var zip = assets.FirstOrDefault(a => (a["name"] as string ?? "").EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            var sums = assets.FirstOrDefault(a => (a["name"] as string) == "SHA256SUMS.txt");

            var latest = ParseVersion(tag);
            var mine = ParseVersion(Program.Version);
            if (latest == null || mine == null || latest <= mine) { Available = null; UpToDate = true; Status = $"You're on the latest version (v{Program.Version})."; return; }
            UpToDate = false;
            if (zip == null || sums == null) { Available = null; Status = $"v{latest} is out, but its download isn't attached yet."; return; }

            string notes = (r.TryGetValue("body", out var b) ? b as string : null) ?? "";
            Available = new Release(latest.ToString(), notes, zip["name"] as string, zip["url"] as string, sums["url"] as string, r["html_url"] as string);
            Status = $"v{Available.Version} is available.";
        }
        catch (Exception ex) { Status = "Couldn't check for updates: " + ex.Message; }
        finally { Busy = false; }
    }

    // ---------- installing ----------

    /// <summary>Downloads, verifies and swaps in the new exe. Returns null when it's ready to restart, else why not.</summary>
    public async Task<string> InstallAsync()
    {
        var rel = Available;
        if (rel == null) return "There's no update to install.";
        if (Busy) return "Already working on it.";
        Busy = true;
        string newExe = ExePath + ".new";
        try
        {
            Status = $"Downloading v{rel.Version}…";
            var sumsText = System.Text.Encoding.UTF8.GetString(await Download(rel.SumsUrl));
            var sums = sumsText.Split('\n').Select(l => l.Trim().Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries))
                .Where(p => p.Length == 2).ToDictionary(p => p[1].Trim(), p => p[0].ToLowerInvariant(), StringComparer.OrdinalIgnoreCase);
            var zipBytes = await Download(rel.ZipUrl);

            // Only install exactly what was published: both the zip and the exe inside must match their fingerprints.
            Status = "Checking the download…";
            if (!sums.TryGetValue(rel.ZipName, out var zipHash) || Sha256(zipBytes) != zipHash)
                return Fail("The download didn't match its published fingerprint, so it wasn't installed.");
            byte[] exeBytes;
            using (var za = new ZipArchive(new MemoryStream(zipBytes)))
            {
                var entry = za.Entries.FirstOrDefault(e => e.Name.Equals(ExeName, StringComparison.OrdinalIgnoreCase));
                if (entry == null) return Fail("The download doesn't contain the app.");
                using var es = entry.Open();
                using var ms = new MemoryStream();
                es.CopyTo(ms);
                exeBytes = ms.ToArray();
            }
            if (!sums.TryGetValue(ExeName, out var exeHash) || Sha256(exeBytes) != exeHash)
                return Fail("The app inside the download didn't match its fingerprint, so it wasn't installed.");

            Status = "Installing…";
            string old = ExePath + ".old";
            File.WriteAllBytes(newExe, exeBytes);
            if (File.Exists(old)) File.Delete(old);
            File.Move(ExePath, old); // allowed while it's running
            try { File.Move(newExe, ExePath); }
            catch { File.Move(old, ExePath); throw; }
            Status = $"Updated to v{rel.Version}. Restarting…";
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return Fail("Windows didn't allow replacing the app in this folder. Move Roblox AFK Hub to a folder you own (like Documents or the Desktop) and try again.");
        }
        catch (Exception ex) { return Fail("The update failed: " + ex.Message); }
        finally
        {
            try { if (File.Exists(newExe)) File.Delete(newExe); } catch { }
            Busy = false;
        }
    }

    string Fail(string why) { Status = why; return why; }

    static string Sha256(byte[] data)
    {
        using var sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(data).Select(x => x.ToString("x2")));
    }

    /// <summary>Starts the freshly installed exe; it waits for this copy to close (see Program.Main).</summary>
    public static void Restart() =>
        Process.Start(new ProcessStartInfo(ExePath, "--restart --updated") { UseShellExecute = true });
}
