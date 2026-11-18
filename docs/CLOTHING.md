# Clothing variations

An avatar's "outfits" are not one thing. The converter now detects and can bake
all of these without knowing the author's naming scheme:

## Mechanisms

| Mechanism | Curves | Where it shows up |
|---|---|---|
| GameObject toggle | `m_IsActive` | Per-outfit objects (Lesi Hoodie/Top/Shorts) |
| Renderer/Behaviour toggle | `m_Enabled` | Props, dusters, hidden objects |
| Material property swap | `material._*` | Poiyomi UDIM discard, dissolve, decal blend, cutoff, hue |
| Non-face blendshape | `blendShape.*` | Bangs, pigtails, chest, sleeves |

Grouping is by **shared curve targets** (path + property), so every clip that
toggles the same object or material becomes one mutually-exclusive group with
one option per clip. A face blendshape-only clip is never clothing.

## Poiyomi UDIM discard (Rosary)

Rosary shares meshes (`Main`, `Secondary`, `Extras`, `Hair Bangs`) across outfits
and hides tiles with `material._UDIMDiscardRow<row>_<col>_*` (1 = discard).
The converter bakes discarded tiles into the main texture's alpha and forces
cutout, so each exported variant shows only its own outfit.

## Per-outfit objects (Lesi)

Lesi uses separate objects (`Lesi_HOODIE`, `Lesi_TOP`, ...) with `m_IsActive`
plus `material._DissolveAlpha_*` for fade transitions. The Clothing tab picks
one variant per group; the dissolvemap is folded into alpha and inactive
outfits are dropped by the usual cleanup.

## Export workflow

1. Select the avatar, open the **Clothing** tab.
2. Pick one option per group (or leave **Default**).
3. Convert once per combination you want as separate `.vrm` files.

## Limitations

- Material swaps beyond the last key keep their end value (hold-pose assumption,
  same as expressions).
- Poiyomi decals (black/white swaps) are warned, not baked; the base texture is exported.
- Extra Poiyomi emission/matcap channels are dropped (MToon has one lane each).
