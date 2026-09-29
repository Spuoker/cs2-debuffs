namespace DebuffRoulette;

public sealed class FileLog : ILog
{
    private readonly object _lock = new();
    private readonly string _path;

    private const long MaxBytes = 5 * 1024 * 1024;   // trim past this: the log lives for years, but only the tail matters

    public FileLog(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Rotate();
    }

    /// <summary>Grown too big: keep one previous .old file and start over.</summary>
    private void Rotate()
    {
        try
        {
            var fi = new FileInfo(_path);
            if (!fi.Exists || fi.Length < MaxBytes) return;
            var old = _path + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(_path, old);
        }
        catch { }
    }

    public void Info(string msg) => Write("INFO", msg);
    public void Warn(string msg) => Write("WARN", msg);
    public void Error(string msg, Exception? ex = null) => Write("ERR ", ex is null ? msg : $"{msg}: {ex}");

    private void Write(string lvl, string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{lvl}] {msg}";
        lock (_lock)
        {
            try { File.AppendAllText(_path, line + Environment.NewLine); } catch { }
        }
        try { Console.WriteLine(line); } catch { }
    }
}
