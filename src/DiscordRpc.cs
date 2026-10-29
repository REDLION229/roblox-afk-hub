using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace AfkHub;

/// <summary>
/// Minimal Discord Rich Presence client: talks to the Discord desktop app over its local IPC pipe
/// (\\.\pipe\discord-ipc-N). Frames are [int32 opcode][int32 length][UTF-8 JSON]. Needs a Discord
/// Application ID (created once at discord.com/developers) — its name is what shows as "Playing …".
/// All work happens on a background task, so a slow or missing Discord never stalls the app.
/// </summary>
sealed class DiscordRpc : IDisposable
{
    const int OpHandshake = 0, OpFrame = 1;
    static readonly JavaScriptSerializer Json = new();

    NamedPipeClientStream _pipe;
    string _appId, _lastSent;
    DateTime _nextConnect;
    int _busy;

    public bool Connected => _pipe?.IsConnected == true;
    public string Status { get; private set; } = "Off";

    /// <summary>Sets (or clears, with null) the activity. Cheap to call often: only sends when something changed.</summary>
    public void Update(string appId, object activity)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        Task.Run(() =>
        {
            try { UpdateCore(appId, activity); }
            catch (Exception ex) { Close($"Disconnected: {ex.Message}"); _nextConnect = DateTime.UtcNow.AddSeconds(20); }
            finally { Interlocked.Exchange(ref _busy, 0); }
        });
    }

    void UpdateCore(string appId, object activity)
    {
        if (string.IsNullOrWhiteSpace(appId)) { Close("Off"); return; }
        if (appId != _appId) { Close("Connecting…"); _appId = appId; _nextConnect = DateTime.MinValue; }

        string json = activity == null ? "null" : Json.Serialize(activity);
        if (Connected && json == _lastSent) return;

        if (!Connected)
        {
            if (DateTime.UtcNow < _nextConnect) return;
            if (!Connect(appId)) { _nextConnect = DateTime.UtcNow.AddSeconds(30); return; }
        }

        Send(OpFrame, new
        {
            cmd = "SET_ACTIVITY",
            args = new { pid = Process.GetCurrentProcess().Id, activity },
            nonce = Guid.NewGuid().ToString(),
        });
        var reply = Read();
        if (reply != null && reply.Contains("\"evt\":\"ERROR\""))
        {
            Status = "Discord rejected the status: " + Extract(reply, "message");
            _lastSent = json; // don't hammer it with the same bad payload
            return;
        }
        _lastSent = json;
        Status = activity == null ? "Connected (nothing to show right now)" : "Showing your AFK status";
    }

    bool Connect(string appId)
    {
        for (int i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { pipe.Connect(150); }
            catch { pipe.Dispose(); continue; }
            _pipe = pipe;
            _lastSent = null;
            Send(OpHandshake, new { v = 1, client_id = appId });
            var ready = Read();
            if (ready == null || !ready.Contains("READY"))
            {
                Close(ready != null && ready.Contains("Invalid Client ID") ? "That Application ID isn't valid." : "Discord didn't accept the connection.");
                return false;
            }
            Status = "Connected";
            return true;
        }
        Status = "Discord isn't running (or isn't the desktop app).";
        return false;
    }

    void Send(int op, object payload)
    {
        var body = Encoding.UTF8.GetBytes(Json.Serialize(payload));
        var frame = new byte[8 + body.Length];
        BitConverter.GetBytes(op).CopyTo(frame, 0);
        BitConverter.GetBytes(body.Length).CopyTo(frame, 4);
        body.CopyTo(frame, 8);
        _pipe.Write(frame, 0, frame.Length);
        _pipe.Flush();
    }

    /// <summary>Reads one reply frame, giving up after 3 seconds.</summary>
    string Read()
    {
        var header = ReadExact(8);
        if (header == null) return null;
        int len = BitConverter.ToInt32(header, 4);
        if (len <= 0 || len > 1 << 20) return null;
        var body = ReadExact(len);
        return body == null ? null : Encoding.UTF8.GetString(body);
    }

    byte[] ReadExact(int count)
    {
        var buf = new byte[count];
        int got = 0;
        while (got < count)
        {
            var t = _pipe.ReadAsync(buf, got, count - got);
            if (!t.Wait(3000) || t.Result <= 0) return null;
            got += t.Result;
        }
        return buf;
    }

    static string Extract(string json, string key)
    {
        int i = json.IndexOf("\"" + key + "\":\"", StringComparison.Ordinal);
        if (i < 0) return "unknown error";
        i += key.Length + 4;
        int j = json.IndexOf('"', i);
        return j > i ? json.Substring(i, j - i) : "unknown error";
    }

    void Close(string status)
    {
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
        _lastSent = null;
        Status = status;
    }

    public void Dispose()
    {
        // Clearing the activity happens automatically when the pipe closes.
        Close("Off");
    }
}
