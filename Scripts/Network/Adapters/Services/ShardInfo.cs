namespace Game.Networking.Adapters
{
    public sealed class ShardInfo
    {
        public ushort Total;
        public ushort Index;
        public int DataLength;
        public byte[] Data;
    }
}