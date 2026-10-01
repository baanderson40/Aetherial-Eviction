# Aetherial Eviction

## Build

- Requires Windows, .NET 10 SDK, Dalamud API 15 development files, and x64.
- From the repository root:
  - `dotnet restore "AetherialEviction/AetherialEviction.slnx" --locked-mode`
  - `dotnet build "AetherialEviction/AetherialEviction.slnx" --configuration Release --no-restore`
- Focused build: `dotnet build "AetherialEviction/AetherialEviction/AetherialEviction.csproj" --configuration Release`
- No test project, lint/format command, code generator, or CI workflow exists.
- Preserve `AetherialEviction/packages.lock.json`; do not edit `bin/`, `obj/`, `.vs/`, or user files.

## Architecture

- `AetherialEviction/Plugin.cs` is the Dalamud entrypoint and composition root.
- `BackgroundRenderGateService.cs` controls ECommons render suppression and combat/transition bypasses.
- `WindowPlacementService.cs` discovers, moves, and sizes the native FFXIV window across monitors.
- `Configuration.cs` stores persisted Dalamud configuration; `ConfigWindow.cs` owns configuration UI.

The plugin targets `net10.0-windows7.0`, uses unsafe code, and calls `psapi.dll` and `user32.dll`. Changes to native handles, render gating, or working-set trimming require live FFXIV/Dalamud validation; a successful build is not runtime verification.
