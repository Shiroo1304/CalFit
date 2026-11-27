# Photo-only robot captures

This mode replaces the previous scale-based workflow. The robot sends the JPEG;
CalFit's existing backend AI estimates portion grams and nutrition from the image.
These grams are estimates, not HX711 measurements.

## App

- The scanner no longer displays robot captured grams or live scale readings.
- Status/scan/photo/ACK reception, foreground navigation and persistent receipts
  are retained. The receiver never polls `/weight`.
- Nullable `weightGrams` remains in the wire model for backwards compatibility.
  Legacy stored metadata is retained but never used to prefill the form or AI request.
- First Analyze Food sends only the image unless the user has manually entered grams.
  A finite positive AI `WeightGrams` populates the editable weight field. User edits
  survive closing/reopening the review form and subsequent analysis.
- Unknown/invalid AI grams stay blank; no placeholder weight is invented. Journal
  save retains the backend's existing absent-weight convention (0).
- No backend endpoint, authentication, nutrition calculation or database change is
  needed. The AI provider must be configured and available on the existing backend.

## Robot: flash the photo-only sketch too

`firmware/CalFitBotV41PhotoOnly/CalFitBotV41PhotoOnly.ino` is based on the V4.1 sketch
provided on November 27. It removes HX711 initialization/reading and tare/calibration
collection from the main loop, removes their dashboard buttons and weight display,
and removes both scale-validity guards from CAPTURE. Camera UART, touchscreen,
motor controls, network API, pending-scan/ACK behavior and standby are retained.
`/weight` and `/scan` return `weightGrams:null`; route names remain unchanged.

Before upload, replace `YOUR_HOTSPOT_PASSWORD` in `WiFi.begin` with your phone hotspot
password locally. SSID remains `calfit`. Use the same ESP32-S3 board, libraries and
PSRAM settings as your working V4.1 build. The supplied sketch has not been compiled
with an Arduino toolchain or tested on physical hardware here.

Installing only the new APK does not change the old firmware's tare/calibration
capture guards. Flash this sketch to remove that requirement. HX711 is no longer
required for this mode. The prior schematic's scale connections are optional.

Known existing firmware limit: during a sustained motor hold, the main loop's
return still skips HTTP servicing; release movement before testing transfer.

## Test

1. Install a real-device APK built from this branch (`CalFitBotSimulation=false`).
2. Flash the photo-only robot sketch with your hotspot password; join phone hotspot.
3. Sign in to CalFit, leave it active, and press CAPTURE on the robot without taring.
4. The photo should appear, with no captured-weight/live-scale panel. ACK should
   let the robot resume its own preview.
5. Tap Analyze Food manually. AI-estimated grams should populate the editable field.
6. Edit grams if needed, review nutrition and save. Verify the saved journal weight.

Automated checks: protocol/recovery tests accept photo-only scans and verify that
`/weight` is never polled. Browser checks verify image-only AI input, estimated grams,
editable/preserved weight, saving, duplicate protection and foreground/background
reception. Browser API responses are explicit test doubles, not real AI.
