"""End-to-end: build a synthetic .unitypackage, run the dry-run, assert the manifest.

Real packages (Rosary / Lesi) are opt-in via env vars because they are GBs and
cannot be committed; when VRC_PKGS lists them, the same assertions run on real data.
"""
import io
import os
import tarfile

import pytest

from tools.dryrun import dry_run
from tools.unitypackage import UnityPackage

PREFAB = """%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!1 &100
GameObject:
  m_Name: Avatar
  m_IsActive: 1
--- !u!4 &200
Transform:
  m_GameObject: {fileID: 100}
  m_Children: []
  m_Father: {fileID: 0}
--- !u!1 &101
GameObject:
  m_Name: Main
  m_IsActive: 1
--- !u!4 &201
Transform:
  m_GameObject: {fileID: 101}
  m_Children: []
  m_Father: {fileID: 200}
--- !u!137 &300
SkinnedMeshRenderer:
  m_GameObject: {fileID: 101}
  m_Materials: [{fileID: 2100000, guid: 11111111111111111111111111111111, type: 2}]
"""

MAT = """Material:
  m_Shader: {fileID: 4800000, guid: 22222222222222222222222222222222, type: 3}
  m_Name: Main
"""

SHADER = 'Shader "Poiyomi Toon" { Properties { _Mode ("Mode", Float) = 0 } }'

ANIM_TOP = """AnimationClip:
  m_Name: top dress
  m_FloatCurves:
  - serializedVersion: 2
    curve:
      m_Curve:
      - serializedVersion: 3
        time: 0
        value: 1
      m_PreInfinity: 2
    attribute: m_IsActive
    path: Main
    classID: 1
"""
ANIM_TOP2 = ANIM_TOP.replace("top dress", "top corset")

CONTROLLER = """AnimatorController:
  m_Motion: {fileID: 0, guid: 33333333333333333333333333333333, type: 3}
  m_Motion2: {fileID: 0, guid: 44444444444444444444444444444444, type: 3}
"""


def build_pkg(tmp_path) -> str:
    assets = {
        "11111111111111111111111111111111": ("Assets/Main.mat", MAT),
        "22222222222222222222222222222222": ("Assets/Poiyomi Toon.shader", SHADER),
        "33333333333333333333333333333333": ("Assets/top dress.anim", ANIM_TOP),
        "44444444444444444444444444444444": ("Assets/top corset.anim", ANIM_TOP2),
        "55555555555555555555555555555555": ("Assets/Avatar.prefab", PREFAB),
        "66666666666666666666666666666666": ("Assets/FX.controller", CONTROLLER),
    }
    path = tmp_path / "synthetic.unitypackage"
    with tarfile.open(path, "w:gz") as tf:
        for guid, (name, body) in assets.items():
            data = body.encode("utf-8")
            info = tarfile.TarInfo(guid + "/asset")
            info.size = len(data)
            tf.addfile(info, io.BytesIO(data))
            pn = tarfile.TarInfo(guid + "/pathname")
            pn.size = len(name.encode())
            tf.addfile(pn, io.BytesIO(name.encode()))
            meta = b"fileFormatVersion: 2\n"
            mn = tarfile.TarInfo(guid + "/asset.meta")
            mn.size = len(meta)
            tf.addfile(mn, io.BytesIO(meta))
    return str(path)


def test_synthetic_pkg_e2e(tmp_path):
    pkg_path = build_pkg(tmp_path)
    pkg = UnityPackage(pkg_path)
    assert len(pkg.paths()) == 6
    report = dry_run(pkg_path)
    assert report["counts"]["materials"] == 1
    assert report["material_families"] == {"Poiyomi": 1}
    assert report["fx"]["referenced_anims"] == 2
    assert report["fx"]["clothing_clips"] == 2
    assert len(report["fx"]["groups"]) == 1
    assert report["fx"]["groups"][0]["category"] == "Top"


@pytest.mark.parametrize("env_key,min_families", [("VRC_ROSARY", {"Poiyomi", "Other"}), ("VRC_LESI", {"Poiyomi"})])
def test_real_package(env_key, min_families):
    path = os.environ.get(env_key)
    if not path or not os.path.exists(path):
        pytest.skip(f"{env_key} not set to a real package")
    report = dry_run(path)
    fams = set(report["material_families"])
    assert min_families <= fams, fams
    assert report["fx"]["referenced_anims"] > 0
    assert report["fx"]["clothing_clips"] > 0
    assert len(report["fx"]["groups"]) > 0
