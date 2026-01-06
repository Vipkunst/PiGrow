using Microsoft.Extensions.Caching.Memory;
using MQTTnet;
using System.Buffers;
using System.Text;

namespace EEEUUHH.Services
{
    public class MqttClientService : BackgroundService
    {
        private readonly IMemoryCache _cache;
        private readonly IMqttClient _mqttClient;
        private readonly MqttClientOptions _options;
        private readonly IConfiguration _configuration;
        private readonly ILogger<MqttClientService> _logger;

        public MqttClientService(IMemoryCache cache, IConfiguration configuration, ILogger<MqttClientService> logger)
        {
            _cache = cache;
            _configuration = configuration;
            var factory = new MqttClientFactory();
            _mqttClient = factory.CreateMqttClient();
            _logger = logger;

            _options = new MqttClientOptionsBuilder()
                .WithTcpServer(_configuration["Mqtt:Host"], int.Parse(_configuration["Mqtt:Port"]))
                .WithClientId($"WebApiClient-{Environment.MachineName}-{Guid.NewGuid()}")
                .WithCredentials(_configuration["Mqtt:Username"], _configuration["Mqtt:Password"])
                .Build();

            // Setup message handler
            _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceived;
        }

        private async Task OnMessageReceived(MqttApplicationMessageReceivedEventArgs e)
        {
            try
            {
                var payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload.ToArray());
                var data = new Classes.SensorData
                {
                    Topic = e.ApplicationMessage.Topic,
                    Message = payload,
                    Timestamp = DateTime.UtcNow
                };

                // Cache the latest data
                _cache.Set(data.Topic, data, TimeSpan.FromMinutes(10));
                _logger.LogInformation(payload);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MQTT message handling failed");
            }

            return;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await _mqttClient.ConnectAsync(_options, stoppingToken);

            var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter("sensor/bme680/gas")
                .WithTopicFilter("sensor/bme680/humidity")
                .WithTopicFilter("sensor/bme680/temperature")
                .Build();

            await _mqttClient.SubscribeAsync(subscribeOptions, stoppingToken);
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_mqttClient.IsConnected)
                await _mqttClient.DisconnectAsync();

            await base.StopAsync(cancellationToken);
        }
    }
}