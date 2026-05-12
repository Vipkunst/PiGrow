# PiGrow

PiGrow is a Raspberry Pi-based automated plant care system that monitors soil moisture and environmental conditions (temperature, humidity, air quality, light) via MQTT sensors and an Arduino, then automatically controls a water pump via GPIO relay to keep plants watered within configurable thresholds. It sends notifications when plants need more light.

## Watering Behaviour

PiGrow continuously monitors soil moisture and waters plants automatically using a pulse-based hysteresis loop:

1. When soil humidity drops below the minimum threshold (default 5%), a watering cycle begins
2. The pump runs for 30 seconds, then turns off
3. Soil humidity is read again — if still below the maximum threshold (default 20%), the pump runs for another 30 seconds
4. This repeats until the soil reaches the maximum threshold
5. After the cycle completes, a 1-hour cooldown (counted from when the cycle started) prevents another cycle from triggering too soon

Additional behaviours:

- **Minimum run time**: The pump runs for at least 5 seconds before it can be turned off, protecting it from short cycling
- **Startup test**: Optionally runs a brief pump pulse on boot to verify hardware is working correctly (disabled by default)
- **Safe shutdown**: When the service stops for any reason, the pump is guaranteed to turn off
- **Alerts**: Sends a push notification (via ntfy) when light or humidity drops below configured minimums, with a 6-hour cooldown per alert type

All thresholds and timings are configurable via `appsettings.json`.
