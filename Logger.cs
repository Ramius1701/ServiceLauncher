namespace ServiceLauncher;

public class Logger
{
    private readonly string _path;
    private readonly object _lock = new();

    public Logger(string path)
    {
        _path = path;
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    public void Info(string message)
    {
        string line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC  {message}";
        Console.WriteLine(line);
        lock (_lock)
        {
            try { File.AppendAllText(_path, line + Environment.NewLine); }
            catch { /* logging must never take the launcher down */ }
        }
    }
}
