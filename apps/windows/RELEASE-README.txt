Zone 0.1.0 for Windows x64

Extract the entire ZIP into a folder, then open Zone.Desktop.exe. Keep the other
files beside the executable. Windows 10 version 2004 or newer, x64, is required.
The .NET and Windows App SDK runtimes are included; no .NET SDK is required.
If Windows reports a missing Visual C++ runtime, install Microsoft's supported
Visual C++ x64 Redistributable: https://aka.ms/vs/17/release/vc_redist.x64.exe

This is an unsigned early build. Windows may show an unknown-publisher warning.
Check BUILD-INFO.json for the exact source commit and the release's SHA256 file
for the ZIP's checksum. Do not run untrusted copies of the app.

Open + on this PC and on the Mac, connect from either, compare the six-digit codes,
and approve only when they match. Already paired devices reconnect automatically.
Each device owns its board. Other boards are read-only and remain cached offline.

Type and press Enter to add a note (Shift+Enter for a line break), paste with Ctrl+V,
or use the paperclip to add files. Files are limited to 8 MiB, text to 1 MiB.
Drag cards to arrange them; positions stay on this PC. Click cards to copy or save;
hover or right-click for actions. + pairs, - or Escape minimizes, Alt+F4 quits.
Right-click the top bar for Keep on top and Quit. There is no tray/global hotkey yet.

Data is stored under %LOCALAPPDATA%\Zone\PairingProbe, shared with the development
pairing probe so existing pairing carries over. Close the probe before opening Zone.
Notes, cached files and pairing keys are not encrypted at rest. Do not copy a live
data directory to another device. Set ZONE_DATA_DIR for an isolated test store.

Windows invitations, automatic group introduction, Forget and renaming are pending.
The Windows build has been exercised on the development PC, including real Mac
pairing/sync; it has not been verified on a clean Windows installation. Light/high-
contrast themes, Explorer file drops and touch need further hands-on testing.

Source, license and current acceptance checks:
https://github.com/matteomejia/Zone
