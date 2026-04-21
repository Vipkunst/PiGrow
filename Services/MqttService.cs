using Microsoft.Extensions.Caching.Memory;
using MQTTnet;
using System.Buffers;
using System.Text;

namespace PiGrow.Services
{
    /// <summary>
    /// Background service that connects to the MQTT broker, subscribes to sensor topics,
    /// and caches each incoming message so other services can read the latest values.
    /// </summary>
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
            _logger = logger;

            var factory = new MqttClientFactory();
            _mqttClient = factory.CreateMqttClient();

            _options = new MqttClientOptionsBuilder()
                .WithTcpServer(_configuration["Mqtt:Host"], int.Parse(_configuration["Mqtt:Port"]!))
                .WithClientId($"WebApiClient-{Environment.MachineName}-{Guid.NewGuid()}")
                .WithCredentials(_configuration["Mqtt:Username"], _configuration["Mqtt:Password"])
                .Build();

            _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceived;
        }

        /// <summary>
        /// Handles an incoming MQTT message: decodes the payload, wraps it in a
        /// <see cref="Classes.SensorData"/> object, and stores it in the cache keyed by topic.
        /// Cache entries expire after 10 minutes so stale sensor data is not acted upon indefinitely.
        /// </summary>
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

                _cache.Set(data.Topic, data, TimeSpan.FromMinutes(10));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MQTT message handling failed");
            }
        }

        /// <summary>
        /// Connects to the broker and subscribes to all sensor topics.
        /// The service stays alive until the host requests cancellation.
        /// </summary>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await _mqttClient.ConnectAsync(_options, stoppingToken);

            var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter("sensor/bme680/gas")
                .WithTopicFilter("sensor/bme680/humidity")
                .WithTopicFilter("sensor/bme680/temperature")
                .WithTopicFilter("sensor/bodenfeuchte/prozent")
                .Build();

            await _mqttClient.SubscribeAsync(subscribeOptions, stoppingToken);
        }

        /// <summary>
        /// Disconnects from the broker cleanly before the service is destroyed.
        /// </summary>
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_mqttClient.IsConnected)
                await _mqttClient.DisconnectAsync();

            await base.StopAsync(cancellationToken);
        }
    }
}
