"""Clothing variant detection — offline mirror of Editor/ClothingVariants.cs.

Input: AnimationClip names + their curves (see tools/anims.py). Output: groups
of mutually-exclusive clips (one option per variant). The heuristics (category
words + shared-target grouping) are kept identical to the C# code; update both.
"""
from __future__ import annotations

from .anims import AnimCurve

CATEGORY_WORDS: dict[str, list[str]] = {
    "Top": ["top", "dress", "bodysuit", "corset", "tube", "hoodie", "onesie", "shirt", "bikini top", "bustier", "vest", "jacket", "coat", "sweater", "blouse"],
    "Bottom": ["bottom", "pants", "shorts", "skirt", "bikini bottom", "bottoms", "sweats", "sweatpants", "leggings", "thong", "bikini"],
    "Hair": ["hair", "bangs", "pigtail", "ponytail", "back long", "back short", "fronts", "bang "],
    "Feet": ["feet", "shoe", "sneaker", "heels", "slipon", "socks", "stocking", "legfishnet", "fishnet", "armfishnet", "boots", "warmers"],
    "Face": ["blindfold", "makeup", "lipstick", "lashes", "sclera", "fangs", "piercing", "tattoo", "blush", "freckles"],
    "Body": ["body ", "skin", "tan", "stripe", "tights", "abbs", "chest ", "splatter"],
    "Extra": ["choker", "belt", "bracelet", "necklace", "ring", "garter", "wings", "tail", "earring", "claws", "bag", "fan", "umbrella", "accs", "lore", "extras", "sleeves", "cuffs", "batbra"],
    "Color": ["color", "colour", "hue", "saturation", "black", "white", "swap", "rgb", "greyscale", "grayscale", "outfit hue", "outfit sat", "hair hue", "hair sat", "dress color"],
}
CATEGORY_ORDER = ["Top", "Bottom", "Hair", "Feet", "Face", "Body", "Extra", "Color"]


class ClothingClip:
    def __init__(self, name: str, curves: list[AnimCurve]):
        self.name = name
        self.curves = curves
        self.category = guess_category(name, curves)

    @property
    def has_active(self) -> bool:
        return any(c.kind == "Active" for c in self.curves)

    @property
    def has_material(self) -> bool:
        return any(c.kind == "Material" for c in self.curves)

    @property
    def has_blendshape(self) -> bool:
        return any(c.kind == "BlendShape" for c in self.curves)


class ClothingGroup:
    def __init__(self, clips: list[ClothingClip]):
        self.clips = clips
        self.category = clips[0].category if clips else "Other"
        self.key = f"{self.category}: {clips[0].name}" if len(clips) == 1 else f"{self.category} [{len(clips)}]"


def guess_category(name: str, curves: list[AnimCurve]) -> str:
    haystack = (name + " " + " ".join(c.path + " " + c.attribute for c in curves)).lower()
    for cat in CATEGORY_ORDER:
        if any(w in haystack for w in CATEGORY_WORDS[cat]):
            return cat
    return "Other"


def group_clips(clips: list[ClothingClip]) -> list[ClothingGroup]:
    kept = [c for c in clips if c.curves]
    parent = list(range(len(kept)))

    def find(x: int) -> int:
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x

    def union(a: int, b: int):
        a, b = find(a), find(b)
        if a != b:
            parent[b] = a

    by_target: dict[str, int] = {}
    for i, c in enumerate(kept):
        for target in {(cur.path, cur.attribute) for cur in c.curves}:
            if target in by_target:
                union(i, by_target[target])
            else:
                by_target[target] = i
    groups: dict[int, list[ClothingClip]] = {}
    for i, c in enumerate(kept):
        groups.setdefault(find(i), []).append(c)
    result = []
    for members in groups.values():
        members.sort(key=lambda c: c.name.lower())
        result.append(ClothingGroup(members))
    result.sort(key=lambda g: g.key)
    return result


def detect_clothing(anims: dict[str, list[AnimCurve]], face_paths: set[str] | None = None) -> tuple[list[ClothingClip], list[ClothingGroup]]:
    """anims: clip name -> curves. Pure face blendshape clips are dropped (they are expressions)."""
    face_paths = face_paths or set()
    clips = []
    for name, curves in anims.items():
        clothing = [c for c in curves if c.is_clothing]
        if not clothing:
            continue
        # Blendshape-only clips are face expressions; without an explicit face mesh, still treat them as such (they never change clothing).
        only_face = all(c.kind == "BlendShape" for c in clothing) and (not face_paths or all(c.path in face_paths for c in clothing))
        if only_face:
            continue
        clips.append(ClothingClip(name, clothing))
    return clips, group_clips(clips)
