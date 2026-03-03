using System.Device.Gpio;

namespace PiGrow.Services
{
    public class PiRelayService : IPiRelayController, IDisposable
    {
        private readonly GpioController _controller;
        private readonly ILogger<PiRelayService> _logger;
        private readonly IConfiguration config;
        private readonly int _pin;
        private readonly bool _activeLow;
        private readonly TimeSpan _minOnTime;
        private readonly object _lock = new();
        private bool _isOn;
        private DateTime _lastOnTime = DateTime.MinValue;

        public PiRelayService(ILogger<PiRelayService> logger, IConfiguration config, GpioController controller)
        {
            _logger = logger;
            this.config = config;
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));

            _pin = config.GetValue("Relay:Pin", 17); // default GPIO17
            _activeLow = config.GetValue("Relay:ActiveLow", false);
            var minOnSeconds = config.GetValue("Relay:MinOnSeconds", 5);
            _minOnTime = TimeSpan.FromSeconds(Math.Max(0, minOnSeconds));

            try
            {
                _controller.OpenPin(_pin, PinMode.Output);
                // Ensure relay starts in OFF position
                WritePin(false);
                _logger.LogInformation("PiRelayService initialized on pin {Pin} (activeLow={ActiveLow}), minOnSeconds={MinOnSeconds}", _pin, _activeLow, minOnSeconds);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to open GPIO pin {Pin}", _pin);
                throw;
            }
        }

        public bool IsOn
        {
            get
            {
                lock (_lock) { return _isOn; }
            }
        }

        public async Task SetStateAsync(bool on, CancellationToken cancellationToken = default)
        {
            TimeSpan wait = TimeSpan.Zero;

            lock (_lock)
            {
                if (_isOn == on) return;

                if (!on)
                {
                    var elapsed = DateTime.UtcNow - _lastOnTime;
                    if (elapsed < _minOnTime)
                        wait = _minOnTime - elapsed;
                }
            }

            if (wait > TimeSpan.Zero)
            {
                _logger.LogInformation("Waiting {WaitMs}ms before turning OFF", (int)wait.TotalMilliseconds);
                await Task.Delay(wait, cancellationToken); // properly async, outside the lock
            }

            cancellationToken.ThrowIfCancellationRequested();

            lock (_lock)
            {
                if (_isOn == on) return; // re-check after the await
                WritePin(on);
                if (on) _lastOnTime = DateTime.UtcNow;
                _isOn = on;
            }
        }

        private void WritePin(bool on)
        {
            try
            {
                var pinValue = _activeLow ? !on : on;
                _controller.Write(_pin, pinValue ? PinValue.High : PinValue.Low);
                _logger.LogInformation("Relay pin {Pin} set to {Value} (logical on={On})", _pin, pinValue ? "High" : "Low", on);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write relay pin {Pin}", _pin);
            }
        }

        public void Dispose()
        {
            try
            {
                if (_controller.IsPinOpen(_pin))
                {
                    // make sure relay is off
                    WritePin(false);
                    _controller.ClosePin(_pin);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing PiRelayService");
            }
        }
    }
}