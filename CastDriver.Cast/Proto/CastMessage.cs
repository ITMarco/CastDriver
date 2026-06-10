using Google.Protobuf;

namespace CastDriver.Cast.Proto;

// Mirrors the Cast V2 cast_channel.proto CastMessage definition.
// Hand-written to avoid a protoc build step.
internal sealed class CastMessage
{
    public string SourceId      { get; set; } = "";
    public string DestinationId { get; set; } = "";
    public string Namespace     { get; set; } = "";
    public string? PayloadUtf8  { get; set; }
    public byte[]? PayloadBinary { get; set; }

    // Computed tags: (field_number << 3) | wire_type  (0=varint, 2=length-delimited)
    private const uint TagProtocolVersion = (1u << 3) | 0u;
    private const uint TagSourceId        = (2u << 3) | 2u;
    private const uint TagDestinationId   = (3u << 3) | 2u;
    private const uint TagNamespace       = (4u << 3) | 2u;
    private const uint TagPayloadType     = (5u << 3) | 0u;
    private const uint TagPayloadUtf8     = (6u << 3) | 2u;
    private const uint TagPayloadBinary   = (7u << 3) | 2u;

    public byte[] ToByteArray()
    {
        using var ms  = new MemoryStream();
        using var cos = new CodedOutputStream(ms);

        cos.WriteTag(1, WireFormat.WireType.Varint);
        cos.WriteInt32(0); // CASTV2_1_0

        cos.WriteTag(2, WireFormat.WireType.LengthDelimited);
        cos.WriteString(SourceId);

        cos.WriteTag(3, WireFormat.WireType.LengthDelimited);
        cos.WriteString(DestinationId);

        cos.WriteTag(4, WireFormat.WireType.LengthDelimited);
        cos.WriteString(Namespace);

        if (PayloadUtf8 != null)
        {
            cos.WriteTag(5, WireFormat.WireType.Varint);
            cos.WriteInt32(0); // STRING
            cos.WriteTag(6, WireFormat.WireType.LengthDelimited);
            cos.WriteString(PayloadUtf8);
        }
        else if (PayloadBinary != null)
        {
            cos.WriteTag(5, WireFormat.WireType.Varint);
            cos.WriteInt32(1); // BINARY
            cos.WriteTag(7, WireFormat.WireType.LengthDelimited);
            cos.WriteBytes(ByteString.CopyFrom(PayloadBinary));
        }

        cos.Flush();
        return ms.ToArray();
    }

    public static CastMessage FromByteArray(byte[] data)
    {
        var msg = new CastMessage();
        var cis = new CodedInputStream(data);

        while (!cis.IsAtEnd)
        {
            var tag = cis.ReadTag();
            switch (tag)
            {
                case TagProtocolVersion: cis.ReadInt32();   break;
                case TagSourceId:        msg.SourceId        = cis.ReadString(); break;
                case TagDestinationId:   msg.DestinationId   = cis.ReadString(); break;
                case TagNamespace:       msg.Namespace       = cis.ReadString(); break;
                case TagPayloadType:     cis.ReadInt32();    break; // infer from field presence
                case TagPayloadUtf8:     msg.PayloadUtf8     = cis.ReadString(); break;
                case TagPayloadBinary:   msg.PayloadBinary   = cis.ReadBytes().ToByteArray(); break;
                default:                 cis.SkipLastField(); break;
            }
        }

        return msg;
    }
}
