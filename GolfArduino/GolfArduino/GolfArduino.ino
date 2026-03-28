#include <Adafruit_NeoPixel.h>

enum WorkflowState
{
  Idle,
  Launch,
  Active
};

class BallLauncherController
{
public:
  BallLauncherController(
    uint8_t ultrasonicTriggerPin,
    uint8_t ultrasonicEchoPin,
    uint8_t relay1Pin,
    uint8_t neoPixelPin,
    uint16_t pixelCount,
    float triggerDistanceInches,
    unsigned long triggerHoldMs,
    unsigned long launchDurationMs,
    unsigned long activeDurationMs,
    unsigned long minimumRestMs,
    unsigned long heartbeatIntervalMs)
    : pinUltrasonicTrigger(ultrasonicTriggerPin),
      pinUltrasonicEcho(ultrasonicEchoPin),
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
    pinMode(pinUltrasonicTrigger, OUTPUT);
    pinMode(pinUltrasonicEcho, INPUT);
    pinMode(pinRelay1, OUTPUT);

    digitalWrite(pinUltrasonicTrigger, LOW);
    setRelayOff(pinRelay1);

    pixels.begin();
    pixels.show();

    setState(Idle);

    lastCycleEndedAtMs = millis() - minimumRestMs;
    lastHeartbeatAtMs = 0;
    triggerBelowThresholdStartedAtMs = 0;

    sendDebug("Controller initialized.");
    printDebugStatus();
  }

  void execute()
  {
    unsigned long nowMs = millis();

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
  uint8_t pinUltrasonicTrigger;
  uint8_t pinUltrasonicEcho;
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
  unsigned long triggerBelowThresholdStartedAtMs = 0;
  unsigned long lastHeartbeatAtMs = 0;
  unsigned long lastDebugStatusAtMs = 0;

  float lastDistanceInches = -1.0f;

  const unsigned long debugStatusIntervalMs = 500;

  Adafruit_NeoPixel pixels;

  void executeIdle(unsigned long nowMs)
  {
    lastDistanceInches = readDistanceInches();

    if (!isRestComplete(nowMs))
    {
      triggerBelowThresholdStartedAtMs = 0;
      return;
    }

    if (lastDistanceInches > 0.0f && lastDistanceInches < triggerDistanceInches)
    {
      if (triggerBelowThresholdStartedAtMs == 0)
      {
        triggerBelowThresholdStartedAtMs = nowMs;
        sendDebug("Distance went below trigger threshold.");
      }

      if (nowMs - triggerBelowThresholdStartedAtMs >= triggerHoldMs)
      {
        sendProtocolEvent("TRIGGER");
        sendDebug("Trigger confirmed.");
        triggerBelowThresholdStartedAtMs = 0;
        setState(Launch);
      }
    }
    else
    {
      triggerBelowThresholdStartedAtMs = 0;
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

  float readDistanceInches()
  {
    digitalWrite(pinUltrasonicTrigger, LOW);
    delayMicroseconds(2);

    digitalWrite(pinUltrasonicTrigger, HIGH);
    delayMicroseconds(10);
    digitalWrite(pinUltrasonicTrigger, LOW);

    unsigned long pulseDurationMicroseconds = pulseIn(pinUltrasonicEcho, HIGH, 30000);

    if (pulseDurationMicroseconds == 0)
    {
      return -1.0f;
    }

    return pulseDurationMicroseconds / 148.0f;
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
    Serial.print(F(" | Relay1: "));
    Serial.print(isRelayOn(pinRelay1) ? F("ON") : F("OFF"));
    Serial.print(F(" | DistanceInches: "));

    if (lastDistanceInches > 0.0f)
    {
      Serial.print(lastDistanceInches, 2);
    }
    else
    {
      Serial.print(F("no reading"));
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

    if (triggerBelowThresholdStartedAtMs == 0)
    {
      Serial.println(0);
    }
    else
    {
      unsigned long heldMs = nowMs - triggerBelowThresholdStartedAtMs;
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

const uint8_t pinUltrasonicEcho = 2;
const uint8_t pinUltrasonicTrigger = 4;
const uint8_t pinRelay1 = 5;
const uint8_t pinNeoPixel = 8;

const uint16_t pixelCount = 10;
const float triggerDistanceInches = 3.0f;
const unsigned long triggerHoldMs = 1000;
const unsigned long launchDurationMs = 3000;
const unsigned long activeDurationMs = 5000;
const unsigned long minimumRestMs = 5000;
const unsigned long heartbeatIntervalMs = 1000;

BallLauncherController controller(
  pinUltrasonicTrigger,
  pinUltrasonicEcho,
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