using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NvimWinFormsGui.NvimRpc;

internal sealed class NvimRpcClient : IDisposable
{
    private readonly Process _proc;
    private readonly Stream _stdin;
    private readonly Stream _stdout;
    private readonly Stream _stderr;
    private readonly MsgPackStreamReader _reader;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pumpTask;
    private readonly Task _stderrTask;

    private int _msgId;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<object?>> _pending = new();

    public event Action<IReadOnlyList<object?>>? Redraw;   // params[0] = list of events
    public event Action<int>? Exited;
    public event Action<string>? Stderr;
    public event Action<Exception>? Faulted;

    public NvimRpcClient(string nvimExe = "nvim", string? extraArgs = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ExeResolver.ResolveOnPath(nvimExe),
            Arguments = string.IsNullOrWhiteSpace(extraArgs) ? "--embed" : $"--embed {extraArgs}",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.Environment["TERM"] = "xterm-256color";
        psi.Environment["COLORTERM"] = "truecolor";

        _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _proc.Exited += (_, __) => Exited?.Invoke(_proc.ExitCode);

        if (!_proc.Start())
            throw new InvalidOperationException("Failed to start nvim.");

        _stdin = _proc.StandardInput.BaseStream;
        _stdout = _proc.StandardOutput.BaseStream;
        _stderr = _proc.StandardError.BaseStream;
        _reader = new MsgPackStreamReader(_stdout);

        _pumpTask = Task.Run(() => PumpAsync(_cts.Token));
        _stderrTask = Task.Run(() => DrainStderrAsync(_cts.Token));
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _stdin.Dispose(); } catch { }
        try { _stdout.Dispose(); } catch { }
        try { _stderr.Dispose(); } catch { }
        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { }
        try { _proc.Dispose(); } catch { }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var obj = await _reader.ReadNextAsync(ct).ConfigureAwait(false);
                HandleIncoming(obj);
            }
        }
        catch (Exception ex)
        {
            try { Faulted?.Invoke(ex); } catch { }
        }
    }

    private async Task DrainStderrAsync(CancellationToken ct)
    {
        var buf = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await _stderr.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false);
                if (n <= 0) break;
                var s = Encoding.UTF8.GetString(buf, 0, n);
                try { Stderr?.Invoke(s); } catch { }
            }
        }
        catch (Exception ex)
        {
            try { Faulted?.Invoke(ex); } catch { }
        }
    }

    private void HandleIncoming(object? obj)
    {
        if (obj is not List<object?> arr || arr.Count == 0) return;

        // msgpack-rpc: [type, ...]
        if (!TryToInt(arr[0], out int type)) return;

        if (type == 1)
        {
            // response: [1, msgid, error, result]
            if (arr.Count < 4 || !TryToInt(arr[1], out int id)) return;
            if (_pending.TryRemove(id, out var tcs))
            {
                var err = arr[2];
                if (err is null)
                    tcs.TrySetResult(arr[3]);
                else
                    tcs.TrySetException(new Exception("nvim error: " + JsonSerializer.Serialize(ToJsonable(err))));
            }
        }
        else if (type == 2)
        {
            // notification: [2, method, params]
            if (arr.Count < 3) return;
            var method = DecodeString(arr[1]);
            if (method == "redraw")
            {
                if (arr[2] is List<object?> p)
                    Redraw?.Invoke(p);
            }
        }
        // requests from nvim (type=0) are rare; ignore.
    }

    public Task<object?> CallAsync(string method, params object?[] args)
    {
        int id = Interlocked.Increment(ref _msgId);
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var msg = new object?[] { 0L, (long)id, method, args };
        var bin = MsgPack.Pack(msg);

        lock (_stdin)
        {
            _stdin.Write(bin, 0, bin.Length);
            _stdin.Flush();
        }
        return tcs.Task;
    }

    public Task NotifyAsync(string method, params object?[] args)
    {
        var msg = new object?[] { 2L, method, args };
        var bin = MsgPack.Pack(msg);

        lock (_stdin)
        {
            _stdin.Write(bin, 0, bin.Length);
            _stdin.Flush();
        }
        return Task.CompletedTask;
    }

    private static string? DecodeString(object? v)
{
    if (v is null) return null;
    if (v is string s) return s;
    if (v is byte[] b)
    {
        try { return Encoding.UTF8.GetString(b); }
        catch { return Convert.ToBase64String(b); }
    }
    return v.ToString();
}

public static object? ToJsonable(object? v)
{
    if (v is null) return null;

    if (v is string or bool) return v;

    // Neovim often encodes "String" as msgpack bin => byte[]
    if (v is byte[] bytes)
    {
        try { return Encoding.UTF8.GetString(bytes); }
        catch { return Convert.ToBase64String(bytes); }
    }

    if (v is MsgPack.Ext ext) return new Dictionary<string, object?>
    {
        ["$extType"] = ext.Type,
        ["$extData"] = Convert.ToBase64String(ext.Data)
    };

    if (TryToLong(v, out long l)) return l;

    if (v is List<object?> list)
    {
        var outList = new List<object?>(list.Count);
        foreach (var x in list) outList.Add(ToJsonable(x));
        return outList;
    }

    if (v is Dictionary<object, object?> map)
    {
        var outMap = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var kv in map)
        {
            var k = DecodeString(kv.Key) ?? "";
            outMap[k] = ToJsonable(kv.Value);
        }
        return outMap;
    }

    return v.ToString();
}

private static bool TryToInt(object? v, out int i)
    {
        i = 0;
        if (v is long l) { i = (int)l; return true; }
        if (v is int ii) { i = ii; return true; }
        if (v is byte b) { i = b; return true; }
        return false;
    }

    private static bool TryToLong(object? v, out long l)
    {
        l = 0;
        if (v is long ll) { l = ll; return true; }
        if (v is int ii) { l = ii; return true; }
        if (v is byte bb) { l = bb; return true; }
        if (v is sbyte sb) { l = sb; return true; }
        if (v is short s) { l = s; return true; }
        if (v is ushort us) { l = us; return true; }
        if (v is uint ui) { l = ui; return true; }
        return false;
    }
}
