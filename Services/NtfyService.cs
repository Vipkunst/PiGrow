namespace PiGrow.Services
{
    /// <summary>
    /// Sends push notifications via ntfy.sh (or a self-hosted ntfy instance).
    /// The topic name acts as the auth — keep it long and unguessable.
    /// </summary>
    public class NtfyService
    {
        private readonly ILogger<NtfyService> _logger;
        private readonly HttpClient _http;
        private readonly string _topicUrl;

        public NtfyService(HttpClient http, IConfiguration config, ILogger<NtfyService> logger)
        {
            _logger = logger;
            _http = http;

            var server = config["Ntfy:Server"] ?? "https://ntfy.sh";
            var topic  = config["Ntfy:Topic"]  ?? throw new InvalidOperationException("Ntfy:Topic missing");
            _topicUrl  = $"{server.TrimEnd('/')}/{topic}";
        }

        public async Task NotifyAsync(string title, string message, CancellationToken ct = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _topicUrl)
            {
                Content = new StringContent(message)
            };
            request.Headers.Add("Title", title);

            using var response = await _http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            _logger.LogInformation("Notification sent: {Title}", title);
        }
    }
}
