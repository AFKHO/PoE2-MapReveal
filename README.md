# github-map

A full-map overlay for **Path of Exile 2**. On entering an area it reveals the
whole instance map and draws it directly on top of the game's own large (Tab)
map — matching its position, zoom and panning — plus monsters coloured by
rarity and a routed path to every exit.

> Reads the game's memory read-only to draw the map. It does not modify the
> game, inject code, or send input. See **Disclaimer** below.

## What it does

- **Full instance map on entry** — the explored *and* unexplored layout, drawn
  as an outline over the game's own map so it lines up as you zoom and pan.
- **Monsters by rarity** — normal (red), magic (blue), rare (yellow),
  unique (brown). Each rarity can be toggled and sized independently.
- **Exit paths** — a differently-coloured route from the player to each exit,
  labelled with where it leads (destination names need the optional data file,
  see [`data/README.md`](data/README.md)).
- **Sits on the game map** — projection scale, angle and centre are read live
  from the game's map UI element, so nothing is hand-dialled.

## Projects

| Project | Type | Purpose |
|---|---|---|
| **MapOverlay** | WinForms tray app | The product. Run it and the map appears in-game. Tray → *Ayarlar* for line colour/thickness and the per-rarity monster panel. |
| **MapView** | WinForms | Development / calibration tool with all the alignment controls exposed. |
| **MapScan** | Console | Memory diagnostics — the commands used to locate structures and offsets (terrain, entities, UI, exits). |

## Requirements

- Windows (x64)
- [.NET 10 SDK](https://dotnet.microsoft.com/) or later

## Build & run

```bash
# the tray overlay (the product)
dotnet build MapOverlay/MapOverlay.csproj -c Release
dotnet run  --project MapOverlay/MapOverlay.csproj -c Release
```

`MapView` and `MapScan` build the same way. A single-file, self-contained
build:

```bash
dotnet publish MapOverlay/MapOverlay.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

## How it works (short version)

Nothing depends on hardcoded addresses. Structures are recognised by their own
internal consistency — for example the terrain block is accepted only when its
tile counts, row stride and data-vector length all agree with each other, which
random memory never does. That makes it survive game patches without a new
offset list. Details live in the comments of `MapScan/TerrainFinder.cs`,
`MapScan/LocalPlayer.cs`, `MapScan/UiFinder.cs` and `MapView/OverlayForm.cs`.

## Optional data

Exit **destination names** use an optional file that is not shipped here — see
[`data/README.md`](data/README.md). Everything works without it; exits are just
labelled generically.

## Disclaimer

This is an independent, unofficial project. It is **not** affiliated with,
endorsed by, or connected to Grinding Gear Games.

It reads game memory to render a map. Third-party tools of any kind may be
against the game's Terms of Service; whether and how you use this is your own
responsibility and risk. Provided "as is", without warranty — see
[`LICENSE`](LICENSE).

## License

[MIT](LICENSE).
