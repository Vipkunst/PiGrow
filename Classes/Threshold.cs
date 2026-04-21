namespace PiGrow.Classes
{
    /// <summary>
    /// Defines the acceptable operating range for a sensor value.
    /// </summary>
    public class Threshold
    {
        /// <summary>Lower bound — values below this trigger an action (e.g. pump ON).</summary>
        public double Min { get; set; }

        /// <summary>Upper bound — values at or above this stop the action (e.g. pump OFF).</summary>
        public double Max { get; set; }
    }
}
