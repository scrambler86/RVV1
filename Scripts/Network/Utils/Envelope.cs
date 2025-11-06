// Assets/Scripts/Network/Utils/Envelope.cs
public struct Envelope
{
    public uint messageId;
    public uint seq;
    public int payloadLen;
    public ulong payloadHash;
    public byte flags;
}
