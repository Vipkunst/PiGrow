using Microsoft.Extensions.Caching.Memory;

namespace PiGrow.Services
{
    public class ConditionCheckerService: BackgroundService
    {
        private readonly IMemoryCache _data;
        private readonly ILogger<ConditionCheckerService> _logger;
        private readonly IPiRelayController _relayController;

        private readonly bool _runStartupTest = true;
        private readonly TimeSpan _testPulseDuration = TimeSpan.FromSeconds(10);

        public ConditionCheckerService(IMemoryCache data, ILogger<ConditionCheckerService> logger, IPiRelayController relayService)
        {
            _data = data;
            _logger = logger;
            _relayController = relayService;
        }

        double humidityThreshold = 50;

        protected async override Task ExecuteAsync(CancellationToken stoppingToken)
        {

            // One-shot test pulse on startup (for testing the pump)
            if (_runStartupTest)
            {
                try
                {
                    _logger.LogInformation("Starting relay test: turning pump ON for {Seconds}s", _testPulseDuration.TotalSeconds);
                    await _relayController.SetStateAsync(true, stoppingToken);
                    await Task.Delay(_testPulseDuration, stoppingToken);
                    await _relayController.SetStateAsync(false, stoppingToken);
                    // _logger.LogInformation("Relay test complete: pump OFF");
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // shutdown requested during test - try to leave relay off
                    try { await _relayController.SetStateAsync(false); } catch { }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during relay startup test");
                }
            }

            // Main loop
            while (!stoppingToken.IsCancellationRequested)
            {
                _data.TryGetValue("sensor/bme680/gas", out var gas);
                _data.TryGetValue("sensor/bme680/humidity", out var humidity);
                _data.TryGetValue("sensor/bme680/temperature", out var temperature);

                if (humidity is Classes.SensorData humidityData && 
                    double.TryParse(humidityData.Message, out double humidityValue) && 
                    humidityValue < humidityThreshold)
                {
                    // ...
                }

                await Task.Delay(1000, stoppingToken);
            }
        }
    }
}
