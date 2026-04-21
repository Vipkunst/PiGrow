using Microsoft.Extensions.Caching.Memory;

namespace PiGrow.Services
{
    public class ConditionCheckerService : BackgroundService
    {
        private const string SoilHumidityTopic = "sensor/bodenfeuchte/prozent";
        private const string HumidityTopic = "sensor/bme680/humidity";
        private const string TemperatureTopic = "sensor/bme680/temperature";
        private const string GasTopic = "sensor/bme680/gas";

        private readonly IMemoryCache _data;
        private readonly ILogger<ConditionCheckerService> _logger;
        private readonly IPiRelayController _relayController;

        private readonly bool _runStartupTest;
        private readonly TimeSpan _testPulseDuration;

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

            _soilHumidityThreshold = config.GetSection("Conditions:SoilHumidityThreshold").Get<Classes.Threshold>() ?? new Classes.Threshold { Min = 40.0, Max = 80.0 };
            _humidityThreshold    = config.GetSection("Conditions:HumidityThreshold").Get<Classes.Threshold>()    ?? new Classes.Threshold { Min = 40.0, Max = 80.0 };
            _temperatureThreshold = config.GetSection("Conditions:TemperatureThreshold").Get<Classes.Threshold>() ?? new Classes.Threshold { Min = 15.0, Max = 30.0 };
            _gasThreshold         = config.GetSection("Conditions:GasThreshold").Get<Classes.Threshold>()         ?? new Classes.Threshold { Min =  0.0, Max = 300.0 };
            _lightThreshold       = config.GetSection("Conditions:LightThreshold").Get<Classes.Threshold>()       ?? new Classes.Threshold { Min = 20.0, Max = 100.0 };

            _logger.LogInformation(
                "Thresholds loaded — SoilHumidity: {SMin}-{SMax}, Humidity: {HMin}-{HMax}, Temperature: {TMin}-{TMax}, Gas: {GMin}-{GMax}, Light: {LMin}-{LMax}",
                _soilHumidityThreshold.Min, _soilHumidityThreshold.Max,
                _humidityThreshold.Min, _humidityThreshold.Max,
                _temperatureThreshold.Min, _temperatureThreshold.Max,
                _gasThreshold.Min, _gasThreshold.Max,
                _lightThreshold.Min, _lightThreshold.Max);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_runStartupTest && !await RunStartupTestAsync(stoppingToken))
                return;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    bool shouldWater = EvaluateWateringCondition();

                    if (shouldWater != _relayController.IsOn)
                        await _relayController.SetStateAsync(shouldWater, stoppingToken);

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

            try { await _relayController.SetStateAsync(false); } catch { }
        }

        // Returns false if cancelled during the test (caller should exit without further work).
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
                try { await _relayController.SetStateAsync(false); } catch { }
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during relay startup test");
                return true;
            }
        }

        private bool EvaluateWateringCondition()
        {
            if (!TryGetSensorValue(SoilHumidityTopic, out double soilHumidity))
            {
                _logger.LogWarning("Soil humidity data unavailable — keeping pump OFF");
                return false;
            }
            _logger.LogInformation("Soil Humidity value: "+ soilHumidity);

            if (soilHumidity < _soilHumidityThreshold.Min)
            {
                _logger.LogInformation("Soil humidity {Value:F1}% below min {Min}% → pump ON", soilHumidity, _soilHumidityThreshold.Min);
                return true;
            }

            if (soilHumidity >= _soilHumidityThreshold.Max)
                _logger.LogInformation("Soil humidity {Value:F1}% at or above max {Max}% → pump OFF", soilHumidity, _soilHumidityThreshold.Max);

            return false;
        }

        private bool TryGetSensorValue(string topic, out double value)
        {
            value = 0;
            if (!_data.TryGetValue(topic, out var raw) || raw is not Classes.SensorData sensorData)
            {
                _logger.LogDebug("Cache miss for topic {Topic}", topic);
                return false;
            }

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
