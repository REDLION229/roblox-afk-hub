using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AfkHub;

/// <summary>
/// Roblox's real login page in a throwaway browser profile. Once the .ROBLOSECURITY cookie appears
/// the window closes and the profile is deleted. That session is separate from your normal browser,
/// so logging out in Chrome doesn't break it.
/// </summary>
sealed class LoginForm : Form
{
    WebView2 _web;
    readonly CoreWebView2Environment _shared;
    readonly string _profileDir = Path.Combine(Path.GetTempPath(), "AfkHub-login-" + Guid.NewGuid().ToString("N"));
    readonly Timer _poll = new() { Interval = 1000 };
    bool _done;

    public string Cookie { get; private set; }
    /// <summary>True once Roblox's login page has started loading (used by the --test-login self-check).</summary>
    public bool Started { get; private set; }
    public string Error { get; private set; }

    /// <param name="shared">The main window's WebView2 environment. Reusing it avoids Windows refusing a second,
    /// differently-configured browser instance ("Operation failed").</param>
    public LoginForm(CoreWebView2Environment shared = null)
    {
        _shared = shared;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Log in to Roblox — Roblox AFK Hub";
        Icon = AppIcon.Get();
        Size = new Size(560, 780);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Bg;

        var hint = new Label
        {
            Dock = DockStyle.Top, Height = 34, BackColor = Theme.Side, ForeColor = Theme.Muted, Font = Theme.Small,
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "Log in normally. This window closes by itself once you're in.",
        };
        _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Theme.Bg };
        Controls.Add(_web);
        Controls.Add(hint);

        _poll.Tick += async (_, _) => await CheckCookie();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            try { await StartBrowser(_shared); }
            catch (Exception first) when (_shared != null)
            {
                // The app's own browser engine can end up refusing new windows (E_ABORT) after it has been
                // running a while. Fall back to a separate, throwaway browser in its own temp folder.
                FirstError = $"{first.Message} (0x{first.HResult:X8})";
                Controls.Remove(_web);
                _web.Dispose();
                _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Theme.Bg };
                Controls.Add(_web);
                _web.BringToFront();
                await StartBrowser(null);
            }
            _web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _web.CoreWebView2.NavigationCompleted += async (_, _) => await CheckCookie();
            _web.CoreWebView2.Navigate("https://www.roblox.com/login");
            Started = true;
            _poll.Start();
        }
        catch (Exception ex)
        {
            Error = $"{ex.Message} (0x{ex.HResult:X8})";
            if (Environment.GetCommandLineArgs().Contains("--test-login")) return;
            MessageBox.Show(this, "The login window couldn't start: " + Error +"\n\nUse \"Add with cookie…\" instead.",
                "Roblox AFK Hub", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.Cancel;
            Close();
        }
    }

    /// <summary>Set when the shared browser refused and the separate one was used instead (for the activity log).</summary>
    public string FirstError { get; private set; }

    async Task StartBrowser(CoreWebView2Environment shared)
    {
        var env = shared ?? await CoreWebView2Environment.CreateAsync(null, _profileDir);
        // A private (InPrivate) profile: a clean Roblox login that's never written to disk,
        // separate from the app's own browser data.
        var options = env.CreateCoreWebView2ControllerOptions();
        options.ProfileName = "login" + Guid.NewGuid().ToString("N").Substring(0, 8);
        options.IsInPrivateModeEnabled = true;
        await _web.EnsureCoreWebView2Async(env, options);
    }

    async Task CheckCookie()
    {
        if (_done || _web.CoreWebView2 == null) return;
        var cookies = await _web.CoreWebView2.CookieManager.GetCookiesAsync("https://www.roblox.com");
        var c = cookies.FirstOrDefault(x => x.Name == ".ROBLOSECURITY" && !string.IsNullOrEmpty(x.Value));
        if (c == null) return;
        _done = true;
        Cookie = c.Value;
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _poll.Stop();
        _web.Dispose();
        base.OnFormClosed(e);
        var dir = _profileDir;
        Task.Run(async () =>
        {
            // The browser process takes a moment to release the profile folder.
            for (int i = 0; i < 20 && Directory.Exists(dir); i++)
            {
                await Task.Delay(500);
                try { Directory.Delete(dir, true); } catch { }
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _poll.Dispose();
        base.Dispose(disposing);
    }
}
