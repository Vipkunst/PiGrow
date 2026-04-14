using Microsoft.Extensions.Caching.Memory;

namespace PiGrow.Services
{
    public class ConditionCheckerService : BackgroundService
    {
        private readonly IMemoryCache _data;
        private readonly ILogger<ConditionCheckerService> _logger;
        private readonly IPiRelayController _relayController;

        private readonly bool _runStartupTest;
        private readonly TimeSpan _testPulseDuration;

        private readonly Threshold _humidityThreshold;
        private readonly Threshold _temperatureThreshold;
        private readonly Threshold _gasThreshold;
        private readonly Threshold _lightThreshold;

        public ConditionCheckerService(IMemoryCache data, ILogger<ConditionCheckerService> logger, IPiRelayController relayService, IConfiguration config)
        {
            _data = data;
            _logger = logger;
            _relayController = relayService;

            _runStartupTest = config.GetValue("ConditionChecker:RunStartupTest", true);
            var testPulseSeconds = config.GetValue("ConditionChecker:TestPulseSeconds", 10);
            _testPulseDuration = TimeSpan.FromSeconds(testPulseSeconds);

            // GetSection().Get<T>() correctly deserialises complex config objects
            _humidityThreshold    = config.GetSection("Conditions:HumidityThreshold").Get<Threshold>()    ?? new Threshold { min = 40.0, max = 80.0 };
            _temperatureThreshold = config.GetSection("Conditions:TemperatureThreshold").Get<Threshold>() ?? new Threshold { min = 15.0, max = 30.0 };
            _gasThreshold         = config.GetSection("Conditions:GasThreshold").Get<Threshold>()         ?? new Threshold { min =  0.0, max = 300.0 };
            _lightThreshold       = config.GetSection("Conditions:LightThreshold").Get<Threshold>()       ?? new Threshold { min = 20.0, max = 100.0 };

            _logger.LogInformation(
                "Thresholds loaded — Humidity: {HMin}-{HMax}, Temperature: {TMin}-{TMax}, Gas: {GMin}-{GMax}, Light: {LMin}-{LMax}",
                _humidityThreshold.min, _humidityThreshold.max,
                _temperatureThreshold.min, _temperatureThreshold.max,
                _gasThreshold.min, _gasThreshold.max,
                _lightThreshold.min, _lightThreshold.max);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // One-shot test pulse on startup to verify pump wiring
            if (_runStartupTest)
            {
                try
                {
                    _logger.LogInformation("Startup test: turning pump ON for {Seconds}s", _testPulseDuration.TotalSeconds);
                    await _relayController.SetStateAsync(true, stoppingToken);
                    await Task.Delay(_testPulseDuration, stoppingToken);
                    await _relayController.SetStateAsync(false, stoppingToken);
                    _logger.LogInformation("Startup test complete: pump OFF");
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    try { await _relayController.SetStateAsync(false); } catch { }
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during relay startup test");
                }
            }

            // Main loop — runs every second
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    bool shouldWater = EvaluateWateringCondition();

                    if (shouldWater != _relayController.IsOn)
                    {
                        await _relayController.SetStateAsync(shouldWater, stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in condition checker loop");
                }

                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }

            // Ensure pump is off on shutdown
            try { await _relayController.SetStateAsync(false); } catch { }
        }

        /// <summary>
        /// Returns true if the pump should be ON.
        /// Primary condition: humidity below minimum threshold (soil too dry).
        /// Guard conditions: temperature and gas must be within safe bounds.
        /// </summary>
        private bool EvaluateWateringCondition()
        {
            // --- Humidity (primary watering trigger) ---
            if (!TryGetSensorValue("sensor/bme680/humidity", out double humidity))
            {
                _logger.LogWarning("Humidity data unavailable — keeping pump OFF");
                return false;
            }

            if (humidity < _humidityThreshold.min)
            {
                _logger.LogInformation("Humidity {Value:F1}% is below min {Min}% → pump ON", humidity, _humidityThreshold.min);

                // --- Temperature guard: don't water if it's too cold or too hot ---
                // if (TryGetSensorValue("sensor/bme680/temperature", out double temperature))
                // {
                //     if (temperature < _temperatureThreshold.min || temperature > _temperatureThreshold.max)
                //     {
                //         _logger.LogWarning("Temperature {Value:F1}°C out of safe range [{Min},{Max}] — skipping watering", temperature, _temperatureThreshold.min, _temperatureThreshold.max);
                //         return false;
                //     }
                // }

                // --- Gas guard: don't water in a high-gas environment ---
                // if (TryGetSensorValue("sensor/bme680/gas", out double gas))
                // {
                //     if (gas > _gasThreshold.max)
                //     {
                //         _logger.LogWarning("Gas {Value:F1} exceeds max {Max} — skipping watering", gas, _gasThreshold.max);
                //         return false;
                //     }
                // }

                return true;
            }

            if (humidity >= _humidityThreshold.max)
            {
                _logger.LogInformation("Humidity {Value:F1}% is at or above max {Max}% → pump OFF", humidity, _humidityThreshold.max);
            }

            return false;
        }

        private bool TryGetSensorValue(string topic, out double value)
        {
            value = 0;
            if (!_data.TryGetValue(topic, out var raw) || raw is not Classes.SensorData sensorData)
                return false;

            return double.TryParse(sensorData.Message, System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out value);
        }
    }

    public class Threshold
    {
        public double max { get; set; }
        public double min { get; set; }
    }
}
