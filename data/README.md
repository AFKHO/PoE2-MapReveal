# data/

The overlay can label each exit with the **name of the area it leads to**
(e.g. "Chimeral Wetlands"). That labelling uses an optional data file:

```
data/important_tgt_files.txt
```

This file is **not** included in this repository. It is a curated
area/tile → name mapping that originates from
[MordWraith's Radar](https://github.com/MordWraith/Gamehelper); redistributing
someone else's data asset under this project's MIT license would not be correct,
so it is left out.

## Without the file

Everything still works. Exits are still detected and routed; they are just
labelled generically (or by nearby entity id) instead of by destination name.

## With the file

If you place a compatible `important_tgt_files.txt` here, the build copies it
next to the executables automatically and exit labels show real destination
names. The expected format is JSON:

```json
{
  "AreaKey": { "TileKey": "Destination Name" }
}
```
