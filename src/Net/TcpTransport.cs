using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace PlanetbaseCoop.Net
{
    // Length-prefixed messages over TCP. One reader thread per connection, sends are
    // queued and written by one writer thread per connection so the main thread never blocks.
    public class TcpTransport : ITransport
    {
        private class Conn
        {
            public int Id;
            public TcpClient Client;
            public NetworkStream Stream;
            public readonly BlockingCollection<byte[]> Outgoing = new BlockingCollection<byte[]>();
            public volatile bool Closed;
        }

        private readonly ConcurrentQueue<NetEvent> mEvents = new ConcurrentQueue<NetEvent>();
        private readonly Dictionary<int, Conn> mConns = new Dictionary<int, Conn>();
        private readonly object mLock = new object();
        private TcpListener mListener;
        private Thread mAcceptThread;
        private int mNextPeer = 1;
        private volatile bool mClosed;

        public bool IsHost { get; private set; }
        public string Describe { get; private set; }

        public static TcpTransport Host(int port)
        {
            var t = new TcpTransport { IsHost = true, Describe = "TCP host on port " + port };
            t.mListener = new TcpListener(IPAddress.Any, port);
            t.mListener.Start();
            t.mAcceptThread = new Thread(t.AcceptLoop) { IsBackground = true, Name = "coop-accept" };
            t.mAcceptThread.Start();
            return t;
        }

        public static TcpTransport Connect(string host, int port)
        {
            var t = new TcpTransport { IsHost = false, Describe = "TCP to " + host + ":" + port };
            var th = new Thread(() =>
            {
                try
                {
                    var c = new TcpClient();
                    c.NoDelay = true;
                    c.Connect(host, port);
                    t.Add(c, 0);
                }
                catch (Exception e)
                {
                    t.mEvents.Enqueue(new NetEvent { Type = NetEventType.Disconnected, Peer = 0, Reason = "could not connect: " + e.Message });
                }
            }) { IsBackground = true, Name = "coop-connect" };
            th.Start();
            return t;
        }

        private void AcceptLoop()
        {
            while (!mClosed)
            {
                try
                {
                    var c = mListener.AcceptTcpClient();
                    c.NoDelay = true;
                    int id;
                    lock (mLock) id = mNextPeer++;
                    Add(c, id);
                }
                catch (Exception)
                {
                    if (mClosed) return;
                    Thread.Sleep(100);
                }
            }
        }

        private void Add(TcpClient client, int id)
        {
            var conn = new Conn { Id = id, Client = client, Stream = client.GetStream() };
            lock (mLock) mConns[id] = conn;
            mEvents.Enqueue(new NetEvent { Type = NetEventType.Connected, Peer = id });
            new Thread(() => ReadLoop(conn)) { IsBackground = true, Name = "coop-read-" + id }.Start();
            new Thread(() => WriteLoop(conn)) { IsBackground = true, Name = "coop-write-" + id }.Start();
        }

        private void ReadLoop(Conn conn)
        {
            string reason = "connection closed";
            try
            {
                var header = new byte[4];
                while (!conn.Closed)
                {
                    ReadExact(conn.Stream, header, 4);
                    int len = BitConverter.ToInt32(header, 0);
                    if (len <= 0 || len > Wire.MaxMessage) throw new InvalidDataException("bad frame length " + len);
                    var data = new byte[len];
                    ReadExact(conn.Stream, data, len);
                    mEvents.Enqueue(new NetEvent { Type = NetEventType.Data, Peer = conn.Id, Data = data });
                }
            }
            catch (Exception e)
            {
                if (!conn.Closed) reason = e is EndOfStreamException ? "connection closed" : e.Message;
            }
            Drop(conn, reason);
        }

        private static void ReadExact(Stream s, byte[] buf, int len)
        {
            int got = 0;
            while (got < len)
            {
                int n = s.Read(buf, got, len - got);
                if (n <= 0) throw new EndOfStreamException();
                got += n;
            }
        }

        private void WriteLoop(Conn conn)
        {
            try
            {
                foreach (var msg in conn.Outgoing.GetConsumingEnumerable())
                {
                    var len = BitConverter.GetBytes(msg.Length);
                    conn.Stream.Write(len, 0, 4);
                    conn.Stream.Write(msg, 0, msg.Length);
                }
            }
            catch (Exception e)
            {
                Drop(conn, e.Message);
            }
        }

        private void Drop(Conn conn, string reason)
        {
            lock (mLock)
            {
                if (conn.Closed) return;
                conn.Closed = true;
                mConns.Remove(conn.Id);
            }
            try { conn.Outgoing.CompleteAdding(); } catch { }
            try { conn.Client.Close(); } catch { }
            mEvents.Enqueue(new NetEvent { Type = NetEventType.Disconnected, Peer = conn.Id, Reason = reason });
        }

        public void Send(int peer, byte[] message)
        {
            Conn c;
            lock (mLock) mConns.TryGetValue(peer, out c);
            if (c == null || c.Closed) return;
            try { c.Outgoing.Add(message); } catch (InvalidOperationException) { }
        }

        public void Broadcast(byte[] message)
        {
            List<Conn> all;
            lock (mLock) all = new List<Conn>(mConns.Values);
            foreach (var c in all)
            {
                try { if (!c.Closed) c.Outgoing.Add(message); } catch (InvalidOperationException) { }
            }
        }

        public void Disconnect(int peer, string reason)
        {
            Conn c;
            lock (mLock) mConns.TryGetValue(peer, out c);
            if (c == null) return;
            // let queued messages (a Refuse, a Bye) go out first
            new Thread(() =>
            {
                for (int i = 0; i < 50 && c.Outgoing.Count > 0; i++) Thread.Sleep(20);
                Drop(c, reason);
            }) { IsBackground = true }.Start();
        }

        public void Poll(List<NetEvent> into)
        {
            while (mEvents.TryDequeue(out var e)) into.Add(e);
        }

        public void Close()
        {
            mClosed = true;
            try { mListener?.Stop(); } catch { }
            List<Conn> all;
            lock (mLock) all = new List<Conn>(mConns.Values);
            foreach (var c in all) Drop(c, "closed");
        }
    }
}
