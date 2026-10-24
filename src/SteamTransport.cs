using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using PlanetbaseCoop.Net;
using Steamworks;

namespace PlanetbaseCoop
{
    // Steam: a friends-only lobby for invites, ISteamNetworkingMessages for relayed traffic (no IPs,
    // no port forwarding). Uses the game's own Steamworks.NET (20.2.0) and its app id 403190, so it
    // needs both players logged in to Steam and owning Planetbase.
    // NOT tested between two real Steam accounts yet (one PC, one account).
    public class SteamTransport : ITransport
    {
        private const int Channel = 0;
        private const int SendFlags = Constants.k_nSteamNetworkingSend_Reliable | Constants.k_nSteamNetworkingSend_NoNagle;
        private const int Piece = 256 * 1024;          // under the 512 KB message limit
        private const byte Whole = 0, Part = 1, Last = 2;

        public static SteamTransport Current;
        private static Callback<GameLobbyJoinRequested_t> sJoinRequested;
        private static Callback<GameRichPresenceJoinRequested_t> sRichJoin;
        private static Callback<SteamNetworkingMessagesSessionRequest_t> sSessionRequest;
        private static Callback<LobbyChatUpdate_t> sLobbyChat;
        private static CallResult<LobbyCreated_t> sLobbyCreated;
        private static CallResult<LobbyEnter_t> sLobbyEnter;
        private static bool sCallbacksReady;
        private static bool sCheckedCommandLine;

        public bool IsHost { get; private set; }
        public string Describe => IsHost ? "Steam lobby" : "Steam, host " + mHostName;

        private CSteamID mLobby = CSteamID.Nil;
        private CSteamID mHost = CSteamID.Nil;
        private string mHostName = "?";
        private readonly Dictionary<ulong, int> mPeerOf = new Dictionary<ulong, int>();
        private readonly Dictionary<int, CSteamID> mIdOf = new Dictionary<int, CSteamID>();
        private readonly Dictionary<int, MemoryStream> mPartial = new Dictionary<int, MemoryStream>();
        private readonly List<NetEvent> mEvents = new List<NetEvent>();
        private readonly IntPtr[] mIn = new IntPtr[64];
        private int mNextPeer = 1;
        private bool mClosed;

        public static bool Available
        {
            get
            {
                if (TestEnv.IsTest) return false;
                try { return SteamManager.Initialized; } catch { return false; }
            }
        }

        public static bool Active => Current != null && !Current.mClosed;

        // ---------------------------------------------------------------- static entry points
        public static void Update()
        {
            if (!Available) return;
            if (!sCallbacksReady) InitCallbacks();
            if (!sCheckedCommandLine) CheckCommandLine();
            Current?.Pump();
        }

        private static void InitCallbacks()
        {
            sCallbacksReady = true;
            sJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(cb => JoinLobby(cb.m_steamIDLobby));
            sRichJoin = Callback<GameRichPresenceJoinRequested_t>.Create(cb => ParseConnect(cb.m_rgchConnect));
            sSessionRequest = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSessionRequest);
            sLobbyChat = Callback<LobbyChatUpdate_t>.Create(OnLobbyChat);
            sLobbyCreated = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
            sLobbyEnter = CallResult<LobbyEnter_t>.Create(OnLobbyEnter);
            Log.Info("Steam: callbacks ready, user " + SteamFriends.GetPersonaName());
            if (Session.PlayerName == "Player") Session.PlayerName = SteamFriends.GetPersonaName();
        }

        // Steam starts the game with "+connect_lobby <id>" when a friend clicks Join Game while it is closed.
        private static void CheckCommandLine()
        {
            var state = GameAccess.CurrentState();
            if (state == null || !state.isTitleState()) return;
            sCheckedCommandLine = true;
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "+connect_lobby" && ulong.TryParse(args[i + 1], out ulong id)) JoinLobby(new CSteamID(id));
        }

        private static void ParseConnect(string connect)
        {
            if (string.IsNullOrEmpty(connect)) return;
            var parts = connect.Split(' ');
            for (int i = 0; i < parts.Length - 1; i++)
                if (parts[i] == "+connect_lobby" && ulong.TryParse(parts[i + 1], out ulong id)) JoinLobby(new CSteamID(id));
        }

        public static string StartHosting()
        {
            if (!Available) return "Steam is not running";
            if (GameAccess.Game() == null) return "load or start a colony first";
            sLobbyCreated.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, 8));
            Session.Status = "Creating a Steam lobby";
            return null;
        }

        private static void OnLobbyCreated(LobbyCreated_t cb, bool ioFailure)
        {
            if (Active) return;   // real Steam can report the lobby twice
            if (ioFailure || cb.m_eResult != EResult.k_EResultOK) { Ui.Toast("Could not create a Steam lobby: " + cb.m_eResult); return; }
            var t = new SteamTransport { IsHost = true, mLobby = new CSteamID(cb.m_ulSteamIDLobby) };
            SteamMatchmaking.SetLobbyData(t.mLobby, "planetbase-coop", Plugin.Version);
            SteamMatchmaking.SetLobbyData(t.mLobby, "game", VersionInfo.Local.Game);
            SteamFriends.SetRichPresence("connect", "+connect_lobby " + cb.m_ulSteamIDLobby);
            SteamFriends.SetRichPresence("status", "Hosting a Planetbase co-op colony");
            string err = Session.StartHost(t);
            if (err != null) { t.Close(); Ui.Toast(err); return; }
            Current = t;
            SteamFriends.ActivateGameOverlayInviteDialog(t.mLobby);
        }

        private static void JoinLobby(CSteamID lobby)
        {
            if (Session.Active) { Ui.Toast("Leave your current session first (F8)."); return; }
            if (GameAccess.Game() != null)
            {
                Ui.Toast("To join a friend, go back to the main menu first, then accept the invite again.", 10f);
                return;
            }
            Session.Status = "Joining Steam lobby";
            sLobbyEnter.Set(SteamMatchmaking.JoinLobby(lobby));
        }

        private static void OnLobbyEnter(LobbyEnter_t cb, bool ioFailure)
        {
            if (ioFailure) { Ui.Toast("Could not join the Steam lobby."); return; }
            var lobby = new CSteamID(cb.m_ulSteamIDLobby);
            var owner = SteamMatchmaking.GetLobbyOwner(lobby);
            if (owner == SteamUser.GetSteamID()) return;   // our own lobby
            string theirVersion = SteamMatchmaking.GetLobbyData(lobby, "planetbase-coop");
            if (!string.IsNullOrEmpty(theirVersion) && theirVersion != Plugin.Version)
                Ui.Toast($"Your friend runs Planetbase Co-op {theirVersion}, you run {Plugin.Version}. The host will check and tell you.", 10f);
            var t = new SteamTransport { IsHost = false, mLobby = lobby, mHost = owner, mHostName = SteamFriends.GetFriendPersonaName(owner) };
            t.mPeerOf[owner.m_SteamID] = 0;
            t.mIdOf[0] = owner;
            string err = Session.StartClient(t);
            if (err != null) { t.Close(); Ui.Toast(err); return; }
            Current = t;
            t.mEvents.Add(new NetEvent { Type = NetEventType.Connected, Peer = 0 });   // Session sends Hello now
        }

        private static void OnSessionRequest(SteamNetworkingMessagesSessionRequest_t cb)
        {
            var who = cb.m_identityRemote.GetSteamID();
            var t = Current;
            if (t == null) return;
            // host: accept lobby members; joiner: only the host
            bool ok = t.IsHost ? IsLobbyMember(t.mLobby, who) : who == t.mHost;
            if (ok) SteamNetworkingMessages.AcceptSessionWithUser(ref cb.m_identityRemote);
            else Log.Info("Steam: ignored session request from a non-member");
        }

        private static bool IsLobbyMember(CSteamID lobby, CSteamID who)
        {
            int n = SteamMatchmaking.GetNumLobbyMembers(lobby);
            for (int i = 0; i < n; i++) if (SteamMatchmaking.GetLobbyMemberByIndex(lobby, i) == who) return true;
            return false;
        }

        private static void OnLobbyChat(LobbyChatUpdate_t cb)
        {
            var t = Current;
            if (t == null || cb.m_ulSteamIDLobby != t.mLobby.m_SteamID) return;
            const uint left = 2 | 4 | 8 | 16;   // left, disconnected, kicked, banned
            if ((cb.m_rgfChatMemberStateChange & left) == 0) return;
            if (t.mPeerOf.TryGetValue(cb.m_ulSteamIDUserChanged, out int peer))
            {
                if (t.IsHost) t.Drop(peer, "left the lobby");
                else if (peer == 0) t.mEvents.Add(new NetEvent { Type = NetEventType.Disconnected, Peer = 0, Reason = "host closed the lobby" });
            }
        }

        public static void OpenInviteDialog()
        {
            if (Current != null && Current.IsHost) SteamFriends.ActivateGameOverlayInviteDialog(Current.mLobby);
        }

        // ---------------------------------------------------------------- transport
        private void Pump()
        {
            if (mClosed) return;
            int n = SteamNetworkingMessages.ReceiveMessagesOnChannel(Channel, mIn, mIn.Length);
            for (int i = 0; i < n; i++)
            {
                var msg = SteamNetworkingMessage_t.FromIntPtr(mIn[i]);
                try
                {
                    var from = msg.m_identityPeer.GetSteamID();
                    var data = new byte[msg.m_cbSize];
                    Marshal.Copy(msg.m_pData, data, 0, msg.m_cbSize);
                    if (!mPeerOf.TryGetValue(from.m_SteamID, out int peer))
                    {
                        if (!IsHost) continue;   // joiners only talk to the host
                        peer = mNextPeer++;
                        mPeerOf[from.m_SteamID] = peer;
                        mIdOf[peer] = from;
                        mEvents.Add(new NetEvent { Type = NetEventType.Connected, Peer = peer });
                    }
                    Receive(peer, data);
                }
                finally
                {
                    SteamNetworkingMessage_t.Release(mIn[i]);
                }
            }
        }

        private void Receive(int peer, byte[] data)
        {
            if (data.Length == 0) return;
            byte kind = data[0];
            if (kind == Whole)
            {
                var body = new byte[data.Length - 1];
                Buffer.BlockCopy(data, 1, body, 0, body.Length);
                mEvents.Add(new NetEvent { Type = NetEventType.Data, Peer = peer, Data = body });
                return;
            }
            if (!mPartial.TryGetValue(peer, out var ms)) mPartial[peer] = ms = new MemoryStream();
            ms.Write(data, 1, data.Length - 1);
            if (ms.Length > Wire.MaxMessage) { mPartial.Remove(peer); Drop(peer, "message too large"); return; }
            if (kind == Last)
            {
                mPartial.Remove(peer);
                mEvents.Add(new NetEvent { Type = NetEventType.Data, Peer = peer, Data = ms.ToArray() });
            }
        }

        public void Send(int peer, byte[] message)
        {
            if (mClosed || !mIdOf.TryGetValue(peer, out var id)) return;
            var ident = new SteamNetworkingIdentity();
            ident.SetSteamID(id);
            if (message.Length < Piece) { SendRaw(ref ident, Whole, message, 0, message.Length); return; }
            for (int off = 0; off < message.Length; off += Piece)
            {
                int len = Math.Min(Piece, message.Length - off);
                SendRaw(ref ident, off + len >= message.Length ? Last : Part, message, off, len);
            }
        }

        private void SendRaw(ref SteamNetworkingIdentity ident, byte kind, byte[] src, int off, int len)
        {
            var buf = new byte[len + 1];
            buf[0] = kind;
            Buffer.BlockCopy(src, off, buf, 1, len);
            var h = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                for (int attempt = 0; attempt < 50; attempt++)
                {
                    var res = SteamNetworkingMessages.SendMessageToUser(ref ident, h.AddrOfPinnedObject(), (uint)buf.Length, SendFlags, Channel);
                    if (res == EResult.k_EResultOK) return;
                    if (res != EResult.k_EResultLimitExceeded) { Log.Warn("Steam send failed: " + res); return; }
                    System.Threading.Thread.Sleep(5);   // send buffer full
                }
            }
            finally { h.Free(); }
        }

        public void Broadcast(byte[] message)
        {
            foreach (var peer in new List<int>(mIdOf.Keys)) Send(peer, message);
        }

        public void Disconnect(int peer, string reason) => Drop(peer, reason);

        private void Drop(int peer, string reason)
        {
            if (!mIdOf.TryGetValue(peer, out var id)) return;
            var ident = new SteamNetworkingIdentity();
            ident.SetSteamID(id);
            SteamNetworkingMessages.CloseSessionWithUser(ref ident);
            mIdOf.Remove(peer);
            mPeerOf.Remove(id.m_SteamID);
            mEvents.Add(new NetEvent { Type = NetEventType.Disconnected, Peer = peer, Reason = reason });
        }

        public void Poll(List<NetEvent> into)
        {
            into.AddRange(mEvents);
            mEvents.Clear();
        }

        public void Close()
        {
            if (mClosed) return;
            mClosed = true;
            foreach (var peer in new List<int>(mIdOf.Keys))
            {
                var ident = new SteamNetworkingIdentity();
                ident.SetSteamID(mIdOf[peer]);
                SteamNetworkingMessages.CloseSessionWithUser(ref ident);
            }
            if (mLobby != CSteamID.Nil) SteamMatchmaking.LeaveLobby(mLobby);
            SteamFriends.ClearRichPresence();
            if (Current == this) Current = null;
        }
    }
}
