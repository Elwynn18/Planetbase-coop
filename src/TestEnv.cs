using System;
using System.IO;

namespace PlanetbaseCoop
{
    // Test switches come from environment variables set by test/run.sh. With none set the
    // mod behaves exactly as for players.
    public static class TestEnv
    {
        public static string Get(string name)
        {
            try { return Environment.GetEnvironmentVariable(name); }
            catch { return null; }
        }

        // COOP_TEST=host|client|boot
        public static string Mode => Get("COOP_TEST");
        public static bool IsTest => !string.IsNullOrEmpty(Mode);

        // Mutation twins: COOP_MUT=name[,name] turns one protection off; its test must then fail.
        public static bool Mut(string name)
        {
            var m = Get("COOP_MUT");
            return !string.IsNullOrEmpty(m) && ("," + m + ",").Contains("," + name + ",");
        }

        public static void Quit()
        {
            if (Get("COOP_KEEP_OPEN") == "1") return;
            Log.Info("Test finished, quitting");
            UnityEngine.Application.Quit();
        }

        public static void Fail(string reason)
        {
            WriteStatus("RESULT: FAIL " + reason);
        }

        public static void WriteStatus(string line)
        {
            Log.Info(line);
            string path = Get("COOP_STATUS");
            if (string.IsNullOrEmpty(path)) return;
            try { File.AppendAllText(path, line + "\n"); }
            catch (Exception e) { Log.Error("status write failed: " + e.Message); }
        }
    }
}
