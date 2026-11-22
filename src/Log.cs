using System;
using System.IO;
using System.Text;

namespace MozaHud
{
    /// <summary>
    /// Small log in %APPDATA%\MozaHud\log.txt (trimmed to the last ~200 KB at start).
    /// Opened with full sharing so a viewer or editor holding the file can't block it. If writing still fails, the
    /// reason goes to log-error.txt next to it (and to %TEMP%\MozaHud-log-error.txt if that fails too), once per reason.
    /// </summary>
    public static class Log
    {
        static readonly object Gate = new object();
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MozaHud");
        public static readonly string FilePath = Path.Combine(Dir, "log.txt");
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        static string _lastError;

        /// <summary>The last reason writing the log failed, or null.</summary>
        public static string LastError { get { return _lastError; } }

        public static void Start()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 200 * 1024)
                {
                    var text = File.ReadAllText(FilePath);
                    File.WriteAllText(FilePath, text.Substring(text.Length - 100 * 1024));
                }
            }
            catch (Exception ex) { Failed("trimming", ex); }
            Write("---- MozaHud started (" + System.Reflection.Assembly.GetExecutingAssembly().Location + ")");
        }

        public static void Write(string msg)
        {
            var line = Utf8.GetBytes(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff ") + msg + Environment.NewLine);
            lock (Gate)
            {
                try
                {
                    using (var fs = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                        fs.Write(line, 0, line.Length);
                }
                catch (Exception ex) { Failed("writing", ex); }
            }
        }

        static void Failed(string what, Exception ex)
        {
            string reason = what + ": " + ex.GetType().Name + ": " + ex.Message;
            if (reason == _lastError) return;
            _lastError = reason;
            string text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff ") + "log.txt " + reason + Environment.NewLine;
            foreach (var path in new[] { Path.Combine(Dir, "log-error.txt"), Path.Combine(Path.GetTempPath(), "MozaHud-log-error.txt") })
            {
                try { File.AppendAllText(path, text); return; } catch { }
            }
        }
    }
}
