# Agent guide — VrcToVrm

Unity editor tool converting VRChat avatars to VRM 0.x. Core logic lives in `Editor/`.

## Layout

- `Editor/` — C# converter (the product). Unity 2022.3 + VRC SDK + lilToon + UniVRM 0.x.
- `tools/` — Python offline harness: inspect `.unitypackage` files, detect clothing
  variants, dry-run conversion, emit a JSON manifest. Mirrors the C# logic so it
  can be tested without a Unity license.
- `tests/` — pytest suites (unit + synthetic-package e2e; real GB packages opt-in).
- `docs/` — user docs. `Localization~/strings.json` is the single source of UI text.

## Run tests (no Unity needed)

```bash
python3 -m pytest tests -q
python3 -m tools.cli dryrun <pkg.unitypackage> --out /tmp/reports
```

Real packages: `VRC_ROSARY=<path> VRC_LESI=<path> python3 -m pytest tests/test_e2e_pkg.py -q`

## Rules

1. **C# and Python must agree.** `PoiyomiToMToon.IsPoiyomi` / `MaterialFamily` and
   the category words + grouping in `ClothingVariants` have mirrors in `tools/`.
   Change both in the same commit; the tests check the Python side.
2. **UI text goes in `Localization~/strings.json`**, never hard-coded. After editing,
   run `python3 Localization~/gen_table.py` and commit the regenerated table.
   Placeholders (`{0}`) must match across all four languages.
3. **Clothing clips are hold-pose toggles**: sample the last key. Never assume a
   clip name implies visibility; always read the curves.
4. **Never mutate the source prefab.** Variant application works on the converter
   copy and clones materials before setting animated properties.
5. **New `.cs` files need a `.cs.meta`** (Unity MonoImporter with a fresh GUID;
   keep existing GUIDs stable).

## Producing an actual .vrm

Requires Unity 2022.3 + VRChat SDK + lilToon + UniVRM 0.x on Windows/macOS.
Install per `README.md`, then Tools > VRC \> VRM > Converter, or batch:

```
Temp/VrcToVrm.run   (one prefab asset path per line)
```

Report lands in `Logs/VrcToVrm_report.txt`. This repo's CI does not run Unity;
it runs the Python suites and the localization check.
