namespace J0kersMediaServer.Services;

/// <summary>
/// Total bytes of media this server has pushed out, across every protocol.
///
/// Deliberately a single monotonic counter rather than a sum over live
/// sessions: viewers and RTSP sessions come and go, and summing what's
/// currently connected makes the total *fall* whenever someone stops
/// watching, which reads as a negative rate. The dashboard turns
/// consecutive readings into a rate, so the number it samples has to only
/// ever go up.
/// </summary>
public sealed class Throughput
{
    private long _bytes;

    /// <summary>Bytes sent since the server started.</summary>
    public long TotalBytes => Interlocked.Read(ref _bytes);

    public void Add(long bytes)
    {
        if (bytes > 0) Interlocked.Add(ref _bytes, bytes);
    }
}
