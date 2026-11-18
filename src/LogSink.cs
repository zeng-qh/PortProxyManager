using System.Collections.Concurrent;
using System.Text;

namespace PortProxyManager;

/// <summary>内存环形日志 + 按天落盘。</summary>
public sealed class LogSink
{
    private const int Capacity = 500;
    private readonly ConcurrentQueue<LogEntry> _queue = new();
    private readonly string _dir;
    private readonly object _fileLock = new();

    public LogSink(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
    }

    public void Add(string level, string message)
    {
        var e = new LogEntry { Time = DateTime.Now, Level = level, Message = message };
        _queue.Enqueue(e);
        while (_queue.Count > Capacity && _queue.TryDequeue(out _)) { }

        try
        {
            var file = Path.Combine(_dir, $"portproxy-{DateTime.Now:yyyyMMdd}.log");
            var line = $"{e.Time:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}";
            lock (_fileLock) File.AppendAllText(file, line, Encoding.UTF8);
        }
        catch
        {
            // 日志落盘失败不影响主流程
        }
    }

    public void Info(string m) => Add("INFO", m);
    public void Warn(string m) => Add("WARN", m);
    public void Error(string m) => Add("ERROR", m);
    public void Ok(string m) => Add("OK", m);

    public List<LogEntry> Recent(int n = 200)
    {
        var all = _queue.ToList();
        all.Reverse();
        return all.Take(n).ToList();
    }
}
