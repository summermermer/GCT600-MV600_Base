using System;
using System.IO;
using System.Text;

namespace GCT600.AvatarNetworking
{
    // Transport framing is big endian; the MAV1 body uses BinaryWriter's little endian.
    public static class MetaAvatarNetworkProtocol
    {
        public const int MaxPacketBytes = 1024 * 1024;
        public const int HeaderBytes = 44;

        public sealed class PosePacket
        {
            public uint Sequence;
            public double Timestamp;
            public float[] Root; // px, py, pz, qx, qy, qz, qw
            public byte[] AvatarData;
        }

        public static byte[] Encode(uint sequence, double timestamp, float[] root, byte[] avatarData)
        {
            if (root == null || root.Length != 7 || avatarData == null || avatarData.Length == 0 ||
                avatarData.Length > MaxPacketBytes - HeaderBytes)
                throw new InvalidDataException("Invalid avatar packet");
            using (var memory = new MemoryStream(HeaderBytes + avatarData.Length))
            using (var writer = new BinaryWriter(memory, Encoding.UTF8, true))
            {
                writer.Write(new byte[] { 77, 65, 86, 49 }); // MAV1
                writer.Write(sequence);
                writer.Write(timestamp);
                foreach (float value in root) writer.Write(value);
                writer.Write(avatarData);
                byte[] packet = memory.ToArray();
                ValidateHeader(packet);
                return packet;
            }
        }

        public static PosePacket Decode(byte[] bytes)
        {
            ValidateHeader(bytes);
            using (var memory = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(memory))
            {
                reader.ReadBytes(4);
                var packet = new PosePacket { Sequence = reader.ReadUInt32(), Timestamp = reader.ReadDouble(), Root = new float[7] };
                for (int i = 0; i < packet.Root.Length; i++) packet.Root[i] = reader.ReadSingle();
                packet.AvatarData = reader.ReadBytes(bytes.Length - HeaderBytes);
                return packet;
            }
        }

        private static void ValidateHeader(byte[] bytes)
        {
            if (bytes == null || bytes.Length <= HeaderBytes || bytes.Length > MaxPacketBytes ||
                bytes[0] != 77 || bytes[1] != 65 || bytes[2] != 86 || bytes[3] != 49)
                throw new InvalidDataException("Invalid MAV1 header");
            using (var reader = new BinaryReader(new MemoryStream(bytes, false)))
            {
                reader.ReadBytes(8);
                double stamp = reader.ReadDouble();
                if (double.IsNaN(stamp) || double.IsInfinity(stamp)) throw new InvalidDataException("Invalid timestamp");
                float norm = 0;
                for (int i = 0; i < 7; i++)
                {
                    float value = reader.ReadSingle();
                    if (float.IsNaN(value) || float.IsInfinity(value)) throw new InvalidDataException("Invalid root pose");
                    if (i >= 3) norm += value * value;
                }
                if (norm <= 0.5f || norm >= 1.5f) throw new InvalidDataException("Invalid root quaternion");
            }
        }

        public static byte[] ReadFrame(Stream stream, int maximum = MaxPacketBytes)
        {
            byte[] header = ReadExactly(stream, 4);
            uint size = ((uint)header[0] << 24) | ((uint)header[1] << 16) | ((uint)header[2] << 8) | header[3];
            if (size == 0 || size > maximum) throw new InvalidDataException("Invalid frame size: " + size);
            return ReadExactly(stream, (int)size);
        }

        private static byte[] ReadExactly(Stream stream, int count)
        {
            var result = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int received = stream.Read(result, offset, count - offset);
                if (received == 0) throw new EndOfStreamException("Relay disconnected");
                offset += received;
            }
            return result;
        }

        public static void WriteFrame(Stream stream, byte[] body)
        {
            if (body == null || body.Length == 0 || body.Length > MaxPacketBytes)
                throw new InvalidDataException("Invalid outgoing frame size");
            int n = body.Length;
            var header = new[] { (byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n };
            stream.Write(header, 0, header.Length);
            stream.Write(body, 0, body.Length);
        }
    }
}
