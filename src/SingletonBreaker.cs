using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using static AfkHub.Native;

namespace AfkHub;

enum ScanResult { NotFound, Closed, AccessDenied }

/// <summary>
/// Multi-instance: finds the ROBLOX_singletonEvent handle
/// inside each Roblox process and closes it, so the next Roblox that starts doesn't take over
/// the existing one.
/// </summary>
sealed class SingletonBreaker : IDisposable
{
    const string EventName = "ROBLOX_singletonEvent";

    // SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX layout on x64.
    const int HeaderSize = 16, EntrySize = 40;
    const int OffPid = 8, OffHandle = 16, OffType = 30;

    IntPtr _buf = IntPtr.Zero;
    int _bufLen;
    int _eventType = -1;
    readonly IntPtr _nameBuf = Marshal.AllocHGlobal(2048);

    public Dictionary<int, ScanResult> Scan(ICollection<int> pids)
    {
        var results = new Dictionary<int, ScanResult>();
        foreach (var pid in pids) results[pid] = ScanResult.NotFound;
        if (results.Count == 0) return results;

        if (_eventType < 0) _eventType = FindEventTypeIndex();
        long count = Snapshot();

        var procHandles = new Dictionary<int, IntPtr>();
        try
        {
            for (long i = 0; i < count; i++)
            {
                IntPtr entry = _buf + HeaderSize + (int)(i * EntrySize);
                int pid = (int)Marshal.ReadInt64(entry, OffPid);
                if (!results.ContainsKey(pid)) continue;
                if ((ushort)Marshal.ReadInt16(entry, OffType) != _eventType) continue;

                if (!procHandles.TryGetValue(pid, out var proc))
                {
                    proc = OpenProcess(PROCESS_DUP_HANDLE, false, pid);
                    procHandles[pid] = proc;
                    if (proc == IntPtr.Zero) results[pid] = ScanResult.AccessDenied;
                }
                if (proc == IntPtr.Zero) continue;

                IntPtr handle = Marshal.ReadIntPtr(entry, OffHandle);
                if (!DuplicateHandle(proc, handle, GetCurrentProcess(), out var dup, 0, false, DUPLICATE_SAME_ACCESS)) continue;
                string name = ObjectName(dup);
                CloseHandle(dup);
                if (name == null || !name.EndsWith("\\" + EventName, StringComparison.Ordinal)) continue;

                if (DuplicateHandle(proc, handle, IntPtr.Zero, out _, 0, false, DUPLICATE_CLOSE_SOURCE))
                    results[pid] = ScanResult.Closed;
                else if (results[pid] != ScanResult.Closed)
                    results[pid] = ScanResult.AccessDenied;
            }
        }
        finally
        {
            foreach (var h in procHandles.Values)
                if (h != IntPtr.Zero) CloseHandle(h);
        }
        return results;
    }

    /// <summary>Object type indexes differ between Windows builds, so learn the Event index from an event we own.</summary>
    int FindEventTypeIndex()
    {
        IntPtr ev = CreateEvent(IntPtr.Zero, false, false, null);
        try
        {
            long count = Snapshot();
            int self = Process.GetCurrentProcess().Id;
            for (long i = 0; i < count; i++)
            {
                IntPtr entry = _buf + HeaderSize + (int)(i * EntrySize);
                if ((int)Marshal.ReadInt64(entry, OffPid) == self && Marshal.ReadIntPtr(entry, OffHandle) == ev)
                    return (ushort)Marshal.ReadInt16(entry, OffType);
            }
            throw new InvalidOperationException("Couldn't identify the Event object type.");
        }
        finally { CloseHandle(ev); }
    }

    long Snapshot()
    {
        if (_buf == IntPtr.Zero) { _bufLen = 8 << 20; _buf = Marshal.AllocHGlobal(_bufLen); }
        while (true)
        {
            int status = NtQuerySystemInformation(SystemExtendedHandleInformation, _buf, _bufLen, out int needed);
            if (status == STATUS_INFO_LENGTH_MISMATCH)
            {
                Marshal.FreeHGlobal(_buf);
                _bufLen = Math.Max(_bufLen * 2, needed + (1 << 20));
                _buf = Marshal.AllocHGlobal(_bufLen);
                continue;
            }
            if (status != 0) throw new InvalidOperationException($"Handle query failed (0x{status:X8}).");
            return Marshal.ReadInt64(_buf);
        }
    }

    string ObjectName(IntPtr handle)
    {
        // Safe for Event handles; NtQueryObject only hangs on certain file handles, which are filtered out by type.
        if (NtQueryObject(handle, ObjectNameInformation, _nameBuf, 2048, out _) != 0) return null;
        int lengthBytes = (ushort)Marshal.ReadInt16(_nameBuf);
        IntPtr str = Marshal.ReadIntPtr(_nameBuf, 8);
        return lengthBytes == 0 || str == IntPtr.Zero ? null : Marshal.PtrToStringUni(str, lengthBytes / 2);
    }

    public void Dispose()
    {
        if (_buf != IntPtr.Zero) { Marshal.FreeHGlobal(_buf); _buf = IntPtr.Zero; }
        Marshal.FreeHGlobal(_nameBuf);
    }
}
