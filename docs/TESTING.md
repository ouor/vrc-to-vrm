# Testing

## Offline (this repo, no Unity)

```bash
python3 -m pytest tests -q
python3 -m tools.cli dryrun <pkg.unitypackage> --out /tmp/reports
```

- `tests/test_materials.py` — shader-family detection (Poiyomi/lilToon/MToon/Other)
- `tests/test_clothing.py` — category words + mutual-exclusion grouping
- `tests/test_anims.py` — AnimationClip last-key curve parsing
- `tests/test_e2e_pkg.py` — synthetic `.unitypackage` -> dry-run -> manifest assertions;
  real packages run only when `VRC_ROSARY` / `VRC_LESI` point at them.

## With Unity (on a VRChat workstation)

Prerequisites per `README.md` (Unity 2022.3, VRChat SDK, lilToon, UniVRM 0.x).
Open a Rosary/Lesi-based avatar, pick variants in the **Clothing** tab, convert,
and check `Logs/VrcToVrm_report.txt`. Batch via `Temp/VrcToVrm.run` (one prefab
path per line). A correct run logs the shader-family counts and clothing groups.
