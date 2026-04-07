const int pin4 = 4;
const int pin5 = 5;
const int pin6 = 6;
const int pin7 = 7;

const int usedPins[] =
{
  pin4,
  pin5,
  pin6,
  pin7
};

const int usedPinCount = sizeof(usedPins) / sizeof(usedPins[0]);

void setup()
{
  delay(8000);

  for (int i = 0; i < usedPinCount; i++)
  {
    pinMode(usedPins[i], OUTPUT);
    digitalWrite(usedPins[i], LOW);
  }
}

void loop()
{
  for (int i = 0; i < usedPinCount; i++)
  {
    digitalWrite(usedPins[i], LOW);
    delay(500);
  }

  for (int i = 0; i < usedPinCount; i++)
  {
    digitalWrite(usedPins[i], HIGH);
    delay(500);
  }
}