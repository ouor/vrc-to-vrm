"""Parse Unity .anim (AnimationClip) YAML: float curves with last-key values.

Mirrors ClothingVariants.ReadCurves in C#: clothing clips are hold-pose toggles,
so the last key is the variant's value. Curves are `attribute` + `path` + `classID`
under AnimationClip -> m_FloatCurves.
"""
from __future__ import annotations

import re

CURVE_RE = re.compile(
    r"attribute: '([^']+)'\n    path: ([^\n]+)\n    classID: (\d+)"
)
CURVE_RE_PLAIN = re.compile(
    r"attribute: ([^'\n][^\n]*)\n    path: ([^\n]+)\n    classID: (\d+)"
)
# value of the first m_Curve key after each attribute is hard to pin down with one regex;
# instead we collect curves in document order and look ahead for `value:` lines.
SKIP = {"m_RotationCurves", "m_CompressedRotationCurves", "m_EulerCurves", "m_PositionCurves", "m_ScaleCurves"}


class AnimCurve:
    def __init__(self, attribute: str, path: str, class_id: int, value: float):
        self.attribute = attribute
        self.path = path
        self.class_id = class_id
        self.value = value

    @property
    def kind(self) -> str:
        if self.attribute == "m_IsActive":
            return "Active"
        if self.attribute == "m_Enabled":
            return "Enabled"
        if self.attribute.startswith("material."):
            return "Material"
        if self.attribute.startswith("blendShape."):
            return "BlendShape"
        return "Other"

    @property
    def is_clothing(self) -> bool:
        return self.attribute in ("m_IsActive", "m_Enabled") or self.attribute.startswith(("material.", "blendShape."))


def parse_anim(text: str) -> list[AnimCurve]:
    """All float curves of the clip, in document order."""
    curves: list[AnimCurve] = []
    # Each entry: `- serializedVersion: 2` then curve block ... then attribute/path/classID.
    # Split on the entry marker, parse each chunk independently.
    chunks = re.split(r"(?=\n  - serializedVersion: 2\n    curve:)", text)
    for chunk in chunks:
        m = re.search(r"attribute: '([^']+)'", chunk) or re.search(r"^    attribute: (.+)$", chunk, re.M)
        if not m:
            continue
        attribute = m.group(1).strip().strip("'")
        pm = re.search(r"^    path: (.+)$", chunk, re.M)
        cm = re.search(r"^    classID: (\d+)$", chunk, re.M)
        if not pm or not cm:
            continue
        values = re.findall(r"^        value: ([^\n]+)$", chunk, re.M)
        # the curve block holds keys; take the last `value:` that belongs to m_Curve keys
        # (heuristic: the first group of values before m_PreInfinity)
        pre = chunk.find("m_PreInfinity")
        scope = chunk[:pre] if pre > 0 else chunk
        vals = re.findall(r"value: ([^\n]+)", scope)
        try:
            value = float(vals[-1]) if vals else 0.0
        except ValueError:
            value = 0.0
        curves.append(AnimCurve(attribute, pm.group(1).strip(), int(cm.group(1)), value))
    return curves
