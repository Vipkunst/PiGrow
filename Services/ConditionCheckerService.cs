using Microsoft.Extensions.Caching.Memory;

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
        private const string HumidityTopic     = "sensor/bme680/humidity";
        private const string TemperatureTopic  = "sensor/bme680/temperature";
        private const string GasTopic          = "sensor/bme680/gas";
        private const string LightTopic        = ArduinoDataService.LightTopic;

        private readonly IMemoryCache _data;
        private readonly ILogger<ConditionCheckerService> _logger;
        private readonly IPiRelayController _relayController;

        private readonly bool _runStartupTest;
        private readonly TimeSpan _testPulseDuration;
        private readonly bool _mqttLogging;
        private readonly bool _arduinoLogging;

        // Thresholds loaded from appsettings.json — fallback defaults used if the section is missing.
        private readonly Classes.Threshold _soilHumidityThreshold;
        private readonly Classes.Threshold _humidityThreshold;
        private readonly Classes.Threshold _temperatureThreshold;
        private readonly Classes.Threshold _gasThreshold;
        private readonly Classes.Threshold _lightThreshold;

        public ConditionCheckerService(IMemoryCache data, ILogger<ConditionCheckerService> logger, IPiRelayController relayService, IConfiguration config)
        {
            _data = data;
            _logger = logger;
            _relayController = relayService;

            _runStartupTest = config.GetValue("ConditionChecker:RunStartupTest", true);
            _testPulseDuration = TimeSpan.FromSeconds(config.GetValue("ConditionChecker:TestPulseSeconds", 10));

            _mqttLogging = config.GetSection("Debug:Logging:LogMqttValues").Get<bool>();
            _arduinoLogging = config.GetSection("Debug:Logging:LogArduinoValues").Get<bool>();

            _soilHumidityThreshold = config.GetSection("Conditions:SoilHumidityThreshold").Get<Classes.Threshold>() ?? new Classes.Threshold { Min = 40.0, Max = 80.0 };
            _humidityThreshold     = config.GetSection("Conditions:HumidityThreshold").Get<Classes.Threshold>()     ?? new Classes.Threshold { Min = 40.0, Max = 80.0 };
            _temperatureThreshold  = config.GetSection("Conditions:TemperatureThreshold").Get<Classes.Threshold>()  ?? new Classes.Threshold { Min = 15.0, Max = 30.0 };
            _gasThreshold          = config.GetSection("Conditions:GasThreshold").Get<Classes.Threshold>()          ?? new Classes.Threshold { Min =  0.0, Max = 300.0 };
            _lightThreshold        = config.GetSection("Conditions:LightThreshold").Get<Classes.Threshold>()        ?? new Classes.Threshold { Min = 20.0, Max = 100.0 };

            _logger.LogInformation(
                "Thresholds loaded — SoilHumidity: {SMin}-{SMax}, Humidity: {HMin}-{HMax}, Temperature: {TMin}-{TMax}, Gas: {GMin}-{GMax}, Light: {LMin}-{LMax}",
                _soilHumidityThreshold.Min, _soilHumidityThreshold.Max,
                _humidityThreshold.Min, _humidityThreshold.Max,
                _temperatureThreshold.Min, _temperatureThreshold.Max,
                _gasThreshold.Min, _gasThreshold.Max,
                _lightThreshold.Min, _lightThreshold.Max);
        }

        /// <summary>
        /// Entry point called by the BackgroundService host. Runs the optional startup
        /// test then enters the main evaluation loop.
        /// </summary>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Optional one-shot pulse to verify pump wiring before entering the main loop.
            if (_runStartupTest && !await RunStartupTestAsync(stoppingToken))
                return;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (_mqttLogging || _arduinoLogging)
                    {
                        var parts = new List<string>();
                        if (_mqttLogging)
                        {
                            parts.Add(FormatSensor("Soil",  SoilHumidityTopic));
                            parts.Add(FormatSensor("Hum",   HumidityTopic));
                            parts.Add(FormatSensor("Temp",  TemperatureTopic));
                            parts.Add(FormatSensor("Gas",   GasTopic));
                        }
                        if (_arduinoLogging)
                            parts.Add(FormatSensor("Light", LightTopic));

                        var line = string.Join(" | ", parts);
                        Console.Write("\r" + line.PadRight(Console.WindowWidth - 1));
                    }

                    bool shouldWater = EvaluateWateringCondition();

                    // Only toggle the relay when the desired state differs from the current state.
                    if (shouldWater != _relayController.IsOn)
                        await _relayController.SetStateAsync(shouldWater, stoppingToken);

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

        /// <summary>
        /// Turns the pump ON for <see cref="_testPulseDuration"/> then OFF, to verify wiring.
        /// </summary>
        /// <returns>
        /// <c>true</c> if the test completed normally or failed with a non-cancellation error;
        /// <c>false</c> if the host is shutting down mid-test (caller should return immediately).
        /// </returns>
        private async Task<bool> RunStartupTestAsync(CancellationToken stoppingToken)
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
        /// Determines whether the pump should be ON based on the latest soil humidity reading.
        /// Primary trigger: soil humidity below minimum threshold.
        /// Guard conditions (temperature, gas) can be wired in here when needed.
        /// </summary>
        /// <returns><c>true</c> if the pump should be ON, <c>false</c> otherwise.</returns>
        private bool EvaluateWateringCondition()
        {
            if (!TryGetSensorValue(SoilHumidityTopic, out double soilHumidity))
            {
                _logger.LogWarning("Soil humidity data unavailable — keeping pump OFF");
                return false;
            }

            _logger.LogInformation("Soil humidity value: {Value:F1}%", soilHumidity);

            if (soilHumidity < _soilHumidityThreshold.Min)
            {
                _logger.LogInformation("Soil humidity {Value:F1}% below min {Min}% → pump ON", soilHumidity, _soilHumidityThreshold.Min);
                return true;
            }

            if (soilHumidity >= _soilHumidityThreshold.Max)
                _logger.LogInformation("Soil humidity {Value:F1}% at or above max {Max}% → pump OFF", soilHumidity, _soilHumidityThreshold.Max);

            return false;
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
            if (!_data.TryGetValue(topic, out var raw) || raw is not Classes.SensorData sensorData)
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
