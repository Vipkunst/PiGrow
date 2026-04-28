using Microsoft.Extensions.Caching.Memory;
using SensorData = PiGrow.Classes.SensorData;
using Threshold = PiGrow.Classes.Threshold;

namespace PiGrow.Services
{
    /// <summary>
    /// Background service that reads sensor values from the in-memory cache and
    /// controls the water pump relay based on configurable thresholds.
    /// </summary>
    public class ConditionCheckerService : BackgroundService
    {
        // MQTT topic constants — must match the topics subscribed to in MqttClientService.
        private const string SoilHumidityTopic = "sensor/bodenfeuchte/prozent";
        private const string HumidityTopic = "sensor/bme680/humidity";
        private const string TemperatureTopic = "sensor/bme680/temperature";
        private const string GasTopic = "sensor/bme680/gas";
        private const string LightTopic = ArduinoDataService.LightTopic;

        private readonly IMemoryCache _data;
        private readonly ILogger<ConditionCheckerService> _logger;
        private readonly IPiRelayController _relayController;

        private readonly bool _runStartupTest;
        private readonly TimeSpan _testPulseDuration;
        private readonly bool _mqttLogging;
        private readonly bool _arduinoLogging;

        // Thresholds loaded from appsettings.json — fallback defaults used if the section is missing.
        private readonly Threshold _soilHumidityThreshold;
        private readonly Threshold _humidityThreshold;
        private readonly Threshold _temperatureThreshold;
        private readonly Threshold _gasThreshold;
        private readonly Threshold _lightThreshold;

        // Minimum time between the start of one watering cycle and the start of the next.
        private readonly TimeSpan _timeBetweenWatering;
        private DateTime _lastTimeWatered;

        // Cooldown between low-light alerts so a dim afternoon doesn't spam the phone.
        private readonly TimeSpan _lightAlertCooldown;
        private DateTime _lastLightAlert;

        private readonly NtfyService _ntfyService;

        public ConditionCheckerService(IMemoryCache data, ILogger<ConditionCheckerService> logger, IPiRelayController relayService, IConfiguration config, NtfyService ntfyService)
        {
            _data = data;
            _logger = logger;
            _relayController = relayService;
            _ntfyService = ntfyService;

            _runStartupTest = config.GetValue("ConditionChecker:RunStartupTest", true);
            _testPulseDuration = TimeSpan.FromSeconds(config.GetValue("ConditionChecker:TestPulseSeconds", 10));
            _mqttLogging = config.GetSection("Debug:Logging:LogMqttValues").Get<bool>();
            _arduinoLogging = config.GetSection("Debug:Logging:LogArduinoValues").Get<bool>();

            _soilHumidityThreshold = LoadThreshold(config, "SoilHumidityThreshold", 40.0, 80.0);
            _humidityThreshold = LoadThreshold(config, "HumidityThreshold", 40.0, 80.0);
            _temperatureThreshold = LoadThreshold(config, "TemperatureThreshold", 15.0, 30.0);
            _gasThreshold = LoadThreshold(config, "GasThreshold", 0.0, 300.0);
            _lightThreshold = LoadThreshold(config, "LightThreshold", 20.0, 100.0);

            _timeBetweenWatering = TimeSpan.FromSeconds(config.GetValue("TimeThresholds:TimeBetweenWateringSeconds", 3600));
            // Allow watering to start immediately on first eligible reading after startup.
            _lastTimeWatered = DateTime.UtcNow - _timeBetweenWatering;

            _lightAlertCooldown = TimeSpan.FromSeconds(config.GetValue("TimeThresholds:LightAlertCooldownSeconds", 21600));
            // Suppress alert on startup; first alert can fire after one full cooldown of running.
            _lastLightAlert = DateTime.UtcNow;

            _logger.LogInformation(
                "Thresholds loaded — SoilHumidity: {SMin}-{SMax}, Humidity: {HMin}-{HMax}, Temperature: {TMin}-{TMax}, Gas: {GMin}-{GMax}, Light: {LMin}-{LMax}, Cooldown: {Cooldown}",
                _soilHumidityThreshold.Min, _soilHumidityThreshold.Max,
                _humidityThreshold.Min, _humidityThreshold.Max,
                _temperatureThreshold.Min, _temperatureThreshold.Max,
                _gasThreshold.Min, _gasThreshold.Max,
                _lightThreshold.Min, _lightThreshold.Max,
                _timeBetweenWatering);
        }

        private static Threshold LoadThreshold(IConfiguration config, string key, double defaultMin, double defaultMax) =>
            config.GetSection($"Conditions:{key}").Get<Threshold>() ?? new Threshold { Min = defaultMin, Max = defaultMax };

        /// <summary>
        /// Entry point called by the BackgroundService host. Runs the optional startup
        /// test then enters the main evaluation loop.
        /// </summary>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Optional one-shot pulse to verify pump wiring before entering the main loop.
            if (_runStartupTest && !await TryRunStartupTestAsync(stoppingToken))
                return;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    WriteSensorStatusLine();

                    bool shouldWater = EvaluateCondition(
                        SoilHumidityTopic, _soilHumidityThreshold,
                        _relayController.IsOn,
                        _timeBetweenWatering, ref _lastTimeWatered);

                    // Only toggle the relay when the desired state differs from the current state.
                    if (shouldWater != _relayController.IsOn)
                        await _relayController.SetStateAsync(shouldWater, stoppingToken);

                    if (EvaluateCondition(LightTopic, _lightThreshold, _lightAlertCooldown, ref _lastLightAlert))
                    {
                        try
                        {
                            await _ntfyService.NotifyAsync(
                                "PiGrow: plant needs light",
                                $"Light reading is below the configured minimum ({_lightThreshold.Min}). Consider moving the plant or turning on a grow light.",
                                stoppingToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Light alert notification failed");
                        }
                    }

                    // Task.Delay is inside the try so cancellation is caught and exits cleanly,
                    // allowing the final SetStateAsync(false) below to always run.
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in condition checker loop");
                }
            }

            // Guarantee the pump is off when the service stops.
            try { await _relayController.SetStateAsync(false); } catch { }
        }

        private void WriteSensorStatusLine()
        {
            if (!_mqttLogging && !_arduinoLogging)
                return;

            var parts = new List<string>();
            if (_mqttLogging)
            {
                parts.Add(FormatSensor("Soil", SoilHumidityTopic));
                parts.Add(FormatSensor("Hum", HumidityTopic));
                parts.Add(FormatSensor("Temp", TemperatureTopic));
                parts.Add(FormatSensor("Gas", GasTopic));
            }
            if (_arduinoLogging)
                parts.Add(FormatSensor("Light", LightTopic));

            var line = string.Join(" | ", parts);
            Console.Write("\r" + line.PadRight(Console.WindowWidth - 1));
        }

        /// <summary>
        /// Turns the pump ON for <see cref="_testPulseDuration"/> then OFF, to verify wiring.
        /// </summary>
        /// <returns>
        /// <c>true</c> if the test completed normally or failed with a non-cancellation error;
        /// <c>false</c> if the host is shutting down mid-test (caller should return immediately).
        /// </returns>
        private async Task<bool> TryRunStartupTestAsync(CancellationToken stoppingToken)
        {
            try
            {
                _logger.LogInformation("Startup test: turning pump ON for {Seconds}s", _testPulseDuration.TotalSeconds);
                await _relayController.SetStateAsync(true, stoppingToken);
                await Task.Delay(_testPulseDuration, stoppingToken);
                await _relayController.SetStateAsync(false, stoppingToken);
                _logger.LogInformation("Startup test complete: pump OFF");
                return true;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Ensure the pump is off even if we're interrupted mid-pulse.
                try { await _relayController.SetStateAsync(false); } catch { }
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during relay startup test");
                return true; // non-fatal — proceed to main loop
            }
        }

        /// <summary>
        /// Generic predicate: returns <c>true</c> when the latest reading on <paramref name="topic"/>
        /// is below <paramref name="threshold"/>.Min. Returns <c>false</c> if the value is missing,
        /// unparseable, or at/above Min.
        /// </summary>
        private bool EvaluateCondition(string topic, Threshold threshold)
        {
            if (!TryGetSensorValue(topic, out double value))
            {
                _logger.LogWarning("Data for topic {Topic} unavailable — condition not evaluated", topic);
                return false;
            }
            if (value < threshold.Min)
            {
                _logger.LogInformation("Topic {Topic}: value {Value:F1} below min {Min}", topic, value, threshold.Min);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Generic predicate with cooldown: returns <c>true</c> only when the value is below Min
        /// AND <paramref name="timeBetween"/> has elapsed since <paramref name="lastTime"/>.
        /// On success, <paramref name="lastTime"/> is advanced to <see cref="DateTime.UtcNow"/>.
        /// </summary>
        /// <param name="lastTime">
        /// Caller-owned cooldown timestamp; passed by ref so the update persists across calls.
        /// </param>
        private bool EvaluateCondition(string topic, Threshold threshold, TimeSpan timeBetween, ref DateTime lastTime)
        {
            if (!EvaluateCondition(topic, threshold))
                return false;

            var elapsed = DateTime.UtcNow - lastTime;
            if (elapsed <= timeBetween)
            {
                _logger.LogInformation("Topic {Topic}: cooldown not elapsed ({Elapsed} ≤ {Cooldown}) — suppressed", topic, elapsed, timeBetween);
                return false;
            }

            lastTime = DateTime.UtcNow;
            return true;
        }

        /// <summary>
        /// Generic predicate with Min/Max hysteresis: when inactive, becomes active if value &lt; Min;
        /// when active, stays active until value &gt;= Max.
        /// </summary>
        private bool EvaluateCondition(string topic, Threshold threshold, bool currentlyActive)
        {
            if (!TryGetSensorValue(topic, out double value))
            {
                _logger.LogWarning("Data for topic {Topic} unavailable — condition not evaluated", topic);
                return false;
            }

            if (currentlyActive)
            {
                if (value >= threshold.Max)
                {
                    _logger.LogInformation("Topic {Topic}: value {Value:F1} at or above max {Max} — deactivating", topic, value, threshold.Max);
                    return false;
                }
                return true;
            }

            if (value < threshold.Min)
            {
                _logger.LogInformation("Topic {Topic}: value {Value:F1} below min {Min} — activating", topic, value, threshold.Min);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Generic predicate with Min/Max hysteresis and a cooldown that gates only the
        /// inactive→active edge. Once active, the cycle continues until value &gt;= Max
        /// regardless of cooldown. <paramref name="lastTime"/> is advanced when a new cycle starts.
        /// </summary>
        private bool EvaluateCondition(string topic, Threshold threshold, bool currentlyActive, TimeSpan timeBetween, ref DateTime lastTime)
        {
            if (currentlyActive)
                return EvaluateCondition(topic, threshold, currentlyActive: true);

            if (!EvaluateCondition(topic, threshold))
                return false;

            var elapsed = DateTime.UtcNow - lastTime;
            if (elapsed <= timeBetween)
            {
                _logger.LogInformation("Topic {Topic}: cooldown not elapsed ({Elapsed} ≤ {Cooldown}) — suppressed", topic, elapsed, timeBetween);
                return false;
            }

            lastTime = DateTime.UtcNow;
            return true;
        }

        private string FormatSensor(string name, string topic) =>
            TryGetSensorValue(topic, out double value) ? $"{name}: {value}" : $"{name}: ---";

        /// <summary>
        /// Retrieves a sensor reading from the cache and parses it as a double.
        /// Handles payloads with a trailing '%' (e.g. the soil humidity sensor sends "5.12%").
        /// </summary>
        /// <param name="topic">The MQTT topic used as the cache key.</param>
        /// <param name="value">The parsed sensor value, or 0 on failure.</param>
        /// <returns><c>true</c> if the value was found and parsed successfully.</returns>
        private bool TryGetSensorValue(string topic, out double value)
        {
            value = 0;
            if (!_data.TryGetValue(topic, out var raw) || raw is not SensorData sensorData)
            {
                _logger.LogDebug("Cache miss for topic {Topic}", topic);
                return false;
            }

            // Strip trailing '%' before parsing — some sensors include the unit in the payload.
            var message = sensorData.Message.TrimEnd('%').Trim();
            if (!double.TryParse(message, System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out value))
            {
                _logger.LogDebug("Failed to parse value '{Message}' for topic {Topic}", sensorData.Message, topic);
                return false;
            }

            return true;
        }
    }
}
