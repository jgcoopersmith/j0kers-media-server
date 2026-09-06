using System.IO.Compression;
using System.Diagnostics;
using System.Text;

namespace J0kersMediaServer.Setup;

/// <summary>
/// The self-installing package: this program with the whole install payload
/// appended to it.
///
/// Why it is written rather than assembled from something off the shelf. The
/// package has to install onto a Windows 11 machine that has nothing on it -
/// no .NET, no 7-Zip, no WinRAR - which rules out anything the target would
/// have to already own. The two candidates that ship with Windows or were
/// already here both failed on test: 7-Zip's 7z.sfx is a plain extractor and
/// ignores a RunProgram directive, and IExpress built a package that extracted
/// and then never launched its install command. Rather than keep guessing at
/// their quirks, the mechanism is fifty lines that can be read.
///
/// The layout is the stub, then a zip, then a footer naming the zip's length:
///
///     [ this program ][ payload.zip ][ 8-byte length ][ 16-byte marker ]
///
/// Appending to a .NET single-file program is safe - measured before relying
/// on it: 1MB of trailing bytes added to the server's own executable and it
/// still ran. The host finds its bundle from a header near the front, not by
/// assuming it owns the end of the file.
/// </summary>
internal static class Program
{
    /// <summary>Marks a file as one of these packages, and where its payload ends.</summary>
    private static readonly byte[] Marker = Encoding.ASCII.GetBytes("J0KERSMEDIAPKG01");
    private const int FooterLength = 8 + 16;   // length, then marker

    private static int Main(string[] args)
    {
        Console.Title = "j0kers Media Server - Setup";
        var self = Environment.ProcessPath;
        if (self is null) return Fail("could not work out where this program is running from");

        string work;
        try { work = Unpack(self); }
        catch (Exception ex) { return Fail("could not unpack the installer: " + ex.Message); }

        try
        {
            var entry = Path.Combine(work, "Install.cmd");
            if (!File.Exists(entry)) return Fail("the package is missing Install.cmd");

            var psi = new ProcessStartInfo(entry)
            {
                WorkingDirectory = work,
                UseShellExecute = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return Fail("could not start the installer");
            p.WaitForExit();
            return p.ExitCode;
        }
        finally
        {
            // Best effort: the installer has finished with it, and what is left
            // is under the machine's own temp folder either way.
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Reads the payload off the end of this file and unpacks it into a
    /// directory of its own under temp.
    /// </summary>
    private static string Unpack(string self)
    {
        using var file = File.OpenRead(self);
        if (file.Length < FooterLength) throw new InvalidDataException("no payload attached");

        file.Seek(-FooterLength, SeekOrigin.End);
        var footer = new byte[FooterLength];
        file.ReadExactly(footer);
        for (var i = 0; i < Marker.Length; i++)
            if (footer[8 + i] != Marker[i])
                throw new InvalidDataException(
                    "this file does not carry an install payload - it may have been truncated by a download");

        var length = BitConverter.ToInt64(footer, 0);
        if (length <= 0 || length > file.Length - FooterLength)
            throw new InvalidDataException("the attached payload is not the size it says it is");

        var work = Path.Combine(Path.GetTempPath(), "j0kers-setup-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(work);

        Console.WriteLine();
        Console.WriteLine("  j0kers Media Server - Setup");
        Console.WriteLine();
        Console.WriteLine($"  unpacking {length / (1024 * 1024)} MB...");

        file.Seek(file.Length - FooterLength - length, SeekOrigin.Begin);
        // Copied out rather than read whole: the payload is half a gigabyte and
        // there is no reason for all of it to be in memory at once.
        var zipPath = Path.Combine(work, "payload.zip");
        using (var zip = File.Create(zipPath))
        {
            var buffer = new byte[1024 * 1024];
            var left = length;
            while (left > 0)
            {
                var want = (int)Math.Min(buffer.Length, left);
                var read = file.Read(buffer, 0, want);
                if (read <= 0) throw new EndOfStreamException("the payload ended early");
                zip.Write(buffer, 0, read);
                left -= read;
            }
        }

        ZipFile.ExtractToDirectory(zipPath, work, overwriteFiles: true);
        File.Delete(zipPath);
        return work;
    }

    private static int Fail(string message)
    {
        Console.WriteLine();
        Console.WriteLine("  Setup could not run: " + message);
        Console.WriteLine();
        Console.WriteLine("  Press any key to close.");
        try { Console.ReadKey(true); } catch { }
        return 1;
    }
}
