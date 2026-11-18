/*
 * AgOpenGPS steer module + Bluetooth GNSS bridge for ESP32
 *
 * One ESP32 on the tractor does both jobs for the AgOpenGPS Android app:
 *
 *   receiver --UART--> ESP32 --Bluetooth--> tablet   NMEA position
 *   receiver <--UART-- ESP32 <--Bluetooth-- tablet   RTCM corrections (NTRIP in the app)
 *   motor    <--PWM--- ESP32 <--Bluetooth-- tablet   PGN 254: wanted wheel angle, on/off, speed
 *   sensor   --I2C---> ESP32 --Bluetooth--> tablet   PGN 253: actual wheel angle, switches, PWM
 *                               <---------- tablet   PGN 252 / 251: steer settings (stored here)
 *
 * The tablet sends PGN frames only between whole RTCM frames, so this firmware splits the
 * incoming stream by the same rule: RTCM frames (0xD3 ...) and loose bytes go to the receiver,
 * PGN frames (0x80 0x81 ...) are for the steer module. The messages and the motor control are
 * the same as the AgOpenGPS steer firmware, so the app's settings mean the same thing.
 *
 * Safety: the motor stops when no PGN 254 arrived for WATCHDOG_MS, when the app switches
 * steering off, below the minimum speed, when the wheel angle sensor fails, and when the driver
 * takes the wheel (steer switch, encoder or current sensor). It only starts again after the app
 * switched steering off and on.
 *
 * Board: a classic ESP32 (ESP32-WROOM-32 / DevKit), "ESP32 Dev Module". Not S2/S3/C3/C6 (no
 * Bluetooth Classic). Wiring and setup: README.md in the folder above.
 */

#include <Arduino.h>
#include <BluetoothSerial.h>
#include <Preferences.h>
#include <Wire.h>

#if !defined(CONFIG_BT_ENABLED) || !defined(CONFIG_BLUEDROID_ENABLED) || !defined(CONFIG_BT_SPP_ENABLED)
#error "Bluetooth Classic SPP is not available: use a classic ESP32 (not S2/S3/C3/C6) and board 'ESP32 Dev Module'."
#endif

// ================================================================= settings

static const char *BT_NAME = "AgOpenGPS-Steer";

// Receiver UART (must match the receiver's UART1 baud rate).
static const uint32_t GNSS_BAUD = 115200;
static const int GNSS_RX_PIN = 16;  // <- receiver TX1
static const int GNSS_TX_PIN = 17;  // -> receiver RX1

// Wheel angle sensor (potentiometer or hall sensor, 0..3.3 V, middle = wheels straight).
#define WAS_ADS1115 1   // ADS1115 board on I2C, input A0 (recommended: precise and quiet)
#define WAS_ESP32_ADC 2 // the ESP32's own analog input (works, but noisier)
#define WAS_INPUT WAS_ADS1115
static const int I2C_SDA_PIN = 21;
static const int I2C_SCL_PIN = 22;
static const uint8_t ADS1115_ADDRESS = 0x48;  // ADDR pin to GND
static const int WAS_ADC_PIN = 34;            // for WAS_ESP32_ADC
// Sensor counts for wheels straight before the offset from the app: both inputs give 4 counts
// per millivolt here, so 6600 is 1.65 V, the middle of a 3.3 V potentiometer.
static const int WAS_CENTER = 6600;

// Motor driver. Cytron (MD10C, MD13S...): PWM + direction. IBT-2 (BTS7960): two PWM inputs.
// Which one is set in the app (Cytron motor driver tick box).
static const int MOTOR_PWM_PIN = 25;  // Cytron PWM   / IBT-2 RPWM
static const int MOTOR_DIR_PIN = 26;  // Cytron DIR   / IBT-2 LPWM
static const uint32_t MOTOR_PWM_HZ = 20000;  // above hearing
// Optional output that is on while steering (e.g. a motor clutch or a relay). -1 = none.
static const int STEER_RELAY_PIN = 4;

// Inputs to GND, with internal pull-ups. -1 = not fitted.
static const int STEER_SWITCH_PIN = 32;  // steer switch or push button (type set in the app)
static const int WORK_SWITCH_PIN = 33;   // implement work switch (reported to the app)
static const int ENCODER_PIN = 27;       // pulses from a wheel or motor encoder (take-over detection)
static const int CURRENT_PIN = 35;       // motor current sensor output, e.g. ACS712 (take-over detection)

static const int LED_PIN = 2;        // blinks: Bluetooth / data; -1 = none
static const int STEER_LED_PIN = -1; // on while steering; -1 = none

static const uint32_t WATCHDOG_MS = 1000;  // no PGN 254 for this long: stop the motor
static const bool DEBUG_STATS = true;      // statistics on USB serial every 5 s

// ================================================================= protocol

static const uint8_t PGN_STEER_DATA = 0xFE;  // 254 from the app
static const uint8_t PGN_FEEDBACK = 0xFD;    // 253 to the app
static const uint8_t PGN_SETTINGS = 0xFC;    // 252 from the app
static const uint8_t PGN_CONFIG = 0xFB;      // 251 from the app

struct SteerSettings {
  uint8_t kp = 50;
  uint8_t highPwm = 180;
  uint8_t lowPwm = 60;
  uint8_t minPwm = 25;
  uint8_t countsPerDegree = 100;
  int16_t wasOffset = 0;
  uint8_t ackermann = 100;
};

struct SteerConfig {
  bool invertWas = false;
  bool relayActiveHigh = false;
  bool motorDirection = false;
  bool singleInputWas = true;
  bool cytron = true;
  bool steerSwitch = false;
  bool steerButton = false;
  bool shaftEncoder = false;
  uint8_t maxPulse = 3;
  uint8_t minSpeedX10 = 10;  // km/h * 10
  bool currentSensor = false;
};

// ================================================================= state

static BluetoothSerial SerialBT;
static HardwareSerial GNSS(2);
static Preferences prefs;
static SteerSettings settings;
static SteerConfig config;

// From the app (PGN 254).
static bool appSteerOn = false;
static float setpointDeg = 0;
static float speedKmh = 0;
static uint32_t lastCommandMs = 0;
static bool haveCommand = false;

// Steering.
static float actualDeg = 0;
static bool wasOk = false;
static int16_t pwmDrive = 0;
static bool steering = false;
static bool takeOver = false;        // driver took the wheel: off until the app switches off
static bool buttonOn = false;        // push button mode: toggled state
static bool lastButtonLevel = HIGH;
static volatile uint32_t encoderPulses = 0;
static float currentFiltered = 0;

// Bluetooth output: receiver data is sent up to line ends, so a PGN 253 never lands inside an
// NMEA sentence (the app would drop that sentence).
static uint8_t txBuf[1024];
static size_t txLen = 0;
static uint32_t txPartialSinceMs = 0;
static uint8_t feedbackFrame[14];
static bool feedbackPending = false;

// Tablet -> ESP32 stream splitter.
enum InState { IN_IDLE, IN_RTCM, IN_PGN_HEADER2, IN_PGN_BODY };
static InState inState = IN_IDLE;
static int rtcmPos = 0, rtcmTotal = 0;
static uint8_t pgnBuf[5 + 255 + 1];
static int pgnPos = 0, pgnNeed = 0;
static uint8_t gnssOut[1100];
static size_t gnssOutLen = 0;

// Statistics.
static uint32_t statFrames = 0, statBadFrames = 0, statToReceiver = 0, statToTablet = 0;
static uint32_t lastStatsMs = 0, lastDataMs = 0, lastSteerLoopMs = 0;
static bool wasConnected = false;

// ================================================================= settings storage

static void loadSettings() {
  prefs.begin("steer", true);
  if (prefs.getBytesLength("set") == sizeof(SteerSettings)) prefs.getBytes("set", &settings, sizeof(SteerSettings));
  if (prefs.getBytesLength("cfg") == sizeof(SteerConfig)) prefs.getBytes("cfg", &config, sizeof(SteerConfig));
  prefs.end();
}

static void saveIfChanged(const char *key, const void *value, size_t len) {
  uint8_t old[64];
  prefs.begin("steer", false);
  bool same = prefs.getBytesLength(key) == len && len <= sizeof(old) && prefs.getBytes(key, old, len) == len &&
              memcmp(old, value, len) == 0;
  if (!same) prefs.putBytes(key, value, len);  // flash is only written when something changed
  prefs.end();
}

// ================================================================= wheel angle sensor

static bool adsStart() {
  // Continuous conversion, A0 to GND, +-4.096 V, 860 samples/s, comparator off.
  Wire.beginTransmission(ADS1115_ADDRESS);
  Wire.write(0x01);
  Wire.write(0x42);
  Wire.write(0xE3);
  return Wire.endTransmission() == 0;
}

static bool adsRead(int16_t &value) {
  Wire.beginTransmission(ADS1115_ADDRESS);
  Wire.write(0x00);
  if (Wire.endTransmission() != 0) return false;
  if (Wire.requestFrom((int)ADS1115_ADDRESS, 2) != 2) return false;
  value = (int16_t)((Wire.read() << 8) | Wire.read());
  return true;
}

/** Reads the sensor in counts (4 per mV); false if it failed or sits at a rail (wire broken). */
static bool readWasCounts(int32_t &counts) {
#if WAS_INPUT == WAS_ADS1115
  int16_t raw;
  if (!adsRead(raw)) {
    adsStart();  // the ADS may have been reset by a voltage dip
    return false;
  }
  counts = raw >> 1;  // 0.125 mV per bit -> 4 counts per mV
#else
  counts = (int32_t)analogReadMilliVolts(WAS_ADC_PIN) * 4;
#endif
  return counts > 200 && counts < 13000;  // 50 mV from either end: disconnected or shorted
}

static void readWheelAngle() {
  int32_t counts;
  wasOk = readWasCounts(counts);
  if (!wasOk) return;
  // As the AgOpenGPS firmware: centre and offset, optional inversion, counts per degree, Ackermann on the left side.
  float pos = (float)(counts - WAS_CENTER + settings.wasOffset);
  if (config.invertWas) pos = -pos;
  float deg = pos / (float)(settings.countsPerDegree > 0 ? settings.countsPerDegree : 1);
  if (deg < 0) deg *= settings.ackermann * 0.01f;
  actualDeg = deg;
}

// ================================================================= motor

static void pwmAttach(int pin, int channel) {
#if ESP_ARDUINO_VERSION_MAJOR >= 3
  (void)channel;
  ledcAttach(pin, MOTOR_PWM_HZ, 8);
#else
  ledcSetup(channel, MOTOR_PWM_HZ, 8);
  ledcAttachPin(pin, channel);
#endif
}

static void pwmWrite(int pin, int channel, uint32_t duty) {
#if ESP_ARDUINO_VERSION_MAJOR >= 3
  (void)channel;
  ledcWrite(pin, duty);
#else
  (void)pin;
  ledcWrite(channel, duty);
#endif
}

static bool motorPinsAreCytron = true;

static void motorSetup() {
  motorPinsAreCytron = config.cytron;
  pwmAttach(MOTOR_PWM_PIN, 0);
  if (motorPinsAreCytron) {
    pinMode(MOTOR_DIR_PIN, OUTPUT);
    digitalWrite(MOTOR_DIR_PIN, LOW);
  } else {
    pwmAttach(MOTOR_DIR_PIN, 1);
  }
}

/** Drives the motor: positive = turn the wheel right. 0 stops it. */
static void motorDrive(int16_t pwm) {
  if (config.cytron != motorPinsAreCytron) ESP.restart();  // driver type changed in the app: re-set the pins cleanly
  uint32_t duty = (uint32_t)min(255, abs((int)pwm));
  if (config.cytron) {
    digitalWrite(MOTOR_DIR_PIN, pwm >= 0 ? HIGH : LOW);
    pwmWrite(MOTOR_PWM_PIN, 0, duty);
  } else {
    pwmWrite(MOTOR_PWM_PIN, 0, pwm > 0 ? duty : 0);
    pwmWrite(MOTOR_DIR_PIN, 1, pwm < 0 ? duty : 0);
  }
}

/**
 * The AgOpenGPS calcSteeringPID: proportional plus minimum PWM, limited to the low PWM near the
 * target and the high PWM further away. Returned with the firmware's sign (positive = wheel is
 * right of the setpoint), so the motor turns the other way.
 */
static int16_t steeringPid() {
  float error = actualDeg - setpointDeg;
  int16_t drive = (int16_t)(settings.kp * error);
  float errorAbs = fabsf(error);
  float highLowPerDeg = ((float)settings.highPwm - settings.lowPwm) / 3.0f;
  int16_t newMax = errorAbs < 3.0f ? (int16_t)(errorAbs * highLowPerDeg + settings.lowPwm) : settings.highPwm;
  if (drive < 0) drive -= settings.minPwm;
  else if (drive > 0) drive += settings.minPwm;
  if (drive > newMax) drive = newMax;
  else if (drive < -newMax) drive = -newMax;
  if (config.motorDirection) drive = -drive;
  return drive;
}

// ================================================================= switches and take-over

static void IRAM_ATTR onEncoderPulse() { encoderPulses = encoderPulses + 1; }

/** Steer switch bit for PGN 253: true (high) = off. */
static bool steerSwitchHigh() {
  if (takeOver) return true;
  if (config.steerSwitch && STEER_SWITCH_PIN >= 0) return digitalRead(STEER_SWITCH_PIN) == HIGH;
  if (config.steerButton && STEER_SWITCH_PIN >= 0) return !buttonOn;
  return false;  // no switch: only the tablet switches steering
}

static void readSwitches() {
  if (config.steerButton && STEER_SWITCH_PIN >= 0) {
    bool level = digitalRead(STEER_SWITCH_PIN);
    if (level == LOW && lastButtonLevel == HIGH) buttonOn = !buttonOn;  // pressed
    lastButtonLevel = level;
  }

  // Take-over: the driver turns the wheel against (or without) the motor.
  if (steering && config.shaftEncoder && ENCODER_PIN >= 0 && encoderPulses >= config.maxPulse) takeOver = true;
  if (config.currentSensor && CURRENT_PIN >= 0) {
    // 0..255 like the AgOpenGPS sensor value: mV from the sensor's zero (about 1.65 V), filtered.
    float mv = fabsf((float)analogReadMilliVolts(CURRENT_PIN) - 1650.0f);
    currentFiltered = currentFiltered * 0.8f + min(255.0f, mv * 0.2f) * 0.2f;
    // maxPulse from the app is the threshold, x10 to give a usable range.
    if (steering && currentFiltered > config.maxPulse * 10.0f) takeOver = true;
  }
  if (!steering) encoderPulses = 0;
}

// ================================================================= the steer loop, 100 Hz

static void steerLoop() {
  readWheelAngle();
  readSwitches();

  bool fresh = haveCommand && millis() - lastCommandMs < WATCHDOG_MS;
  bool switchOk = !(config.steerSwitch && STEER_SWITCH_PIN >= 0 && digitalRead(STEER_SWITCH_PIN) == HIGH);
  bool fastEnough = speedKmh * 10.0f >= config.minSpeedX10;
  steering = fresh && appSteerOn && wasOk && !takeOver && switchOk && fastEnough;

  pwmDrive = steering ? steeringPid() : 0;
  // Positive PID = wheel right of the setpoint: drive it left.
  motorDrive(steering ? -pwmDrive : 0);

  if (STEER_RELAY_PIN >= 0) digitalWrite(STEER_RELAY_PIN, steering == config.relayActiveHigh ? HIGH : LOW);
  if (STEER_LED_PIN >= 0) digitalWrite(STEER_LED_PIN, steering ? HIGH : LOW);
}

// ================================================================= messages

static uint8_t crc(const uint8_t *f, int len) {
  uint8_t sum = 0;
  for (int i = 2; i < len; i++) sum += f[i];
  return sum;
}

static void buildFeedback() {
  uint8_t *f = feedbackFrame;
  int16_t angle = (int16_t)lroundf(actualDeg * 100.0f);
  f[0] = 0x80; f[1] = 0x81; f[2] = 0x7E; f[3] = PGN_FEEDBACK; f[4] = 8;
  f[5] = (uint8_t)angle; f[6] = (uint8_t)(angle >> 8);
  f[7] = (uint8_t)9999; f[8] = (uint8_t)(9999 >> 8);   // no IMU heading
  f[9] = (uint8_t)8888; f[10] = (uint8_t)(8888 >> 8);  // no IMU roll
  bool work = WORK_SWITCH_PIN >= 0 && digitalRead(WORK_SWITCH_PIN) == HIGH;
  f[11] = (work ? 1 : 0) | (steerSwitchHigh() ? 2 : 0);
  f[12] = (uint8_t)min(255, abs((int)pwmDrive));
  f[13] = crc(f, 13);
  feedbackPending = true;
}

static void handlePgn(const uint8_t *f, int len) {
  if (len != 14) return;  // all steer messages are 14 bytes
  switch (f[3]) {
    case PGN_STEER_DATA: {
      speedKmh = (f[5] | (f[6] << 8)) * 0.1f;
      bool on = f[7] == 1;
      if (!on) takeOver = false;            // the app switched off: may steer again next time
      if (on && !appSteerOn) buttonOn = true;
      if (!on && appSteerOn) buttonOn = false;
      appSteerOn = on;
      setpointDeg = (int16_t)(f[8] | (f[9] << 8)) * 0.01f;
      lastCommandMs = millis();
      haveCommand = true;
      buildFeedback();  // answer every command, like the AgOpenGPS firmware
      break;
    }
    case PGN_SETTINGS:
      settings.kp = f[5];
      settings.highPwm = f[6];
      settings.lowPwm = f[7];
      settings.minPwm = f[8];
      settings.countsPerDegree = f[9];
      settings.wasOffset = (int16_t)(f[10] | (f[11] << 8));
      settings.ackermann = f[12];
      saveIfChanged("set", &settings, sizeof(settings));
      break;
    case PGN_CONFIG: {
      uint8_t s0 = f[5], s1 = f[8];
      config.invertWas = s0 & 1;
      config.relayActiveHigh = s0 & 2;
      config.motorDirection = s0 & 4;
      config.singleInputWas = s0 & 8;
      config.cytron = s0 & 16;
      config.steerSwitch = s0 & 32;
      config.steerButton = s0 & 64;
      config.shaftEncoder = s0 & 128;
      config.maxPulse = f[6];
      config.minSpeedX10 = f[7];
      config.currentSensor = s1 & 4;
      saveIfChanged("cfg", &config, sizeof(config));
      break;
    }
  }
}

static void toReceiver(uint8_t b) {
  gnssOut[gnssOutLen++] = b;
  if (gnssOutLen == sizeof(gnssOut)) {
    GNSS.write(gnssOut, gnssOutLen);
    statToReceiver += gnssOutLen;
    gnssOutLen = 0;
  }
}

/** One byte from the tablet: RTCM and loose bytes to the receiver, PGN frames to handlePgn. */
static void fromTablet(uint8_t b) {
  switch (inState) {
    case IN_IDLE:
      if (b == 0xD3) {
        inState = IN_RTCM;
        rtcmPos = 1;
        rtcmTotal = 0;
        toReceiver(b);
      } else if (b == 0x80) {
        inState = IN_PGN_HEADER2;
      } else {
        toReceiver(b);
      }
      break;

    case IN_RTCM:
      toReceiver(b);
      rtcmPos++;
      if (rtcmPos == 2) rtcmTotal = (b & 0x03) << 8;
      else if (rtcmPos == 3) rtcmTotal = 3 + (rtcmTotal | b) + 3;
      else if (rtcmPos >= rtcmTotal) inState = IN_IDLE;
      break;

    case IN_PGN_HEADER2:
      if (b == 0x81) {
        pgnBuf[0] = 0x80;
        pgnBuf[1] = 0x81;
        pgnPos = 2;
        pgnNeed = 5;
        inState = IN_PGN_BODY;
      } else {
        // Not a PGN after all: the 0x80 was data for the receiver.
        toReceiver(0x80);
        inState = IN_IDLE;
        fromTablet(b);
      }
      break;

    case IN_PGN_BODY:
      pgnBuf[pgnPos++] = b;
      if (pgnPos == 5) pgnNeed = 5 + b + 1;
      if (pgnPos == pgnNeed) {
        inState = IN_IDLE;
        if (pgnBuf[pgnNeed - 1] == crc(pgnBuf, pgnNeed - 1)) {
          statFrames++;
          handlePgn(pgnBuf, pgnNeed);
        } else {
          statBadFrames++;
        }
      }
      break;
  }
}

/** Sends receiver data up to the last line end, then a waiting PGN 253. */
static void flushToTablet(bool connected) {
  if (!connected) {
    txLen = 0;
    feedbackPending = false;
    return;
  }
  size_t upTo = 0;
  for (size_t i = txLen; i > 0; i--) {
    if (txBuf[i - 1] == '\n') { upTo = i; break; }
  }
  // Binary data without line ends (e.g. UBX), or a sentence stuck for 100 ms: send it all.
  bool stuck = txLen > 0 && upTo == 0 && (txLen > 600 || millis() - txPartialSinceMs > 100);
  if (stuck) upTo = txLen;
  if (upTo > 0) {
    SerialBT.write(txBuf, upTo);
    statToTablet += upTo;
    memmove(txBuf, txBuf + upTo, txLen - upTo);
    txLen -= upTo;
    txPartialSinceMs = millis();
  }
  if (feedbackPending && (txLen == 0 || stuck)) {
    SerialBT.write(feedbackFrame, sizeof(feedbackFrame));
    feedbackPending = false;
  }
}

// ================================================================= LED

static void updateLed(bool connected) {
  if (LED_PIN < 0) return;
  uint32_t now = millis();
  bool on;
  if (!connected) on = (now % 1000) < 100;                  // short blink: waiting for the tablet
  else if (steering) on = (now % 400) < 300;                // long blink: steering
  else if (now - lastDataMs < 200) on = (now % 100) < 50;   // flicker: data flowing
  else on = true;                                           // solid: connected, no data
  digitalWrite(LED_PIN, on ? HIGH : LOW);
}

// ================================================================= setup and loop

void setup() {
  Serial.begin(115200);
  loadSettings();

  if (LED_PIN >= 0) pinMode(LED_PIN, OUTPUT);
  if (STEER_LED_PIN >= 0) pinMode(STEER_LED_PIN, OUTPUT);
  if (STEER_RELAY_PIN >= 0) {
    pinMode(STEER_RELAY_PIN, OUTPUT);
    digitalWrite(STEER_RELAY_PIN, config.relayActiveHigh ? LOW : HIGH);  // off
  }
  if (STEER_SWITCH_PIN >= 0) pinMode(STEER_SWITCH_PIN, INPUT_PULLUP);
  if (WORK_SWITCH_PIN >= 0) pinMode(WORK_SWITCH_PIN, INPUT_PULLUP);
  if (ENCODER_PIN >= 0) {
    pinMode(ENCODER_PIN, INPUT_PULLUP);
    attachInterrupt(digitalPinToInterrupt(ENCODER_PIN), onEncoderPulse, FALLING);
  }
  motorSetup();
  motorDrive(0);

#if WAS_INPUT == WAS_ADS1115
  Wire.begin(I2C_SDA_PIN, I2C_SCL_PIN, 400000);
  if (!adsStart()) Serial.println("ADS1115 not found: check SDA/SCL wiring and address (steering stays off)");
#else
  analogReadResolution(12);
  analogSetPinAttenuation(WAS_ADC_PIN, ADC_11db);
#endif
  if (CURRENT_PIN >= 0) analogSetPinAttenuation(CURRENT_PIN, ADC_11db);

  GNSS.setRxBufferSize(4096);
  GNSS.begin(GNSS_BAUD, SERIAL_8N1, GNSS_RX_PIN, GNSS_TX_PIN);

  if (!SerialBT.begin(BT_NAME)) {
    Serial.println("Bluetooth init failed");
    while (true) {
      motorDrive(0);
      if (LED_PIN >= 0) digitalWrite(LED_PIN, (millis() % 200) < 100);
      delay(10);
    }
  }
  Serial.printf("AgOpenGPS steer module ready: '%s', receiver %lu baud, gain %u, counts/deg %u, offset %d\n",
                BT_NAME, (unsigned long)GNSS_BAUD, settings.kp, settings.countsPerDegree, settings.wasOffset);
}

void loop() {
  bool connected = SerialBT.hasClient();
  if (connected != wasConnected) {
    wasConnected = connected;
    Serial.println(connected ? "Tablet connected" : "Tablet disconnected");
    if (!connected) appSteerOn = false;  // also stopped by the watchdog; this is just quicker
  }

  // Steering at 100 Hz.
  uint32_t now = millis();
  if (now - lastSteerLoopMs >= 10) {
    lastSteerLoopMs = now;
    steerLoop();
  }

  // Receiver -> tablet.
  int avail = GNSS.available();
  if (avail > 0) {
    if (txLen == 0) txPartialSinceMs = millis();
    size_t room = sizeof(txBuf) - txLen;
    size_t n = GNSS.readBytes(txBuf + txLen, min((size_t)avail, room));
    txLen += n;
    lastDataMs = millis();
  }
  flushToTablet(connected);

  // Tablet -> receiver and steer module.
  int fromBt = SerialBT.available();
  for (int i = 0; i < fromBt; i++) {
    int b = SerialBT.read();
    if (b < 0) break;
    fromTablet((uint8_t)b);
  }
  if (gnssOutLen > 0) {
    GNSS.write(gnssOut, gnssOutLen);
    statToReceiver += gnssOutLen;
    gnssOutLen = 0;
  }
  if (fromBt > 0) lastDataMs = millis();

  updateLed(connected);

  if (DEBUG_STATS && millis() - lastStatsMs >= 5000) {
    lastStatsMs = millis();
    Serial.printf("tablet:%s  wheel %.1f -> %.1f deg  pwm %d  %s%s%s  frames %lu (bad %lu)  to tablet %lu B  to receiver %lu B\n",
                  connected ? "yes" : "no", actualDeg, setpointDeg, pwmDrive,
                  steering ? "STEERING" : "off", wasOk ? "" : "  SENSOR FAULT", takeOver ? "  TAKE-OVER" : "",
                  (unsigned long)statFrames, (unsigned long)statBadFrames, (unsigned long)statToTablet,
                  (unsigned long)statToReceiver);
  }

  if (avail <= 0 && fromBt <= 0) delay(1);  // idle: let the Bluetooth stack run
}
