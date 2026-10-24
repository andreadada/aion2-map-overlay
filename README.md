# Aion 2 Map Overlay

Find gatherables and collectibles without switching between the game and a website. The overlay places known spawn locations on your in-game map and follows it as you pan and zoom.

Uses screen capture only. It does not read game memory, capture game network traffic, or send input to the game.

**[Download the portable Windows app](https://github.com/andreadada/aion2-map-overlay/actions/workflows/build.yml)**

Extract the ZIP, open the `aion2-map-overlay` folder, and run `aion2-map-overlay.exe`. On first launch, download the map data from the app, then follow the map calibration prompt. No installer is required.

For the larger minimap layout, open **Larger minimap setup…** in the app. Copy the preset code, then in Aion 2 press **ESC → Edit HUD → Load → Import Code**, paste it, and click **Import**.

## Fork improvements\n\n- **Guided minimap selection:** click **Select minimap area…**, drag a cyan rectangle around the minimap on the game screen, and release. The monitor-relative rectangle persists across restarts; **Reset to preset** restores legacy positioning.\n- **Minimap navigation:** nearest mapped resource is now shown by a direction ring *on the minimap* instead of over the middle of gameplay. The selected destination is outlined in gold. The direction is map-relative (north-up), so it does not require camera cone detection.\n- **First run:** download Questlog data and calibrate the full Verteron map as before. For an untagged fork build, download the Windows portable artifact from the GitHub Actions build run, not the upstream release.\n\n## Features

- **Resource filters:** Choose from 15 gatherable and collectible categories covering 4,733 Verteron locations.
- **Map overlay:** Icons stay aligned as you pan and zoom, and hide when tracking is lost.
- **Minimap icons:** Selected resources and collectibles appear on the minimap while the full map is closed.
- **Nearest resource direction:** A north-up pointer centered on the minimap indicates the closest selected mapped gatherable, and a gold ring marks it when in view.
- **Custom appearance:** Adjust icon size and opacity, search categories, and toggle resources with a click.
- **Saved preferences:** Your filters, display, and appearance settings carry over between sessions.
- **In-app updates:** Check GitHub for new versions and install them without losing your settings or downloaded data.
- **Capture recovery:** Temporary capture interruptions retry automatically; error details are saved locally in `logs/overlay.log`.
- **Usage display:** Keep an eye on the app's CPU and RAM use.

Currently supports **Verteron with the English map UI**, at a 16:9 display ratio. Markers show possible spawn locations; live availability and gathering are not detected. Minimap guidance is experimental.

Map data and artwork are downloaded directly from Questlog for local use. Attribution: Questlog.gg / NCSOFT and respective owners.

## Configurator

![Configurator showing resource tiles, appearance controls, optional minimap guidance, and update settings](assets/configurator.png)
![Showcase Large Map](assets/large-map.png)
![Showcase Minimap](assets/minimap.png)


## Build it yourself

You need **Windows x64**, the **.NET 8 SDK**, and **PowerShell 7**.

```powershell
git clone https://github.com/andreadada/aion2-map-overlay.git
cd aion2-map-overlay
dotnet build Aion2MapOverlay.sln -c Release
.\scripts\package-release.ps1
```

The manual-download ZIP is `artifacts/release/aion2-map-overlay-win-x64.zip`. It includes the .NET runtime; map data downloads through the app on first launch.
