# CalFit Modern — .NET 10 MAUI Blazor Hybrid frontend

**What this is:** A clean, redesigned Android frontend based on the two CalFit ZIP files supplied on 2026-10-18. It targets the **existing, unmodified `CalFit.Api` backend**, not a made-up new API. It replaces the old XAML-heavy frontend with a minimal native MAUI host and a Razor/HTML/CSS interface.

**Build status:** Source and XML were statically validated in the generation environment. A Windows/.NET Android workload, running SQL Server, physical camera, and your live API were not available for an actual build/end-to-end test. Please treat the first Visual Studio build as the final validation step—not a guaranteed zero-error binary.

## Quick start

1. **Back up** your original frontend. This ZIP is a **replacement project**, not files to scatter into the old project.
2. Unzip it and open `CalFit.sln` using Visual Studio with .NET 10 and the **.NET MAUI Android workload** installed. Restore NuGet packages if prompted.
3. In a **separate instance of Visual Studio**, open and run **your existing** `CalFit.Api` project. Select its **HTTP** launch profile. From your uploaded `launchSettings.json`, this binds to `http://localhost:5206` on the PC.
4. Ensure SQL Server connection, EF Core migrations, and JWT settings in your backend are configured. AI scanning additionally requires a valid vision-capable Groq model and API key configured **on the server**.
5. Run the **CalFit** app on your Android emulator. It defaults to `http://10.0.2.2:5206/`. Tap **API connection settings** on the login screen, then **Test connection** before signing in.
6. For a **physical USB Android phone**, run `adb devices` and `adb reverse tcp:5206 tcp:5206` on the computer. In CalFit's login settings, switch the backend URL to `http://127.0.0.1:5206/`. Every time ADB reverse disconnects, reapply it.
7. For Wi-Fi-connected phones, use your PC's reachable LAN address and configure backend binding/firewall accordingly. **Do not** expect `localhost` or `10.0.2.2` to work from a Wi-Fi-only physical device. Use HTTPS and proper authentication when exposing your API beyond local development.
8. Register an account or sign in. The token is stored through native **SecureStorage** under the same `auth_token` key as your prior CalFit app. The app may overwrite the old installed APK because it uses the same ApplicationId (`com.calfit.prototype`).

If login fails, first confirm that **Test connection** says `Backend reachable`, then check your API HTTP launch profile and SQL Server. `app.UseHttpsRedirection()` on the supplied backend may redirect HTTP to a development HTTPS certificate in some configurations; prefer using the HTTP launch profile for emulator testing or set up a certificate that the device trusts. Never disable HTTPS validation in production.

## Screens and capabilities

| Screen | Actual behavior |
|---|---|
| Welcome / Sign in / Sign up | POST `api/auth/login` and POST `api/auth/register` against your existing server; secure JWT token storage, restored session |
| Dashboard | GET `api/dashboard` and `api/meals?date=...`, GET `api/profile`; live calorie ring, macro progress, daily date picker, recent meals |
| Meal Journal | GET `api/meals/all`, searchable category filters, real detail panel, DELETE `api/meals/{id}` with confirmation |
| Food Scanner | Camera/gallery using MAUI MediaPicker; POST multipart `api/ai/analyze-food`; editable estimates; manual food entry and POST `api/meals` |
| Profile | GET and PUT `api/profile`; height, weight, name, energy and macro goals; BMI calculation is informational |
| Insights | Real history via GET `api/meals/all`; seven-day totals and simple calculated insights, **not** a chat AI |
| Settings | Four theme palettes, custom accent, text size preference, persistent API URL with connection diagnostics |
| Device Center | Honest device integration checklist and navigation to manual scan; no simulated connection |

### Important limitations of your **current backend**

These require further API work / real ESP32 firmware, **not just frontend UI**:

- The uploaded backend has **no robot/device routes**, **no device pairing**, **no weight streaming**, and **no actuator or TFT endpoints**. This package deliberately does not fake a device connection. Robot integration requires firmware, device authentication, scan synchronization, and safety testing.
- The uploaded backend has **no AI chat endpoint**. The Insights screen is a real computed summary, not a claimed Groq chatbot.
- The uploaded backend supports meal creation and deletion, **not meal updating**. To edit a saved meal you must currently delete and recreate it; this app does not fake an update API.
- Ingredients and personal notes are stored in the phone's `Preferences` keyed by **user ID + meal ID**. They are **not stored in SQL Server** or synchronized to other phones or the TFT because the current meal database/DTO has no ingredient fields. Clearing app data removes these notes.
- The provided AI service requests `foodName`, macros and `notes`, but **does not return a structured ingredients array**; ingredients must be entered/verified manually. Visual food recognition and estimated nutrition depend on the server's configured Groq model and API key.
- Food weight comes from **manual user entry** until real scale integration is implemented. An AI photo is not a reliable scale and will never silently override the entered weight.

## Backend endpoint mapping (verified against supplied code)

- `POST /api/auth/login`
- `POST /api/auth/register`
- `GET /api/profile`
- `PUT /api/profile`
- `GET /api/dashboard?date=YYYY-MM-DD&offsetMinutes=...`
- `GET /api/meals?date=YYYY-MM-DD&offsetMinutes=...`
- `GET /api/meals/all`
- `POST /api/meals`
- `DELETE /api/meals/{id}`
- `POST /api/ai/analyze-food` (multipart `image`, optional `weightGrams`)

The app expects the API at an absolute URL with a trailing slash. `api/meals` and profile calls use JWT bearer authorization. Data comes from real API responses, never hardcoded meal lists.

## Files of interest

```text
CalFit.sln
CalFit/
  CalFit.csproj                  .NET 10 Android MAUI + Razor SDK
  MauiProgram.cs                 DI registration
  App.xaml / App.xaml.cs         Native application
  MainPage.xaml / .xaml.cs       One BlazorWebView host
  Components/
    CalFitRoot.razor              Main navigation/auth gate
    Views/*.razor                 Working interface pages
  Models/ApiModels.cs            Types that match existing API responses
  Services/CalFitApi.cs          HTTP API client and per-device URL setting
  Services/AuthSession.cs        Secure JWT login session
  Services/AppearanceService.cs  Themes, custom accent, font scaling
  Services/MealNotesService.cs   Local-only ingredients/notes
  wwwroot/css/calfit.css         Responsive premium dark/light design
  Platforms/Android/             Android permissions and entry point
```

## Security and release considerations

Do not put database connection strings, Groq API secrets, or JWT signing keys in this Android project. Keep them on the backend. The app permits cleartext HTTP for **local development only**; for a public release set up HTTPS and remove broad cleartext allowance. The Android app is not a substitute for secure server-side validation, which your existing backend performs on JWT-protected routes. Calories and macros are estimates; review before saving.

## If the first Visual Studio build fails

Check that the MAUI workload installed matches `Microsoft.Maui.Controls` and `Microsoft.AspNetCore.Components.WebView.Maui` version `10.0.110` used by your supplied project. Do **not** paste C# into `CalFit.csproj`; it is XML. Restore packages and build the **CalFit** startup project. Send the exact compiler error and filename rather than changing the backend.

No test APK was generated here. Final Android build, network test, camera permissions and authenticated API/SQL tests must be performed in Visual Studio on your machine.
