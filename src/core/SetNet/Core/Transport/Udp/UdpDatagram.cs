using System;

namespace SetNet.Core.Transport.Udp
{
    /// <summary>
    /// Build/parse helpers for the UDP wire format. All multi-byte integers are little-endian
    /// (both ends are this library, so this is self-consistent).
    /// </summary>
    /// <remarks>
    /// This is the single place that knows the on-wire byte layout of each <see cref="PacketKind"/>.
    /// Every <c>Build*</c>/<c>Write*</c> method produces a datagram body and every <c>TryParse*</c>
    /// method validates the minimum length before decoding, returning <c>false</c> on a malformed or
    /// truncated frame rather than throwing — callers can safely ignore bad datagrams.
    /// </remarks>
    internal static class UdpDatagram
    {
        // ── Handshake / Disconnect: [kind][16-byte token] ─────────────────────

        /// <summary>
        /// Builds a token-carrying control datagram (handshake, handshake-ack, or disconnect).
        /// Exists so the connection-setup and teardown paths can frame a session token uniformly.
        /// </summary>
        /// <param name="kind">The <see cref="PacketKind"/> byte to place first (e.g. <see cref="PacketKind.Handshake"/> or <see cref="PacketKind.Disconnect"/>).</param>
        /// <param name="token">The 16-byte session token identifying the UDP flow.</param>
        /// <returns>A newly allocated 17-byte datagram: the kind byte followed by the token's raw bytes.</returns>
        public static byte[] BuildToken(byte kind, Guid token)
        {
            var dg = new byte[1 + 16];
            dg[0] = kind;
            token.ToByteArray().CopyTo(dg, 1);
            return dg;
        }

        /// <summary>
        /// Extracts the 16-byte session token from a token-carrying control datagram.
        /// Used by handshake and disconnect handling to recover which flow a control frame refers to.
        /// </summary>
        /// <param name="dg">The received datagram, expected to be <c>[kind][16-byte token]</c>.</param>
        /// <param name="token">On success, the decoded token; otherwise <see cref="Guid.Empty"/>.</param>
        /// <returns><c>true</c> if the datagram was long enough to contain a token; <c>false</c> if truncated.</returns>
        public static bool TryParseToken(byte[] dg, out Guid token)
        {
            token = Guid.Empty;
            if (dg.Length < 1 + 16) return false;
            var bytes = new byte[16];
            Array.Copy(dg, 1, bytes, 0, 16);
            token = new Guid(bytes);
            return true;
        }

        // ── Unreliable: [0x10][2-byte type][payload] ──────────────────────────

        /// <summary>Number of header bytes preceding the payload of an unreliable datagram: one kind byte plus a two-byte little-endian message type.</summary>
        public const int UnreliableHeader = 3;

        /// <summary>
        /// Builds a complete unreliable application datagram into a freshly allocated buffer.
        /// Convenience wrapper over <see cref="WriteUnreliable"/> for callers that do not pool buffers.
        /// </summary>
        /// <param name="type">The application-defined message type identifier.</param>
        /// <param name="payload">The serialized message body to send.</param>
        /// <returns>A datagram of length <see cref="UnreliableHeader"/> + <paramref name="payload"/>.Length.</returns>
        public static byte[] BuildUnreliable(ushort type, byte[] payload)
        {
            var dg = new byte[UnreliableHeader + payload.Length];
            WriteUnreliable(dg, type, payload);
            return dg;
        }

        /// <summary>Frame an unreliable datagram into <paramref name="dest"/> (may be pooled/oversized); returns total length.</summary>
        /// <param name="dest">Destination buffer; must hold at least <see cref="UnreliableHeader"/> + <paramref name="payload"/>.Length bytes. May be larger (e.g. rented from an <c>ArrayPool</c>).</param>
        /// <param name="type">The application-defined message type identifier, written little-endian.</param>
        /// <param name="payload">The serialized message body to copy after the header.</param>
        /// <returns>The total number of bytes written, i.e. the actual datagram length to send.</returns>
        public static int WriteUnreliable(byte[] dest, ushort type, byte[] payload)
        {
            dest[0] = PacketKind.Unreliable;
            dest[1] = (byte)(type & 0xFF);
            dest[2] = (byte)(type >> 8);
            Buffer.BlockCopy(payload, 0, dest, UnreliableHeader, payload.Length);
            return UnreliableHeader + payload.Length;
        }

        /// <summary>
        /// Decodes an unreliable application datagram back into its message type and payload.
        /// Called on the receive path to turn raw bytes into a dispatchable message.
        /// </summary>
        /// <param name="dg">The received datagram, expected to be <c>[0x10][2-byte type][payload]</c>.</param>
        /// <param name="type">On success, the little-endian message type; otherwise <c>0</c>.</param>
        /// <param name="payload">On success, a newly allocated copy of the payload bytes; otherwise an empty array.</param>
        /// <returns><c>true</c> if the datagram contained at least a full header; <c>false</c> if truncated.</returns>
        public static bool TryParseUnreliable(byte[] dg, out ushort type, out byte[] payload)
        {
            type = 0; payload = Array.Empty<byte>();
            if (dg.Length < 3) return false;
            type = (ushort)(dg[1] | (dg[2] << 8));
            payload = new byte[dg.Length - 3];
            Array.Copy(dg, 3, payload, 0, payload.Length);
            return true;
        }

        // ── Reliable: [0x20][channel:1][2-byte seq][2-byte type][payload] ─────

        /// <summary>Returns the channel byte of a reliable/ack datagram, or 0 if too short to carry one.</summary>
        /// <param name="dg">The received datagram.</param>
        /// <returns>The channel id at offset 1, or 0 when the datagram is shorter than two bytes.</returns>
        public static byte GetChannel(byte[] dg) => dg.Length >= 2 ? dg[1] : (byte)0;

        /// <summary>
        /// Builds a complete reliable application datagram on a given channel. Carries the channel id and a
        /// per-channel sequence number so the reliability layer can ack and detect duplicates/gaps independently
        /// of other channels.
        /// </summary>
        /// <param name="channel">The reliable channel id this message belongs to.</param>
        /// <param name="seq">The per-channel sequence number assigned to this message, written little-endian.</param>
        /// <param name="type">The application-defined message type identifier, written little-endian.</param>
        /// <param name="payload">The serialized message body to append after the header.</param>
        /// <returns>A newly allocated datagram of length 6 + <paramref name="payload"/>.Length.</returns>
        public static byte[] BuildReliable(byte channel, ushort seq, ushort type, byte[] payload)
        {
            var dg = new byte[1 + 1 + 2 + 2 + payload.Length];
            dg[0] = PacketKind.Reliable;
            dg[1] = channel;
            dg[2] = (byte)(seq & 0xFF);
            dg[3] = (byte)(seq >> 8);
            dg[4] = (byte)(type & 0xFF);
            dg[5] = (byte)(type >> 8);
            Array.Copy(payload, 0, dg, 6, payload.Length);
            return dg;
        }

        /// <summary>
        /// Decodes a reliable application datagram into its channel, sequence number, message type, and payload.
        /// </summary>
        /// <param name="dg">The received datagram, expected to be <c>[0x20][channel][2-byte seq][2-byte type][payload]</c>.</param>
        /// <param name="channel">On success, the channel id; otherwise <c>0</c>.</param>
        /// <param name="seq">On success, the little-endian sequence number; otherwise <c>0</c>.</param>
        /// <param name="type">On success, the little-endian message type; otherwise <c>0</c>.</param>
        /// <param name="payload">On success, a newly allocated copy of the payload bytes; otherwise an empty array.</param>
        /// <returns><c>true</c> if the datagram contained at least a full reliable header; <c>false</c> if truncated.</returns>
        public static bool TryParseReliable(byte[] dg, out byte channel, out ushort seq, out ushort type, out byte[] payload)
        {
            channel = 0; seq = 0; type = 0; payload = Array.Empty<byte>();
            if (dg.Length < 6) return false;
            channel = dg[1];
            seq = (ushort)(dg[2] | (dg[3] << 8));
            type = (ushort)(dg[4] | (dg[5] << 8));
            payload = new byte[dg.Length - 6];
            Array.Copy(dg, 6, payload, 0, payload.Length);
            return true;
        }

        // ── Ack: [0x21][channel:1][2-byte ackSeq][8-byte bitfield] ────────────

        /// <summary>Fixed total size in bytes of an ack datagram: kind, channel, two-byte ack sequence, eight-byte bitfield.</summary>
        public const int AckSize = 1 + 1 + 2 + 8;

        /// <summary>
        /// Frames an acknowledgement datagram for a channel into <paramref name="dest"/>. Communicates the highest
        /// received reliable sequence on that channel plus a bitfield of the 64 preceding sequences.
        /// </summary>
        /// <param name="dest">Destination buffer; must hold at least <see cref="AckSize"/> bytes (may be pooled/oversized).</param>
        /// <param name="channel">The reliable channel this ack applies to.</param>
        /// <param name="ackSeq">The most recent reliable sequence number received, written little-endian.</param>
        /// <param name="bitfield">Bit <c>i</c> set means the sequence <c>ackSeq - (i + 1)</c> was also received; written little-endian.</param>
        /// <returns>The number of bytes written, always <see cref="AckSize"/>.</returns>
        public static int WriteAck(byte[] dest, byte channel, ushort ackSeq, ulong bitfield)
        {
            dest[0] = PacketKind.Ack;
            dest[1] = channel;
            dest[2] = (byte)(ackSeq & 0xFF);
            dest[3] = (byte)(ackSeq >> 8);
            for (int i = 0; i < 8; i++)
                dest[4 + i] = (byte)(bitfield >> (8 * i));
            return AckSize;
        }

        /// <summary>
        /// Decodes an acknowledgement datagram into its ack sequence and history bitfield.
        /// Called when an ack is received so the sender can clear acknowledged messages from its
        /// retransmit buffer.
        /// </summary>
        /// <param name="dg">The received datagram, expected to be <c>[0x21][channel][2-byte ackSeq][8-byte bitfield]</c>.</param>
        /// <param name="channel">On success, the channel this ack applies to; otherwise <c>0</c>.</param>
        /// <param name="ackSeq">On success, the highest acknowledged sequence number; otherwise <c>0</c>.</param>
        /// <param name="bitfield">On success, the 64-bit history of preceding acked sequences (see <see cref="WriteAck"/>); otherwise <c>0</c>.</param>
        /// <returns><c>true</c> if the datagram was a full ack frame (at least <see cref="AckSize"/> bytes); <c>false</c> if truncated.</returns>
        public static bool TryParseAck(byte[] dg, out byte channel, out ushort ackSeq, out ulong bitfield)
        {
            channel = 0; ackSeq = 0; bitfield = 0;
            if (dg.Length < AckSize) return false;
            channel = dg[1];
            ackSeq = (ushort)(dg[2] | (dg[3] << 8));
            for (int i = 0; i < 8; i++)
                bitfield |= (ulong)dg[4 + i] << (8 * i);
            return true;
        }
    }
}
