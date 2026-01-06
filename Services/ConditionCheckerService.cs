using Microsoft.Extensions.Caching.Memory;

namespace EEEUUHH.Services
{
    public class ConditionCheckerService: BackgroundService
    {
        private readonly IMemoryCache _data;

        public ConditionCheckerService(IMemoryCache data)
        {
            _data = data;
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _data.TryGetValue("gas", out var gas);
            _data.TryGetValue("humidity", out var humidity);
            _data.TryGetValue("temperature", out var temperature);

            return Task.CompletedTask;
        }
    }
}
