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
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
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

    // ---- one channel's shared, growing, offset-stable copy ----------------

    private sealed class Segment
    {
        public required string Path;
        public long Start;      // byte offset of this segment in the virtual file
        public long Length;
        public long End => Start + Length;
    }

    private sealed class Buffer : IDisposable
    {
        public string Stream { get; }
        private readonly string _channelDir;
        private readonly string _bufDir;

        private readonly object _gate = new();          // guards _segs/_end/_lastSrc/_dead
        private readonly List<Segment> _segs = new();
        private long _end;                               // virtual size produced so far
        private int _lastSrc = int.MinValue;             // highest source index copied
        private bool _seeded;
        private volatile bool _channelDead;
        private DateTime _lastAppend = DateTime.UtcNow;

        private readonly ConcurrentDictionary<object, long> _cursors = new();
        private volatile int _refs;
        private DateTime _idleSince = DateTime.UtcNow;

        private CancellationTokenSource? _cts;
        private Task? _pump;

        public Buffer(string stream, string channelDir, string bufDir)
        {
            Stream = stream;
            _channelDir = channelDir;
            _bufDir = bufDir;
        }

        public TimeSpan IdleFor => _refs > 0 ? TimeSpan.Zero : DateTime.UtcNow - _idleSince;

        public void Start()
        {
            try { Directory.CreateDirectory(_bufDir); } catch { }
            _cts = new CancellationTokenSource();
            _pump = Task.Run(() => Pump(_cts.Token));
        }

        // ---- the copier: source segments → offset-stable buffer -----------

        private void Pump(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { CopyFinalised(); }
                catch (Exception ex) { Log.Debug("dlna", $"live pump {Stream}: {ex.Message}"); }

                lock (_gate)
                {
                    _channelDead = DateTime.UtcNow - _lastAppend > DeadAfter;
                    System.Threading.Monitor.PulseAll(_gate);   // wake edge-waiters to re-check
                }
                Trim();
                try { Task.Delay(250, ct).Wait(ct); } catch { break; }
            }
        }

        private void CopyFinalised()
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

            // A segment is safe to copy only once a higher-numbered one exists:
            // the newest file on disk may still be open in ffmpeg, half-written.
            var maxIdx = onDisk[^1].idx;

            lock (_gate)
            {
                if (!_seeded)
                {
                    // Byte 0 is the oldest segment still on disk, so a player
                    // starts with the whole live window in hand and then rides
                    // the edge. Anything already deleted is simply not offered.
                    _lastSrc = onDisk[0].idx - 1;
                    _seeded = true;
                }
            }

            foreach (var (idx, path) in onDisk)
            {
                if (idx >= maxIdx) break;                 // newest: not finalised yet
                if (idx <= _lastSrc) continue;            // already have it

                var dst = Path.Combine(_bufDir, $"seg_{idx:D5}.ts");
                long len;
                try
                {
                    if (!File.Exists(dst)) File.Copy(path, dst);
                    len = new FileInfo(dst).Length;
                }
                catch (FileNotFoundException) { continue; }  // deleted from under us; skip
                catch (IOException) { continue; }
                if (len <= 0) continue;

                lock (_gate)
                {
                    _segs.Add(new Segment { Path = dst, Start = _end, Length = len });
                    _end += len;
                    _lastSrc = idx;
                    _lastAppend = DateTime.UtcNow;
                    System.Threading.Monitor.PulseAll(_gate);
                }
            }
        }

        private void Trim()
        {
            long floor;
            var minCursor = _cursors.IsEmpty ? _end : _cursors.Values.Min();
            floor = minCursor - RewindBytes;
            if (floor <= 0) return;

            List<Segment> drop = new();
            lock (_gate)
            {
                foreach (var s in _segs)
                {
                    if (s.End > floor) break;             // keep from here on (list is ordered)
                    drop.Add(s);
                }
                // Keep the entries (offsets must stay valid); only the files go.
            }
            foreach (var s in drop)
                try { File.Delete(s.Path); } catch { }
        }

        // ---- the reader: virtual file → HTTP response ---------------------

        public void Serve(HttpListenerContext ctx, Action<long>? onBytes)
        {
            System.Threading.Interlocked.Increment(ref _refs);
            var key = new object();
            var res = ctx.Response;
            try
            {
                long from = 0, to = AdvertisedBytes - 1;
                var partial = false;

                var range = ctx.Request.Headers["Range"];
                if (!string.IsNullOrEmpty(range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                {
                    var span = range["bytes=".Length..].Split(',')[0].Split('-');
                    if (span.Length == 2)
                    {
                        var hasFrom = long.TryParse(span[0], out var f);
                        var hasTo = long.TryParse(span[1], out var t);
                        if (hasFrom) { from = f; if (hasTo) to = t; }
                        else if (hasTo) { from = Math.Max(0, AdvertisedBytes - t); }
                        partial = hasFrom || hasTo;
                    }
                    // A seek to somewhere past what the channel has produced —
                    // most often a set probing near the end for an index that a
                    // raw TS stream does not have — cannot be answered honestly.
                    // Reject it rather than block the connection forever.
                    long produced;
                    lock (_gate) produced = _end;
                    if (from < 0 || from > produced + 16L * 1024 * 1024 || to < from)
                    {
                        res.StatusCode = 416;
                        res.Headers["Content-Range"] = $"bytes */{AdvertisedBytes}";
                        res.Close();
                        return;
                    }
                    if (to > AdvertisedBytes - 1) to = AdvertisedBytes - 1;
                }

                res.StatusCode = partial ? 206 : 200;
                res.ContentType = "video/mp2t";
                res.ContentLength64 = to - from + 1;
                // OP=01 (seekable). This set will NOT start playback without
                // it — OP=00 gave three bouncing dots for ever, the same as the
                // chunked stream it rejected. So it must be advertised seekable,
                // and the seeking that invites is dealt with below by mapping a
                // range request onto the live buffer rather than fighting the
                // profile the set demands.
                res.Headers["Accept-Ranges"] = "bytes";
                res.Headers["transferMode.dlna.org"] = "Streaming";
                res.Headers["contentFeatures.dlna.org"] =
                    "DLNA.ORG_OP=01;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000";
                if (partial) res.Headers["Content-Range"] = $"bytes {from}-{to}/{AdvertisedBytes}";
                if (ctx.Request.HttpMethod == "HEAD") { res.Close(); return; }

                var buffer = new byte[64 * 1024];
                long pending = 0;
                var cursor = from;
                _cursors[key] = cursor;

                while (cursor <= to)
                {
                    Segment? seg;
                    long produced;
                    lock (_gate)
                    {
                        produced = _end;
                        seg = FindLocked(cursor);
                        if (seg is null && cursor >= produced)
                        {
                            // At the live edge. Either the channel is gone —
                            // stop — or the next segment is still on its way;
                            // wait to be woken when the pump appends one.
                            if (_channelDead) break;
                            System.Threading.Monitor.Wait(_gate, 1000);
                            continue;
                        }
                    }
                    if (seg is null) continue;             // raced past a trim; re-resolve

                    var offInSeg = cursor - seg.Start;
                    var wantHere = Math.Min(seg.Length - offInSeg, to - cursor + 1);
                    FileStream fs;
                    try { fs = new FileStream(seg.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024); }
                    catch { break; }                       // trimmed away beneath a deep seek
                    try
                    {
                        if (offInSeg > 0) fs.Seek(offInSeg, SeekOrigin.Begin);
                        while (wantHere > 0)
                        {
                            var read = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, wantHere));
                            if (read <= 0) break;
                            res.OutputStream.Write(buffer, 0, read);
                            cursor += read;
                            wantHere -= read;
                            pending += read;
                            _cursors[key] = cursor;
                            if (onBytes is not null && pending >= 1024 * 1024) { onBytes(pending); pending = 0; }
                        }
                    }
                    finally { fs.Dispose(); }
                }

                if (onBytes is not null && pending > 0) { try { onBytes(pending); } catch { } }
            }
            catch (HttpListenerException) { /* the set stopped or seeked away */ }
            catch (IOException) { }
            catch (Exception ex) { Log.Debug("dlna", $"serving live {Stream} failed: {ex.Message}"); }
            finally
            {
                _cursors.TryRemove(key, out _);
                if (System.Threading.Interlocked.Decrement(ref _refs) == 0) _idleSince = DateTime.UtcNow;
                try { res.Close(); } catch { }
            }
        }

        private Segment? FindLocked(long offset)
        {
            // ordered, contiguous — a small linear scan from the back is fine
            for (var i = _segs.Count - 1; i >= 0; i--)
            {
                var s = _segs[i];
                if (offset >= s.Start && offset < s.End) return s;
                if (offset >= s.End) return null;          // at or past the live edge
            }
            return _segs.Count > 0 && offset < _segs[0].Start ? _segs[0] : null;
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
