using System.Device.Gpio;

var builder = WebApplication.CreateBuilder(args);

if (OperatingSystem.IsLinux())
{
    builder.Services.AddSingleton<GpioController>(_ => new GpioController());
    builder.Services.AddSingleton<PiGrow.Services.IPiRelayController, PiGrow.Services.PiRelayService>();
}

builder.Services.AddMemoryCache();
builder.Services.AddHostedService<PiGrow.Services.MqttClientService>();
builder.Services.AddHostedService<PiGrow.Services.ArduinoDataService>();
builder.Services.AddHostedService<PiGrow.Services.ConditionCheckerService>();
//builder.Services.AddHostedService<PiGrow.Services.ArduinoDataService>();

var app = builder.Build();

app.Run();
