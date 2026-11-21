using System;
using System.IO;

namespace MozaHud
{
    /// <summary>Small log in %APPDATA%\MozaHud\log.txt (trimmed to the last ~200 KB at start).</summary>
    public static class Log
    {
        static readonly object Gate = new object();
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MozaHud");
        public static readonly string FilePath = Path.Combine(Dir, "log.txt");

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
            catch { }
            Write("---- MozaHud started");
        }

        public static void Write(string msg)
        {
            lock (Gate)
            {
                try { File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff ") + msg + Environment.NewLine); }
                catch { }
            }
        }
    }
}
