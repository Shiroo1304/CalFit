# CalFitBot photo and weight receiver

The scanner now receives robot captures. See [the V4.1 firmware review](CALFITBOT-V4.1-REVIEW.md) for verified endpoint behavior and firmware limitations. It has no motor controls, phone camera,
ESP32 capture button or live preview. Those tasks belong to the robot touchscreen.
The existing CalFit themes, navigation, meal review popup, AI API client and journal
save endpoint remain in use. No robot firmware or backend files were changed.

## Get this version

Check out the feature branch or merge its pull request before using these commands.
Keep any uncommitted changes backed up/committed before switching branches.

## Simulation (no robot required)

Use .NET 10, the MAUI Android workload and your existing Android SDK/JDK setup:

```powershell
dotnet build .\CalFit\CalFit.csproj -t:SignAndroidPackage -c Debug -f net10.0-android -r android-arm64 -p:CalFitBotSimulation=true -p:EmbedAssembliesIntoApk=true -p:AndroidPackageFormats=apk
```

Install the resulting `*-Signed.apk`, sign in with your existing backend and open
Food Scanner. Expand Development simulator and choose Submit sample meal. The
bundled JPEG is a synthetic illustration, not a measured food photograph. The
simulator uses the same scan receiver and persistence logic as the real service.

Available scenarios: normal submission, missing weight, two failed photo transfers,
cancellation before photo receipt, two failed acknowledgements, size mismatch,
replayed duplicate scan, disconnect/reconnect, standby and network timeout.
The captured weight stays associated with its photo even if the live scale changes.

Receiving never runs AI or creates a meal. Tap Analyze Food manually, review the
result in the existing popup, then Save to journal. Simulation changes only robot
communication; the real application still needs a functioning backend for login,
AI and saving. A separate browser harness below uses explicit test-only API doubles.

Simulator receipts and remembered addresses use separate storage from real robot data.

The simulation flag is **off by default**, even in Debug builds. It is rejected in
Release builds. Mock service code is excluded outside DEBUG; production does not
render simulation controls. Clean the Debug output when switching modes:

```powershell
dotnet clean .\CalFit\CalFit.csproj -c Debug -f net10.0-android -r android-arm64
dotnet build .\CalFit\CalFit.csproj -t:SignAndroidPackage -c Debug -f net10.0-android -r android-arm64 -p:CalFitBotSimulation=false -p:EmbedAssembliesIntoApk=true -p:AndroidPackageFormats=apk
```

## Physical CalFitBot connection

1. Enable the phone hotspot and connect the robot using its existing firmware.
2. Start the real-device app build and sign in. Foreground reception runs across all tabs; Food Scanner shows status and connection settings.
3. Discovery tries the last verified address, native Android NSD for
   `_calfitbot._tcp.local.`, then `http://calfitbot.local/`.
4. If discovery is blocked by the hotspot implementation, expand Robot connection,
   enter the current private robot IP (no endpoint path), and tap Connect/reconnect.
   Clear the field to return to automatic discovery. Only protocol-verified
   successful addresses are remembered; no robot IP is hardcoded.
5. Press CAPTURE on the V4.1 robot touchscreen. The app receives the JPEG and captured
   grams, stores them together, then POSTs acknowledgement. The robot can return
   to its own live camera mode without waiting for AI analysis.
6. Manually Analyze Food, review, and Save to journal.

The app never exposes robot GPIOs, motor movement, live camera controls, credentials
or AI keys. Local requests use a dedicated HTTP client, not the backend client or
JWT. Android INTERNET, network-state, Wi-Fi-state and multicast permissions support
local HTTP and NSD; no Bluetooth/location permissions are introduced. Standby is
not inferred as offline: responsive /status means connected, and missing live
weight is displayed as unavailable. Analysis uses /scan weight, never later /weight.

## Protocol preserved

| Request | Meaning |
| --- | --- |
| GET /status | Validate device=CalFitBot, protocol=1 and inspect pending |
| GET /weight | Optional current weight; null/unavailable is allowed |
| GET /scan | Scan ID, captured nullable grams, JPEG byte count; 204 means none |
| GET /photo?id=... | Binary JPEG; 404 means unavailable/canceled |
| POST /ack?id=... | Confirm stored receipt; require {"ok":true} |

No new robot endpoints are required. No /stream, /capture or /control calls are
made by the receiver. The optional live weight read does not wake the display.

## Persistence, recovery and limits

- One shared poll loop per signed-in foreground app, scheduled every two seconds, never overlapping. Slow
  requests stretch the interval rather than start additional requests. Discovery
  and reconnects are bounded; leaving the scanner keeps reception active; backgrounding, closing the app or signing out cancels reception. Pending scans resume on return.
- Metadata requests time out after 4 s; photo transfers after 20 s. Photos are capped
  at 10 MiB, checked against photoBytes and JPEG markers, and decoded through Android
  BitmapFactory before committing. Oversized decoded dimensions are rejected.
- JPEG and metadata are written into a staging directory, then renamed as a single
  receipt directory. Only afterward can /ack be called. Storage is under the app's
  private data directory, partitioned by signed-in user; untrusted scan IDs are hashed
  for filenames. Incomplete staging transfers are never acknowledged.
- Scan IDs and completed receipts survive navigation/restart. Failed ACKs reuse the
  stored photo, not a second download. Failed metadata/transfers/ACKs pause after
  three attempts; Retry transfer explicitly resumes. Status polling continues.
- A new submission cannot overwrite an unsaved current draft. Finish or discard it;
  the next pending scan remains on the robot in the meantime. Discard does not send
  a cancel request or invent a robot endpoint.
- Duplicate completed scans are acknowledged without redisplaying them as new meals.
  AI and meal saving are explicit actions, never callbacks from ACK or status polling.
- Nullable captured weight stays nullable locally. A captured 0 g requires reweighing before analysis/saving; it is not silently replaced
  by an AI estimate. A known measured weight is read-only
  in the form; if missing, a user may supply a manual value. Existing backend Meal
  requires a non-null double, so absent weight is sent as 0 on journal save, matching
  its existing optional-weight convention. The UI never calls this a measured 0 g.
- A /photo 404 is not a successful submission. An /ack 404 after storage is treated
  as unconfirmed/canceled, retains the local receipt, and blocks analysis. The contract
  cannot distinguish robot cancellation from a lost successful ACK response unless
  firmware accepts repeated /ack idempotently. No undocumented guarantee is assumed.
- The unchanged backend does not accept a scanId/idempotency key for meal creation.
  Duplicate robot delivery is prevented locally, and a confirmed save disables
  resubmission. Exactly-once meal creation across a crash or a lost backend response
  cannot be guaranteed without a backend idempotency contract.
- Receipts/photos are retained for recovery and duplicate detection. Uninstalling or
  clearing app data deletes them. This change adds no cloud photo backup.

## Verification

Protocol/persistence tests (no hardware, backend or Android SDK required):

```powershell
dotnet run --project tests/CalFitBot.Tests
```

Compile and run the actual ScannerView in a test-only browser host:

```powershell
dotnet build tests/CalFitBot.Preview
cd tests/CalFitBot.Preview
dotnet run --no-build --urls http://127.0.0.1:3033
```

Open http://127.0.0.1:3033. This harness supplies test authentication and nutrition
responses; it is not linked into the mobile app and must not be deployed publicly.
For automated UI checks, in a second terminal in that folder:

```powershell
npm ci
npx playwright install chromium
node browser-test.cjs
```

GitHub Actions `CalFitBot integration` builds simulation and real ARM64 Debug APKs,
a production Release configuration, protocol tests and the browser workflow. Debug
APK signing is for testing; it does not publish to Google Play or host the backend.

## Files

Created:
- CalFit/Models/CalFitBotModels.cs
- CalFit/Services/CalFitBot/ICalFitBotService.cs
- CalFit/Services/CalFitBot/CalFitBotService.cs
- CalFit/Services/CalFitBot/CalFitBotInbox.cs
- CalFit/Services/CalFitBot/CalFitBotSession.cs
- CalFit/Services/CalFitBot/MockCalFitBotService.cs
- CalFit/Services/CalFitBot/CalFitBotPlatform.cs
- CalFit/Platforms/Android/AndroidCalFitBotDiscovery.cs
- CalFit/Platforms/Android/AndroidCalFitBotPhotoValidator.cs
- CalFit/Resources/Raw/calfitbot_sample.jpg
- tests/CalFitBot.Tests/* and tests/CalFitBot.Preview/*
- .github/workflows/calfitbot.yml, global.json and this document

Modified:
- ScannerView.razor and DeviceView.razor
- MauiProgram.cs, CalFit.csproj, AndroidManifest.xml
- wwwroot/css/calfit.css and .gitignore
- App icon filename normalized to calfitlogo.png to match the existing project and
  manifest references on case-sensitive build machines; image content unchanged.

Old Esp32CameraService/esp32-controls.js files remain for reference but are not
registered or referenced by the new scanner. No firmware, backend/API service,
authentication, database, dashboard, profile or meal-history changes were made.

## Remaining physical-device checks

NSD/multicast on the hotspot-hosting phone; calfitbot.local resolution; manual IP
fallback; reconnect after DHCP/IP changes; real JPEG decoding; weight calibration
and null standby readings; cancel/submit races; repeated acknowledgement behavior;
20 s transfer interruptions; router/mobile-data access to the cloud backend; app
background cancellation and foreground resume; theme/font/accessibility and device safe areas. The HTTP
protocol identifies the robot by device/protocol fields, not cryptographic pairing.
