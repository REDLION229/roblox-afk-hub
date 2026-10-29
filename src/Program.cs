using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Forms;

namespace AfkHub;

static class Program
{
    public const string ShowSignalName = "AfkHub.ShowWindow.v1";
    public const string Version = "2.0";

    [STAThread]
    static void Main(string[] args)
    {
        // Must be hooked up before any WebView2 type is touched (they live inside this exe).
        AppDomain.CurrentDomain.AssemblyResolve += Embedded.ResolveAssembly;
        Run(args);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Run(string[] args)
    {
        using var single = new Mutex(true, "AfkHub.SingleInstance.v1", out bool owned);
        if (!owned)
        {
            // When restarting as admin, the old copy is still closing; give it a moment.
            bool restarting = args.Contains("--elevated") || args.Contains("--restart");
            try { owned = restarting && single.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned)
            {
                // Already running (probably in the tray): ask that copy to show its window instead.
                if (EventWaitHandle.TryOpenExisting(ShowSignalName, out var show)) using (show) show.Set();
                else MessageBox.Show("Roblox AFK Hub is already running — check the system tray.", "Roblox AFK Hub", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) =>
            MessageBox.Show("Something went wrong:\n\n" + e.Exception.Message, "Roblox AFK Hub", MessageBoxButtons.OK, MessageBoxIcon.Error);

        try
        {
            Embedded.Unpack();
            Microsoft.Web.WebView2.Core.CoreWebView2Environment.SetLoaderDllFolderPath(Embedded.RuntimeDir);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Roblox AFK Hub couldn't unpack its interface files:\n\n" + ex.Message, "Roblox AFK Hub", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Application.Run(new MainForm());
    }
}
