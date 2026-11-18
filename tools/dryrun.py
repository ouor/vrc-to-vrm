"""Dry-run converter: inventory a package and simulate the export without Unity.

What it proves:
- shader families present (what the MToon mapping must handle)
- clothing groups / variants detected from FX-referenced clips
- which renderers are visible / hidden per variant
- a stable JSON manifest agents and tests can consume

It does not produce .vrm bytes: that needs Unity + UniVRM (see README).
"""
from __future__ import annotations

import json
from pathlib import Path

from .anims import parse_anim
from .clothing import detect_clothing
from .materials import parse_material
from .prefab import PrefabInfo
from .unitypackage import UnityPackage
from .unityyaml import ref_guids


def fx_referenced_anims(pkg: UnityPackage, controllers: list[str]) -> set[str]:
    """Project paths of .anim clips referenced by any controller."""
    g2p = pkg.guid_to_path()
    out: set[str] = set()
    for c in controllers:
        try:
            text = pkg.read_text(c)
        except KeyError:
            continue
        for guid in ref_guids(text):
            p = g2p.get(guid)
            if p and p.endswith(".anim"):
                out.add(p)
    return out


def dry_run(package_path: str | Path) -> dict:
    pkg = UnityPackage(package_path)
    g2p = pkg.guid_to_path()
    prefabs = pkg.find(".prefab")
    mats = pkg.find(".mat")
    controllers = pkg.find(".controller")
    anims = pkg.find(".anim")
    shaders = pkg.find(".shader")
    report: dict = {
        "package": str(package_path),
        "counts": {"prefabs": len(prefabs), "materials": len(mats), "controllers": len(controllers), "anims": len(anims), "shaders": len(shaders)},
        "prefabs": [],
        "materials": [],
        "fx": {"controllers": controllers, "referenced_anims": 0, "clothing_clips": 0, "groups": []},
    }
    prefab_texts = pkg.read_text_many(prefabs)
    for p in prefabs:
        if p not in prefab_texts:
            continue
        info = PrefabInfo(p, prefab_texts[p], g2p)
        report["prefabs"].append({
            "path": p, "game_objects": len(info.game_objects),
            "skinned_meshes": len(info.renderers),
            "inactive_renderers": [r.game_object for r in info.inactive_renderers][:20],
        })
    family_counts: dict[str, int] = {}
    mat_texts = pkg.read_text_many(mats)
    for p in mats:
        text = mat_texts.get(p)
        if text is None:
            continue
        import re
        m = re.search(r"m_Shader: \{[^}]*guid: ([0-9a-f]{32})", text)
        shader_path = g2p.get(m.group(1), "?") if m else "?"
        info = parse_material(p, text, shader_path)
        family_counts[info.family] = family_counts.get(info.family, 0) + 1
        report["materials"].append({
            "path": p, "shader": shader_path, "family": info.family,
            "mode": info.props.get("_Mode"), "cutoff": info.props.get("_Cutoff"),
            "udim": len(info.udim_tiles),
        })
    report["material_families"] = dict(sorted(family_counts.items()))
    referenced = fx_referenced_anims(pkg, controllers)
    anim_map: dict[str, list] = {}
    anim_texts = pkg.read_text_many(sorted(referenced))
    for p in sorted(anim_texts):
        anim_map[p.rsplit("/", 1)[-1]] = parse_anim(anim_texts[p])
    # clip names are basenames; duplicates collapse in this dict (matches C# byClip behaviour)
    clips, groups = detect_clothing(anim_map)
    report["fx"]["referenced_anims"] = len(anim_map)
    report["fx"]["clothing_clips"] = len(clips)
    report["fx"]["groups"] = [{"key": g.key, "category": g.category, "variants": [c.name for c in g.clips]} for g in groups]
    return report


def report_markdown(report: dict) -> str:
    c = report["counts"]
    lines = [
        "# Dry-run report", "", f"Package: `{report['package']}`", "",
        f"prefabs={c['prefabs']} materials={c['materials']} controllers={c['controllers']} anims={c['anims']} shaders={c['shaders']}", "",
        "Material families: " + ", ".join(f"{k} x{v}" for k, v in report["material_families"].items()), "",
        f"FX-referenced anims: {report['fx']['referenced_anims']}, clothing clips: {report['fx']['clothing_clips']}, groups: {len(report['fx']['groups'])}", "",
    ]
    for g in report["fx"]["groups"]:
        lines.append(f"- {g['key']} ({g['category']}): {', '.join(g['variants'])}")
    return "\n".join(lines) + "\n"


def write_report(package_path: str | Path, out_dir: str | Path) -> Path:
    out_dir = Path(out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)
    report = dry_run(package_path)
    stem = Path(str(package_path)).stem.replace("[", "").replace("]", "").strip() or "package"
    (out_dir / f"{stem}.manifest.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    (out_dir / f"{stem}.manifest.md").write_text(report_markdown(report), encoding="utf-8")
    return out_dir / f"{stem}.manifest.json"
