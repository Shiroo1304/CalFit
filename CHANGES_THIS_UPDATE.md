# CalFit frontend update

Changes:
- Scanner: blank food weight is accepted and sent to existing API as 0 grams. Blank macros are also treated as 0; calories and food name remain required. This is not a nullable weight in the database.
- Removed the in-app Settings page, its top-right button, and Profile settings shortcut.
- Kept the login-screen API connection configuration because it is functional and required for local emulator/phone connectivity.
- Added CSS fixes for scanner field/button spacing, narrow screens, and Android navigation/safe-area padding.
- Preserved API endpoints, authentication, AI scanning, meal saving, and navigation.

Replace the existing frontend project with the CalFit folder in this archive. Back up your old project first. Keep the API running and retain your working `wwwroot/index.html` configuration if it differs.

This update has been source-checked, not compiled or tested on an Android device in this environment.
