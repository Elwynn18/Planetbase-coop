using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Planetbase;
using PlanetbaseCoop.Net;
using PlanetbaseCoop.Sync;
using UnityEngine;

namespace PlanetbaseCoop
{
    public enum Role { None, Host, Client }
    public enum ClientPhase { None, Connecting, Handshake, Loading, Mirroring }

    // One co-op session: the host runs the game; joiners mirror it and send their actions.
    public static class Session
    {
        public class Peer
        {
            public int Id;
            public string Name = "?";
            public bool Welcomed;
            public bool Ready;
            public float LastHeard;
            public VersionInfo Version;
        }

        public static Role Role { get; private set; }
        public static ClientPhase Phase { get; private set; }
        public static ITransport Transport { get; private set; }
        public static string PlayerName = "Player";
        public static string Status = "Not in a session";
        public static string LastRefusal;
        public static int MyPlayerId;
        public static readonly Dictionary<int, Peer> Peers = new Dictionary<int, Peer>();

        private static readonly List<NetEvent> sEvents = new List<NetEvent>();
        private static readonly List<byte[]> sQueuedWhileLoading = new List<byte[]>();
        private static float sCensusTimer, sPingTimer, sClock;
        private static float sHostLastHeard;

        public static bool IsHost => Role == Role.Host;
        public static bool IsClient => Role == Role.Client;
        public static bool Active => Role != Role.None;
        public static IEnumerable<int> ReadyPeers => Peers.Values.Where(p => p.Ready).Select(p => p.Id);

        // ---------------------------------------------------------------- start / stop
        public static string StartHost(ITransport t)
        {
            if (Active) return "already in a session";
            if (GameAccess.Game() == null || !Mirror.IsUpdating()) { t.Close(); return "load or start a colony first"; }
            Transport = t;
            Role = Role.Host;
            Peers.Clear();
            WorldSync.Reset();
            Settings.Reset();
            Application.runInBackground = true;   // the host must keep simulating when alt-tabbed
            Status = "Hosting (" + t.Describe + ")";
            Log.Info("Hosting: " + t.Describe + ", " + VersionInfo.Local);
            return null;
        }

        public static string StartClient(ITransport t)
        {
            if (Active) return "already in a session";
            if (GameAccess.Game() != null) { t.Close(); return "joiners start from the main menu (quit your colony first)"; }
            Transport = t;
            Role = Role.Client;
            Phase = ClientPhase.Connecting;
            LastRefusal = null;
            WorldApply.Reset();
            Settings.Reset();
            sQueuedWhileLoading.Clear();
            Application.runInBackground = true;
            Status = "Connecting (" + t.Describe + ")";
            Log.Info("Joining: " + t.Describe + ", " + VersionInfo.Local);
            return null;
        }

        public static void Stop(string reason)
        {
            if (!Active) return;
            Log.Info("Session ended: " + reason);
            try
            {
                if (Transport != null)
                {
                    var bye = Wire.Build(Msg.Bye, w => w.Write(reason ?? ""));
                    if (IsHost) Transport.Broadcast(bye); else Transport.Send(0, bye);
                    Transport.Close();
                }
            }
            catch (Exception e) { Log.Warn("stop: " + e.Message); }
            bool wasMirror = Mirror.Active;
            Transport = null;
            Role = Role.None;
            Phase = ClientPhase.None;
            Peers.Clear();
            Mirror.End();
            Status = "Not in a session (" + reason + ")";
            if (wasMirror)
            {
                // the frozen mirror is not a playable colony: back to the title screen
                Ui.Toast("Disconnected: " + reason);
                GameAccess.Manager?.setGameStateTitle();
            }
        }

        // ---------------------------------------------------------------- per frame
        public static void Update(float dt)
        {
            if (!Active || Transport == null) return;
            sClock += dt;
            sEvents.Clear();
            Transport.Poll(sEvents);
            foreach (var e in sEvents)
            {
                try { Handle(e); }
                catch (Exception ex) { Log.Error($"handling {e.Type} from {e.Peer}: {ex}"); }
                if (!Active) return;
            }
            if (IsHost) HostUpdate(dt); else ClientUpdate(dt);
        }

        private static void Handle(NetEvent e)
        {
            switch (e.Type)
            {
                case NetEventType.Connected:
                    if (IsHost)
                    {
                        Peers[e.Peer] = new Peer { Id = e.Peer, LastHeard = sClock };
                        Log.Info("peer " + e.Peer + " connected");
                    }
                    else
                    {
                        Phase = ClientPhase.Handshake;
                        sHostLastHeard = sClock;
                        Status = "Connected, checking versions";
                        Transport.Send(0, Wire.Build(Msg.Hello, w => { VersionInfo.Local.Write(w); w.Write(PlayerName); }));
                    }
                    break;
                case NetEventType.Disconnected:
                    if (IsHost)
                    {
                        if (Peers.TryGetValue(e.Peer, out var p))
                        {
                            Peers.Remove(e.Peer);
                            if (p.Welcomed) Notice(p.Name + " left (" + e.Reason + ")");
                        }
                    }
                    else
                    {
                        Stop(LastRefusal ?? e.Reason ?? "connection lost");
                    }
                    break;
                case NetEventType.Data:
                    if (IsHost) HostMessage(e.Peer, e.Data); else ClientMessage(e.Data);
                    break;
            }
        }

        // ---------------------------------------------------------------- host
        private static void HostMessage(int peer, byte[] data)
        {
            if (!Peers.TryGetValue(peer, out var p)) return;
            p.LastHeard = sClock;
            var r = Wire.Reader(data, out var type);
            if (!p.Welcomed && type != Msg.Hello) return;
            switch (type)
            {
                case Msg.Hello:
                {
                    var v = VersionInfo.Read(r);
                    p.Name = Clean(r.ReadString());
                    p.Version = v;
                    string why = VersionInfo.Local.Mismatch(v);
                    if (why != null)
                    {
                        Log.Info($"refused {p.Name}: {why} (theirs: {v})");
                        Transport.Send(peer, Wire.Build(Msg.Refuse, w => w.Write(why)));
                        Transport.Disconnect(peer, "version mismatch");
                        return;
                    }
                    p.Welcomed = true;
                    Transport.Send(peer, Wire.Build(Msg.Welcome, w => { w.Write(peer); w.Write(PlayerName); }));
                    SendSnapshot(peer);
                    Notice(p.Name + " is joining");
                    break;
                }
                case Msg.Ready:
                    p.Ready = true;
                    Notice(p.Name + " joined");
                    WorldSync.OnCensus(Transport, peer, r);
                    break;
                case Msg.Census:
                    WorldSync.OnCensus(Transport, peer, r);
                    break;
                case Msg.Command:
                    Commands.OnHostCommand(peer, p.Name, r);
                    break;
                case Msg.Ping:
                    Transport.Send(peer, Wire.Build(Msg.Pong, w => w.Write(r.ReadSingle())));
                    break;
                case Msg.Bye:
                    Transport.Disconnect(peer, "left");
                    break;
                case Msg.TestProbe:
                    TestBot.OnProbe(peer, r);
                    break;
            }
        }

        private static void SendSnapshot(int peer)
        {
            var t0 = DateTime.UtcNow;
            string xml = Snapshot.Capture();
            WorldSync.Baseline();   // later diffs are relative to what the snapshot holds
            byte[] gz = Wire.Gzip(Snapshot.ToBytes(xml));
            Transport.Send(peer, Wire.Build(Msg.Snapshot, w => w.WriteBytes(gz)));
            Log.Info($"snapshot for peer {peer}: {xml.Length / 1024} KB xml, {gz.Length / 1024} KB gzip, {(DateTime.UtcNow - t0).TotalMilliseconds:F0} ms");
        }

        private static void HostUpdate(float dt)
        {
            if (GameAccess.Game() == null) { Stop("host left the colony"); return; }
            var ready = ReadyPeers.ToList();
            if (ready.Count > 0)
            {
                WorldSync.Tick(dt, Transport, null, ready);
                Settings.HostTick(dt, Transport, ready);
            }
            foreach (var p in Peers.Values.ToList())
            {
                if (sClock - p.LastHeard > 30f)
                {
                    Log.Info("peer " + p.Id + " timed out");
                    Transport.Disconnect(p.Id, "timed out");
                }
            }
        }

        public static void Notice(string text)
        {
            Log.Info("notice: " + text);
            Ui.Toast(text);
            if (IsHost && Transport != null) Transport.Broadcast(Wire.Build(Msg.Chat, w => w.Write(text)));
        }

        // ---------------------------------------------------------------- client
        private static void ClientMessage(byte[] data)
        {
            sHostLastHeard = sClock;
            var r = Wire.Reader(data, out var type);
            if (Phase == ClientPhase.Loading && (type == Msg.Created || type == Msg.Destroyed || type == Msg.States || type == Msg.Globals || type == Msg.Settings || type == Msg.GameMessage))
            {
                sQueuedWhileLoading.Add(data);
                return;
            }
            Commands.ApplyingRemote = true;
            try { ClientDispatch(type, r); }
            finally { Commands.ApplyingRemote = false; }
        }

        private static void ClientDispatch(Msg type, BinaryReader r)
        {
            switch (type)
            {
                case Msg.Refuse:
                    LastRefusal = r.ReadString();
                    Status = "Refused: " + LastRefusal;
                    Log.Info("refused by host: " + LastRefusal);
                    Ui.Toast(LastRefusal, 15f);
                    TestBot.OnRefused(LastRefusal);
                    break;
                case Msg.Welcome:
                    MyPlayerId = r.ReadInt32();
                    string hostName = r.ReadString();
                    Status = "Welcome from " + hostName + ", receiving the colony";
                    break;
                case Msg.Snapshot:
                {
                    var xml = Snapshot.FromBytes(Wire.Gunzip(r.ReadBytes()));
                    string err = Snapshot.BeginLoad(xml);
                    if (err != null) { Stop(err); return; }
                    Phase = ClientPhase.Loading;
                    Status = "Loading the host's colony";
                    break;
                }
                case Msg.Created: WorldApply.OnCreated(r); break;
                case Msg.Destroyed: WorldApply.OnDestroyed(r); break;
                case Msg.States: WorldApply.OnStates(r); break;
                case Msg.Globals: Globals.Apply(r); break;
                case Msg.Settings: Settings.OnSettings(r); break;
                case Msg.GameMessage: Messages.OnMessage(r); break;
                case Msg.CommandResult: Commands.OnResult(r); break;
                case Msg.Chat: Ui.Toast(r.ReadString()); break;
                case Msg.Pong: break;
                case Msg.Bye: Stop("host: " + r.ReadString()); break;
                case Msg.TestProbe: TestBot.OnProbe(0, r); break;
            }
        }

        private static void ClientUpdate(float dt)
        {
            if (Phase == ClientPhase.Loading)
            {
                var game = GameAccess.Game();
                if (game != null && (Mirror.SkipLoadWait() || Mirror.IsUpdating()))
                {
                    Mirror.Begin();
                    Phase = ClientPhase.Mirroring;
                    Status = "In the host's colony";
                    foreach (var q in sQueuedWhileLoading) ClientMessage(q);
                    sQueuedWhileLoading.Clear();
                    Transport.Send(0, BuildReady());
                    sCensusTimer = 0f;
                }
                return;
            }
            if (Phase == ClientPhase.Mirroring)
            {
                if (GameAccess.Game() == null) { Stop("left the colony"); return; }
                Settings.ClientTick(dt);
                sCensusTimer += dt;
                if (sCensusTimer >= 5f && !TestEnv.Mut("no-census"))
                {
                    sCensusTimer = 0f;
                    Transport.Send(0, Census.Build());
                }
            }
            sPingTimer += dt;
            if (sPingTimer >= 2f)
            {
                sPingTimer = 0f;
                Transport.Send(0, Wire.Build(Msg.Ping, w => w.Write(sClock)));
            }
            if (Phase >= ClientPhase.Handshake && sClock - sHostLastHeard > 30f) Stop("host stopped answering");
        }

        private static byte[] BuildReady()
        {
            var census = Census.Build();
            census[0] = (byte)Msg.Ready;   // same body as a census
            return census;
        }

        public static void SendCommand(byte[] msg)
        {
            if (IsClient && Transport != null) Transport.Send(0, msg);
        }

        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "Player";
            s = s.Trim();
            if (s.Length > 24) s = s.Substring(0, 24);
            return s;
        }
    }
}
