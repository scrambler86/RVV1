// IPlayerNetworkDriver.cs
// REVIVO-NET-BASELINE — Interface
// BOOKMARK: [FILE IPlayerNetworkDriver]

namespace Revivo.Network.Adapters
{
    public interface IPlayerNetworkDriver
    {
        INetTime NetTime { get; }
        int OwnerClientId { get; }
        uint LastSeqReceived { get; }
        void SetLastSeqReceived(uint seq);

        // RTT misurato per questo client (ms), lato server
        double ClientRttMs { get; }
    }
}
