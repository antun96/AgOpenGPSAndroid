# ESP32 Bluetooth bridge (tractor)

Firmware that turns a classic ESP32 into a Bluetooth link between the rover RTK receiver and the
AgOpenGPS Android app. It is the low-cost alternative to the Raspberry Pi bridge in
`SourceCode/Android/pi/`: it starts in under a second and does not mind the ignition cutting the power.

```
 RTK base station ──► NTRIP caster ──internet──► Android tablet (AgOpenGPS app, NTRIP on)
                                                     ▲  │
                                       NMEA position │  │ RTCM corrections
                                                     │  ▼  Bluetooth SPP
                                                 ESP32 (this firmware)
                                                     ▲  │
                                                     │  ▼  UART
                                                 Rover receiver (F9P)
```

The ESP32 only passes bytes along. The tablet does NTRIP, so the app settings are:
pick **AgOpenGPS-GNSS** as the device and turn **Send NTRIP corrections** on.

## What you need

- A **classic ESP32** board: ESP32-WROOM-32 / "ESP32 DevKit V1" or similar.
  **Not** ESP32-S2, S3, C3 or C6: they have no Bluetooth Classic, only BLE.
- The rover receiver with a UART (e.g. an F9P board with TX1/RX1 pins).
- A 12 V to 5 V converter for the tractor supply (at least 1 A if the receiver also runs from it).

## Wiring

| Receiver (F9P) | ESP32 DevKit |
|---|---|
| TX1 | GPIO16 (RX2) |
| RX1 | GPIO17 (TX2) |
| GND | GND |
| 5V (if the receiver board takes 5 V) | VIN / 5V |

Both sides use 3.3 V logic, so the wires connect directly. Cross TX to RX. The pins can be
changed at the top of the sketch.

## Flashing (Arduino IDE)

1. Arduino IDE 2: *Boards Manager* → install **esp32 by Espressif Systems**, version 2.0.x or 3.x
   (tested to compile with 2.0.17 and 3.3.12). Do not use 4.x: it drops the Bluetooth serial
   library by default. A "BluetoothSerial is deprecated" warning with 3.x is expected and harmless.
2. Open `AgOpenGPS_BT_Bridge/AgOpenGPS_BT_Bridge.ino`.
3. Board: **ESP32 Dev Module**. Select the USB port of the ESP32.
4. Check the settings at the top of the sketch (`BT_NAME`, `GNSS_BAUD`, pins).
5. Upload. If the upload does not start, hold the board's **BOOT** button until it does.
6. *Serial Monitor* at 115200 baud should print
   `AgOpenGPS BT bridge ready: 'AgOpenGPS-GNSS', receiver UART 115200 baud ...`

## Receiver setup (u-blox F9P, once, with u-center)

The F9P must send NMEA on UART1 and accept RTCM on UART1:

1. *View → Configuration View → PRT (Ports)*: target **UART1**, protocol in **UBX+NMEA+RTCM3**,
   protocol out **UBX+NMEA**, baud **115200** (or 460800, then also change `GNSS_BAUD`).
2. *MSG (Messages)*: enable **NMEA GxGGA** and **NMEA GxVTG** on UART1. Turn other NMEA messages
   off on UART1 to save bandwidth.
3. *RATE*: measurement period **100 ms** (10 Hz) or 200 ms (5 Hz).
4. *CFG*: **Save current configuration** (BBR + Flash), or the settings are lost at power off.

Many AgOpenGPS F9P configuration files already do this. If yours sends `$PANDA` / `$PAOGI`
instead, that works too.

**Bandwidth:** 10 Hz GGA + VTG is about 1.5 KB/s and RTCM up to about 5 KB/s. 115200 baud
carries about 11 KB/s each way, which is enough. Use 460800 if you enable more messages.

## Using it

1. Power the ESP32. The LED gives a short blink every second: waiting for the tablet.
2. On the tablet: Android Bluetooth settings → pair **AgOpenGPS-GNSS** (no PIN needed).
3. In the AgOpenGPS app pick it and press **Connect**. The LED is solid while connected and
   flickers while data flows.
4. Fill in the NTRIP caster of your base station and turn *Send NTRIP corrections* on. The app's
   status should go to **RTK Float**, then **RTK Fix**.

| LED | Meaning |
|---|---|
| Short blink every second | No tablet connected (receiver data is dropped) |
| Solid | Tablet connected, no data at the moment |
| Fast flicker | Data flowing |
| Fast blinking, all the time | Bluetooth failed to start |

## Troubleshooting

- **Connected, but "Waiting for position"**: no NMEA from the receiver. Check TX1 → GPIO16, the
  baud rate, and that NMEA output is on for UART1. The Serial Monitor shows `to tablet 0 B`.
- **RTK Float never becomes Fix, or no Float at all**: corrections do not reach the receiver.
  Check RX1 ← GPIO17 and RTCM3 *in* on UART1. The Serial Monitor shows `to receiver ... B` growing
  when the app sends corrections.
- **Device not in the Android list**: it is not a classic ESP32, or it was flashed with the wrong
  board setting.

# Steer module (AutoSteer)

`AgOpenGPS_Steer/` is the same Bluetooth bridge **plus** an AutoSteer module for an electric motor
on the steering wheel. One ESP32 does both, so the tractor needs only this board, the receiver,
a wheel angle sensor and a motor driver. Use it instead of `AgOpenGPS_BT_Bridge`, not next to it.
It shows up in Bluetooth as **AgOpenGPS-Steer**.

```
 tablet ◄──Bluetooth──► ESP32 ──UART──► receiver (NMEA out, RTCM in)
                          │ ├──I2C──► ADS1115 ◄── wheel angle sensor (potentiometer)
                          │ └──PWM+DIR──► Cytron driver ──► steering wheel motor
                          └── steer switch, work switch, encoder / current sensor (optional)
```

The tablet works out the wheel angle (pure pursuit, like AgOpenGPS); the ESP32 turns the wheel to
that angle and reports the angle it measures. The messages are AgOpenGPS PGN 254/253/252/251.

## What you need

- A classic ESP32 board (as above).
- **Wheel angle sensor**: a potentiometer or a hall-effect angle sensor, fitted on the front
  axle and linked to a steering knuckle, so it turns when the front wheels turn. Supplied with
  3.3 V from the ESP32; its output is near 1.65 V with the wheels straight.
- **ADS1115** analog-to-digital board (I2C, about 3 €). It measures the sensor voltage far more
  precisely and quietly than the ESP32's own input. Without one, set `WAS_INPUT` to
  `WAS_ESP32_ADC` and wire the sensor to GPIO34.
- **Motor driver**: Cytron MD10C / MD13S (PWM + direction, recommended) or IBT-2 (BTS7960).
- The **steering wheel motor** (12 V DC motor with gear and a wheel adapter, e.g. the motors
  sold for AgOpenGPS wheel kits).
- Strongly recommended: a way to notice the driver taking the wheel: an **encoder** on the motor
  or a **current sensor** (ACS712) in the motor wire, or at least a **steer switch** in reach.

## Wiring

| From | To ESP32 |
|---|---|
| Receiver TX1 / RX1 | GPIO16 / GPIO17 (as for the bridge) |
| ADS1115 SDA / SCL | GPIO21 / GPIO22 |
| ADS1115 VDD / GND / ADDR | 3.3V / GND / GND |
| Wheel angle sensor: + / signal / − | 3.3V / ADS1115 **A0** / GND |
| Cytron PWM / DIR (IBT-2: RPWM / LPWM) | GPIO25 / GPIO26 |
| Cytron GND | GND (common ground with the ESP32) |
| Steer switch or button (to GND) | GPIO32 |
| Work switch (to GND) | GPIO33 |
| Encoder output (to GND, open collector) | GPIO27 |
| Current sensor output (ACS712, 3.3 V supply) | GPIO35 |
| Relay or clutch (optional) | GPIO4 (on while steering) |

The motor gets its 12 V through the driver only, with a fuse, never through the ESP32. Keep the
sensor wires away from the motor wires. Pins can be changed at the top of the sketch.

## Flashing

As for the bridge, but open `AgOpenGPS_Steer/AgOpenGPS_Steer.ino`. No extra libraries are needed.
The Serial Monitor (115200) prints every 5 s the wheel angle, the wanted angle, the PWM and
`STEERING`, `SENSOR FAULT` or `TAKE-OVER`.

## First start (tractor standing, engine running, wheels free to turn)

1. App: pair **AgOpenGPS-Steer**, pick it as the receiver, and in **Settings → AutoSteer** tick
   *A steer module is fitted*. Connect. The AutoSteer section shows the live wheel angle.
2. Wheels straight: press **Wheels are straight now: set sensor zero**.
3. Turn the wheels right by hand: the angle must go **positive**. If it goes negative, tick
   *Invert the wheel angle sensor*.
4. **Sensor counts per degree**: turn to full lock, compare the shown angle with the real one
   (measured, or from the tractor's data) and change the value until they match. More counts =
   smaller angle.
5. On a field with a line, at walking speed: switch AutoSteer on. If the wheel turns the wrong
   way (away from the line, then stops at full lock), switch off and tick *Invert the motor direction*.
6. Tune: **Minimum PWM** just enough that the motor moves, **Proportional gain** up until the
   wheel follows briskly without shaking, **Maximum PWM** to limit the speed of the wheel.

## Safety

The motor stops at once when: no command came for 1 s (Bluetooth lost, tablet asleep), the app
switches AutoSteer off, the speed is below the minimum, the sensor reading fails or sits at 0 V
or 3.3 V (broken wire), the steer switch is off, or the encoder / current sensor sees the driver
turning the wheel. After a take-over it only steers again once the app was switched off and on.
Always stay in the seat with your hands near the wheel.
