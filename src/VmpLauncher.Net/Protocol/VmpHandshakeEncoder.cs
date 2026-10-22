using System.Buffers.Binary;
using System.Text;

namespace VmpLauncher.Net.Protocol;

/// <summary>Fields of LmpCommon's <c>HandshakeRequestMsgData</c> (plus the <c>MessageData</c> header).</summary>
public sealed record HandshakeRequest(string PlayerName, string UniqueIdentifier, string ProtocolForkId, string ExactClientBuild)
{
    public long SentTime { get; init; }
    public ushort MajorVersion { get; init; }
    public ushort MinorVersion { get; init; }
    public ushort BuildVersion { get; init; }
}

/// <summary>LmpCommon <c>HandshakeReply</c> values.</summary>
public enum HandshakeReplyCode
{
    HandshookSuccessfully = 0,
    PlayerBanned = 1,
    ServerFull = 2,
    InvalidPlayername = 3,
    ProtocolForkMismatch = 4,
    ProtocolBuildMismatch = 5,
}

/// <summary>
/// Hand-written equivalent of <c>HandshakeCliMsg.Serialize</c> (LmpCommon\Message\Base\MessageBase.cs + MessageData.cs +
/// HandshakeRequestMsgData.cs). Every field is byte aligned so Lidgren's bit writer reduces to little-endian bytes,
/// and strings are a 7-bit varint byte count followed by UTF-8. Verified against golden bytes from the real classes.
/// </summary>
public static class VmpHandshakeEncoder
{
    public const ushort ClientHandshakeMessageType = 0; // ClientMessageType.Handshake
    public const ushort ServerHandshakeMessageType = 0; // ServerMessageType.Handshake
    public const ushort RequestSubType = 0;             // HandshakeMessageType.Request
    public const ushort ReplySubType = 1;               // HandshakeMessageType.Reply
    public const int Channel = 1;                       // HandshakeCliMsg.DefaultChannel (ReliableOrdered)

    public static byte[] EncodeRequest(HandshakeRequest r)
    {
        var w = new List<byte>(96);
        U16(w, ClientHandshakeMessageType);
        U16(w, RequestSubType);
        Span<byte> l = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(l, r.SentTime);
        w.AddRange(l.ToArray());
        U16(w, r.MajorVersion);
        U16(w, r.MinorVersion);
        U16(w, r.BuildVersion);
        Str(w, r.PlayerName);
        Str(w, r.UniqueIdentifier);
        Str(w, r.ProtocolForkId);
        Str(w, r.ExactClientBuild);
        return w.ToArray();
    }

    /// <summary>Decodes <c>HandshakeSrvMsg</c>/<c>HandshakeReplyMsgData</c> up to Reason; null if it is another message.</summary>
    public static (HandshakeReplyCode Response, string Reason)? TryDecodeReply(ReadOnlySpan<byte> data)
    {
        try
        {
            if (data.Length < 18 || BinaryPrimitives.ReadUInt16LittleEndian(data) != ServerHandshakeMessageType
                || BinaryPrimitives.ReadUInt16LittleEndian(data[2..]) != ReplySubType)
            {
                return null;
            }
            var pos = 4 + 8 + 6; // type, subtype, SentTime, Major/Minor/Build
            var response = (HandshakeReplyCode)BinaryPrimitives.ReadInt32LittleEndian(data[pos..]);
            pos += 4;
            uint len = 0;
            for (var shift = 0; ; shift += 7)
            {
                var b = data[pos++];
                len |= (uint)(b & 0x7f) << shift;
                if ((b & 0x80) == 0) break;
                if (shift > 28) return null;
            }
            return (response, Encoding.UTF8.GetString(data.Slice(pos, checked((int)len))));
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static void U16(List<byte> w, ushort v)
    {
        w.Add((byte)v);
        w.Add((byte)(v >> 8));
    }

    private static void Str(List<byte> w, string? s)
    {
        var bytes = Encoding.UTF8.GetBytes(s ?? string.Empty);
        for (var v = (uint)bytes.Length; ; v >>= 7)
        {
            if (v < 0x80) { w.Add((byte)v); break; }
            w.Add((byte)(v | 0x80));
        }
        w.AddRange(bytes);
    }
}
