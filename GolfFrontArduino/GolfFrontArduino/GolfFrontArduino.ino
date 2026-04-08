#include <Adafruit_NeoPixel.h>
#include <Bounce2.h>

enum WorkflowState
{
  Idle,
  Launch,
  Active
};

// Configurable values
const unsigned long startupDelayMs = 8000;
const unsigned long manualTriggerDebounceMs = 10;
const unsigned long manualTriggerLongPressMs = 1000;
const unsigned long debugStatusIntervalMs = 500;

const uint8_t pinBreakBeam = 6;
const uint8_t pinManualTrigger = 5;
const uint8_t pinRelay = 7;
const uint8_t pinNeoPixel = 2;

const uint16_t pixelCount = 10;
const unsigned long launchDurationMs = 3000;
const unsigned long activeDurationMs = 5000;
const unsigned long minimumRestMs = 1000;
const unsigned long heartbeatIntervalMs = 1000;

enum PixelColorIndex
{
  IdleColor,
  LaunchColor,
  ActiveColor
};

enum ManualButtonEventType
{
  ManualButtonNone,
  ManualButtonTap,
  ManualButtonLong
};

const uint8_t stateColors[][3] =
{
  { 0, 0, 255 },
  { 255, 0, 0 },
  { 0, 255, 0 }
};

class BallLauncherController
{
public:
  BallLauncherController(
    uint8_t breakBeamPin,
    uint8_t manualTriggerPin,
    uint8_t relayPin,
    uint8_t neoPixelPin,
    uint16_t pixelCount,
    unsigned long launchDurationMs,
    unsigned long activeDurationMs,
    unsigned long minimumRestMs,
    unsigned long heartbeatIntervalMs)
    : pinBreakBeam(breakBeamPin),
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
    pinMode(pinBreakBeam, INPUT_PULLUP);
    pinMode(pinRelay, OUTPUT);

    pinMode(pinManualTrigger, INPUT_PULLUP);
    manualTriggerButton.attach(pinManualTrigger);
    manualTriggerButton.interval(manualTriggerDebounceMs);

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
    updateManualButtonEvent(nowMs);

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
  uint8_t pinBreakBeam;
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

  bool lastBreakBeamTriggered = false;
  bool isManualButtonDown = false;
  unsigned long manualButtonPressedAtMs = 0;
  ManualButtonEventType lastManualButtonEvent = ManualButtonNone;
  ManualButtonEventType pendingManualButtonEvent = ManualButtonNone;

  Adafruit_NeoPixel pixels;
  Bounce manualTriggerButton = Bounce();

  void executeIdle(unsigned long nowMs)
  {
    lastBreakBeamTriggered = isBreakBeamTriggered();

    if (!isRestComplete(nowMs))
    {
      return;
    }

    if (pendingManualButtonEvent != ManualButtonNone)
    {
      pendingManualButtonEvent = ManualButtonNone;
      sendProtocolEvent("TRIGGER");
      sendDebug("Manual trigger confirmed after release.");
      setState(Launch);
      return;
    }

    if (lastBreakBeamTriggered)
    {
      sendProtocolEvent("TRIGGER");
      sendDebug("Break beam trigger confirmed.");
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

  bool isBreakBeamTriggered() const
  {
    return digitalRead(pinBreakBeam) == LOW;
  }

  void updateManualButtonEvent(unsigned long nowMs)
  {
    bool manualButtonDown = manualTriggerButton.read() == LOW;

    if (manualButtonDown && !isManualButtonDown)
    {
      isManualButtonDown = true;
      manualButtonPressedAtMs = nowMs;
      return;
    }

    if (!manualButtonDown && isManualButtonDown)
    {
      isManualButtonDown = false;
      unsigned long pressDurationMs = nowMs - manualButtonPressedAtMs;
      lastManualButtonEvent = pressDurationMs >= manualTriggerLongPressMs ? ManualButtonLong : ManualButtonTap;
      pendingManualButtonEvent = lastManualButtonEvent;
    }
  }

  const char* getManualButtonEventName() const
  {
    switch (lastManualButtonEvent)
    {
      case ManualButtonTap:
        return "TAP";
      case ManualButtonLong:
        return "LONG";
      case ManualButtonNone:
      default:
        return "NONE";
    }
  }

  void setState(WorkflowState newState)
  {
    currentState = newState;
    stateStartedAtMs = millis();

    switch (currentState)
    {
      case Idle:
        setRelayOff();
        setAllPixels(
          stateColors[IdleColor][0],
          stateColors[IdleColor][1],
          stateColors[IdleColor][2]);
        sendProtocolState("IDLE");
        sendDebug("State changed to Idle.");
        break;

      case Launch:
        setAllPixels(
          stateColors[LaunchColor][0],
          stateColors[LaunchColor][1],
          stateColors[LaunchColor][2]);
        sendProtocolState("LAUNCH");
        sendDebug("State changed to Launch.");
        break;

      case Active:
        setRelayOff();
        setAllPixels(
          stateColors[ActiveColor][0],
          stateColors[ActiveColor][1],
          stateColors[ActiveColor][2]);
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
    Serial.print(getManualButtonEventName());
    Serial.print(F(" | BreakBeamTriggered: "));
    Serial.print(lastBreakBeamTriggered ? F("YES") : F("NO"));
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
      lastManualButtonEvent = ManualButtonNone;
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

BallLauncherController controller(
  pinBreakBeam,
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
  delay(startupDelayMs);
  Serial.begin(115200);
  controller.begin();
}

void loop()
{
  controller.execute();
}