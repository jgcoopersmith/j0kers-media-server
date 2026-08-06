using System.Net;
using System.Text;
using J0kersMediaServer.Logging;

namespace J0kersMediaServer.Dlna;

/// <summary>
/// A UPnP/DLNA MediaServer over the library folders.
///
/// DLNA is what a TV means by "Media Server" in its input list, and what
/// devices with no browser and no VLC can actually play. It is a different
/// shape from everything else here: the client browses a tree over SOAP
/// (ContentDirectory), gets back an XML catalogue (DIDL-Lite), and then
/// fetches a whole file over plain HTTP with byte ranges. No playlists, no
/// segments, no transcoding — the file is handed over as it sits on disk,
/// so what plays is whatever the device itself can decode.
///
/// <b>It also cannot authenticate.</b> There is no account, cookie or token
/// in the protocol — a TV that finds the server expects to browse it. That
/// is why this is off by default and refuses anything that isn't a private
/// LAN address: turning it on shares the library with every device on the
/// network, which is the deal DLNA offers and worth stating plainly.
/// </summary>
public sealed class DlnaService
{
    private readonly Media.LibraryStore _library;
    private readonly Func<string> _serverName;
    private readonly string _uuid;

    public DlnaService(Media.LibraryStore library, Func<string> serverName, string uuid)
    {
        _library = library;
        _serverName = serverName;
        _uuid = uuid;
    }

    /// <summary>
    /// Whether a caller may use the DLNA endpoints at all. The protocol has
    /// no credentials, so the boundary is the network itself: loopback and
    /// private ranges only, never something that arrived off the internet.
    /// </summary>
    public static bool IsLocalClient(IPAddress? ip)
    {
        if (ip is null) return false;
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            else return ip.GetAddressBytes()[0] == 0xfd || ip.GetAddressBytes()[0] == 0xfc; // ULA
        }
        var b = ip.GetAddressBytes();
        return b[0] switch
        {
            10 => true,
            127 => true,
            172 => b[1] >= 16 && b[1] <= 31,
            192 => b[1] == 168,
            169 => b[1] == 254,   // link-local, when DHCP failed
            _ => false,
        };
    }

    // ---- the content tree -------------------------------------------------

    /// <summary>
    /// Object ids are the path, encoded. DLNA ids are opaque strings the
    /// client echoes back, so carrying the path in them means no server-side
    /// session or id table to keep in step with the disk. Every id is checked
    /// against the library roots before it is used for anything.
    /// </summary>
    private static string Encode(string path) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(path)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? Decode(string id)
    {
        try
        {
            var s = id.Replace('-', '+').Replace('_', '/');
            return Encoding.UTF8.GetString(Convert.FromBase64String(s.PadRight((s.Length + 3) / 4 * 4, '=')));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves an object id to a real path inside a library root. Anything
    /// else — an id from another server, a crafted one, a folder that has
    /// since been removed from the library — resolves to null.
    /// </summary>
    public string? ResolvePath(string id)
    {
        var decoded = Decode(id);
        if (decoded is null) return null;
        string full;
        try { full = Path.GetFullPath(decoded); }
        catch { return null; }
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) return null;

        foreach (var root in _library.All)
        {
            string r;
            try { r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); }
            catch { continue; }
            if (full.Equals(r, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return full;
        }
        return null;
    }

    private static readonly HashSet<string> Video = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mkv", ".avi", ".mov", ".wmv", ".mpg", ".mpeg", ".ts", ".m2ts", ".webm", ".flv", ".3gp", ".vob", ".divx",
    };
    private static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".m4a", ".m4b", ".aac", ".ogg", ".oga", ".opus", ".wma", ".aiff", ".ape", ".mka", ".ac3",
    };
    private static readonly HashSet<string> Image = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff",
    };

    public static string MimeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" or ".mov" => "video/mp4",
        ".mkv" => "video/x-matroska",
        ".avi" or ".divx" => "video/x-msvideo",
        ".wmv" => "video/x-ms-wmv",
        ".mpg" or ".mpeg" or ".vob" => "video/mpeg",
        ".ts" or ".m2ts" => "video/mp2t",
        ".webm" => "video/webm",
        ".flv" => "video/x-flv",
        ".3gp" => "video/3gpp",
        ".mp3" => "audio/mpeg",
        ".flac" => "audio/flac",
        ".wav" => "audio/wav",
        ".m4a" or ".m4b" or ".aac" => "audio/mp4",
        ".ogg" or ".oga" => "audio/ogg",
        ".opus" => "audio/opus",
        ".wma" => "audio/x-ms-wma",
        ".ac3" => "audio/ac3",
        ".mka" => "audio/x-matroska",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        ".tif" or ".tiff" => "image/tiff",
        _ => "application/octet-stream",
    };

    private static string ClassFor(string path)
    {
        var ext = Path.GetExtension(path);
        if (Video.Contains(ext)) return "object.item.videoItem";
        if (Audio.Contains(ext)) return "object.item.audioItem.musicTrack";
        if (Image.Contains(ext)) return "object.item.imageItem.photo";
        return "";
    }

    private static bool IsMedia(string path) =>
        ClassFor(path).Length > 0;

    // ---- SOAP: ContentDirectory ------------------------------------------

    private sealed record BrowseResult(string Didl, int Returned, int Total);

    /// <summary>
    /// Browse. <c>BrowseDirectChildren</c> lists a container's contents;
    /// <c>BrowseMetadata</c> describes the object itself, which is what
    /// clients ask for first and what several of them refuse to continue
    /// without.
    /// </summary>
    private BrowseResult Browse(string objectId, string flag, int start, int count, string baseUrl)
    {
        var metadata = flag == "BrowseMetadata";
        var sb = new StringBuilder();

        // the root lists the library folders, one container each
        if (objectId is "0" or "" )
        {
            if (metadata)
            {
                sb.Append(Container("0", "-1", Escape(_serverName()), _library.All.Count));
                return new BrowseResult(Didl(sb.ToString()), 1, 1);
            }
            var roots = _library.All.Where(Directory.Exists).ToList();
            var page = Page(roots, start, count);
            foreach (var folder in page)
                sb.Append(Container(Encode(folder), "0", Escape(NameOf(folder)), CountChildren(folder)));
            return new BrowseResult(Didl(sb.ToString()), page.Count, roots.Count);
        }

        var path = ResolvePath(objectId);
        if (path is null) return new BrowseResult(Didl(""), 0, 0);

        if (Directory.Exists(path))
        {
            if (metadata)
            {
                sb.Append(Container(objectId, ParentId(path), Escape(NameOf(path)), CountChildren(path)));
                return new BrowseResult(Didl(sb.ToString()), 1, 1);
            }
            var children = Children(path);
            var page = Page(children, start, count);
            foreach (var child in page)
                sb.Append(Directory.Exists(child)
                    ? Container(Encode(child), objectId, Escape(NameOf(child)), CountChildren(child))
                    : Item(child, objectId, baseUrl));
            return new BrowseResult(Didl(sb.ToString()), page.Count, children.Count);
        }

        if (File.Exists(path) && IsMedia(path))
        {
            sb.Append(Item(path, ParentId(path), baseUrl));
            return new BrowseResult(Didl(sb.ToString()), 1, 1);
        }

        return new BrowseResult(Didl(""), 0, 0);
    }

    private static List<T> Page<T>(List<T> all, int start, int count)
    {
        if (start >= all.Count) return new List<T>();
        // count 0 means "everything from here", per the spec
        var take = count <= 0 ? all.Count - start : Math.Min(count, all.Count - start);
        return all.GetRange(start, take);
    }

    /// <summary>Folders first, then playable files, each alphabetical — how a remote expects to scroll.</summary>
    private static List<string> Children(string folder)
    {
        try
        {
            var dirs = Directory.GetDirectories(folder).OrderBy(d => d, StringComparer.OrdinalIgnoreCase);
            var files = Directory.GetFiles(folder).Where(IsMedia).OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
            return dirs.Concat(files).ToList();
        }
        catch
        {
            return new List<string>(); // unreadable folder: an empty one beats a fault
        }
    }

    private static int CountChildren(string folder)
    {
        try { return Children(folder).Count; }
        catch { return 0; }
    }

    private string ParentId(string path)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
        if (parent is null) return "0";
        // a library root's parent is the root container, not the folder above
        // it on disk — which is outside the library and must not be browsable
        return ResolvePath(Encode(parent)) is null ? "0" : Encode(parent);
    }

    private static string NameOf(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return name.Length > 0 ? name : path; // a drive root has no file name
    }

    private static string Container(string id, string parent, string title, int children) =>
        $"""<container id="{id}" parentID="{parent}" restricted="1" childCount="{children}"><dc:title>{title}</dc:title><upnp:class>object.container.storageFolder</upnp:class></container>""";

    private static string Item(string path, string parentId, string baseUrl)
    {
        long size;
        try { size = new FileInfo(path).Length; } catch { size = 0; }
        var mime = MimeFor(path);
        var url = $"{baseUrl}/dlna/file?id={Encode(path)}";
        // DLNA.ORG_OP=01 advertises byte-range seeking, which is what lets a
        // TV scrub through a film instead of only playing it from the start
        var protocolInfo = $"http-get:*:{mime}:DLNA.ORG_OP=01;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000";
        return $"""<item id="{Encode(path)}" parentID="{parentId}" restricted="1"><dc:title>{Escape(Media.StreamTitle.PrettifyFile(Path.GetFileName(path)))}</dc:title><upnp:class>{ClassFor(path)}</upnp:class><res protocolInfo="{protocolInfo}" size="{size}">{Escape(url)}</res></item>""";
    }

    private static string Didl(string body) =>
        """<DIDL-Lite xmlns="urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:upnp="urn:schemas-upnp-org:metadata-1-0/upnp/" xmlns:dlna="urn:schemas-dlna-org:metadata-1-0/">"""
        + body + "</DIDL-Lite>";

    public static string Escape(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;").Replace("'", "&apos;");

    /// <summary>
    /// Handles a ContentDirectory or ConnectionManager SOAP call. Only the
    /// actions a browsing client actually issues are implemented; anything
    /// else gets a proper SOAP fault rather than a broken envelope.
    /// </summary>
    public (int Status, string Xml) HandleSoap(string soapAction, string body, string baseUrl)
    {
        var action = soapAction.Trim('"');
        var name = action.Contains('#') ? action[(action.IndexOf('#') + 1)..] : action;

        switch (name)
        {
            case "Browse":
            {
                var objectId = SoapArg(body, "ObjectID") ?? "0";
                var flag = SoapArg(body, "BrowseFlag") ?? "BrowseDirectChildren";
                _ = int.TryParse(SoapArg(body, "StartingIndex"), out var start);
                _ = int.TryParse(SoapArg(body, "RequestedCount"), out var count);
                var result = Browse(objectId, flag, start, count, baseUrl);
                return (200, SoapEnvelope(
                    $"""<u:BrowseResponse xmlns:u="urn:schemas-upnp-org:service:ContentDirectory:1"><Result>{Escape(result.Didl)}</Result><NumberReturned>{result.Returned}</NumberReturned><TotalMatches>{result.Total}</TotalMatches><UpdateID>1</UpdateID></u:BrowseResponse>"""));
            }

            case "GetSearchCapabilities":
                return (200, SoapEnvelope(
                    """<u:GetSearchCapabilitiesResponse xmlns:u="urn:schemas-upnp-org:service:ContentDirectory:1"><SearchCaps></SearchCaps></u:GetSearchCapabilitiesResponse>"""));

            case "GetSortCapabilities":
                return (200, SoapEnvelope(
                    """<u:GetSortCapabilitiesResponse xmlns:u="urn:schemas-upnp-org:service:ContentDirectory:1"><SortCaps></SortCaps></u:GetSortCapabilitiesResponse>"""));

            case "GetSystemUpdateID":
                return (200, SoapEnvelope(
                    """<u:GetSystemUpdateIDResponse xmlns:u="urn:schemas-upnp-org:service:ContentDirectory:1"><Id>1</Id></u:GetSystemUpdateIDResponse>"""));

            case "GetProtocolInfo":
                return (200, SoapEnvelope(
                    $"""<u:GetProtocolInfoResponse xmlns:u="urn:schemas-upnp-org:service:ConnectionManager:1"><Source>{Escape(ProtocolInfo)}</Source><Sink></Sink></u:GetProtocolInfoResponse>"""));

            case "GetCurrentConnectionIDs":
                return (200, SoapEnvelope(
                    """<u:GetCurrentConnectionIDsResponse xmlns:u="urn:schemas-upnp-org:service:ConnectionManager:1"><ConnectionIDs>0</ConnectionIDs></u:GetCurrentConnectionIDsResponse>"""));

            case "GetCurrentConnectionInfo":
                return (200, SoapEnvelope(
                    """<u:GetCurrentConnectionInfoResponse xmlns:u="urn:schemas-upnp-org:service:ConnectionManager:1"><RcsID>-1</RcsID><AVTransportID>-1</AVTransportID><ProtocolInfo></ProtocolInfo><PeerConnectionManager></PeerConnectionManager><PeerConnectionID>-1</PeerConnectionID><Direction>Output</Direction><Status>OK</Status></u:GetCurrentConnectionInfoResponse>"""));

            default:
                return (501, SoapEnvelope(
                    """<s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring><detail><UPnPError xmlns="urn:schemas-upnp-org:control-1-0"><errorCode>401</errorCode><errorDescription>Invalid Action</errorDescription></UPnPError></detail></s:Fault>"""));
        }
    }

    private const string ProtocolInfo =
        "http-get:*:video/mp4:*,http-get:*:video/x-matroska:*,http-get:*:video/x-msvideo:*," +
        "http-get:*:video/mpeg:*,http-get:*:video/mp2t:*,http-get:*:video/webm:*,http-get:*:video/x-ms-wmv:*," +
        "http-get:*:audio/mpeg:*,http-get:*:audio/mp4:*,http-get:*:audio/flac:*,http-get:*:audio/wav:*," +
        "http-get:*:audio/ogg:*,http-get:*:image/jpeg:*,http-get:*:image/png:*,http-get:*:image/gif:*";

    private static string SoapEnvelope(string body) =>
        """<?xml version="1.0" encoding="utf-8"?><s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/"><s:Body>"""
        + body + "</s:Body></s:Envelope>";

    /// <summary>
    /// Pulls one argument out of a SOAP body. Deliberately not an XML parse:
    /// the arguments are flat strings in a fixed shape, clients differ on
    /// namespace prefixes, and a strict parser rejecting a slightly odd
    /// envelope is a TV that shows nothing.
    /// </summary>
    private static string? SoapArg(string body, string name)
    {
        var open = body.IndexOf("<" + name, StringComparison.OrdinalIgnoreCase);
        if (open < 0) return null;
        var gt = body.IndexOf('>', open);
        if (gt < 0) return null;
        var close = body.IndexOf("</", gt, StringComparison.Ordinal);
        if (close < 0) return null;
        var raw = body[(gt + 1)..close];
        return raw.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"")
                  .Replace("&apos;", "'").Replace("&amp;", "&").Trim();
    }

    // ---- device and service descriptions ----------------------------------

    /// <summary>ContentDirectory's SCPD — the action list a client reads before calling anything.</summary>
    public const string ContentDirectoryScpd = """
        <?xml version="1.0" encoding="utf-8"?>
        <scpd xmlns="urn:schemas-upnp-org:service-1-0">
          <specVersion><major>1</major><minor>0</minor></specVersion>
          <actionList>
            <action><name>Browse</name><argumentList>
              <argument><name>ObjectID</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_ObjectID</relatedStateVariable></argument>
              <argument><name>BrowseFlag</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_BrowseFlag</relatedStateVariable></argument>
              <argument><name>Filter</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_Filter</relatedStateVariable></argument>
              <argument><name>StartingIndex</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_Index</relatedStateVariable></argument>
              <argument><name>RequestedCount</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_Count</relatedStateVariable></argument>
              <argument><name>SortCriteria</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_SortCriteria</relatedStateVariable></argument>
              <argument><name>Result</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_Result</relatedStateVariable></argument>
              <argument><name>NumberReturned</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_Count</relatedStateVariable></argument>
              <argument><name>TotalMatches</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_Count</relatedStateVariable></argument>
              <argument><name>UpdateID</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_UpdateID</relatedStateVariable></argument>
            </argumentList></action>
            <action><name>GetSearchCapabilities</name><argumentList>
              <argument><name>SearchCaps</name><direction>out</direction><relatedStateVariable>SearchCapabilities</relatedStateVariable></argument>
            </argumentList></action>
            <action><name>GetSortCapabilities</name><argumentList>
              <argument><name>SortCaps</name><direction>out</direction><relatedStateVariable>SortCapabilities</relatedStateVariable></argument>
            </argumentList></action>
            <action><name>GetSystemUpdateID</name><argumentList>
              <argument><name>Id</name><direction>out</direction><relatedStateVariable>SystemUpdateID</relatedStateVariable></argument>
            </argumentList></action>
          </actionList>
          <serviceStateTable>
            <stateVariable sendEvents="no"><name>A_ARG_TYPE_ObjectID</name><dataType>string</dataType></stateVariable>
            <stateVariable sendEvents="no"><name>A_ARG_TYPE_Result</name><dataType>string</dataType></stateVariable>
            <stateVariable sendEvents="no"><name>A_ARG_TYPE_BrowseFlag</name><dataType>string</dataType>
              <allowedValueList><allowedValue>BrowseMetadata</allowedValue><allowedValue>BrowseDirectChildren</allowedValue></allowedValueList></stateVariable>
            <stateVariable sendEvents="no"><name>A_ARG_TYPE_Filter</name><dataType>string</dataType></stateVariable>
            <stateVariable sendEvents="no"><name>A_ARG_TYPE_SortCriteria</name><dataType>string</dataType></stateVariable>
            <stateVariable sendEvents="no"><name>A_ARG_TYPE_Index</name><dataType>ui4</dataType></stateVariable>
            <stateVariable sendEvents="no"><name>A_ARG_TYPE_Count</name><dataType>ui4</dataType></stateVariable>
            <stateVariable sendEvents="no"><name>A_ARG_TYPE_UpdateID</name><dataType>ui4</dataType></stateVariable>
            <stateVariable sendEvents="no"><name>SearchCapabilities</name><dataType>string</dataType></stateVariable>
            <stateVariable sendEvents="no"><name>SortCapabilities</name><dataType>string</dataType></stateVariable>
            <stateVariable sendEvents="yes"><name>SystemUpdateID</name><dataType>ui4</dataType></stateVariable>
          </serviceStateTable>
        </scpd>
        """;

    /// <summary>ConnectionManager's SCPD — small, but clients look for it before trusting the device.</summary>
    public const string ConnectionManagerScpd = """
        <?xml version="1.0" encoding="utf-8"?>
        <scpd xmlns="urn:schemas-upnp-org:service-1-0">
          <specVersion><major>1</major><minor>0</minor></specVersion>
          <actionList>
            <action><name>GetProtocolInfo</name><argumentList>
              <argument><name>Source</name><direction>out</direction><relatedStateVariable>SourceProtocolInfo</relatedStateVariable></argument>
              <argument><name>Sink</name><direction>out</direction><relatedStateVariable>SinkProtocolInfo</relatedStateVariable></argument>
            </argumentList></action>
          </actionList>
          <serviceStateTable>
            <stateVariable sendEvents="yes"><name>SourceProtocolInfo</name><dataType>string</dataType></stateVariable>
            <stateVariable sendEvents="yes"><name>SinkProtocolInfo</name><dataType>string</dataType></stateVariable>
            <stateVariable sendEvents="yes"><name>CurrentConnectionIDs</name><dataType>string</dataType></stateVariable>
          </serviceStateTable>
        </scpd>
        """;

    /// <summary>
    /// The two service entries that turn the UPnP description into a
    /// MediaServer. Without these a client finds the device and then has
    /// nothing to call.
    /// </summary>
    public static string ServiceListXml => """
          <serviceList>
            <service>
              <serviceType>urn:schemas-upnp-org:service:ContentDirectory:1</serviceType>
              <serviceId>urn:upnp-org:serviceId:ContentDirectory</serviceId>
              <SCPDURL>/dlna/cds.xml</SCPDURL>
              <controlURL>/dlna/control</controlURL>
              <eventSubURL>/dlna/events</eventSubURL>
            </service>
            <service>
              <serviceType>urn:schemas-upnp-org:service:ConnectionManager:1</serviceType>
              <serviceId>urn:upnp-org:serviceId:ConnectionManager</serviceId>
              <SCPDURL>/dlna/cm.xml</SCPDURL>
              <controlURL>/dlna/control</controlURL>
              <eventSubURL>/dlna/events</eventSubURL>
            </service>
          </serviceList>
        """;

    // ---- serving the file itself ------------------------------------------

    /// <summary>
    /// Hands over a file, honouring a Range request. A TV seeking through a
    /// film sends ranges constantly, and one that gets the whole file back
    /// each time either stalls or refuses to seek at all.
    /// </summary>
    public void ServeFile(HttpListenerContext ctx, string path)
    {
        var res = ctx.Response;
        FileInfo info;
        try { info = new FileInfo(path); }
        catch { res.StatusCode = 404; res.Close(); return; }
        if (!info.Exists) { res.StatusCode = 404; res.Close(); return; }

        var length = info.Length;
        long from = 0, to = length - 1;
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
                else if (hasTo) { from = Math.Max(0, length - t); }   // suffix range: last N bytes
                partial = hasFrom || hasTo;
            }
            if (from >= length || from < 0 || to < from)
            {
                res.StatusCode = 416;
                res.Headers["Content-Range"] = $"bytes */{length}";
                res.Close();
                return;
            }
            if (to > length - 1) to = length - 1;
        }

        var count = to - from + 1;
        res.StatusCode = partial ? 206 : 200;
        res.ContentType = MimeFor(path);
        res.ContentLength64 = count;
        res.Headers["Accept-Ranges"] = "bytes";
        // the two headers DLNA clients check before they will seek
        res.Headers["transferMode.dlna.org"] = "Streaming";
        res.Headers["contentFeatures.dlna.org"] = "DLNA.ORG_OP=01;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000";
        if (partial) res.Headers["Content-Range"] = $"bytes {from}-{to}/{length}";

        // HEAD is how a client checks size and seekability before playing
        if (ctx.Request.HttpMethod == "HEAD") { res.Close(); return; }

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
            fs.Seek(from, SeekOrigin.Begin);
            var buffer = new byte[64 * 1024];
            while (count > 0)
            {
                var read = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
                if (read <= 0) break;
                res.OutputStream.Write(buffer, 0, read);
                count -= read;
            }
        }
        catch (HttpListenerException) { /* the TV stopped or seeked away */ }
        catch (IOException) { }
        catch (Exception ex) { Log.Debug("dlna", $"serving {Path.GetFileName(path)} failed: {ex.Message}"); }
        finally
        {
            try { res.Close(); } catch { }
        }
    }
}
