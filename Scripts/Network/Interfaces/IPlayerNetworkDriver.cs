namespace Game.Networking.Adapters
{
    public interface IPlayerNetworkDriver
    {
        INetTime NetTime { get; }

        int OwnerClientId { get; }
        uint LastSeqReceived { get; }
        void SetLastSeqReceived(uint seq);

        double ClientRttMs { get; }

        /// <summary>
        /// Returns true if the local machine is allowed to author input for this driver.
        /// </summary>
        /// <param name="allowServerFallback">
        /// When true, the host/server is allowed to drive input even if it does not own the object.
        /// </param>
        bool HasInputAuthority(bool allowServerFallback);
    }
}