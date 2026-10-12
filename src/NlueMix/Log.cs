using System.Diagnostics;
using System.IO;
using System.Text;

namespace NlueMix;

/// <summary>极简日志。设计原则：绝不因为写日志而抛异常，也绝不静默丢失日志。</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly Queue<string> Recent = new();
    private static string? _path;

    public static string LogPath
    {
        get
        {
            if (_path is not null) return _path;

            foreach (var dir in CandidateDirs())
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    Directory.CreateDirectory(dir);
                    var probe = Path.Combine(dir, ".write-probe");
                    File.WriteAllText(probe, "ok");
                    File.Delete(probe);
                    _path = Path.Combine(dir, "nluemix.log");
                    return _path;
                }
                catch
                {
                    // 换下一个地方
                }
            }

            // 最后的兜底：程序自己所在目录（一定存在）
            try
            {
                _path = Path.Combine(AppContext.BaseDirectory, "nluemix.log");
                return _path;
            }
            catch { }

            _path = "nluemix.log";
            return _path;
        }
    }

    private static IEnumerable<string> CandidateDirs()
    {
        string? local = null, roaming = null, temp = null;
        try { local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); } catch { }
        try { roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); } catch { }
        try { temp = Path.GetTempPath(); } catch { }

        if (!string.IsNullOrWhiteSpace(local)) yield return Path.Combine(local, "NlueMix");
        if (!string.IsNullOrWhiteSpace(roaming)) yield return Path.Combine(roaming, "NlueMix");
        if (!string.IsNullOrWhiteSpace(temp)) yield return Path.Combine(temp, "NlueMix");

        string baseDir = "";
        try { baseDir = AppContext.BaseDirectory; } catch { }
        if (!string.IsNullOrWhiteSpace(baseDir)) yield return baseDir;
    }

    public static IReadOnlyList<string> Tail(int count = 6)
    {
        lock (Gate) return Recent.Reverse().Take(count).Reverse().ToArray();
    }

    public static void Info(string msg) => Write("INFO ", msg);
    public static void Warn(string msg) => Write("WARN ", msg);
    public static void Error(string msg) => Write("ERROR", msg);
    public static void Fatal(string msg, Exception? ex) => Write("FATAL", msg + " :: " + ex);

    private static void Write(string level, string msg)
    {
        var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level + "] " + msg;
        lock (Gate)
        {
            try
            {
                Recent.Enqueue(line);
                while (Recent.Count > 200) Recent.Dequeue();
            }
            catch { }

            try
            {
                var path = LogPath;
                if (!string.IsNullOrWhiteSpace(path))
                    File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                try { Debug.WriteLine("[NlueMix] 日志写入失败: " + ex.Message); } catch { }
            }
        }
    }
}
