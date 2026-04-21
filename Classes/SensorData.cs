namespace PiGrow.Classes
{
    /// <summary>
    /// Represents a single reading received from an MQTT sensor topic.
    /// </summary>
    public class SensorData
    {
        /// <summary>The MQTT topic the message arrived on (used as the cache key).</summary>
        public string Topic { get; set; } = default!;

        /// <summary>Raw payload string as published by the sensor.</summary>
        public string Message { get; set; } = default!;

        /// <summary>UTC time the message was received.</summary>
        public DateTime Timestamp { get; set; }
    }
}
