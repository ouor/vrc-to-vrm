"""Detect material families from .mat YAML (mirror of LilToonToMToon / PoiyomiToMToon).

Shader identity comes from the shader GUID -> .shader project path mapping in the same
package. Optimized Poiyomi shaders are per-material (`.../OptimizedShaders/<mat>/Poiyomi Toon.shader`),
so the path itself tells the family.
"""
from __future__ import annotations

import re


class MaterialInfo:
    def __init__(self, path: str, shader_path: str, family: str, props: dict[str, float], udim_tiles: list[str]):
        self.path = path
        self.shader_path = shader_path
        self.family = family  # Poiyomi | lilToon | MToon | FakeShadow | Other
        self.props = props
        self.udim_tiles = udim_tiles

    @property
    def name(self) -> str:
        return self.path.rsplit("/", 1)[-1][:-4] if self.path.endswith(".mat") else self.path


def shader_family(shader_path: str, mat_text: str) -> str:
    if "MToon" in shader_path:
        return "MToon"
    if "Poiyomi" in shader_path or "poiyomi" in shader_path:
        return "Poiyomi"
    if "lilToon" in shader_path or "lilToon" in mat_text:
        return "lilToon"
    if "FakeShadow" in shader_path or "FakeShadow" in mat_text:
        return "FakeShadow"
    # fallback: optimized lilToon/Standard copies often keep the original shader name in the file
    if " lil" in shader_path.lower() or "lil" in shader_path.lower():
        return "lilToon"
    return "Other"


def parse_material(path: str, mat_text: str, shader_path: str) -> MaterialInfo:
    props: dict[str, float] = {}
    mfloats = mat_text.split("m_Floats:", 1)
    if len(mfloats) == 2:
        scope = mfloats[1].split("m_Colors:", 1)[0]
        for name, val in re.findall(r"- (\S+): ([^\n]+)", scope):
            try:
                props[name] = float(val)
            except ValueError:
                pass
    udim = sorted({name for name in props if name.startswith("_UDIMDiscardRow")})
    return MaterialInfo(path, shader_path, shader_family(shader_path, mat_text), props, udim)
