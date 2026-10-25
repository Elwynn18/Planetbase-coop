using System;
using System.Collections.Generic;
using HarmonyLib;
using Planetbase;
using PlanetbaseCoop.Net;
using UnityEngine;

namespace PlanetbaseCoop
{
    // The game turns the first logged error or exception into a full-screen "ERROR ... Quit" page
    // (ErrorManager.logCallback). On a joiner, game UI code that reads the frozen mirror can throw
    // now and then; the host repairs the mirror a moment later anyway, so ending the player's game
    // is the worst answer. During a session: log the error, show a short notice, and (joiner) send
    // it to the host so the host's log has it too.
    public static class ErrorGuard
    {
        public static int Caught, Reported;
        private static readonly Dictionary<string, int> sSeen = new Dictionary<string, int>();
        private static float sLastToast = -100f, sReportWindow;
        private static int sReportsInWindow;
        private static readonly string[] GameIgnores = { "<RI.Hid>", "FMOD failed", "Screen position out of view frustum" };

        public static void Patch(Harmony h)
        {
            var m = AccessTools.Method(typeof(ErrorManager), "logCallback");
            if (m == null) throw new MissingMethodException("ErrorManager.logCallback");
            h.Patch(m, prefix: new HarmonyMethod(typeof(ErrorGuard), nameof(LogPrefix)));
        }

        private static bool LogPrefix(string logString, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return true;
            if (!Session.Active || TestEnv.Mut("no-error-guard")) return true;
            foreach (var ig in GameIgnores) if (logString != null && logString.Contains(ig)) return true;   // the game only toasts these
            try { OnError(logString ?? "", stackTrace ?? ""); }
            catch { /* never throw from a log callback */ }
            return false;
        }

        private static void OnError(string text, string stack)
        {
            Caught++;
            string firstFrame = stack.Split('\n')[0];
            string key = text + "|" + firstFrame;
            sSeen.TryGetValue(key, out int n);
            sSeen[key] = ++n;
            // the same error every frame: log 1st, 10th, 100th...
            bool log = n == 1 || n == 10 || n == 100 || n % 1000 == 0;
            if (log) Log.Warn($"game error caught (seen {n}x), the session goes on: {text}\n{stack}");
            float now = Time.realtimeSinceStartup;
            if (now - sLastToast > 30f)
            {
                sLastToast = now;
                Ui.Toast("Co-op: a game error was caught and ignored (details in the log). If something looks wrong, leave and rejoin.", 8f);
            }
            if (log && Session.IsClient && Session.Transport != null)
            {
                if (now - sReportWindow > 60f) { sReportWindow = now; sReportsInWindow = 0; }
                if (sReportsInWindow++ < 10)
                {
                    string body = text.Length > 2000 ? text.Substring(0, 2000) : text;
                    string st = stack.Length > 6000 ? stack.Substring(0, 6000) : stack;
                    Session.Transport.Send(0, Wire.Build(Msg.ErrorReport, w => { w.Write(n); w.Write(body); w.Write(st); }));
                    Reported++;
                }
            }
        }

        // host: an error report from a joiner goes into the host's log
        public static int Received;
        public static void OnReport(string who, System.IO.BinaryReader r)
        {
            int n = r.ReadInt32();
            string text = r.ReadString(), stack = r.ReadString();
            Received++;
            Log.Warn($"game error on {who}'s game (seen {n}x): {text}\n{stack}");
        }
    }
}
