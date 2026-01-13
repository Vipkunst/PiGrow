using System.IO.Ports;

namespace EEEUUHH.Services
{
    public class ArduinoService : BackgroundService
    {
        private readonly string _portName = "/dev/ttyACM0";
        private readonly int _baudRate = 9600;

        private SerialPort? _serialPort;
        private readonly ILogger<ArduinoService> _logger;

        public ArduinoService(ILogger<ArduinoService> logger)
        {
            _logger = logger;
        }

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

            // Main loop
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    string line = _serialPort.ReadLine().Trim();
                    if (line.Length == 0)
                        continue;

                    if (int.TryParse(line, out int value))
                    {
                        _logger.LogInformation($"Light: {value}");
                    }
                    else
                    {
                        _logger.LogInformation($"Raw: {line}");
                    }
                }
                catch (TimeoutException)
                {
                    // nothing received -> just continue
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Serial error: {ex.Message}");
                }

                // Yield to avoid tight CPU loop
                await Task.Delay(10, stoppingToken);
            }

            // Cleanup on shutdown
            if (_serialPort.IsOpen)
            {
                _serialPort.Close();
            }
        }

        public override void Dispose()
        {
            _serialPort?.Dispose();
            base.Dispose();
        }
    }
}
