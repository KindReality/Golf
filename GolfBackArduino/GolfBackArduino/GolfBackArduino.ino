#include <FastLED.h>
// Golf hole LEDs — Toy Story theme (Andy’s sky, clouds, Buzz, Woody accents).
// Two connectors: set each to ultrasonic, PIR, or FastLED strip.
//
// Hole “made” sensor (PIR in box, microswitch, IR): wire to HOLE_SENSOR_PIN.
// PIR/most modules = ACTIVE HIGH. Microswitch to GND = set HOLE_SENSOR_ACTIVE_LOW.
// Set ENABLE_HOLE_SENSOR false until the hole sensor is wired (avoids false triggers).
const bool ENABLE_HOLE_SENSOR = false;
// While the real hole sensor isn’t wired: treat “close” on the ultrasonic (connector A)
// as a hole-in for testing the celebration. Set false when using HOLE_SENSOR_PIN only.
const bool HOLE_TEST_USE_ULTRASONIC = true;
const float HOLE_TEST_MAX_INCHES = 5.0f;
enum DeviceType
{
   DEVICE_NONE,
   DEVICE_ULTRASONIC,
   DEVICE_PIR,
   DEVICE_FASTLED
};
DeviceType connectorAType = DEVICE_ULTRASONIC;
DeviceType connectorBType = DEVICE_FASTLED;
const uint8_t CONNECTOR_A_PIN_1 = 2;
const uint8_t CONNECTOR_A_PIN_2 = 4;
const uint8_t CONNECTOR_B_PIN_1 = 3;
const uint8_t CONNECTOR_B_PIN_2 = 5;
const uint16_t CONNECTOR_A_LED_COUNT = 277;
const uint16_t CONNECTOR_B_LED_COUNT = 277;
CRGB connectorALeds[CONNECTOR_A_LED_COUNT];
CRGB connectorBLeds[CONNECTOR_B_LED_COUNT];
const unsigned long ULTRASONIC_INTERVAL_MS = 100;
const unsigned long PIR_POLL_INTERVAL_MS   = 50;
const unsigned long LED_INTERVAL_MS        = 33;
const float MIN_DISTANCE_INCHES = 1.0f;
const float MAX_DISTANCE_INCHES = 10.0f;
const uint8_t MIN_BLUE_BOOST = 0;
const uint8_t MAX_BLUE_BOOST = 100;
unsigned long lastConnectorAUltrasonicTime = 0;
unsigned long lastConnectorBUltrasonicTime = 0;
unsigned long lastConnectorAPirTime = 0;
unsigned long lastConnectorBPirTime = 0;
unsigned long lastConnectorALedTime = 0;
unsigned long lastConnectorBLedTime = 0;
bool lastConnectorAPirState = false;
bool lastConnectorBPirState = false;
float connectorADistanceInches = -1.0f;
// --- Hole sensor (ball in cup / motion in Andy’s box) ---
const uint8_t HOLE_SENSOR_PIN = 6;
const bool HOLE_SENSOR_ACTIVE_HIGH = true;
const unsigned long HOLE_DEBOUNCE_MS = 45;
const unsigned long CELEBRATION_MS = 2200;
unsigned long celebrationEndMs = 0;
bool holeCanTrigger = true;
bool holeWaitingForClear = false;
void toyStoryLoop(CRGB leds[], uint16_t ledCount);
void holeCelebrationLoop(CRGB leds[], uint16_t ledCount);
static bool holeSensorRawActive()
{
   int v = digitalRead(HOLE_SENSOR_PIN);
   return HOLE_SENSOR_ACTIVE_HIGH ? (v == HIGH) : (v == LOW);
}
static bool ultrasonicHoleTestClose()
{
   float d = connectorADistanceInches;
   return (d >= 0.0f && d <= HOLE_TEST_MAX_INCHES);
}
// True if something is still “holding” the hole trigger after a celebration ends.
static bool holeTriggerStillLatchedAfterCelebration()
{
   if (ENABLE_HOLE_SENSOR && holeSensorRawActive())
   {
       return true;
   }
   if (HOLE_TEST_USE_ULTRASONIC && !ENABLE_HOLE_SENSOR && connectorAType == DEVICE_ULTRASONIC
&& ultrasonicHoleTestClose())
   {
       return true;
   }
   return false;
}
static void updateCelebrationTimer()
{
   unsigned long now = millis();
   if (celebrationEndMs == 0 || now < celebrationEndMs)
   {
       return;
   }
   celebrationEndMs = 0;
   if (holeTriggerStillLatchedAfterCelebration())
   {
       holeWaitingForClear = true;
   }
   else
   {
       holeCanTrigger = true;
       holeWaitingForClear = false;
   }
}
static void pollHoleSensor()
{
   if (!ENABLE_HOLE_SENSOR)
   {
       return;
   }
   static bool lastSample = false;
   static unsigned long stableSinceMs = 0;
   static bool stableActive = false;
   unsigned long now = millis();
   bool raw = holeSensorRawActive();
   if (raw != lastSample)
   {
       lastSample = raw;
       stableSinceMs = now;
       return;
   }
   if ((now - stableSinceMs) < HOLE_DEBOUNCE_MS)
   {
       return;
   }
   if (stableActive != raw)
   {
       stableActive = raw;
   }
   if (holeWaitingForClear && !stableActive)
   {
       holeWaitingForClear = false;
       holeCanTrigger = true;
   }
   static bool prevStable = false;
   bool rising = stableActive && !prevStable;
   prevStable = stableActive;
   if (rising && holeCanTrigger && celebrationEndMs == 0)
   {
       celebrationEndMs = now + CELEBRATION_MS;
       holeCanTrigger = false;
       Serial.println("Hole! Celebration! (pin)");
   }
}
// Debounced “close enough” on connector A ultrasonic to fire the same celebration (testing).
static void pollHoleTestFromUltrasonic()
{
   if (!HOLE_TEST_USE_ULTRASONIC || ENABLE_HOLE_SENSOR)
   {
       return;
   }
   if (connectorAType != DEVICE_ULTRASONIC)
   {
       return;
   }
   static bool lastSample = false;
   static unsigned long stableSinceMs = 0;
   static bool stableClose = false;
   unsigned long now = millis();
   bool raw = ultrasonicHoleTestClose();
   if (raw != lastSample)
   {
       lastSample = raw;
       stableSinceMs = now;
       return;
   }
   if ((now - stableSinceMs) < HOLE_DEBOUNCE_MS)
   {
       return;
   }
   if (stableClose != raw)
   {
       stableClose = raw;
   }
   if (holeWaitingForClear && !stableClose)
   {
       holeWaitingForClear = false;
       holeCanTrigger = true;
   }
   static bool prevStable = false;
   bool rising = stableClose && !prevStable;
   prevStable = stableClose;
   if (rising && holeCanTrigger && celebrationEndMs == 0)
   {
       celebrationEndMs = now + CELEBRATION_MS;
       holeCanTrigger = false;
       Serial.println("Hole! Celebration! (ultrasonic test — move back to re-arm)");
   }
}
static bool isHoleCelebrationActive()
{
   return celebrationEndMs != 0 && millis() < celebrationEndMs;
}
void setAllLeds(CRGB leds[], uint16_t ledCount, const CRGB& color)
{
   for (uint16_t i = 0; i < ledCount; i++)
   {
       leds[i] = color;
   }
}
uint8_t getBlueBoostFromDistance(float distanceInches)
{
   if (distanceInches < 0)
   {
       return 0;
   }
   if (distanceInches <= MIN_DISTANCE_INCHES)
   {
       return MAX_BLUE_BOOST;
   }
   if (distanceInches >= MAX_DISTANCE_INCHES)
   {
       return MIN_BLUE_BOOST;
   }
   float distanceRange = MAX_DISTANCE_INCHES - MIN_DISTANCE_INCHES;
   float normalized = (MAX_DISTANCE_INCHES - distanceInches) / distanceRange;
   float blueBoost = MIN_BLUE_BOOST + (normalized * (MAX_BLUE_BOOST - MIN_BLUE_BOOST));
   return (uint8_t)blueBoost;
}
void addBlueBoost(CRGB leds[], uint16_t ledCount, uint8_t blueBoost)
{
   for (uint16_t i = 0; i < ledCount; i++)
   {
       leds[i].blue = qadd8(leds[i].blue, blueBoost);
   }
}
void configureConnectorA()
{
   switch (connectorAType)
   {
       case DEVICE_ULTRASONIC:
           pinMode(CONNECTOR_A_PIN_2, OUTPUT);
           pinMode(CONNECTOR_A_PIN_1, INPUT);
           digitalWrite(CONNECTOR_A_PIN_2, LOW);
           break;
       case DEVICE_PIR:
           pinMode(CONNECTOR_A_PIN_2, INPUT);
           pinMode(CONNECTOR_A_PIN_1, INPUT);
           break;
       case DEVICE_FASTLED:
           FastLED.addLeds<NEOPIXEL, CONNECTOR_A_PIN_2>(connectorALeds, CONNECTOR_A_LED_COUNT);
           FastLED.show();
           pinMode(CONNECTOR_A_PIN_1, INPUT);
           break;
       case DEVICE_NONE:
       default:
           pinMode(CONNECTOR_A_PIN_1, INPUT);
           pinMode(CONNECTOR_A_PIN_2, INPUT);
           break;
   }
}
void configureConnectorB()
{
   switch (connectorBType)
   {
       case DEVICE_ULTRASONIC:
           pinMode(CONNECTOR_B_PIN_2, OUTPUT);
           pinMode(CONNECTOR_B_PIN_1, INPUT);
           digitalWrite(CONNECTOR_B_PIN_2, LOW);
           break;
       case DEVICE_PIR:
           pinMode(CONNECTOR_B_PIN_2, INPUT);
           pinMode(CONNECTOR_B_PIN_1, INPUT);
           break;
       case DEVICE_FASTLED:
           FastLED.addLeds<NEOPIXEL, CONNECTOR_B_PIN_2>(connectorBLeds, CONNECTOR_B_LED_COUNT);
           FastLED.show();
           pinMode(CONNECTOR_B_PIN_1, INPUT);
           break;
       case DEVICE_NONE:
       default:
           pinMode(CONNECTOR_B_PIN_1, INPUT);
           pinMode(CONNECTOR_B_PIN_2, INPUT);
           break;
   }
}
float readUltrasonicDistanceInches(uint8_t echoPin, uint8_t triggerPin)
{
   digitalWrite(triggerPin, LOW);
   delayMicroseconds(2);
   digitalWrite(triggerPin, HIGH);
   delayMicroseconds(10);
   digitalWrite(triggerPin, LOW);
   unsigned long duration = pulseIn(echoPin, HIGH, 30000UL);
   if (duration == 0)
   {
       return -1.0f;
   }
   return duration / 148.0f;
}
void onConnectorAUltrasonicRead(float distanceInches)
{
   Serial.print("Connector A ultrasonic: ");
   if (distanceInches < 0)
   {
       Serial.println("no echo");
   }
   else
   {
       Serial.print(distanceInches, 2);
       Serial.print(" inches, blue boost: ");
       Serial.println(getBlueBoostFromDistance(distanceInches));
   }
}
void onConnectorBUltrasonicRead(float distanceInches)
{
   Serial.print("Connector B ultrasonic: ");
   if (distanceInches < 0)
   {
       Serial.println("no echo");
   }
   else
   {
       Serial.print(distanceInches, 2);
       Serial.println(" inches");
   }
}
void onConnectorAMotionChanged(bool triggered)
{
   Serial.print("Connector A PIR: ");
   Serial.println(triggered ? "triggered" : "untriggered");
}
void onConnectorBMotionChanged(bool triggered)
{
   Serial.print("Connector B PIR: ");
   Serial.println(triggered ? "triggered" : "untriggered");
}
void runConnectorALeds()
{
   if (isHoleCelebrationActive())
   {
       holeCelebrationLoop(connectorALeds, CONNECTOR_A_LED_COUNT);
   }
   else
   {
       toyStoryLoop(connectorALeds, CONNECTOR_A_LED_COUNT);
   }
   FastLED.show();
}
void runConnectorBLeds()
{
   if (isHoleCelebrationActive())
   {
       holeCelebrationLoop(connectorBLeds, CONNECTOR_B_LED_COUNT);
   }
   else
   {
       uint8_t blueBoost = getBlueBoostFromDistance(connectorADistanceInches);
       toyStoryLoop(connectorBLeds, CONNECTOR_B_LED_COUNT);
       addBlueBoost(connectorBLeds, CONNECTOR_B_LED_COUNT, blueBoost);
   }
   FastLED.show();
}
void executeConnectorARepeatedly()
{
   unsigned long currentTime = millis();
   switch (connectorAType)
   {
       case DEVICE_ULTRASONIC:
           if (currentTime - lastConnectorAUltrasonicTime >= ULTRASONIC_INTERVAL_MS)
           {
               lastConnectorAUltrasonicTime = currentTime;
               float distanceInches = readUltrasonicDistanceInches(
                   CONNECTOR_A_PIN_1,
                   CONNECTOR_A_PIN_2);
               connectorADistanceInches = distanceInches;
               onConnectorAUltrasonicRead(distanceInches);
           }
           break;
       case DEVICE_PIR:
           if (currentTime - lastConnectorAPirTime >= PIR_POLL_INTERVAL_MS)
           {
               lastConnectorAPirTime = currentTime;
               bool currentState = digitalRead(CONNECTOR_A_PIN_2) == HIGH;
               if (currentState != lastConnectorAPirState)
               {
                   lastConnectorAPirState = currentState;
                   onConnectorAMotionChanged(currentState);
               }
           }
           break;
       case DEVICE_FASTLED:
           if (currentTime - lastConnectorALedTime >= LED_INTERVAL_MS)
           {
               lastConnectorALedTime = currentTime;
               runConnectorALeds();
           }
           break;
       case DEVICE_NONE:
       default:
           break;
   }
}
void executeConnectorBRepeatedly()
{
   unsigned long currentTime = millis();
   switch (connectorBType)
   {
       case DEVICE_ULTRASONIC:
           if (currentTime - lastConnectorBUltrasonicTime >= ULTRASONIC_INTERVAL_MS)
           {
               lastConnectorBUltrasonicTime = currentTime;
               float distanceInches = readUltrasonicDistanceInches(
                   CONNECTOR_B_PIN_1,
                   CONNECTOR_B_PIN_2);
               onConnectorBUltrasonicRead(distanceInches);
           }
           break;
       case DEVICE_PIR:
           if (currentTime - lastConnectorBPirTime >= PIR_POLL_INTERVAL_MS)
           {
               lastConnectorBPirTime = currentTime;
               bool currentState = digitalRead(CONNECTOR_B_PIN_2) == HIGH;
               if (currentState != lastConnectorBPirState)
               {
                   lastConnectorBPirState = currentState;
                   onConnectorBMotionChanged(currentState);
               }
           }
           break;
       case DEVICE_FASTLED:
           if (currentTime - lastConnectorBLedTime >= LED_INTERVAL_MS)
           {
               lastConnectorBLedTime = currentTime;
               runConnectorBLeds();
           }
           break;
       case DEVICE_NONE:
       default:
           break;
   }
}
void setup()
{
   // put your setup code here, to run once:
   delay(5000);
   Serial.begin(115200);
   delay(1000);
   configureConnectorA();
   configureConnectorB();
   if (ENABLE_HOLE_SENSOR)
   {
       pinMode(HOLE_SENSOR_PIN, INPUT_PULLUP);
   }
   Serial.println("Toy Story golf hole LEDs ready.");
}
void loop()
{
   executeConnectorARepeatedly();
   updateCelebrationTimer();
   pollHoleSensor();
   pollHoleTestFromUltrasonic();
   executeConnectorBRepeatedly();
}
// ============================================================
// Toy Story theme — Andy’s sky, soft clouds, Buzz green/purple,
// Woody warmth, playful red/gold sparkles (replaces Pacifica).
// ============================================================
// Sky → clouds → Buzz (green/purple) → Woody (amber) → logo reds/gold
CRGBPalette16 toyStoryPalette16 = {
   0x1E5790,  // deep Andy wallpaper blue
   0x3D7AB8,
   0x5CB3E8,  // bright sky
   0xA8D8F0,  // pale cloud blue
   0xE8F4FC,  // cloud white
   0xFEFEFE,
   0x7FFF00,  // Buzz lime
   0x00C46A,  // space green
   0x7B3FA0,  // Buzz purple
   0x4A148C,  // deeper purple
   0xD4A574,  // Woody / desert tan
   0xC17A3A,  // leather brown
   0xFF6B6B,  // playful red
   0xFFD93D,  // gold star / logo yellow
   0x4ECDC4,  // toy accent teal
   0x87CEEB,  // back to sky
};
void toyStoryLoop(CRGB leds[], uint16_t ledCount)
{
   static uint32_t sLastMs = 0;
   static uint16_t sScroll = 0;
   uint32_t ms = millis();
   uint32_t deltaMs = ms - sLastMs;
   if (deltaMs > 1000)
   {
       deltaMs = 20;
   }
   sLastMs = ms;
   // Gentle palette drift (toys “alive” in the clouds)
   uint16_t drift = beat16(8);
   sScroll += (deltaMs * beatsin16(5, 12, 28)) / 256;
   // Soft base: bedroom sky
   fill_solid(leds, ledCount, CRGB(8, 24, 48));
   uint16_t wave1 = beat16(11) + sScroll;
   uint16_t wave2 = 0 - beat16(17);
   for (uint16_t i = 0; i < ledCount; i++)
   {
       uint16_t p = (i * 512) / ledCount;
       uint16_t idx = p + drift + scale16(sin16(wave1 + i * 180), 4000);
       idx += scale16(sin16(wave2 + i * 95), 2500);
       uint8_t palIndex = (idx >> 8) & 0xFF;
       uint8_t br = beatsin8(7, 140, 255);
       CRGB c = ColorFromPalette(toyStoryPalette16, palIndex, br, LINEARBLEND);
       // Wispy “cloud” brighten on slow waves
       uint8_t cloud = beatsin8(9, 0, 80);
       uint16_t cloudWave = sin16(i * 120 + beat16(6)) + 32768;
       if (cloudWave > 48000)
       {
           c.nscale8_video(255 - cloud / 4);
           c += CRGB(cloud / 3, cloud / 2, cloud);
       }
       leds[i] += c;
   }
   // Occasional “star” twinkles (gold / white)
   for (uint16_t t = 0; t < (ledCount / 12) + 2; t++)
   {
       uint16_t pos = (beat16(13 + t) + t * 7919) % ledCount;
       uint8_t tw = beatsin8(10 + (t & 3), 0, 220);
       if (tw > 180)
       {
           leds[pos] += CRGB(tw / 4, tw / 2, tw / 6);
           leds[pos] += CRGB(0, tw / 6, 0);
       }
   }
   // Slight overall warmth so it never reads as cold ocean
   for (uint16_t i = 0; i < ledCount; i++)
   {
       leds[i].red = qadd8(leds[i].red, 4);
       leds[i].green = scale8(leds[i].green, 245);
   }
}
// Short “you made it!” burst — quick reset for the next player (~2.2s).
void holeCelebrationLoop(CRGB leds[], uint16_t ledCount)
{
   uint8_t hue = beat8(32);
   int16_t deltaHue = max((int16_t)1, (int16_t)(256 / (int16_t)ledCount));
   fill_rainbow(leds, ledCount, hue, deltaHue);
   uint8_t flash = beatsin8(18, 40, 255);
   for (uint16_t i = 0; i < ledCount; i++)
   {
       leds[i].nscale8_video(flash);
   }
   for (uint16_t s = 0; s < 8; s++)
   {
       uint16_t pos = (beat16(20 + s) + s * 9973) % ledCount;
       leds[pos] += CRGB(255, 230, 120);
       leds[(pos + ledCount / 2) % ledCount] += CRGB(120, 40, 200);
   }
   if (beat8(12) > 200)
   {
       for (uint16_t i = 0; i < ledCount; i += 5)
       {
           leds[i] += CRGB::White;
       }
   }
}