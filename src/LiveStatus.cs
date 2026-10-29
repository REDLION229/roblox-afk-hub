using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace AfkHub;

/// <summary>
/// One Discord message that keeps itself up to date while you're AFK: how long, which accounts are in game,
/// macro loops, presses — and optionally a picture of the Roblox windows. Webhooks may edit their own
/// messages, so it's the same message every time: pin it and check it from your phone. Edits don't ping,
/// so alerts still come as separate messages.
/// </summary>
sealed class LiveStatus
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    static readonly JavaScriptSerializer Json = new();
    static readonly TimeSpan Every = TimeSpan.FromMinutes(2);
    const string ShotName = "roblox.jpg";

    readonly Hub _hub;
    DateTime _lastUpdate;
    bool _busy;
    DateTime _lastFailLog;

    public LiveStatus(Hub hub) => _hub = hub;

    Config Cfg => _hub.Cfg;
    bool Enabled => Cfg.WebhookLiveStatus && Notifier.LooksValid(Cfg.WebhookUrl);

    public void Tick(DateTime now)
    {
        if (!Enabled || !_hub.AfkMode || _busy) return;
        // Soon after AFK starts (so the accounts have had a moment to open), then every couple of minutes.
        var session = _hub.Stats.Current;
        if (_lastUpdate == default && session != null && now - session.StartUtc < TimeSpan.FromSeconds(45)) return;
        if (now - _lastUpdate < Every) return;
        _lastUpdate = now;
        _ = SendAsync(Build(ended: false), Cfg.LiveScreenshot);
    }

    /// <summary>AFK mode just ended: turn the message into the final summary (called before the watchdog forgets the accounts).</summary>
    public void Finish()
    {
        _lastUpdate = default;
        if (!Enabled || _hub.Stats.Current == null) return;
        _ = SendAsync(Build(ended: true), false);
    }

    // ---------- the message ----------

    (string Title, string Text, int Color) Build(bool ended)
    {
        var s = _hub.Stats.Current;
        var watched = _hub.Watchdog.Accounts;
        var lines = new List<string>();
        int ok;

        if (watched.Count > 0)
        {
            ok = watched.Count(w => w.State == "ok");
            foreach (var w in watched)
            {
                var label = w.Account.ToString();
                var parts = new List<string>();
                if (s != null && s.InGameSeconds.TryGetValue(label, out var secs)) parts.Add($"in game {Format.Duration(TimeSpan.FromSeconds(secs))}");
                if (w.State != "ok") parts.Add(Watchdog.Describe(w));
                if (MacroLine("acct:" + w.UserId) is { } m) parts.Add(m);
                if (w.Rejoins > 0) parts.Add($"rejoined {w.Rejoins}×");
                lines.Add($"{(w.State == "ok" ? "✅" : "❌")} **{w.Account.DisplayName ?? w.Account.Name}** — {string.Join(" · ", parts.DefaultIfEmpty("starting"))}");
            }
        }
        else
        {
            // Windows opened outside the app: no account to check, just show what's there.
            var wins = _hub.Tracker.Windows;
            ok = wins.Count(Watchdog.InGame);
            foreach (var w in wins) lines.Add($"{(Watchdog.InGame(w) ? "✅" : "❌")} **{w.Label}** — {w.Status}");
        }
        int total = Math.Max(watched.Count, watched.Count > 0 ? 0 : _hub.Tracker.Windows.Count);

        string dur = s != null ? Format.Duration(s.Duration) : "0s";
        string stats = s != null ? $"{s.Presses} anti-AFK press{(s.Presses == 1 ? "" : "es")} · {s.Disconnects} disconnect{(s.Disconnects == 1 ? "" : "s")} · {s.Rejoins} rejoin{(s.Rejoins == 1 ? "" : "s")}" : "";

        if (ended)
            return ($"⏹️ AFK ended after {dur}", $"{stats}\n{string.Join("\n", lines)}\n\n*Updates again next time AFK mode is on.*", 0x8A8F98);
        bool allOk = total > 0 && ok == total;
        string title = total == 0 ? $"🟡 AFK for {dur} — no Roblox windows yet" : $"{(allOk ? "🟢" : "🔴")} AFK for {dur} — {ok}/{total} in game";
        return (title, $"{stats}\n{string.Join("\n", lines)}", allOk ? Notifier.Green : total == 0 ? Notifier.Amber : Notifier.Red);
    }

    string MacroLine(string key)
    {
        if (!Cfg.MacroRunKeys.Contains(key) || !Cfg.MacroAssignments.TryGetValue(key, out var a)) return null;
        var name = _hub.Macros.Get(a.MacroId)?.Name;
        if (name == null) return null;
        int loops = (_hub.LoopTotals.TryGetValue(key, out var t) ? t : 0) + (_hub.Engine.Window?.MacroKey == key ? _hub.Engine.Loops : 0);
        return $"▶ {name} ({loops} loop{(loops == 1 ? "" : "s")})";
    }

    // ---------- sending ----------

    async Task SendAsync((string Title, string Text, int Color) msg, bool withShot)
    {
        if (_busy) return;
        _busy = true;
        string url = Cfg.WebhookUrl.Trim();
        try
        {
            var shot = withShot ? await Task.Run(CaptureWindows) : null;
            string id = Cfg.LiveStatusMessageId;
            string error = null;
            if (id != null)
            {
                var (status, err) = await PostAsync(HttpMethod("PATCH"), $"{Base(url)}/messages/{id}{Thread(url)}", msg, shot, editing: true);
                if (status == HttpStatusCode.NotFound) id = null; // deleted in Discord: start a new one
                else error = err;
            }
            if (id == null)
            {
                var (status, err, newId) = await PostNewAsync(url, msg, shot);
                error = err;
                if (newId != null) { Cfg.LiveStatusMessageId = newId; _hub.SaveConfig(); }
            }
            if (error != null && DateTime.UtcNow - _lastFailLog > TimeSpan.FromMinutes(10))
            {
                _lastFailLog = DateTime.UtcNow;
                _hub.Log("Couldn't update the live status message: " + error, Level.Warn);
            }
        }
        catch (Exception ex) { _hub.Log("Couldn't update the live status message: " + ex.Message, Level.Warn); }
        finally { _busy = false; }
    }

    static HttpMethod HttpMethod(string m) => new(m);

    /// <summary>The webhook link without any ?query (a thread_id is passed separately).</summary>
    static string Base(string url) { int q = url.IndexOf('?'); return q < 0 ? url : url.Substring(0, q); }
    static string Thread(string url)
    {
        var m = System.Text.RegularExpressions.Regex.Match(url, @"[?&]thread_id=(\d+)");
        return m.Success ? "?thread_id=" + m.Groups[1].Value : "";
    }

    async Task<(HttpStatusCode, string, string)> PostNewAsync(string url, (string, string, int) msg, byte[] shot)
    {
        var target = Base(url) + (Thread(url).Length > 0 ? Thread(url) + "&wait=true" : "?wait=true");
        var (status, err, body) = await SendRaw(System.Net.Http.HttpMethod.Post, target, msg, shot, editing: false);
        string newId = null;
        if (err == null && body != null)
            try { newId = (Json.DeserializeObject(body) as Dictionary<string, object>)?["id"] as string; } catch { }
        return (status, err, newId);
    }

    async Task<(HttpStatusCode, string)> PostAsync(HttpMethod method, string target, (string, string, int) msg, byte[] shot, bool editing)
    {
        var (status, err, _) = await SendRaw(method, target, msg, shot, editing);
        return (status, err);
    }

    static async Task<(HttpStatusCode, string, string)> SendRaw(HttpMethod method, string target, (string Title, string Text, int Color) msg, byte[] shot, bool editing)
    {
        var embed = new Dictionary<string, object>
        {
            ["title"] = msg.Title,
            ["description"] = msg.Text.Length > 4000 ? msg.Text.Substring(0, 4000) : msg.Text,
            ["color"] = msg.Color,
            ["timestamp"] = DateTime.UtcNow.ToString("o"),
            ["footer"] = new Dictionary<string, object> { ["text"] = "Roblox AFK Hub · live status · updated" },
        };
        if (shot != null) embed["image"] = new Dictionary<string, object> { ["url"] = "attachment://" + ShotName };
        var payload = new Dictionary<string, object> { ["username"] = "Roblox AFK Hub", ["embeds"] = new[] { embed } };
        if (editing) payload.Remove("username"); // can't be changed on an edit
        // On an edit, list the attachments to keep: the new picture, or none (so an old one doesn't linger).
        if (editing || shot != null) payload["attachments"] = shot != null ? new object[] { new Dictionary<string, object> { ["id"] = 0, ["filename"] = ShotName } } : new object[0];

        HttpContent content;
        var json = new StringContent(Json.Serialize(payload), Encoding.UTF8, "application/json");
        if (shot == null) content = json;
        else
        {
            var form = new MultipartFormDataContent();
            form.Add(json, "payload_json");
            var file = new ByteArrayContent(shot);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            form.Add(file, "files[0]", ShotName);
            content = form;
        }
        using var req = new HttpRequestMessage(method, target) { Content = content };
        using var resp = await Http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (resp.IsSuccessStatusCode) return (resp.StatusCode, null, body);
        return (resp.StatusCode, $"Discord answered HTTP {(int)resp.StatusCode}.", null);
    }

    // ---------- the picture ----------

    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    const uint PW_RENDERFULLCONTENT = 2;

    /// <summary>Every Roblox window (not the rest of the screen), side by side, as a small JPEG. Minimized windows can't be drawn and are skipped.</summary>
    byte[] CaptureWindows()
    {
        try
        {
            var shots = new List<Bitmap>();
            foreach (var w in _hub.Tracker.Windows.Take(4))
            {
                if (IsIconic(w.Hwnd) || !InputSender.ClientRect(w.Hwnd, out var r)) continue;
                int cw = r.Right - r.Left, ch = r.Bottom - r.Top;
                if (!Native.GetWindowRect(w.Hwnd, out var wr)) continue;
                using var full = new Bitmap(wr.Right - wr.Left, wr.Bottom - wr.Top);
                using (var g = Graphics.FromImage(full))
                {
                    var hdc = g.GetHdc();
                    bool okDraw = PrintWindow(w.Hwnd, hdc, PW_RENDERFULLCONTENT);
                    g.ReleaseHdc(hdc);
                    if (!okDraw) continue;
                }
                // Keep just the game picture (no title bar), scaled down.
                var crop = new Rectangle(r.Left - wr.Left, r.Top - wr.Top, cw, ch);
                double scale = Math.Min(1, 640.0 / cw);
                var bmp = new Bitmap(Math.Max(1, (int)(cw * scale)), Math.Max(1, (int)(ch * scale)));
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                    g.DrawImage(full, new Rectangle(0, 0, bmp.Width, bmp.Height), crop, GraphicsUnit.Pixel);
                }
                shots.Add(bmp);
            }
            if (shots.Count == 0) return null;

            int cols = shots.Count == 1 ? 1 : 2, rows = (shots.Count + cols - 1) / cols;
            int cellW = shots.Max(b => b.Width), cellH = shots.Max(b => b.Height);
            using var sheet = new Bitmap(cellW * cols, cellH * rows);
            using (var g = Graphics.FromImage(sheet))
            {
                g.Clear(Color.FromArgb(20, 22, 26));
                for (int i = 0; i < shots.Count; i++) g.DrawImage(shots[i], (i % cols) * cellW, (i / cols) * cellH);
            }
            foreach (var b in shots) b.Dispose();

            using var ms = new MemoryStream();
            var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var ps = new EncoderParameters(1);
            ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 70L);
            sheet.Save(ms, jpeg, ps);
            return ms.ToArray();
        }
        catch { return null; }
    }
}
