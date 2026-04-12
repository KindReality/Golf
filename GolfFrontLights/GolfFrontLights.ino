#include <FastLED.h>

#define LED_TYPE WS2812B
#define COLOR_ORDER GRB
#define DATA_PIN 9
#define NUM_LEDS 150
#define BRIGHTNESS 255
#define FRAMES_PER_SECOND 120

CRGB leds[NUM_LEDS];

const uint8_t chaseLength = 12;
const uint8_t fadeAmount = 180;
const uint8_t moveEveryMs = 30;

uint16_t headIndex = 0;
unsigned long lastMoveMs = 0;

void setup()
{
  delay(2000);

  FastLED.addLeds<LED_TYPE, DATA_PIN, COLOR_ORDER>(leds, NUM_LEDS);
  FastLED.setBrightness(BRIGHTNESS);
  FastLED.clear();
  FastLED.show();
}

void loop()
{
  unsigned long currentMs = millis();

  fadeToBlackBy(leds, NUM_LEDS, fadeAmount);

  for (uint8_t i = 0; i < chaseLength; i++)
  {
    uint16_t pixelIndex = (headIndex + NUM_LEDS - i) % NUM_LEDS;
    uint8_t brightness = 255 - (i * (255 / chaseLength));
    leds[pixelIndex] += CHSV(0, 255, brightness);
  }

  FastLED.show();

  if (currentMs - lastMoveMs >= moveEveryMs)
  {
    lastMoveMs = currentMs;

    // reversed direction
    if (headIndex == 0)
      headIndex = NUM_LEDS - 1;
    else
      headIndex--;
  }

  FastLED.delay(1000 / FRAMES_PER_SECOND);
}