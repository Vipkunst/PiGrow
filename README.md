# PiGrow

PiGrow is a Raspberry Pi-based automated plant care system that monitors soil moisture and environmental conditions (temperature, humidity, air quality, light) via MQTT sensors and an Arduino, then automatically controls a water pump via GPIO relay to keep plants watered within configurable thresholds. It sends notifications when plants need more light.

## Watering Behaviour

PiGrow continuously monitors soil moisture and waters plants automatically using a hysteresis-based control loop:

- **Trigger**: The pump activates when soil humidity drops below the minimum threshold (default 5%)
- **Stop**: The pump deactivates once soil humidity reaches the maximum threshold (default 20%)
- **Cooldown**: After each watering cycle, a 1-hour cooldown prevents the pump from running again too soon, protecting against over-watering
- **Minimum run time**: The pump runs for at least 5 seconds per activation to protect it from damage caused by short cycling
- **Startup test**: In development mode, the system runs a brief 10-second pump pulse on boot to verify hardware is working correctly
- **Safe shutdown**: When the service stops for any reason, the pump is guaranteed to turn off, preventing accidental flooding

All thresholds and timings are fully configurable via `appsettings.json`.
