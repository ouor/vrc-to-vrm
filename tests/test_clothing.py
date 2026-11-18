"""Clothing grouping mirrors Editor/ClothingVariants.cs heuristics."""
from tools.anims import AnimCurve
from tools.clothing import detect_clothing, guess_category


def C(attr, path, value=0.0, class_id=1):
    return AnimCurve(attr, path, class_id, value)


def test_category_top_dress():
    assert guess_category("top dress", [C("m_IsActive", "Main")]) == "Top"


def test_category_hair():
    assert guess_category("bangs 1", [C("blendShape.Bangs", "Hair")]) == "Hair"


def test_mutual_toggles_group_together():
    # three variants toggling the same object -> one group of 3
    anims = {
        "top dress": [C("m_IsActive", "Main", 1)],
        "top corset": [C("m_IsActive", "Main", 0)],
        "top bodysuit": [C("m_IsActive", "Main", 1)],
    }
    clips, groups = detect_clothing(anims)
    assert len(clips) == 3
    assert len(groups) == 1
    assert sorted(c.name for c in groups[0].clips) == ["top bodysuit", "top corset", "top dress"]


def test_on_off_pairs_group():
    anims = {
        "choker on": [C("m_IsActive", "Choker", 1)],
        "choker off": [C("m_IsActive", "Choker", 0)],
    }
    _, groups = detect_clothing(anims)
    assert len(groups) == 1
    assert len(groups[0].clips) == 2


def test_udim_variants_share_tiles_group():
    anims = {
        "top dress": [C("material._UDIMDiscardRow0_1_Dress", "Main")],
        "top tube": [C("material._UDIMDiscardRow1_0_Tube", "Main")],
    }
    _, groups = detect_clothing(anims)
    # different discard properties on same path: same group only if properties shared;
    # here properties differ, so they stay separate (conservative, documented)
    assert len(groups) == 2


def test_face_blendshape_only_is_not_clothing():
    anims = {"smile": [C("blendShape.Smile", "Face")], "top dress": [C("m_IsActive", "Main", 1)]}
    clips, _ = detect_clothing(anims, face_paths={"Face"})
    assert sorted(c.name for c in clips) == ["top dress"]


def test_face_blendshape_only_without_face_known():
    anims = {"smile": [C("blendShape.Smile", "Face")]}
    clips, _ = detect_clothing(anims)
    assert clips == []
