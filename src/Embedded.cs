using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace AfkHub;

/// <summary>
/// Makes AfkHub.exe a single portable file: the WebView2 libraries and the HTML interface are embedded
/// as resources. Managed DLLs load straight from memory; the native WebView2 loader and the ui files are
/// unpacked once per build into %LOCALAPPDATA%\AfkHub\runtime\&lt;build&gt;.
/// </summary>
static class Embedded
{
    static readonly Assembly Self = typeof(Embedded).Assembly;

    public static string RuntimeDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AfkHub", "runtime",
        Self.ManifestModule.ModuleVersionId.ToString("N").Substring(0, 12));

    public static string UiDir => Path.Combine(RuntimeDir, "ui");

    public static Assembly ResolveAssembly(object sender, ResolveEventArgs e)
    {
        var file = new AssemblyName(e.Name).Name + ".dll";
        using var stream = Self.GetManifestResourceStream("lib/" + file);
        if (stream == null) return null;
        var bytes = new byte[stream.Length];
        int read = 0;
        while (read < bytes.Length) read += stream.Read(bytes, read, bytes.Length - read);
        return Assembly.Load(bytes);
    }

    /// <summary>Unpacks the native loader and the interface if this build hasn't been unpacked yet.</summary>
    public static void Unpack()
    {
        var marker = Path.Combine(RuntimeDir, ".complete");
        if (File.Exists(marker)) return;
        Directory.CreateDirectory(RuntimeDir);
        foreach (var name in Self.GetManifestResourceNames())
        {
            string target = name.StartsWith("ui/") ? Path.Combine(RuntimeDir, "ui", name.Substring(3).Replace('/', '\\'))
                : name.StartsWith("native/") ? Path.Combine(RuntimeDir, name.Substring(7))
                : null;
            if (target == null) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            using var src = Self.GetManifestResourceStream(name);
            using var dst = File.Create(target);
            src.CopyTo(dst);
        }
        File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));

        // Tidy up older builds' copies.
        foreach (var old in Directory.GetDirectories(Path.GetDirectoryName(RuntimeDir)).Where(d => !d.Equals(RuntimeDir, StringComparison.OrdinalIgnoreCase)))
            try { Directory.Delete(old, true); } catch { /* still in use by another copy; fine */ }
    }
}
