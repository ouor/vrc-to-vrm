"""AnimationClip curve parsing (hold-pose last-key values)."""
from tools.anims import parse_anim

CLIP = """AnimationClip:
  m_Name: top dress
  m_FloatCurves:
  - serializedVersion: 2
    curve:
      m_Curve:
      - serializedVersion: 3
        time: 0
        value: 0
      m_PreInfinity: 2
    attribute: material._UDIMDiscardRow0_1_Dress
    path: Main
    classID: 137
  - serializedVersion: 2
    curve:
      m_Curve:
      - serializedVersion: 3
        time: 0
        value: 1
      m_PreInfinity: 2
    attribute: 'blendShape.Bangs Swap'
    path: Hair
    classID: 137
"""


def test_parse_curves():
    curves = parse_anim(CLIP)
    assert len(curves) == 2
    assert curves[0].attribute == "material._UDIMDiscardRow0_1_Dress"
    assert curves[0].path == "Main"
    assert curves[0].kind == "Material"
    assert curves[0].value == 0
    assert curves[1].kind == "BlendShape"
    assert curves[1].value == 1
