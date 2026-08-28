using System.Collections.Concurrent;
using System.Net;
using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Dlna;

/// <summary>
/// Serves a running live channel to DLNA as one large, byte-range-seekable
/// file — a DVR/timeshift shape over an endless stream.
///
/// The problem this solves: a live channel has no end and no fixed size, and
/// DLNA is built for files that have both. An earlier attempt handed the
/// television a chunked live stream with no Content-Length, and at least one
/// set rejected it outright, auto-advancing to the next item within a second.
/// The set plays a finished conversion perfectly well, though — a fixed-size,
/// seekable HTTP response — so this gives a channel that same shape: a large
/// declared size, real byte ranges, and bytes delivered at the pace the
/// channel produces them once a player catches the live edge.
///
/// The bytes are the channel's own MPEG-TS segments, concatenated. TS was
/// built to be joined, so segment N simply follows segment N-1 on the wire.
/// To keep byte offsets stable — which is what makes seeking work and stops a
/// sliding window from pulling the floor out mid-play — each finalised
/// segment is copied once into a per-channel buffer directory the channel's
/// own ffmpeg never touches, and served from there. The buffer is swept when
/// the last viewer leaves.
/// </summary>
public sealed class DlnaLive : IDisposable
{
    // A size large enough that a television never plays to the end of it in a
    // sitting (~18h at 4 Mbit/s), so the file always looks like it has more to
    // come — which is the whole trick: the set keeps asking for the next bytes
    // instead of deciding the file has ended and advancing to the next item.
    // Public so the DLNA listing advertises the very same size it is served.
    public const long AdvertisedBytes = 32L * 1024 * 1024 * 1024;

    // Keep this much already-played history readable behind the slowest active
    // viewer, so a short rewind works and a brief pause never lands on a gap.
    // Past it, played segments are deleted to keep the buffer bounded.
    private const long RewindBytes = 512L * 1024 * 1024;

    // No new segment for this long, with a viewer already at the live edge,
    // means the channel is gone rather than slow — end the response so the set
    // is not held on a stream that will never continue.
    private static readonly TimeSpan DeadAfter = TimeSpan.FromSeconds(20);

    // How long a channel's buffer lingers with no viewers before it is swept.
    private static readonly TimeSpan IdleGrace = TimeSpan.FromSeconds(30);

    // How often the janitor looks for buffers past that grace. It is the
    // resolution of IdleGrace rather than a policy of its own, so a buffer can
    // outlive the grace by up to one sweep; ten seconds keeps the overshoot to
    // a third of it, which for disk a viewer has already stopped using is close
    // enough, and the sweep is a walk of at most a few buffers.
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(10);

    private readonly string _bufferRoot;
    private readonly Dictionary<string, Buffer> _buffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly System.Threading.Timer _janitor;
    private bool _disposed;

    public DlnaLive(string mediaRoot)
    {
        _bufferRoot = Path.Combine(mediaRoot, ".dlnalive");
        // A buffer left behind by a previous run is stale by definition — its
        // offsets belong to a stream that has moved on. Clear the lot on start.
        try
        {
            if (Directory.Exists(_bufferRoot)) Directory.Delete(_bufferRoot, recursive: true);
        }
        catch { /* best effort; a locked leftover is swept next time */ }
        try { Directory.CreateDirectory(_bufferRoot); } catch { }

        _janitor = new System.Threading.Timer(_ => Sweep(), null,
            SweepInterval, SweepInterval);
    }

    /// <summary>
    /// Streams a running live channel. <paramref name="channelDir"/> is the
    /// channel's own HLS directory (the one its ffmpeg writes segments into);
    /// <paramref name="stream"/> is its "ch-…" name, used to key the shared
    /// buffer so two televisions on the same channel share one copy.
    /// </summary>
    public void Serve(HttpListenerContext ctx, string stream, string channelDir, Action<long>? onBytes = null)
    {
        Buffer buf;
        lock (_lock)
        {
            if (_disposed) { ctx.Response.StatusCode = 503; ctx.Response.Close(); return; }
            if (!_buffers.TryGetValue(stream, out buf!))
            {
                buf = new Buffer(stream, channelDir, Path.Combine(_bufferRoot, stream));
                _buffers[stream] = buf;
                buf.Start();
            }
        }
        buf.Serve(ctx, onBytes);
    }

    /// <summary>The real bytes recorded for a channel so far, or 0 if none.</summary>
    public long CurrentSizeFor(string stream)
    {
        lock (_lock) return _buffers.TryGetValue(stream, out var b) ? b.CurrentSize : 0;
    }

    private void Sweep()
    {
        List<Buffer> dead = new();
        lock (_lock)
        {
            foreach (var (stream, buf) in _buffers.ToList())
                if (buf.IdleFor > IdleGrace)
                {
                    _buffers.Remove(stream);
                    dead.Add(buf);
                }
        }
        foreach (var b in dead)
        {
            Log.Debug("dlna", $"live buffer for {b.Stream} swept — no viewers");
            b.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        try { _janitor.Dispose(); } catch { }
        List<Buffer> all;
        lock (_lock) { all = _buffers.Values.ToList(); _buffers.Clear(); }
        foreach (var b in all) b.Dispose();
        try { if (Directory.Exists(_bufferRoot)) Directory.Delete(_bufferRoot, true); } catch { }
    }

    // ---- one channel recorded to a single real, growing MPEG-TS file ------

    private sealed class Buffer : IDisposable
    {
        public string Stream { get; }
        private readonly string _channelDir;
        private readonly string _bufDir;
        private readonly string _filePath;               // the real growing recording

        private readonly object _gate = new();           // guards _size/_lastSrc/_dead
        private long _size;                               // real bytes recorded so far
        private int _lastSrc = int.MinValue;
        private bool _seeded;
        private volatile bool _channelDead;
        private DateTime _lastAppend = DateTime.UtcNow;
        private FileStream? _writer;

        private volatile int _refs;
        private DateTime _idleSince = DateTime.UtcNow;

        private CancellationTokenSource? _cts;
        private Task? _pump;

        public Buffer(string stream, string channelDir, string bufDir)
        {
            Stream = stream;
            _channelDir = channelDir;
            _bufDir = bufDir;
            _filePath = Path.Combine(bufDir, "dlna.ts");
        }

        public TimeSpan IdleFor => _refs > 0 ? TimeSpan.Zero : DateTime.UtcNow - _idleSince;

        public void Start()
        {
            try { Directory.CreateDirectory(_bufDir); } catch { }
            try { if (File.Exists(_filePath)) File.Delete(_filePath); } catch { }
            _cts = new CancellationTokenSource();
            _pump = Task.Run(() => Pump(_cts.Token));
        }

        // ---- the recorder: source segments → one real .ts file ------------

        private void Pump(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { AppendFinalised(); }
                catch (Exception ex) { Log.Debug("dlna", $"live recorder {Stream}: {ex.Message}"); }

                lock (_gate)
                {
                    _channelDead = DateTime.UtcNow - _lastAppend > DeadAfter;
                    System.Threading.Monitor.PulseAll(_gate);
                }
                try { Task.Delay(250, ct).Wait(ct); } catch { break; }
            }
            try { _writer?.Dispose(); } catch { }
        }

        // Append every newly finalised segment to the one real recording file,
        // in order. MPEG-TS concatenates on the wire, so the result is a single
        // valid, growing stream — a real file with real bytes at every offset,
        // which is the whole point: the set plays it like it plays a film.
        private void AppendFinalised()
        {
            List<(int idx, string path)> onDisk;
            try
            {
                onDisk = new DirectoryInfo(_channelDir).EnumerateFiles("seg_*.ts")
                    .Select(f => (idx: ParseIndex(f.Name), path: f.FullName))
                    .Where(t => t.idx >= 0)
                    .OrderBy(t => t.idx)
                    .ToList();
            }
            catch { return; }
            if (onDisk.Count == 0) return;

            var maxIdx = onDisk[^1].idx;                  // newest may still be half-written
            lock (_gate)
            {
                if (!_seeded) { _lastSrc = onDisk[0].idx - 1; _seeded = true; }
            }

            _writer ??= new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.Read, 1 << 16);

            foreach (var (idx, path) in onDisk)
            {
                if (idx >= maxIdx) break;
                if (idx <= _lastSrc) continue;
                byte[] bytes;
                try { bytes = File.ReadAllBytes(path); }
                catch (FileNotFoundException) { continue; }
                catch (IOException) { continue; }
                if (bytes.Length == 0) continue;

                _writer.Write(bytes, 0, bytes.Length);
                _writer.Flush();
                lock (_gate)
                {
                    _size += bytes.Length;
                    _lastSrc = idx;
                    _lastAppend = DateTime.UtcNow;
                    System.Threading.Monitor.PulseAll(_gate);
                }
            }
        }

        /// <summary>The real bytes recorded so far.</summary>
        public long CurrentSize { get { lock (_gate) return _size; } }

        // ---- the reader: the real recording file → HTTP response ----------

        public void Serve(HttpListenerContext ctx, Action<long>? onBytes)
        {
            System.Threading.Interlocked.Increment(ref _refs);
            var res = ctx.Response;
            long served = 0, from = 0;
            try
            {
                long size;
                lock (_gate)
                {
                    // On a cold tune the file is still empty; wait briefly for
                    // the first real content so the first answer is not a zero.
                    for (var i = 0; i < 40 && _size == 0 && !_channelDead; i++)
                        System.Threading.Monitor.Wait(_gate, 250);
                    size = _size;
                }

                long to = size - 1;
                var partial = false;
                var range = ctx.Request.Headers["Range"];
                if (!string.IsNullOrEmpty(range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                {
                    var span = range["bytes=".Length..].Split(',')[0].Split('-');
                    if (span.Length == 2)
                    {
                        if (long.TryParse(span[0], out var f)) { from = f; if (long.TryParse(span[1], out var t)) to = t; }
                        else if (long.TryParse(span[1], out var t)) from = Math.Max(0, size - t);
                        partial = true;
                    }
                }

                // A read at (or past) the current end is the player asking for
                // the next of a growing file: wait for the recorder to append
                // more rather than answering empty — that is what keeps a live
                // channel playing across successive range requests.
                if (from >= size)
                {
                    lock (_gate)
                    {
                        for (var i = 0; i < 240 && _size <= from && !_channelDead; i++)
                            System.Threading.Monitor.Wait(_gate, 250);
                        size = _size;
                    }
                    if (from >= size)
                    {
                        res.StatusCode = 416;
                        res.Headers["Content-Range"] = $"bytes */{Math.Max(size, 1)}";
                        res.Close();
                        return;
                    }
                    to = size - 1;
                }
                if (from < 0) from = 0;
                if (to > size - 1 || to < from) to = size - 1;

                var count = to - from + 1;
                res.StatusCode = partial ? 206 : 200;
                res.ContentType = "video/mp2t";
                res.ContentLength64 = count;
                res.Headers["Accept-Ranges"] = "bytes";
                res.Headers["transferMode.dlna.org"] = "Streaming";
                res.Headers["contentFeatures.dlna.org"] =
                    "DLNA.ORG_OP=01;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000";
                if (partial) res.Headers["Content-Range"] = $"bytes {from}-{to}/{size}";
                Log.Info("dlnalive", $"{ctx.Request.HttpMethod} {Stream} range='{range ?? "-"}' from={from} to={to} size={size} -> {res.StatusCode}");
                if (ctx.Request.HttpMethod == "HEAD") { res.Close(); return; }

                using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
                fs.Seek(from, SeekOrigin.Begin);
                var buffer = new byte[64 * 1024];
                long remaining = count, pending = 0;
                while (remaining > 0)
                {
                    var read = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read <= 0) break;
                    res.OutputStream.Write(buffer, 0, read);
                    remaining -= read; served += read; pending += read;
                    if (onBytes is not null && pending >= 1024 * 1024) { onBytes(pending); pending = 0; }
                }
                if (onBytes is not null && pending > 0) { try { onBytes(pending); } catch { } }
            }
            catch (HttpListenerException) { /* the set stopped or seeked away */ }
            catch (IOException) { }
            catch (Exception ex) { Log.Debug("dlna", $"serving live {Stream} failed: {ex.Message}"); }
            finally
            {
                if (ctx.Request.HttpMethod != "HEAD")
                    Log.Info("dlnalive", $"END {Stream} from={from} served={served}");
                if (System.Threading.Interlocked.Decrement(ref _refs) == 0) _idleSince = DateTime.UtcNow;
                try { res.Close(); } catch { }
            }
        }

        private static int ParseIndex(string name)
        {
            // seg_00042.ts → 42
            var us = name.LastIndexOf('_');
            var dot = name.IndexOf('.', us + 1);
            if (us < 0 || dot < 0) return -1;
            return int.TryParse(name.AsSpan(us + 1, dot - us - 1), out var n) ? n : -1;
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { }
            try { _pump?.Wait(TimeSpan.FromSeconds(2)); } catch { }
            try { _cts?.Dispose(); } catch { }
            try { if (Directory.Exists(_bufDir)) Directory.Delete(_bufDir, true); } catch { }
        }
    }
}
