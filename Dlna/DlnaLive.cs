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

    // How much of a recording is kept: the newest discovery.dlnaLiveMaxGb of
    // it (DefaultMaxBytes when unset). It used to be all of it, for as long as
    // anyone watched - roughly 1-3 GB an hour, so a set left on overnight
    // filled the drive conversions write to.
    //
    // Trimming the front could not simply be switched on, because of how a
    // set plays this: it reads the recording as one file, by byte offset, and
    // a second set tuning in asks for byte 0 - which, trimmed, would be
    // answered 416 and never play. So the recording is kept as a run of files
    // (see Buffer) and the oldest are deleted as it grows, and each set gets a
    // starting offset of its own: a request for bytes the recording no longer
    // holds is served from the oldest it does, and that set's offsets are
    // shifted to match from then on, so its next request carries on where the
    // last one ended. Offsets never move for anyone else.

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

    /// <summary>
    /// How much of one channel's recording is kept, in bytes: read each time a
    /// segment is added, so a changed setting applies to recordings under way.
    /// See discovery.dlnaLiveMaxGb.
    /// </summary>
    private readonly Func<long> _maxBytes;

    /// <summary>The limit when nothing sets one: 4 GB, one to two hours of an ordinary channel.</summary>
    public const long DefaultMaxBytes = 4L * 1024 * 1024 * 1024;

    public DlnaLive(string mediaRoot, Func<long>? maxBytes = null)
    {
        _maxBytes = maxBytes ?? (() => DefaultMaxBytes);
        _bufferRoot = BufferRootFor(mediaRoot);
        // A buffer left behind by a previous run is stale by definition — its
        // offsets belong to a stream that has moved on. Clear the lot on start.
        RemoveLeftovers(mediaRoot);
        try { Directory.CreateDirectory(_bufferRoot); } catch { }

        _janitor = new System.Threading.Timer(_ => Sweep(), null,
            SweepInterval, SweepInterval);
    }

    private static string BufferRootFor(string mediaRoot) => Path.Combine(mediaRoot, ".dlnalive");

    /// <summary>
    /// Deletes whatever recordings an earlier run left under this media root.
    ///
    /// Public because the constructor is not the only place that has to ask.
    /// The constructor runs only when DLNA is on, so a server that was killed
    /// mid-programme and then started with DLNA off left the recording - often
    /// gigabytes - in the media root with nothing that would ever look at it
    /// again. The server calls this at startup when it is not going to build
    /// one of these.
    /// </summary>
    public static void RemoveLeftovers(string mediaRoot)
    {
        var root = BufferRootFor(mediaRoot);
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch { /* best effort; a locked leftover is swept next time */ }
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
                // A folder of its own, not one named after the channel alone.
                // A swept buffer deletes its folder after waiting up to two
                // seconds for its recorder, and a set tuning the same channel
                // in that time starts the next buffer. Sharing the folder, that
                // late delete took the new recording with it - the delete
                // sharing its files allow (see Serve) made it succeed - and the
                // channel went dead to every set until they all gave up.
                buf = new Buffer(stream, channelDir,
                                 Path.Combine(_bufferRoot, stream + "." + Guid.NewGuid().ToString("N")[..8]),
                                 _maxBytes);
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

    /// <summary>
    /// The size a set tuning in to this channel now is served: the part of the
    /// recording still kept (all of it, until the front has been trimmed).
    /// What the DLNA listing advertises - it used to say the whole recorded
    /// size, and once the front was trimmed a set that believed it and seeked
    /// near that end asked past what it would be served. 0 if none.
    /// </summary>
    public long KeptSizeFor(string stream)
    {
        lock (_lock) return _buffers.TryGetValue(stream, out var b) ? b.KeptBytes : 0;
    }

    /// <summary>The folder the channel's current buffer records into, if it has one. For the tests.</summary>
    internal string? BufferFolderOf(string stream)
    {
        lock (_lock) return _buffers.TryGetValue(stream, out var b) ? b.Folder : null;
    }

    private void Sweep() => SweepIdle(IdleGrace);

    /// <summary>Takes down every buffer idle for longer than <paramref name="grace"/>. Internal for the tests.</summary>
    internal void SweepIdle(TimeSpan grace)
    {
        List<Buffer> dead = new();
        lock (_lock)
        {
            foreach (var (stream, buf) in _buffers.ToList())
                if (buf.IdleFor > grace)
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

    // ---- one channel recorded as one growing MPEG-TS stream -------------
    //
    // One stream of bytes with fixed offsets, kept on disk as a run of files -
    // each named after the offset it starts at - so the oldest can be deleted
    // once the whole is past its limit. See the note on _maxBytes.

    private sealed class Buffer : IDisposable
    {
        public string Stream { get; }
        private readonly string _channelDir;
        private readonly string _bufDir;
        private readonly Func<long> _maxBytes;

        /// <summary>One file of the recording: where in it the file starts, and how long it is so far.</summary>
        private sealed class Piece(long start, string path)
        {
            public long Start { get; } = start;
            public string Path { get; } = path;
            public long Length;
        }

        private readonly object _gate = new();           // guards everything below it that changes
        private long _size;                               // real bytes recorded so far, trimmed or not
        private readonly List<Piece> _pieces = new();     // oldest first; the last one is being written
        private long _floor;                              // the oldest byte still kept
        private bool _saidTrimming;
        /// <summary>Each set's own starting offset, by address: see Serve.</summary>
        private readonly Dictionary<string, long> _shifts = new(StringComparer.Ordinal);
        private int _lastSrc = int.MinValue;
        private bool _seeded;
        private volatile bool _channelDead;
        private DateTime _lastAppend = DateTime.UtcNow;
        private FileStream? _writer;

        private volatile int _refs;
        private DateTime _idleSince = DateTime.UtcNow;

        private CancellationTokenSource? _cts;
        private Task? _pump;

        public Buffer(string stream, string channelDir, string bufDir, Func<long> maxBytes)
        {
            Stream = stream;
            _channelDir = channelDir;
            _bufDir = bufDir;
            _maxBytes = maxBytes;
        }

        public TimeSpan IdleFor => _refs > 0 ? TimeSpan.Zero : DateTime.UtcNow - _idleSince;

        public string Folder => _bufDir;

        public void Start()
        {
            try { Directory.CreateDirectory(_bufDir); } catch { }
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
                // The channel was started afresh, and its numbering with it.
                //
                // Within one run the newest segment on disk is always past the
                // last one recorded - only indices below the newest are ever
                // taken, and a restart that continues the playlist continues
                // the numbering too. So the newest being at or below it means
                // the numbers went backwards: tune-on-demand clears the old
                // segments and the playlist before it starts a channel that
                // had died, which leaves append_list nothing to continue from,
                // and the new ffmpeg writes seg_00000 again. The same happens
                // when a channel is stopped from the dashboard and tuned again,
                // or removed and added back.
                //
                // This used to go unnoticed. Everything the new run wrote was
                // numbered at or below the last index recorded and was skipped
                // as already had, so nothing was appended ever again: a set at
                // the live edge was answered 416 on every retry, and because
                // those retries kept the buffer in use it was never swept and
                // rebuilt either. The channel stayed dead to every television
                // until nobody had asked for it for half a minute.
                //
                // What is on disk now belongs to the new run, so it is recorded
                // from its first segment, on the end of the same file. Offsets
                // stay where they were - a set part way through keeps its place
                // and simply reaches the new picture next, across a seam like
                // the one an ordinary crash-and-restart already leaves.
                else if (maxIdx <= _lastSrc)
                {
                    Log.Info("dlnalive", $"{Stream}: the channel started again from seg_{onDisk[0].idx:D5} "
                                         + $"(the recording had reached seg_{_lastSrc:D5}) - recording on from there");
                    _lastSrc = onDisk[0].idx - 1;
                }
            }

            foreach (var (idx, path) in onDisk)
            {
                if (idx >= maxIdx) break;
                if (idx <= _lastSrc) continue;
                byte[] bytes;
                try { bytes = File.ReadAllBytes(path); }
                catch (FileNotFoundException) { continue; }
                catch (IOException) { continue; }
                if (bytes.Length == 0) continue;

                var piece = PieceToWrite();
                _writer!.Write(bytes, 0, bytes.Length);
                _writer.Flush();
                lock (_gate)
                {
                    _size += bytes.Length;
                    piece.Length += bytes.Length;
                    _lastSrc = idx;
                    _lastAppend = DateTime.UtcNow;
                    System.Threading.Monitor.PulseAll(_gate);
                }
                TrimToLimit();
            }
        }

        /// <summary>The limit in bytes, as set now; the default when the setting gives nothing usable.</summary>
        private long Limit()
        {
            try { var limit = _maxBytes(); return limit > 0 ? limit : DefaultMaxBytes; }
            catch { return DefaultMaxBytes; }
        }

        /// <summary>
        /// The piece the next segment goes into: the one being written, or a
        /// new one once that has had its share. A piece is at most 64 MB and at
        /// most an eighth of the limit, so what is kept stays within an eighth
        /// of the limit rather than a whole piece past it.
        /// </summary>
        private Piece PieceToWrite()
        {
            Piece? current;
            lock (_gate) current = _pieces.Count > 0 ? _pieces[^1] : null;
            var share = Math.Clamp(Limit() / 8, 1, 64L * 1024 * 1024);
            if (current is not null && _writer is not null && current.Length < share) return current;

            try { _writer?.Dispose(); } catch { }
            _writer = null;
            long start;
            lock (_gate) start = _size;
            var next = new Piece(start, Path.Combine(_bufDir, $"dlna-{start:D15}.ts"));
            // Delete shared for the same reason as the readers': a recorder that
            // outlives Dispose's two-second wait must not be what keeps the file.
            _writer = new FileStream(next.Path, FileMode.Create, FileAccess.Write,
                                     FileShare.Read | FileShare.Delete, 1 << 16);
            lock (_gate) _pieces.Add(next);
            return next;
        }

        /// <summary>
        /// Deletes the oldest pieces while what is kept is past the limit -
        /// never the one being written. A set part way through a deleted piece
        /// reads on to the end of it (readers share delete).
        /// </summary>
        private void TrimToLimit()
        {
            var limit = Limit();
            List<Piece>? gone = null;
            var first = false;
            long floor;
            lock (_gate)
            {
                while (_pieces.Count > 1 && _size - _pieces[0].Start > limit)
                {
                    (gone ??= new List<Piece>()).Add(_pieces[0]);
                    _pieces.RemoveAt(0);
                }
                _floor = _pieces.Count > 0 ? _pieces[0].Start : _size;
                floor = _floor;
                if (gone is not null && !_saidTrimming) { _saidTrimming = true; first = true; }
            }
            if (gone is null) return;
            foreach (var p in gone)
            {
                try { File.Delete(p.Path); }
                catch { /* swept with the folder when the buffer goes */ }
            }
            if (first)
                Log.Info("dlnalive", $"{Stream}: the recording reached its limit of {limit / (1024.0 * 1024 * 1024):0.##} GB "
                                     + "(discovery.dlnaLiveMaxGb) - from here the oldest part is dropped as it grows");
            Log.Debug("dlnalive", $"{Stream}: dropped {gone.Count} piece(s); the recording now starts at byte {floor}");
        }

        /// <summary>The real bytes recorded so far, trimmed or not.</summary>
        public long CurrentSize { get { lock (_gate) return _size; } }

        /// <summary>The bytes of the recording still on disk.</summary>
        public long KeptBytes { get { lock (_gate) return _size - _floor; } }

        // ---- the reader: the recording's pieces → HTTP response ------------

        public void Serve(HttpListenerContext ctx, Action<long>? onBytes)
        {
            System.Threading.Interlocked.Increment(ref _refs);
            var res = ctx.Response;
            long served = 0, from = 0;
            // Which set this is, for its own starting offset (see _shifts): its
            // address and what it says it is. A television is one address and
            // its requests for a channel come one after another; two players on
            // one address (a set's own app beside its built-in player, two
            // devices behind one router) are kept apart by what they call
            // themselves, so one tuning in cannot move the other's offsets.
            var client = (ctx.Request.RemoteEndPoint?.Address.ToString() ?? "") + "|" + (ctx.Request.UserAgent ?? "");
            var who = ctx.Request.RemoteEndPoint?.Address.ToString() ?? "?";
            try
            {
                long size, shift;
                lock (_gate)
                {
                    // On a cold tune the file is still empty; wait briefly for
                    // the first real content so the first answer is not a zero.
                    for (var i = 0; i < 40 && _size == 0 && !_channelDead; i++)
                        System.Threading.Monitor.Wait(_gate, 250);
                    size = _size;
                    shift = _shifts.GetValueOrDefault(client);
                }

                // Offsets from here on are this set's: its byte r is byte
                // r + shift of the recording, and the file it sees is that much
                // shorter. Zero for every set until the front has been trimmed
                // under it.
                var seen = size - shift;
                long to = seen - 1;
                var partial = false;
                var range = ctx.Request.Headers["Range"];
                if (!string.IsNullOrEmpty(range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                {
                    var span = range["bytes=".Length..].Split(',')[0].Split('-');
                    if (span.Length == 2)
                    {
                        if (long.TryParse(span[0], out var f)) { from = f; if (long.TryParse(span[1], out var t)) to = t; }
                        else if (long.TryParse(span[1], out var t)) from = Math.Max(0, seen - t);
                        partial = true;
                    }
                }

                // A read at (or past) the current end is the player asking for
                // the next of a growing file: wait for the recorder to append
                // more rather than answering empty — that is what keeps a live
                // channel playing across successive range requests.
                if (from >= seen)
                {
                    lock (_gate)
                    {
                        for (var i = 0; i < 240 && _size - shift <= from && !_channelDead; i++)
                            System.Threading.Monitor.Wait(_gate, 250);
                        size = _size;
                    }
                    seen = size - shift;
                    if (from >= seen)
                    {
                        res.StatusCode = 416;
                        res.Headers["Content-Range"] = $"bytes */{Math.Max(seen, 1)}";
                        res.Close();
                        return;
                    }
                    to = seen - 1;
                }
                if (from < 0) from = 0;
                if (to > seen - 1 || to < from) to = seen - 1;

                // Older than the recording keeps: the front has been trimmed
                // (see _maxBytes). Answered from the oldest byte still kept, and
                // this set's offsets moved to match, so that its next request -
                // for where this one ends - carries on from there. Answering 416
                // instead stopped a set tuning in to a channel another had been
                // watching for longer than the limit before it played a frame.
                //
                // Decided and noted under one hold of the lock, so two requests
                // from one set cannot each read the floor and race to write. And
                // not for a HEAD: it asks what is there and takes nothing, and a
                // set probing byte 0 while it plays at the live edge would
                // otherwise move its own offsets out from under its playback -
                // the next request for where it was would name bytes past the
                // end, wait a minute and be refused.
                var head = ctx.Request.HttpMethod == "HEAD";
                long floor;
                var moved = -1L;
                lock (_gate)
                {
                    floor = _floor;
                    if (from + shift < floor)
                    {
                        moved = floor - from;
                        if (!head) _shifts[client] = moved;
                    }
                }
                if (moved >= 0)
                {
                    if (!head)
                        Log.Info("dlnalive", $"{Stream}: {who} asked for byte {from + shift}, which the recording no longer "
                                             + $"keeps (it starts at {floor}) - served from there");
                    shift = moved;
                    seen = size - shift;
                    if (to > seen - 1) to = seen - 1;
                }

                var count = to - from + 1;
                res.StatusCode = partial ? 206 : 200;
                res.ContentType = "video/mp2t";
                res.ContentLength64 = count;
                res.Headers["Accept-Ranges"] = "bytes";
                res.Headers["transferMode.dlna.org"] = "Streaming";
                res.Headers["contentFeatures.dlna.org"] =
                    "DLNA.ORG_OP=01;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000";
                if (partial) res.Headers["Content-Range"] = $"bytes {from}-{to}/{seen}";
                Log.Info("dlnalive", $"{ctx.Request.HttpMethod} {Stream} range='{range ?? "-"}' from={from} to={to} size={seen}"
                                     + (shift != 0 ? $" (+{shift})" : "") + $" -> {res.StatusCode}");
                if (ctx.Request.HttpMethod == "HEAD") { res.Close(); return; }

                // Short only when the part it needed was trimmed away under a
                // set that fell that far behind. The answer promised `count`
                // bytes, and closing it short left the set waiting for the rest
                // until its own timeout - two minutes, measured. Dropping the
                // connection instead has it ask again at once, and that request
                // is moved on to what is kept.
                if (!CopyRange(from + shift, count, res.OutputStream, onBytes, ref served))
                {
                    Log.Info("dlnalive", $"{Stream}: {who} fell behind what the recording keeps - asked to come again");
                    res.Abort();
                    return;
                }
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

        /// <summary>
        /// Writes <paramref name="count"/> bytes of the recording, from byte
        /// <paramref name="at"/>, across as many pieces as that spans, and says
        /// whether it wrote them all. It stops early only if the piece it needs
        /// has been trimmed away under a reader that fell that far behind.
        /// </summary>
        private bool CopyRange(long at, long count, Stream output, Action<long>? onBytes, ref long served)
        {
            var buffer = new byte[64 * 1024];
            long remaining = count, pending = 0;
            while (remaining > 0)
            {
                Piece? piece;
                lock (_gate) piece = _pieces.LastOrDefault(p => p.Start <= at);
                if (piece is null) break;

                // Delete is shared so that taking the buffer down - the last
                // viewer gone, or the server stopping - or trimming its front
                // removes the recording even while a set is part way through a
                // response. Without it the delete was refused for as long as
                // this handle was open, which on a server stopped mid-programme
                // is exactly when it is open, and a recording of several
                // gigabytes stayed in the media root. The name goes at once;
                // this read carries on from the handle until it is done.
                FileStream fs;
                try
                {
                    fs = new FileStream(piece.Path, FileMode.Open, FileAccess.Read,
                                        FileShare.ReadWrite | FileShare.Delete, 1 << 16);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Trimmed between the lookup above and this open - deleted,
                    // or on its way out. The same as not finding it: the part
                    // this set needed is gone, and the caller drops the
                    // connection rather than leaving the answer short.
                    break;
                }
                using var open = fs;
                fs.Seek(at - piece.Start, SeekOrigin.Begin);
                var before = remaining;
                while (remaining > 0)
                {
                    var read = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read <= 0) break;                 // the end of this piece; the next follows on
                    output.Write(buffer, 0, read);
                    remaining -= read; served += read; pending += read; at += read;
                    if (onBytes is not null && pending >= 1024 * 1024) { onBytes(pending); pending = 0; }
                }
                if (remaining == before) break;           // nothing more here: never spin
            }
            if (onBytes is not null && pending > 0) { try { onBytes(pending); } catch { } }
            return remaining == 0;
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
