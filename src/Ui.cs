using System.Collections.Generic;
using System.Linq;
using PlanetbaseCoop.Net;
using UnityEngine;

namespace PlanetbaseCoop
{
    // F8 panel: host, join by IP or Steam, player list, versions. Plus short notices on screen.
    public static class Ui
    {
        public static bool Visible;
        private static string sIp = "127.0.0.1";
        private static string sPort = "47610";
        private static Rect sRect = new Rect(20, 80, 420, 330);
        private static readonly List<(string text, float until)> sToasts = new List<(string, float)>();
        private static GUIStyle sToastStyle;

        public static void Toast(string text, float seconds = 6f)
        {
            if (string.IsNullOrEmpty(text)) return;
            sToasts.Add((text, Time.realtimeSinceStartup + seconds));
            if (sToasts.Count > 6) sToasts.RemoveAt(0);
        }

        public static void Update()
        {
            if (Input.GetKeyDown(KeyCode.F8)) Visible = !Visible;
        }

        public static void OnGUI()
        {
            float now = Time.realtimeSinceStartup;
            sToasts.RemoveAll(t => t.until < now);
            if (sToasts.Count > 0)
            {
                if (sToastStyle == null)
                {
                    sToastStyle = new GUIStyle(GUI.skin.box) { fontSize = 16, wordWrap = true, alignment = TextAnchor.MiddleCenter };
                    sToastStyle.normal.textColor = Color.white;
                }
                float y = 60;
                foreach (var t in sToasts)
                {
                    var size = sToastStyle.CalcHeight(new GUIContent(t.text), 500);
                    GUI.Box(new Rect(Screen.width / 2 - 250, y, 500, size + 8), t.text, sToastStyle);
                    y += size + 12;
                }
            }
            if (Visible) sRect = GUI.Window(0x504243, sRect, Draw, "Planetbase Co-op " + Plugin.Version + "  (F8)");
        }

        private static void Draw(int id)
        {
            GUILayout.Label(Session.Status);
            GUILayout.Label("Name:");
            Session.PlayerName = GUILayout.TextField(Session.PlayerName, 24);
            if (!Session.Active)
            {
                bool inColony = GameAccess.Game() != null;
                GUILayout.Space(6);
                if (inColony)
                {
                    GUILayout.Label("Host this colony:");
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Port", GUILayout.Width(40));
                    sPort = GUILayout.TextField(sPort, 5, GUILayout.Width(70));
                    if (GUILayout.Button("Host (IP)") && int.TryParse(sPort, out int port))
                    {
                        string err = Session.StartHost(TcpTransport.Host(port));
                        if (err != null) Toast(err);
                    }
                    GUILayout.EndHorizontal();
                    if (SteamTransport.Available && GUILayout.Button("Host with Steam (invite friends)"))
                    {
                        string err = SteamTransport.StartHosting();
                        if (err != null) Toast(err);
                    }
                }
                else
                {
                    GUILayout.Label("Join a friend (stay on the main menu):");
                    GUILayout.BeginHorizontal();
                    sIp = GUILayout.TextField(sIp, 64, GUILayout.Width(170));
                    sPort = GUILayout.TextField(sPort, 5, GUILayout.Width(60));
                    if (GUILayout.Button("Join (IP)") && int.TryParse(sPort, out int port))
                    {
                        string err = Session.StartClient(TcpTransport.Connect(sIp.Trim(), port));
                        if (err != null) Toast(err);
                    }
                    GUILayout.EndHorizontal();
                    if (SteamTransport.Available) GUILayout.Label("Steam: accept your friend's invite, or right-click them > Join Game.");
                }
            }
            else
            {
                if (Session.IsHost)
                {
                    GUILayout.Label("Players:");
                    foreach (var p in Session.Peers.Values.ToList())
                        GUILayout.Label($"  {p.Name} {(p.Ready ? "" : "(joining)")}");
                    if (SteamTransport.Active && GUILayout.Button("Invite Steam friends")) SteamTransport.OpenInviteDialog();
                }
                if (GUILayout.Button(Session.IsHost ? "Stop hosting" : "Leave")) Session.Stop("left");
            }
            GUILayout.FlexibleSpace();
            var v = VersionInfo.Local;
            GUILayout.Label($"Game {v.Game} ({VersionInfo.Short(v.GameCodeHash)}), mod {v.Mod} ({VersionInfo.Short(v.ModHash)})");
            GUI.DragWindow();
        }
    }
}
