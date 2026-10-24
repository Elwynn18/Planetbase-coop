using System;
using System.IO;
using BepInEx.Logging;

namespace PlanetbaseCoop
{
    // Writes to the BepInEx log and, in test mode, to our own flushed file
    // (Unity and BepInEx logs are not flushed reliably when a copy is killed).
    public static class Log
    {
        private static ManualLogSource sSource;
        private static StreamWriter sFile;
        private static readonly object sLock = new object();

        public static void Init(ManualLogSource source)
        {
            sSource = source;
            string path = TestEnv.Get("COOP_LOG");
            if (!string.IsNullOrEmpty(path))
            {
                try
                {
                    sFile = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
                }
                catch (Exception e)
                {
                    source.LogError("Could not open COOP_LOG " + path + ": " + e.Message);
                }
            }
        }

        public static void Info(string msg) { sSource?.LogInfo(msg); Write("INFO", msg); }
        public static void Warn(string msg) { sSource?.LogWarning(msg); Write("WARN", msg); }
        public static void Error(string msg) { sSource?.LogError(msg); Write("ERROR", msg); }

        private static void Write(string level, string msg)
        {
            if (sFile == null) return;
            lock (sLock)
            {
                sFile.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{level}] {msg}");
            }
        }
    }
}
