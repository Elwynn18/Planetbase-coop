using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace PlanetbaseCoop.Net
{
    // Message types. The first byte of every message. Never reuse a number.
    public enum Msg : byte
    {
        Hello = 1,          // client -> host: versions, name
        Refuse = 2,         // host -> client: readable reason, then close
        Welcome = 3,        // host -> client: player id
        Snapshot = 4,       // host -> client: planet, challenge, gzip XML save
        Ready = 5,          // client -> host: snapshot loaded, census follows
        Census = 6,         // client -> host: ids per kind
        Created = 7,        // host -> client: kind + object XML
        Destroyed = 8,      // host -> client: kind + id
        States = 9,         // host -> client: batch of state records
        Globals = 10,       // host -> client: speed, pause, alert, time of day, colony stats
        Command = 11,       // client -> host: a player action
        CommandResult = 12, // host -> client: ok / refused + message
        Ping = 13,
        Pong = 14,
        Bye = 15,
        Chat = 16,          // join/leave notices and chat lines
        TestProbe = 17,     // test bots only: world digests exchanged between bots
        Settings = 18,      // host -> client: landing permissions, manufacture limits
        GameMessage = 19,   // host -> client: a notification added to the host's message log
    }

    public static class Wire
    {
        public const int ProtocolVersion = 1;
        public const int MaxMessage = 64 * 1024 * 1024;

        public static byte[] Build(Msg type, Action<BinaryWriter> body)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write((byte)type);
                body?.Invoke(w);
                w.Flush();
                return ms.ToArray();
            }
        }

        public static BinaryReader Reader(byte[] data, out Msg type)
        {
            var r = new BinaryReader(new MemoryStream(data, false), Encoding.UTF8);
            type = (Msg)r.ReadByte();
            return r;
        }

        public static byte[] Gzip(byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, CompressionMode.Compress, true))
                    gz.Write(data, 0, data.Length);
                return ms.ToArray();
            }
        }

        public static byte[] Gunzip(byte[] data)
        {
            using (var input = new MemoryStream(data))
            using (var gz = new GZipStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                gz.CopyTo(output);
                if (output.Length > MaxMessage * 4L) throw new InvalidDataException("snapshot too large");
                return output.ToArray();
            }
        }

        public static void WriteBytes(this BinaryWriter w, byte[] b)
        {
            w.Write(b.Length);
            w.Write(b);
        }

        public static byte[] ReadBytes(this BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > MaxMessage) throw new InvalidDataException("bad length " + n);
            return r.ReadBytes(n);
        }

        public static void WriteVec(this BinaryWriter w, UnityEngine.Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        public static UnityEngine.Vector3 ReadVec(this BinaryReader r) => new UnityEngine.Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        public static void WriteQuat(this BinaryWriter w, UnityEngine.Quaternion q) { w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w); }
        public static UnityEngine.Quaternion ReadQuat(this BinaryReader r) => new UnityEngine.Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    }
}
