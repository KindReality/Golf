#include <Adafruit_NeoPixel.h>
#include <Bounce2.h>

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
    uint8_t pirPin,
    uint8_t manualTriggerPin,
    uint8_t relayPin,
    uint8_t neoPixelPin,
    uint16_t pixelCount,
    unsigned long launchDurationMs,
    unsigned long activeDurationMs,
    unsigned long minimumRestMs,
    unsigned long heartbeatIntervalMs)
    : pinPir(pirPin),
      pinManualTrigger(manualTriggerPin),
      pinRelay(relayPin),
      pinNeoPixel(neoPixelPin),
      totalPixels(pixelCount),
      launchDurationMs(launchDurationMs),
      activeDurationMs(activeDurationMs),
      minimumRestMs(minimumRestMs),
      heartbeatIntervalMs(heartbeatIntervalMs),
      pixels(pixelCount, neoPixelPin, NEO_GRB + NEO_KHZ800)
  {
  }

  void begin()
  {
    pinMode(pinPir, INPUT);
    pinMode(pinRelay, OUTPUT);

    pinMode(pinManualTrigger, INPUT_PULLUP);
    manualTriggerButton.attach(pinManualTrigger);
    manualTriggerButton.interval(10);

    setRelayOff();

    pixels.begin();
    pixels.show();

    setState(Idle);

    lastCycleEndedAtMs = millis() - minimumRestMs;
    lastHeartbeatAtMs = 0;

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
  uint8_t pinPir;
  uint8_t pinManualTrigger;
  uint8_t pinRelay;
  uint8_t pinNeoPixel;
  uint16_t totalPixels;

  unsigned long launchDurationMs;
  unsigned long activeDurationMs;
  unsigned long minimumRestMs;
  unsigned long heartbeatIntervalMs;

  WorkflowState currentState = Idle;
  unsigned long stateStartedAtMs = 0;
  unsigned long lastCycleEndedAtMs = 0;
  unsigned long lastHeartbeatAtMs = 0;
  unsigned long lastDebugStatusAtMs = 0;

  bool lastPirTriggered = false;
  bool lastManualTriggerPressed = false;

  const unsigned long debugStatusIntervalMs = 500;

  Adafruit_NeoPixel pixels;
  Bounce manualTriggerButton = Bounce();

  void executeIdle(unsigned long nowMs)
  {
    lastPirTriggered = isPirTriggered();
    bool manualTriggerPressed = isManualTriggerPressed();

    if (!isRestComplete(nowMs))
    {
      return;
    }

    if (manualTriggerPressed)
    {
      sendProtocolEvent("TRIGGER");
      sendDebug("Manual trigger confirmed.");
      setState(Launch);
      return;
    }

    if (lastPirTriggered)
    {
      sendProtocolEvent("TRIGGER");
      sendDebug("PIR trigger confirmed.");
      setState(Launch);
    }
  }

  void executeLaunch(unsigned long nowMs)
  {
    setRelayOn();

    if (nowMs - stateStartedAtMs >= launchDurationMs)
    {
      setRelayOff();
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

  bool isPirTriggered() const
  {
    return digitalRead(pinPir) == HIGH;
  }

  bool isManualTriggerPressed()
  {
    lastManualTriggerPressed = manualTriggerButton.fell();
    return lastManualTriggerPressed;
  }

  void setState(WorkflowState newState)
  {
    currentState = newState;
    stateStartedAtMs = millis();

    switch (currentState)
    {
      case Idle:
        setRelayOff();
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
        setRelayOff();
        setAllPixels(0, 255, 0);
        sendProtocolState("ACTIVE");
        sendDebug("State changed to Active.");
        break;
    }
  }

  void setRelayOn()
  {
    digitalWrite(pinRelay, LOW);
  }

  void setRelayOff()
  {
    digitalWrite(pinRelay, HIGH);
  }

  bool isRelayOn() const
  {
    return digitalRead(pinRelay) == LOW;
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
    Serial.print(F(" | Relay: "));
    Serial.print(isRelayOn() ? F("ON") : F("OFF"));
    Serial.print(F(" | ManualButtonEvent: "));
    Serial.print(lastManualTriggerPressed ? F("PRESSED") : F("NONE"));
    Serial.print(F(" | PirTriggered: "));
    Serial.print(lastPirTriggered ? F("YES") : F("NO"));
    Serial.print(F(" | RestRemainingMs: "));

    if (isRestComplete(nowMs))
    {
      Serial.println(0);
    }
    else
    {
      Serial.println(minimumRestMs - (nowMs - lastCycleEndedAtMs));
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

// PIR sensor on Arduino 6
// Manual button on Arduino 5
// Relay on Arduino 7
// NeoPixel on Arduino 2

const uint8_t pinPir = 6;
const uint8_t pinManualTrigger = 5;
const uint8_t pinRelay = 7;
const uint8_t pinNeoPixel = 2;

const uint16_t pixelCount = 10;
const unsigned long launchDurationMs = 3000;
const unsigned long activeDurationMs = 5000;
const unsigned long minimumRestMs = 5000;
const unsigned long heartbeatIntervalMs = 1000;

BallLauncherController controller(
  pinPir,
  pinManualTrigger,
  pinRelay,
  pinNeoPixel,
  pixelCount,
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