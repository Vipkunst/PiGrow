namespace PiGrow.Services
{
    /// <summary>
    /// Controls a relay that switches the water pump on or off.
    /// </summary>
    public interface IPiRelayController
    {
        /// <summary>
        /// Sets the relay state. Implementations may enforce a minimum on-time before allowing OFF.
        /// </summary>
        /// <param name="on">True to turn the pump ON, false to turn it OFF.</param>
        /// <param name="cancellationToken">Token to cancel a minimum-on wait.</param>
        Task SetStateAsync(bool on, CancellationToken cancellationToken = default);

        /// <summary>Whether the pump is currently ON.</summary>
        bool IsOn { get; }
    }
}
