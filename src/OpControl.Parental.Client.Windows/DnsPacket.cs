using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace OpControl.Parental.Client;

public sealed record DnsQuestion(string Name, ushort Type, ushort Class, int QuestionEnd);

public static class DnsPacket
{
    public const ushort TypeA = 1;
    public const ushort TypeCname = 5;
    public const ushort TypeAAAA = 28;
    public const ushort TypeHttps = 65;

    public static bool TryParseQuestion(ReadOnlySpan<byte> packet, out DnsQuestion question)
    {
        question = new DnsQuestion("", 0, 0, 0);
        if (packet.Length < 12) return false;
        var qdCount = BinaryPrimitives.ReadUInt16BigEndian(packet[4..6]);
        if (qdCount < 1) return false;

        var offset = 12;
        var name = ReadName(packet, ref offset);
        if (name is null || offset + 4 > packet.Length) return false;
        var type = BinaryPrimitives.ReadUInt16BigEndian(packet[offset..(offset + 2)]);
        var cls = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 2)..(offset + 4)]);
        question = new DnsQuestion(name, type, cls, offset + 4);
        return true;
    }

    public static byte[] BuildBlockedResponse(ReadOnlySpan<byte> query, DnsQuestion question)
    {
        var isAaaa = question.Type == TypeAAAA;
        var answerLength = isAaaa ? 16 : 4;
        var length = question.QuestionEnd + 12 + answerLength;
        var response = new byte[length];
        var span = response.AsSpan();

        query[..12].CopyTo(span);
        var flags = BinaryPrimitives.ReadUInt16BigEndian(span[2..4]);
        flags |= 0x8000;
        flags |= 0x0080;
        flags &= 0xF8F0;
        BinaryPrimitives.WriteUInt16BigEndian(span[2..4], flags);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..6], 1);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..8], 0);
        BinaryPrimitives.WriteUInt16BigEndian(span[8..10], 0);
        BinaryPrimitives.WriteUInt16BigEndian(span[10..12], 0);

        query[12..question.QuestionEnd].CopyTo(span[12..]);
        if (!IsAddressType(question.Type))
        {
            span[2] = 0x81;
            span[3] = (byte)((span[3] & 0xF0) | 3);
            return response[..question.QuestionEnd];
        }

        BinaryPrimitives.WriteUInt16BigEndian(span[6..8], 1);
        var offset = question.QuestionEnd;
        span[offset++] = 0xC0;
        span[offset++] = 0x0C;
        BinaryPrimitives.WriteUInt16BigEndian(span[offset..(offset + 2)], question.Type);
        offset += 2;
        BinaryPrimitives.WriteUInt16BigEndian(span[offset..(offset + 2)], 1);
        offset += 2;
        BinaryPrimitives.WriteUInt32BigEndian(span[offset..(offset + 4)], 60);
        offset += 4;
        BinaryPrimitives.WriteUInt16BigEndian(span[offset..(offset + 2)], (ushort)answerLength);
        offset += 2;
        if (!isAaaa)
        {
            span[offset++] = 0;
            span[offset++] = 0;
            span[offset++] = 0;
            span[offset++] = 0;
        }

        return response;
    }

    public static bool IsAddressType(ushort type) => type is TypeA or TypeAAAA;

    public static string? ReadName(ReadOnlySpan<byte> packet, ref int offset)
    {
        var labels = new List<string>();
        var position = offset;
        var jumped = false;
        var hops = 0;

        while (position < packet.Length && hops++ < 32)
        {
            var length = packet[position];
            if (length == 0)
            {
                position++;
                if (!jumped) offset = position;
                return string.Join('.', labels);
            }
            if ((length & 0xC0) == 0xC0)
            {
                if (position + 1 >= packet.Length) return null;
                var pointer = ((length & 0x3F) << 8) | packet[position + 1];
                if (!jumped) offset = position + 2;
                jumped = true;
                position = pointer;
                continue;
            }
            if ((length & 0xC0) != 0) return null;
            position++;
            if (position + length > packet.Length) return null;
            labels.Add(Encoding.ASCII.GetString(packet.Slice(position, length)));
            position += length;
            if (!jumped) offset = position;
        }

        return null;
    }

}
