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

  Serial.begin(9600);

  for (int i = 0; i < usedPinCount; i++)
  {
    pinMode(usedPins[i], INPUT);
  }
}

void loop()
{
  for (int i = 0; i < usedPinCount; i++)
  {
    int pinValue = digitalRead(usedPins[i]);

    Serial.print("Pin ");
    Serial.print(usedPins[i]);
    Serial.print(": ");
    Serial.print(pinValue);
    
    Serial.print(" ");
  }

  Serial.println();
  delay(100);
}