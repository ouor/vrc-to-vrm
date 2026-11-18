"""Shader-family detection mirrors PoiyomiToMToon.IsPoiyomi / MaterialFamily."""
from tools.materials import parse_material, shader_family


def test_poiyomi_family():
    assert shader_family("Assets/.../OptimizedShaders/Body 1/Poiyomi Toon.shader", "") == "Poiyomi"


def test_poiyomi_hidden_locked():
    assert shader_family("Hidden/Locked/.poiyomi/Poiyomi Toon/abc", "") == "Poiyomi"


def test_mtoon_family():
    assert shader_family("VRM/MToon.shader", "") == "MToon"


def test_other_family():
    assert shader_family("Assets/Standard.shader", "") == "Other"


MAT = """Material:
  m_Shader: {fileID: 4800000, guid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa, type: 3}
  m_SavedProperties:
    m_Floats:
    - _Mode: 1
    - _Cutoff: 0.5
    - _UDIMDiscardRow0_1_Dress: 0
    m_Colors:
    - _Color: {r: 1, g: 1, b: 1, a: 1}
"""


def test_parse_props():
    info = parse_material("Assets/Body.mat", MAT, "Poiyomi Toon")
    assert info.family == "Poiyomi"
    assert info.props["_Mode"] == 1
    assert info.props["_Cutoff"] == 0.5
    assert len(info.udim_tiles) == 1
