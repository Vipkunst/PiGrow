using System.IO.Ports;

namespace PiGrow.Services
{
    /// <summary>
    /// Background service that reads data from an Arduino over a serial (USB) connection.
    /// Currently reads light sensor values; extend the parsing logic for additional sensors.
    /// </summary>
    public class ArduinoDataService : BackgroundService
    {
        private readonly string _portName = "/dev/ttyACM0";
        private readonly int _baudRate = 9600;

        private SerialPort? _serialPort;
        private readonly ILogger<ArduinoDataService> _logger;

        public ArduinoDataService(ILogger<ArduinoDataService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Opens the serial port and reads lines in a loop until cancellation is requested.
        /// </summary>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _serialPort = new SerialPort(_portName, _baudRate)
            {
                NewLine = "\n",
                ReadTimeout = 1000,
                DtrEnable = true,
                RtsEnable = true
            };

            try
            {
                _serialPort.Open();
                _logger.LogInformation($"Connected: {_serialPort.PortName} @ {_serialPort.BaudRate}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Could not open port {_portName}: {ex.Message}");
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    string line = _serialPort.ReadLine().Trim();
                    if (line.Length == 0)
                        continue;

                    if (int.TryParse(line, out int value))
                    {
                        // Light sensor value received — no action taken yet.
                    }
                    else
                    {
                        _logger.LogInformation($"Raw: {line}");
                    }
                }
                catch (TimeoutException)
                {
                    // ReadTimeout is expected when there is no data; not an error.
                    _logger.LogInformation("Serial read timeout.");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Serial error: {ex.Message}");
                }

                // Small delay to avoid a tight CPU loop between reads.
                await Task.Delay(10, stoppingToken);
            }

            if (_serialPort.IsOpen)
                _serialPort.Close();
        }

        /// <inheritdoc/>
        public override void Dispose()
        {
            _serialPort?.Dispose();
            base.Dispose();
        }
    }
}
