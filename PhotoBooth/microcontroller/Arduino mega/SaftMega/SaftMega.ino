// SPDX-License-Identifier: AGPL-3.0-only
#include <Arduino.h>
#include <ArduinoJson.h>
#include <Bounce2.h>
#include <EEPROM.h>

// Configuration: edit here, then upload again. Each board needs its own name/ID.
constexpr char deviceName[] = "mega1";
constexpr char deviceId[] = "saft-mega-001";
constexpr unsigned long serialBaudRate = 115200;
constexpr uint16_t debounceMilliseconds = 20;
constexpr unsigned long announcementIntervalMilliseconds = 2000;
constexpr uint8_t inputMode = INPUT_PULLUP;

struct BeamSettings { const char* name; uint8_t pin; uint8_t brokenState; };
// The photograph does not establish active polarity. Verify on the real sensor;
// change a beam's LOW to HIGH if broken/restored events are reversed.
constexpr BeamSettings beams[] = {
  { "beam1", 22, LOW }, { "beam2", 24, LOW }, { "beam3", 26, LOW }
};
constexpr size_t beamCount = sizeof(beams) / sizeof(beams[0]);
static_assert(debounceMilliseconds > 0, "A stable interval is required.");

struct BeamState {
  Bounce input;
  bool initialized = false;
  bool rawState = HIGH;
  unsigned long rawChangedAt = 0;
};
struct BeamEvent { uint8_t index; bool broken; };
BeamState states[beamCount];
constexpr uint8_t eventCapacity = 16;
BeamEvent pendingEvents[eventCapacity];
uint8_t eventHead = 0, eventCount = 0;
uint32_t droppedEvents = 0;
uint32_t bootNumber, eventNumber = 0;
constexpr size_t maximumMessageBytes = 1200;
char receiveBuffer[maximumMessageBytes + 1];
char transmitBuffer[maximumMessageBytes + 1];
size_t receiveLength = 0, transmitLength = 0, transmitOffset = 0;
bool discardReceive = false, announceRequested = true, statusRequested = false;
bool firstAnnouncementSent = false;
unsigned long lastReceiveAt = 0, lastAnnouncementAt = 0;

// EEPROM bytes 0..3 are reserved for IDs across resets. No per-event writes.
uint32_t nextBootNumber() {
  uint32_t previous;
  EEPROM.get(0, previous);
  const uint32_t next = previous == UINT32_MAX ? 1 : previous + 1;
  EEPROM.put(0, next);
  return next;
}

void startEvent(JsonDocument& doc, const char* input, const char* event) {
  if (eventNumber == UINT32_MAX) { bootNumber = nextBootNumber(); eventNumber = 0; }
  char requestId[32];
  snprintf(requestId, sizeof(requestId), "mega-%lu-%lu",
    static_cast<unsigned long>(bootNumber), static_cast<unsigned long>(++eventNumber));
  doc["type"] = "event";
  doc["device"] = "*";
  doc["source"] = deviceName;
  doc["requestId"] = requestId;
  doc["input"] = input;
  doc["event"] = event;
}

void prepareMessage() {
  if (transmitOffset < transmitLength) return;
  if (millis() - lastAnnouncementAt >= announcementIntervalMilliseconds) announceRequested = true;
  JsonDocument doc;
  // Identify the board before any sensor event; a noisy input must not prevent discovery.
  if (eventCount > 0 && firstAnnouncementSent) {
    const BeamEvent event = pendingEvents[eventHead];
    eventHead = (eventHead + 1) % eventCapacity; --eventCount;
    startEvent(doc, beams[event.index].name, event.broken ? "beam.broken" : "beam.restored");
    doc["data"]["pin"] = beams[event.index].pin;
    doc["data"]["broken"] = event.broken;
  } else if (statusRequested && firstAnnouncementSent) {
    statusRequested = false;
    startEvent(doc, "device", "device.status");
    doc["data"]["uptimeMilliseconds"] = millis();
    doc["data"]["debounceMilliseconds"] = debounceMilliseconds;
    doc["data"]["droppedEvents"] = droppedEvents;
    for (size_t i = 0; i < beamCount; ++i) {
      doc["data"]["inputs"][i]["name"] = beams[i].name;
      doc["data"]["inputs"][i]["initialized"] = states[i].initialized;
      if (states[i].initialized)
        doc["data"]["inputs"][i]["broken"] = static_cast<uint8_t>(states[i].input.read()) == beams[i].brokenState;
    }
  } else if (announceRequested || !firstAnnouncementSent) {
    announceRequested = false; lastAnnouncementAt = millis();
    startEvent(doc, "device", "device.announced");
    doc["protocol"] = "saft.serial";
    doc["protocolVersion"] = 1;
    doc["deviceId"] = deviceId;
    doc["commands"][0] = "discover";
    doc["commands"][1] = "status";
    for (size_t i = 0; i < beamCount; ++i) {
      doc["inputs"][i]["name"] = beams[i].name;
      doc["inputs"][i]["kind"] = "breakBeam";
      doc["inputs"][i]["events"][0] = "beam.broken";
      doc["inputs"][i]["events"][1] = "beam.restored";
    }
  } else return;

  if (doc.overflowed() || measureJson(doc) > maximumMessageBytes - 1) {
    ++droppedEvents; statusRequested = true; return;
  }
  transmitLength = serializeJson(doc, transmitBuffer, maximumMessageBytes);
  if (strcmp(doc["event"] | "", "device.announced") == 0) firstAnnouncementSent = true;
  transmitBuffer[transmitLength++] = '\n';
  transmitOffset = 0;
}

void pumpTransmit() {
  // Only write what fits in the UART buffer; announcing must not block sampling.
  const size_t available = static_cast<size_t>(Serial.availableForWrite());
  const size_t remaining = transmitLength - transmitOffset;
  const size_t count = available < remaining ? available : remaining;
  if (count > 0)
    transmitOffset += Serial.write(reinterpret_cast<const uint8_t*>(transmitBuffer + transmitOffset), count);
}

void handleCommand() {
  JsonDocument filter, doc;
  filter["type"] = filter["device"] = filter["source"] = filter["requestId"] = filter["command"] = true;
  const auto error = deserializeJson(doc, receiveBuffer, receiveLength,
    DeserializationOption::Filter(filter), DeserializationOption::NestingLimit(12));
  if (error || !doc.is<JsonObject>()) return;
  const char* type = doc["type"] | "";
  const char* target = doc["device"] | "";
  const char* source = doc["source"] | "";
  const char* request = doc["requestId"] | "";
  const char* command = doc["command"] | "";
  if (strcmp(type, "command") != 0 || (strcmp(target, "*") != 0 && strcasecmp(target, deviceName) != 0) ||
      !*source || strlen(source) > 128 || !*request || strlen(request) > 128) return;
  // Both commands are read-only and safe to repeat. No outputs are actuated.
  if (strcmp(command, "discover") == 0) announceRequested = true;
  else if (strcmp(command, "status") == 0) statusRequested = true;
}

void readCommands() {
  if (receiveLength > 0 && millis() - lastReceiveAt > 1000) { receiveLength = 0; discardReceive = true; }
  // Bound receive work per loop so serial traffic cannot monopolize sampling.
  for (uint8_t budget = 0; budget < 32 && Serial.available() > 0; ++budget) {
    const char value = static_cast<char>(Serial.read()); lastReceiveAt = millis();
    if (value == '\n') {
      if (!discardReceive && receiveLength > 0) {
        if (receiveBuffer[receiveLength - 1] == '\r') --receiveLength;
        receiveBuffer[receiveLength] = '\0'; handleCommand();
      }
      receiveLength = 0; discardReceive = false;
    } else if (!discardReceive) {
      if (receiveLength >= maximumMessageBytes) { receiveLength = 0; discardReceive = true; }
      else receiveBuffer[receiveLength++] = value;
    }
  }
}

void setup() {
  Serial.begin(serialBaudRate);
  bootNumber = nextBootNumber();
  for (size_t i = 0; i < beamCount; ++i) {
    states[i].input.attach(beams[i].pin, inputMode);
    states[i].input.interval(debounceMilliseconds);
    states[i].rawState = digitalRead(beams[i].pin);
    states[i].rawChangedAt = millis();
  }
}

void loop() {
  for (size_t i = 0; i < beamCount; ++i) {
    BeamState& state = states[i];
    state.input.update(); // Bounce2 does all ongoing sensor debounce.
    if (!state.initialized) {
      const bool raw = digitalRead(beams[i].pin);
      const unsigned long now = millis();
      if (raw != state.rawState) { state.rawState = raw; state.rawChangedAt = now; }
      if (now - state.rawChangedAt >= debounceMilliseconds && state.input.read() == raw) state.initialized = true;
      continue;
    }
    if (!state.input.changed()) continue;
    if (eventCount == eventCapacity) { ++droppedEvents; statusRequested = true; continue; }
    pendingEvents[(eventHead + eventCount) % eventCapacity] = {
      static_cast<uint8_t>(i), static_cast<uint8_t>(state.input.read()) == beams[i].brokenState
    };
    ++eventCount;
  }
  pumpTransmit();
  readCommands();
  prepareMessage();
  pumpTransmit();
}
