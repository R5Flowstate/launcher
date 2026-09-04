using System.Text;

namespace R5Flowstate.Content;

/// <summary>
/// Append-only record of objects already committed, so a resumed run skips work
/// instead of re-reading it. Purely an optimisation: the bytes on disk are the
/// truth, and a lost or torn journal costs time, never correctness.
///
/// Flushes are batched. Fsyncing per object would halve throughput on a spinning
/// disk to save at most a few chunks of rework.
/// </summary>
public sealed class DownloadJournal : IDisposable
{
    const int FlushEveryRecords = 16;

    readonly object _lock = new();
    readonly FileStream _fs;
    readonly StreamWriter _writer;
    int _sinceFlush;

    DownloadJournal(FileStream fs)
    {
        _fs = fs;
        _writer = new StreamWriter(fs, new UTF8Encoding(false));
    }

    public static DownloadJournal Open(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        return new DownloadJournal(fs);
    }

    public void Record(string relPath, string digest, int chunkIndex)
    {
        lock (_lock)
        {
            _writer.Write(digest);
            _writer.Write('\t');
            _writer.Write(chunkIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _writer.Write('\t');
            _writer.Write(relPath.Replace('\t', ' ').Replace('\n', ' '));
            _writer.Write('\n');

            if (++_sinceFlush >= FlushEveryRecords)
            {
                _writer.Flush();
                _fs.Flush(flushToDisk: true);
                _sinceFlush = 0;
            }
        }
    }

    /// <summary>
    /// Digests this install has already committed. A trailing line without its
    /// newline was being written when the process died, so it is dropped.
    /// </summary>
    public static HashSet<string> ReadCommitted(string path)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(path))
                return set;
            var text = File.ReadAllText(path);
            var lines = text.Split('\n');
            var last = lines.Length - 1;
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Length == 0)
                    continue;
                if (i == last && !text.EndsWith('\n'))
                    break;
                var tab = line.IndexOf('\t');
                if (tab == 64)
                    set.Add(line[..64]);
            }
        }
        catch
        {
            // An unreadable journal is simply an empty one.
        }
        return set;
    }

    public static void Clear(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort */ }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try
            {
                _writer.Flush();
                _fs.Flush(flushToDisk: true);
            }
            catch
            {
                // Closing over a full or removed disk is not worth throwing on.
            }
            _writer.Dispose();
        }
    }
}
