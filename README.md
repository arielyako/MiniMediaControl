# Mini Media Control

Universal always-on-top mini player for Windows 10 (1809+) / 11, powered by the
System Media Transport Controls (SMTC) API. Works with Spotify, browsers, VLC, etc.

## Use
- Drag the top grip bar to move. Position is saved to %LOCALAPPDATA%\MiniMediaControl\settings.json
- Swipe left/right on the card, scroll the mouse wheel, or click the small arrows to switch sources
- Right-click: reset position / exit
- The window auto-hides when no media session exists

## Build (needs .NET 8 SDK)
    dotnet run

## Publish (single .exe, no .NET install needed on target PC)
    dotnet publish -c Release -r win-x64 --self-contained true ^
      -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

Output: bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\MiniMediaControl.exe

## Start with Windows
Press Win+R, type `shell:startup`, and drop a shortcut to MiniMediaControl.exe there.
