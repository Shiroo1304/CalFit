# CalFitBot V4.1 compatibility review

## Assessment

The previous app was **partially compatible**: all five HTTP routes and payloads
matched, but polling existed only while Food Scanner was mounted. A capture from
another app tab could not open the scanner, and Android foreground/background
transitions did not own reception.

The updated app reuses the same HTTP service, models, inbox, scanner and analysis
form. It receives across tabs while signed in and active, opens the existing
scanner once a validated receipt is acknowledged, and leaves analysis manual.
The actual physical robot/hotspot has not been exercised in this environment.

Reviewed source: the user's attached V4.1 Arduino sketch, SHA-256
`4f7de1370a321ef51ed89baaa2b3fc0682065379276e94b9a8a0701c3196f559`.
The sketch is not copied into this repository, including its Wi-Fi credentials.

## Actual firmware contract

| Handler | Actual response and lifecycle | App compatibility |
| --- | --- | --- |
| `GET /status` | 200 JSON `device:"CalFitBot"`, `protocol:1`, `pending:boolean` | Validates identity/version; polls without overlapping |
| `GET /weight` | 200 JSON `weightGrams:number` with 2 decimal places, or null if `weightValid()` fails | Optional live reading; not used instead of captured grams |
| `GET /scan` | 204 without pending scan; otherwise 200 `scanId`, `weightGrams`, `photoBytes` | Same model; nullable weight supported for older compatible firmware |
| `GET /photo?id=...` | 200 binary `image/jpeg`; 404 for absent/wrong ID | Checks byte count/JPEG and Android decoding, then stores |
| `POST /ack?id=...` | 200 `{"ok":true}` for pending scan or repeat ACK of latest acknowledged ID; 404 otherwise | Reuses stored receipt on ACK retry; never interprets 404 as success |

`MDNS.begin("calfitbot")` and `MDNS.addService("calfitbot","tcp",80)` match
`calfitbot.local` and `_calfitbot._tcp.local.`. The ESP32 joins the `calfit` hotspot
as a Wi-Fi station; it does not host the phone's network. No robot IP is hardcoded.

### CAPTURE behavior differs from the old wording

`handleTouch()` calls `requestCapture()` for the camera page's touchscreen CAPTURE
control. There is **no separate physical GPIO capture-button handler** in this
sketch and no SUBMIT step or HTTP cancel endpoint. If a separate wired button is
intended, it needs firmware support; the app cannot detect its raw electrical state.

`requestCapture()` requires buffers, a valid tared/calibrated weight, and no pending
scan. It requests a fresh UART frame, discarding any in-flight preview first.
`cameraTick()` validates JPEG SOI/EOI and its 4..65000-byte size, copies the photo,
snapshots `filtered` into `scanWeight`, generates an ID and sets `pendingScan=true`.
The app detects this completed capture, not a button press before capture succeeds.
If capture times out or weight becomes invalid, no pending scan is published.

ACK clears pending state and allows preview/capture again. Repeating the ACK works
only for the most recent ID while the firmware still remembers it. Robot reboot
loses the in-memory pending scan and ACK history; the app retains its own receipts.

## Issues and firmware limits

- App gap fixed: reception now belongs to the signed-in foreground app, not one tab.
- App gap fixed: receipt-ready events navigate to the existing scanner without
  automatically running AI, saving or opening a new Android activity.
- App gap fixed: window deactivation/stopping cancels local reception; activation/
  resume restarts it. Existing heartbeat monitoring is also paused while inactive
  to avoid background network restrictions being interpreted as a server outage.
- App copy fixed: instructions say CAPTURE, not CAPTURE then SUBMIT.
- Existing correct behavior retained: atomic photo+metadata receipt before ACK,
  per-user storage, duplicate IDs, bounded retries, captured-weight preservation,
  existing JWT/backend/AI/database contracts and the application design.
- Firmware limit: `loop()` returns at `if(motorDirection){delay(2);return;}` before
  `networkTick()`. Holding the motor button can therefore stall HTTP and cause a
  temporary disconnect/retry. The mobile app cannot service the robot's HTTP
  server. For connectivity during motor movement, firmware must service
  `networkTick()` before that return (or otherwise schedule HTTP independently).
  Normal release-motor -> CAPTURE -> transfer does not require a protocol change.
- Firmware limit: `weightValid()` checks freshness/tare/calibration/finite values
  but does not reject negative or zero grams. CalFit rejects negative measurements
  before storage/ACK, and requires a positive measured weight before analysis/save.
  Correct tare/calibration and positive food load are needed. Consider rejecting
  nonpositive captured weights in firmware to avoid an unacknowledgeable negative
  pending scan. No fabricated absolute value or AI replacement is applied in app.
- Standby is not disconnection. Network serving continues when sleeping unless
  the motor-return issue above intervenes; pending data remains. This sketch does
  not call HX711 power_down during standby and may still return live grams. The app
  accepts either live grams or null without treating it as offline.
- Android NSD/multicast and `.local` resolution depend on the hotspot implementation.
  Existing NSD plus last verified address/hostname/manual IP fallback remain. An
  explicitly entered manual IP is pinned until cleared or changed.
- One unsaved received meal is kept at a time. Finish/save or discard it before
  receiving a subsequent pending capture; the new scan waits on the robot rather
  than overwriting the previous meal.

No firmware files were modified. No mandatory endpoint or JSON changes are needed
for a positive-weight capture with the motor released. The networking fix above is
needed if HTTP must stay responsive during sustained motor movement. A separate
physical pushbutton needs a handler only if that hardware behavior is intended.

## Final workflow and Android behavior

1. Sign in. A shared receiver starts only while the app window is active and a
   signed-in user profile is available.
2. Try the last verified address, native Android NSD, then `calfitbot.local`; a user
   can override with the current local IP in the existing scanner connection panel.
3. Handshake via `/status` checks CalFitBot/protocol 1. Status polls every two seconds
   when idle; slow requests do not overlap.
4. Press CAPTURE on the robot. Once pending becomes true, retrieve `/scan` and
   `/photo?id=...`; validate grams, JPEG byte count/markers and native decode.
5. Commit photo and metadata together in the existing per-user inbox, then POST ACK.
   ACK interruptions retry the ACK using that receipt; they do not redownload or
   create a meal. V4.1's repeat-ACK behavior supports recovery from a lost response.
6. After confirmation, navigate to the existing scanner and display its image and
   captured grams. Existing review and Analyze Food actions remain manual.
7. Navigating between tabs does not stop reception. Locking/backgrounding pauses
   requests and does not bring an activity to the front. On return, recover stored
   data and pending robot data. No foreground service or notification permission
   is introduced. Force-stop/OS process termination cannot receive until app reopen.
8. An acknowledged, already-presented receipt does not repeatedly steal navigation.
   Sign-out stops reception and clears the displayed owner; receipts remain isolated
   by account for later recovery.

Android INTERNET, ACCESS_NETWORK_STATE, ACCESS_WIFI_STATE,
CHANGE_WIFI_MULTICAST_STATE and `usesCleartextTraffic=true` were already correct;
no broad location or Bluetooth permissions were added. Robot HTTP uses its own
client without cloud JWTs; Cloudflare is for the existing backend, not robot HTTP.

## Files changed

- `CalFit/App.xaml.cs`: window activity events.
- `CalFit/MauiProgram.cs`: shared scoped session/receiver and activity registration.
- `CalFit/Components/CalFitRoot.razor`: signed-in receiver ownership and scanner
  navigation; pause/resume heartbeat with window activity.
- `CalFit/Components/Views/ScannerView.razor`: reuse shared receiver without stopping
  it on tab exit, show paused state, correct CAPTURE instructions.
- `CalFit/Components/Views/DeviceView.razor`: CAPTURE/foreground instructions.
- `CalFit/Models/CalFitBotModels.cs`: paused connection state.
- `CalFit/Services/CalFitBot/CalFitBotSession.cs`: resumable/cancelable polling and
  owner reset while preserving receipt/ACK/duplicate logic.
- New `CalFitAppActivity.cs` and `CalFitBotForegroundReceiver.cs` in that service folder.
- Existing protocol test runner and browser harness extended for V4.1 and lifecycle.
- `CALFITBOT-INTEGRATION.md` and this review document.

The backend API client, auth service, database and nutritional calculations were
not changed. No second scanner or HTTP protocol was introduced.

## Build and automated verification

Run `dotnet run --project tests/CalFitBot.Tests`. The 31 checks cover the prior
receiver, exact V4.1 handler responses and repeat ACKs, foreground reception without
a scanner mounted, background interruption/resume, no duplicate presentation, and
sign-out/account isolation. `dotnet build tests/CalFitBot.Preview` compiles the actual
scanner; its Playwright workflow also covers auto navigation from another tab and
pending capture delivery after resume. Browser API doubles are test-only.

GitHub Actions `CalFitBot integration` builds both ARM64 Debug APK configurations
and compiles Release with simulation disabled and AOT compilation disabled for the
check. See the pull request's latest check run for final build results. APK signing
is for testing and is not Google Play publishing.

## Physical testing instructions

1. Install the newly built **real-device** APK (`CalFitBotSimulation=false`), not the
   development simulator. Sign in; login and AI still need the existing backend.
2. Enable phone hotspot `calfit` with the credentials already configured on the
   robot. Start the robot; tare/calibrate after boot and place a positive food load.
3. Keep CalFit open on Home. Release any motor button before testing CAPTURE.
4. If needed open Scanner -> Robot connection, enter the current robot IP and tap
   Connect/reconnect. Clear this field to return to automatic discovery. Verify
   CalFitBot/protocol 1 handshake; keep the phone on the same local network.
5. Go back to Home. On the robot camera page press CAPTURE. The robot should say
   Waiting for CalFit ACK, then Sent to CalFit. CalFit should open Food Scanner with
   the correct photo and captured grams. Detection normally starts within the next
   idle two-second poll plus network/download time, not an instantaneous guarantee.
6. Confirm no AI request or journal save has occurred. Tap Analyze Food manually,
   review nutrition, then save. Captured grams must stay unchanged if the scale moves.
7. With no unsaved previous draft, background/lock the phone, CAPTURE on the robot,
   then reopen CalFit. The pending scan should remain on robot and arrive on resume;
   the app must not launch itself over the lock screen.
8. Test 30-second standby with and without pending data, hotspot loss/reconnect,
   manual-IP fallback, DHCP address changes in automatic mode, transfer interruption,
   lost ACK/repeat ACK, duplicate delivery, sign-out/sign-in and native JPEG decoding.
9. Separately hold the motor button long enough to test the documented firmware
   HTTP stall. Releasing it should let the app reconnect; uninterrupted service
   during that hold needs the firmware scheduling fix above.
