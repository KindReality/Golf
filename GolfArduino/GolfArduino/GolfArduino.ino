#include <Adafruit_NeoPixel.h>
#include <Bounce2.h>

enum WorkflowState
{
  Idle,
  Launch,
  Active
};

enum SensorMode
{
  SensorModePir,
  SensorModeUltrasonic
};

class BallLauncherController
{
public:
  static const uint8_t PinNotConnected = 255;

  BallLauncherController(
    SensorMode sensorMode,
    uint8_t sensorSignalPin,
    uint8_t ultrasonicEchoPin,
    uint8_t manualTriggerPin,
    uint8_t relay1Pin,
    uint8_t neoPixelPin,
    uint16_t pixelCount,
    float triggerDistanceInches,
    unsigned long triggerHoldMs,
    unsigned long launchDurationMs,
    unsigned long activeDurationMs,
    unsigned long minimumRestMs,
    unsigned long heartbeatIntervalMs)
    : sensorMode(sensorMode),
      pinSensorSignal(sensorSignalPin),
      pinUltrasonicEcho(ultrasonicEchoPin),
      pinManualTrigger(manualTriggerPin),
      pinRelay1(relay1Pin),
      pinNeoPixel(neoPixelPin),
      totalPixels(pixelCount),
      triggerDistanceInches(triggerDistanceInches),
      triggerHoldMs(triggerHoldMs),
      launchDurationMs(launchDurationMs),
      activeDurationMs(activeDurationMs),
      minimumRestMs(minimumRestMs),
      heartbeatIntervalMs(heartbeatIntervalMs),
      pixels(pixelCount, neoPixelPin, NEO_GRB + NEO_KHZ800)
  {
  }

  void begin()
  {
    if (sensorMode == SensorModeUltrasonic)
    {
      pinMode(pinSensorSignal, OUTPUT);
      digitalWrite(pinSensorSignal, LOW);

      if (pinUltrasonicEcho != PinNotConnected)
      {
        pinMode(pinUltrasonicEcho, INPUT);
      }
    }
    else
    {
      pinMode(pinSensorSignal, INPUT);
    }

    pinMode(pinRelay1, OUTPUT);
    setRelayOff(pinRelay1);

    pinMode(pinManualTrigger, INPUT_PULLUP);
    manualTriggerButton.attach(pinManualTrigger);
    manualTriggerButton.interval(10);

    pixels.begin();
    pixels.show();

    setState(Idle);

    lastCycleEndedAtMs = millis() - minimumRestMs;
    lastHeartbeatAtMs = 0;
    triggerActiveStartedAtMs = 0;

    sendDebug("Controller initialized.");
    printDebugStatus();
  }

  void execute()
  {
    unsigned long nowMs = millis();

    manualTriggerButton.update();

    switch (currentState)
    {
      case Idle:
        executeIdle(nowMs);
        break;

      case Launch:
        executeLaunch(nowMs);
        break;

      case Active:
        executeActive(nowMs);
        break;
    }

    sendHeartbeatIfNeeded(nowMs);
    printDebugStatusIfNeeded(nowMs);
  }

private:
  SensorMode sensorMode;

  uint8_t pinSensorSignal;
  uint8_t pinUltrasonicEcho;
  uint8_t pinManualTrigger;
  uint8_t pinRelay1;
  uint8_t pinNeoPixel;
  uint16_t totalPixels;

  float triggerDistanceInches;
  unsigned long triggerHoldMs;
  unsigned long launchDurationMs;
  unsigned long activeDurationMs;
  unsigned long minimumRestMs;
  unsigned long heartbeatIntervalMs;

  WorkflowState currentState = Idle;
  unsigned long stateStartedAtMs = 0;
  unsigned long lastCycleEndedAtMs = 0;
  unsigned long triggerActiveStartedAtMs = 0;
  unsigned long lastHeartbeatAtMs = 0;
  unsigned long lastDebugStatusAtMs = 0;

  float lastDistanceInches = -1.0f;
  bool lastPirTriggered = false;
  bool lastManualTriggerPressed = false;

  const unsigned long debugStatusIntervalMs = 500;

  Adafruit_NeoPixel pixels;
  Bounce manualTriggerButton = Bounce();

  void executeIdle(unsigned long nowMs)
  {
    bool sensorTriggerActive = isSensorTriggerActive();
    bool manualTriggerPressed = isManualTriggerPressed();

    if (!isRestComplete(nowMs))
    {
      triggerActiveStartedAtMs = 0;
      return;
    }

    if (manualTriggerPressed)
    {
      sendProtocolEvent("TRIGGER");
      sendDebug("Manual trigger confirmed.");
      triggerActiveStartedAtMs = 0;
      setState(Launch);
      return;
    }

    if (sensorTriggerActive)
    {
      if (triggerActiveStartedAtMs == 0)
      {
        triggerActiveStartedAtMs = nowMs;
        sendDebug("Sensor trigger became active.");
      }

      if (nowMs - triggerActiveStartedAtMs >= triggerHoldMs)
      {
        sendProtocolEvent("TRIGGER");
        sendDebug("Sensor trigger confirmed.");
        triggerActiveStartedAtMs = 0;
        setState(Launch);
      }
    }
    else
    {
      triggerActiveStartedAtMs = 0;
    }
  }

  void executeLaunch(unsigned long nowMs)
  {
    setRelayOn(pinRelay1);

    if (nowMs - stateStartedAtMs >= launchDurationMs)
    {
      setRelayOff(pinRelay1);
      setState(Active);
    }
  }

  void executeActive(unsigned long nowMs)
  {
    if (nowMs - stateStartedAtMs >= activeDurationMs)
    {
      lastCycleEndedAtMs = nowMs;
      setState(Idle);
    }
  }

  bool isSensorTriggerActive()
  {
    if (sensorMode == SensorModePir)
    {
      lastPirTriggered = readPirTriggered();
      lastDistanceInches = -1.0f;
      return lastPirTriggered;
    }

    lastDistanceInches = readDistanceInches();
    lastPirTriggered = false;

    return lastDistanceInches > 0.0f && lastDistanceInches < triggerDistanceInches;
  }

  bool isManualTriggerPressed()
  {
    lastManualTriggerPressed = manualTriggerButton.fell();
    return lastManualTriggerPressed;
  }

  bool readPirTriggered() const
  {
    return digitalRead(pinSensorSignal) == HIGH;
  }

  float readDistanceInches()
  {
    if (pinUltrasonicEcho == PinNotConnected)
    {
      return -1.0f;
    }

    digitalWrite(pinSensorSignal, LOW);
    delayMicroseconds(2);

    digitalWrite(pinSensorSignal, HIGH);
    delayMicroseconds(10);
    digitalWrite(pinSensorSignal, LOW);

    unsigned long pulseDurationMicroseconds = pulseIn(pinUltrasonicEcho, HIGH, 30000);

    if (pulseDurationMicroseconds == 0)
    {
      return -1.0f;
    }

    return pulseDurationMicroseconds / 148.0f;
  }

  void setState(WorkflowState newState)
  {
    currentState = newState;
    stateStartedAtMs = millis();

    switch (currentState)
    {
      case Idle:
        setRelayOff(pinRelay1);
        setAllPixels(0, 0, 255);
        sendProtocolState("IDLE");
        sendDebug("State changed to Idle.");
        break;

      case Launch:
        setAllPixels(255, 0, 0);
        sendProtocolState("LAUNCH");
        sendDebug("State changed to Launch.");
        break;

      case Active:
        setRelayOff(pinRelay1);
        setAllPixels(0, 255, 0);
        sendProtocolState("ACTIVE");
        sendDebug("State changed to Active.");
        break;
    }
  }

  void setRelayOn(uint8_t pin)
  {
    digitalWrite(pin, LOW);
  }

  void setRelayOff(uint8_t pin)
  {
    digitalWrite(pin, HIGH);
  }

  bool isRelayOn(uint8_t pin) const
  {
    return digitalRead(pin) == LOW;
  }

  bool isRestComplete(unsigned long nowMs) const
  {
    return nowMs - lastCycleEndedAtMs >= minimumRestMs;
  }

  void setAllPixels(uint8_t red, uint8_t green, uint8_t blue)
  {
    for (uint16_t i = 0; i < totalPixels; i++)
    {
      pixels.setPixelColor(i, pixels.Color(red, green, blue));
    }

    pixels.show();
  }

  const char* getStateName() const
  {
    switch (currentState)
    {
      case Idle:
        return "Idle";
      case Launch:
        return "Launch";
      case Active:
        return "Active";
      default:
        return "Unknown";
    }
  }

  const char* getSensorModeName() const
  {
    switch (sensorMode)
    {
      case SensorModePir:
        return "PIR";
      case SensorModeUltrasonic:
        return "Ultrasonic";
      default:
        return "Unknown";
    }
  }

  void sendProtocolState(const char* stateName)
  {
    Serial.print(F("STATE:"));
    Serial.println(stateName);
  }

  void sendProtocolEvent(const char* eventName)
  {
    Serial.print(F("EVENT:"));
    Serial.println(eventName);
  }

  void sendProtocolHeartbeat()
  {
    Serial.println(F("HEARTBEAT"));
  }

  void sendDebug(const char* message)
  {
    Serial.print(F("DEBUG: "));
    Serial.println(message);
  }

  void printDebugStatus()
  {
    unsigned long nowMs = millis();

    Serial.print(F("DEBUG: Status | State: "));
    Serial.print(getStateName());
    Serial.print(F(" | SensorMode: "));
    Serial.print(getSensorModeName());
    Serial.print(F(" | Relay1: "));
    Serial.print(isRelayOn(pinRelay1) ? F("ON") : F("OFF"));
    Serial.print(F(" | ManualButtonEvent: "));
    Serial.print(lastManualTriggerPressed ? F("PRESSED") : F("NONE"));

    if (sensorMode == SensorModePir)
    {
      Serial.print(F(" | PirTriggered: "));
      Serial.print(lastPirTriggered ? F("YES") : F("NO"));
    }
    else
    {
      Serial.print(F(" | DistanceInches: "));
      if (lastDistanceInches > 0.0f)
      {
        Serial.print(lastDistanceInches, 2);
      }
      else
      {
        Serial.print(F("no reading"));
      }
    }

    Serial.print(F(" | RestRemainingMs: "));
    if (isRestComplete(nowMs))
    {
      Serial.print(0);
    }
    else
    {
      Serial.print(minimumRestMs - (nowMs - lastCycleEndedAtMs));
    }

    Serial.print(F(" | TriggerHoldRemainingMs: "));
    if (triggerActiveStartedAtMs == 0)
    {
      Serial.println(0);
    }
    else
    {
      unsigned long heldMs = nowMs - triggerActiveStartedAtMs;
      unsigned long remainingMs = heldMs >= triggerHoldMs ? 0 : (triggerHoldMs - heldMs);
      Serial.println(remainingMs);
    }
  }

  void printDebugStatusIfNeeded(unsigned long nowMs)
  {
    if (nowMs - lastDebugStatusAtMs >= debugStatusIntervalMs)
    {
      lastDebugStatusAtMs = nowMs;
      printDebugStatus();
      lastManualTriggerPressed = false;
    }
  }

  void sendHeartbeatIfNeeded(unsigned long nowMs)
  {
    if (nowMs - lastHeartbeatAtMs >= heartbeatIntervalMs)
    {
      lastHeartbeatAtMs = nowMs;
      sendProtocolHeartbeat();
    }
  }
};

// RJ45 to Arduino mapping currently in use
// RJ45 pin 8 -> Arduino 7 -> Relay
// RJ45 pin 7 -> Arduino 6 -> PIR signal now, ultrasonic trigger later if needed
// RJ45 pin 6 -> Arduino 5 -> Manual trigger button, normally open
// RJ45 pin 5 -> Arduino 3
// RJ45 pin 4 -> Arduino 4
// RJ45 pin 3 -> Arduino 2 -> NeoPixel

const SensorMode sensorMode = SensorModePir;

const uint8_t pinSensorSignal = 6;
const uint8_t pinUltrasonicEcho = BallLauncherController::PinNotConnected;
const uint8_t pinManualTrigger = 5;
const uint8_t pinRelay1 = 7;
const uint8_t pinNeoPixel = 2;

const uint16_t pixelCount = 10;
const float triggerDistanceInches = 4.5f;
const unsigned long triggerHoldMs = 1000;
const unsigned long launchDurationMs = 3000;
const unsigned long activeDurationMs = 5000;
const unsigned long minimumRestMs = 5000;
const unsigned long heartbeatIntervalMs = 1000;

BallLauncherController controller(
  sensorMode,
  pinSensorSignal,
  pinUltrasonicEcho,
  pinManualTrigger,
  pinRelay1,
  pinNeoPixel,
  pixelCount,
  triggerDistanceInches,
  triggerHoldMs,
  launchDurationMs,
  activeDurationMs,
  minimumRestMs,
  heartbeatIntervalMs
);

void setup()
{
  delay(8000);
  Serial.begin(115200);
  controller.begin();
}

void loop()
{
  controller.execute();
}