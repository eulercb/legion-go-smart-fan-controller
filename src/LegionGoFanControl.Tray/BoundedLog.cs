using System.Text;

namespace LegionGoFanControl.Tray;

internal sealed class BoundedLog(string path)
{
    internal const int MaxBytes = 1024 * 1024;
    internal const int MaxMessageChars = 8192;
    private readonly object gate = new();
    public void Write(string message)
    {
        lock (gate)
        {
            try
            {
                if (message.Length > MaxMessageChars) message = message[..MaxMessageChars] + " [truncated]";
                byte[] entry = Encoding.UTF8.GetBytes($"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
                if (File.Exists(path + ".1") && new FileInfo(path + ".1").Length > MaxBytes) File.Delete(path + ".1");
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) File.Delete(path);
                if (File.Exists(path) && new FileInfo(path).Length + entry.Length > MaxBytes) File.Move(path, path + ".1", true);
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(entry);
            }
            catch { /* Logging must not stop fan control. */ }
        }
    }
}

