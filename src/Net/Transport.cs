using System.Collections.Generic;

namespace PlanetbaseCoop.Net
{
    public enum NetEventType { Connected, Data, Disconnected }

    public struct NetEvent
    {
        public NetEventType Type;
        public int Peer;          // host: peer id per client; client: always 0 (the host)
        public byte[] Data;
        public string Reason;
    }

    // A transport carries whole messages (any size) reliably and in order to a peer.
    // TCP (direct IP) and Steam (relayed P2P) implement it.
    public interface ITransport
    {
        string Describe { get; }
        bool IsHost { get; }
        void Send(int peer, byte[] message);
        void Broadcast(byte[] message);
        void Disconnect(int peer, string reason);
        // Main thread: drain events received since the last call.
        void Poll(List<NetEvent> into);
        void Close();
    }
}
