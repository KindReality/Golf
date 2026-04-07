#include <Servo.h>

Servo servoA;   // Pin 8
Servo servoB;   // Pin 9

const uint8_t SERVO_A_PIN = 8;
const uint8_t SERVO_B_PIN = 9;

void setup() {
  Serial.begin(9600);
  Serial.println("startup...");
  servoA.attach(SERVO_A_PIN);
  servoB.attach(SERVO_B_PIN);

  // Start both servos at 90°
  servoA.write(90);
  servoB.write(90);

  // Seed the random‑number generator (optional but nice)
  randomSeed(analogRead(A0));
}

void loop() {
  Serial.println("loop...");
  // Closed
  servoA.write(90);
  servoB.write(90);
  delay(1000);

  // Wait 5–15 seconds (5000–15000 ms)
  unsigned long waitTime = random(3000, 7000);
  delay(waitTime);
// Decide randomly which side opens first
  if (random(0, 2) == 0) {
    // Open back, then front
    servoA.write(0);
    servoB.write(180);
  } else {
    // Open front, then back
    servoA.write(180);
    servoB.write(0);
  }
  waitTime = random(1000, 3000);
  delay(waitTime);
}