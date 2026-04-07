#include <FastLED.h>

// This sketch lets you use two connectors.
// Each connector can be set to one device type:
// - ultrasonic sensor
// - PIR motion sensor
// - FastLED strip

enum DeviceType
{
    DEVICE_NONE,
    DEVICE_ULTRASONIC,
    DEVICE_PIR,
    DEVICE_FASTLED
};

// Set the device type for each connector here.
DeviceType connectorAType = DEVICE_ULTRASONIC;
DeviceType connectorBType = DEVICE_FASTLED;

// Connector A pins
const uint8_t CONNECTOR_A_PIN_1 = 2;
const uint8_t CONNECTOR_A_PIN_2 = 4;

// Connector B pins
const uint8_t CONNECTOR_B_PIN_1 = 3;
const uint8_t CONNECTOR_B_PIN_2 = 5;

// Number of LEDs on each connector
const uint16_t CONNECTOR_A_LED_COUNT = 100;
const uint16_t CONNECTOR_B_LED_COUNT = 100;

// LED data for each connector
CRGB connectorALeds[CONNECTOR_A_LED_COUNT];
CRGB connectorBLeds[CONNECTOR_B_LED_COUNT];

// How often each type of device should be checked or updated
const unsigned long ULTRASONIC_INTERVAL_MS = 100;
const unsigned long PIR_POLL_INTERVAL_MS   = 50;
const unsigned long LED_INTERVAL_MS        = 20;

// Distance settings for brightness effect
const float MIN_DISTANCE_INCHES = 1.0f;
const float MAX_DISTANCE_INCHES = 10.0f;
const uint8_t MIN_BLUE_BOOST = 0;
const uint8_t MAX_BLUE_BOOST = 100;

// Time tracking
unsigned long lastConnectorAUltrasonicTime = 0;
unsigned long lastConnectorBUltrasonicTime = 0;

unsigned long lastConnectorAPirTime = 0;
unsigned long lastConnectorBPirTime = 0;

unsigned long lastConnectorALedTime = 0;
unsigned long lastConnectorBLedTime = 0;

// Last PIR state so we only print when the state changes
bool lastConnectorAPirState = false;
bool lastConnectorBPirState = false;

// Last distance measured by Connector A ultrasonic sensor
float connectorADistanceInches = -1.0f;


// Sets every LED in one strip to the same color
void setAllLeds(CRGB leds[], uint16_t ledCount, const CRGB& color)
{
    for (uint16_t i = 0; i < ledCount; i++)
    {
        leds[i] = color;
    }
}


// Converts distance to a blue boost value
// 10 inches = 0 extra blue
// 1 inch = 100 extra blue
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


// Adds extra blue to every LED without changing global brightness
void addBlueBoost(CRGB leds[], uint16_t ledCount, uint8_t blueBoost)
{
    for (uint16_t i = 0; i < ledCount; i++)
    {
        leds[i].blue = qadd8(leds[i].blue, blueBoost);
    }
}


// Sets up Connector A based on its selected device type
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


// Sets up Connector B based on its selected device type
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


// Reads distance from an ultrasonic sensor and returns inches
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


// Prints the ultrasonic reading for Connector A
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


// Prints the ultrasonic reading for Connector B
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


// Prints the PIR state for Connector A
void onConnectorAMotionChanged(bool triggered)
{
    Serial.print("Connector A PIR: ");
    Serial.println(triggered ? "triggered" : "untriggered");
}


// Prints the PIR state for Connector B
void onConnectorBMotionChanged(bool triggered)
{
    Serial.print("Connector B PIR: ");
    Serial.println(triggered ? "triggered" : "untriggered");
}


// Runs the LEDs for Connector A
void runConnectorALeds()
{
    pacificaLoop(connectorALeds, CONNECTOR_A_LED_COUNT);
    FastLED.show();
}


// Runs the LEDs for Connector B
// This keeps full brightness and adds more blue based on distance.
void runConnectorBLeds()
{
    uint8_t blueBoost = getBlueBoostFromDistance(connectorADistanceInches);

    pacificaLoop(connectorBLeds, CONNECTOR_B_LED_COUNT);
    addBlueBoost(connectorBLeds, CONNECTOR_B_LED_COUNT, blueBoost);

    FastLED.show();
}


// Runs Connector A again and again based on its device type
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


// Runs Connector B again and again based on its device type
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


// Runs once when the board starts
void setup()
{
    delay(5000);

    Serial.begin(115200);
    delay(1000);

    configureConnectorA();
    configureConnectorB();

    Serial.println("Connector demo started.");
}


// Runs over and over
void loop()
{
    executeConnectorARepeatedly();
    executeConnectorBRepeatedly();
}



// ============================================================
// Pacifica code
// Keep this section together at the end of the file.
// ============================================================

CRGBPalette16 pacificaPalette1 =
    { 0x000507, 0x000409, 0x00030B, 0x00030D, 0x000210, 0x000212, 0x000114, 0x000117,
      0x000019, 0x00001C, 0x000026, 0x000031, 0x00003B, 0x000046, 0x14554B, 0x28AA50 };

CRGBPalette16 pacificaPalette2 =
    { 0x000507, 0x000409, 0x00030B, 0x00030D, 0x000210, 0x000212, 0x000114, 0x000117,
      0x000019, 0x00001C, 0x000026, 0x000031, 0x00003B, 0x000046, 0x0C5F52, 0x19BE5F };

CRGBPalette16 pacificaPalette3 =
    { 0x000208, 0x00030E, 0x000514, 0x00061A, 0x000820, 0x000927, 0x000B2D, 0x000C33,
      0x000E39, 0x001040, 0x001450, 0x001860, 0x001C70, 0x002080, 0x1040BF, 0x2060FF };


void pacificaLoop(CRGB leds[], uint16_t ledCount)
{
    static uint16_t sCIStart1 = 0;
    static uint16_t sCIStart2 = 0;
    static uint16_t sCIStart3 = 0;
    static uint16_t sCIStart4 = 0;
    static uint32_t sLastMs = 0;

    uint32_t ms = GET_MILLIS();
    uint32_t deltaMs = ms - sLastMs;
    sLastMs = ms;

    uint16_t speedFactor1 = beatsin16(3, 179, 269);
    uint16_t speedFactor2 = beatsin16(4, 179, 269);

    uint32_t deltaMs1 = (deltaMs * speedFactor1) / 256;
    uint32_t deltaMs2 = (deltaMs * speedFactor2) / 256;
    uint32_t deltaMs21 = (deltaMs1 + deltaMs2) / 2;

    sCIStart1 += (deltaMs1 * beatsin88(1011, 10, 13));
    sCIStart2 -= (deltaMs21 * beatsin88(777, 8, 11));
    sCIStart3 -= (deltaMs1 * beatsin88(501, 5, 7));
    sCIStart4 -= (deltaMs2 * beatsin88(257, 4, 6));

    fill_solid(leds, ledCount, CRGB(2, 6, 10));

    pacificaOneLayer(leds, ledCount, pacificaPalette1, sCIStart1, beatsin16(3, 11 * 256, 14 * 256), beatsin8(10, 70, 130), 0 - beat16(301));
    pacificaOneLayer(leds, ledCount, pacificaPalette2, sCIStart2, beatsin16(4,  6 * 256,  9 * 256), beatsin8(17, 40,  80), beat16(401));
    pacificaOneLayer(leds, ledCount, pacificaPalette3, sCIStart3, 6 * 256, beatsin8(9, 10, 38), 0 - beat16(503));
    pacificaOneLayer(leds, ledCount, pacificaPalette3, sCIStart4, 5 * 256, beatsin8(8, 10, 28), beat16(601));

    pacificaAddWhitecaps(leds, ledCount);
    pacificaDeepenColors(leds, ledCount);
}


void pacificaOneLayer(
    CRGB leds[],
    uint16_t ledCount,
    CRGBPalette16& palette,
    uint16_t colorIndexStart,
    uint16_t waveScale,
    uint8_t brightness,
    uint16_t indexOffset)
{
    uint16_t colorIndex = colorIndexStart;
    uint16_t waveAngle = indexOffset;
    uint16_t waveScaleHalf = (waveScale / 2) + 20;

    for (uint16_t i = 0; i < ledCount; i++)
    {
        waveAngle += 250;
        uint16_t s16 = sin16(waveAngle) + 32768;
        uint16_t cs = scale16(s16, waveScaleHalf) + waveScaleHalf;
        colorIndex += cs;
        uint16_t sindex16 = sin16(colorIndex) + 32768;
        uint8_t sindex8 = scale16(sindex16, 240);
        CRGB color = ColorFromPalette(palette, sindex8, brightness, LINEARBLEND);
        leds[i] += color;
    }
}


void pacificaAddWhitecaps(CRGB leds[], uint16_t ledCount)
{
    uint8_t baseThreshold = beatsin8(9, 55, 65);
    uint8_t wave = beat8(7);

    for (uint16_t i = 0; i < ledCount; i++)
    {
        uint8_t threshold = scale8(sin8(wave), 20) + baseThreshold;
        wave += 7;
        uint8_t light = leds[i].getAverageLight();

        if (light > threshold)
        {
            uint8_t overage = light - threshold;
            uint8_t overage2 = qadd8(overage, overage);
            leds[i] += CRGB(overage, overage2, qadd8(overage2, overage2));
        }
    }
}


void pacificaDeepenColors(CRGB leds[], uint16_t ledCount)
{
    for (uint16_t i = 0; i < ledCount; i++)
    {
        leds[i].blue = scale8(leds[i].blue, 145);
        leds[i].green = scale8(leds[i].green, 200);
        leds[i] |= CRGB(2, 5, 7);
    }
}