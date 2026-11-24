using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace WeightCheckerCS.Engine
{
    /// <summary>线程安全日志：后台线程写 logs\app_YYYY-MM-DD.log；同时缓存给 UI（有上限，不会无限增长）。</summary>
    public static class Log
    {
        private static readonly BlockingCollection<string> FileQueue = new BlockingCollection<string>(10000);
        private static readonly ConcurrentQueue<string> UiQueue = new ConcurrentQueue<string>();
        private static Thread _thread;
        private static volatile string _dir;
        public static volatile bool Console;

        public static void Start(string dataDir)
        {
            _dir = dataDir;
            if (_thread != null) return;
            _thread = new Thread(Run) { IsBackground = true, Name = "log-writer" };
            _thread.Start();
        }

        public static string DataDir { set { _dir = value; } }

        public static void Info(string msg)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + msg;
            FileQueue.TryAdd(line);
            UiQueue.Enqueue(line);
            string dummy;
            while (UiQueue.Count > 1000 && UiQueue.TryDequeue(out dummy)) { }
            if (Console) { try { System.Console.WriteLine(line); } catch { } }
        }

        public static bool TryDequeueUi(out string line) { return UiQueue.TryDequeue(out line); }

        private static void Run()
        {
            StreamWriter w = null; string current = null;
            foreach (var line in FileQueue.GetConsumingEnumerable())
            {
                try
                {
                    string file = Path.Combine(_dir ?? AppPaths.BaseDir, "logs", "app_" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");
                    if (file != current)
                    {
                        if (w != null) w.Dispose();
                        Directory.CreateDirectory(Path.GetDirectoryName(file));
                        w = new StreamWriter(new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
                        current = file;
                    }
                    w.WriteLine(line);
                    if (FileQueue.Count == 0) w.Flush();
                }
                catch
                {
                    try { if (w != null) w.Dispose(); } catch { }
                    w = null; current = null;
                }
            }
        }

        public static void Flush()
        {
            for (int i = 0; i < 20 && FileQueue.Count > 0; i++) Thread.Sleep(50);
        }
    }
}
