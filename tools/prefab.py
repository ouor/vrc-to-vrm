"""Parse prefab YAML: GameObjects, SkinnedMeshRenderers, components.

Hierarchy needs Transform father/children links; inactive clothing is `m_IsActive: 0`.
Stripped (prefab-instance) transforms have no GameObject name in this file: their
names live in the source asset, which we report as `nested` rather than guessing.
"""
from __future__ import annotations

from .unityyaml import Doc, split_docs


class GameObjectInfo:
    def __init__(self, name: str, active: bool, file_id: int):
        self.name = name
        self.active = active
        self.file_id = file_id


class RendererInfo:
    def __init__(self, path: str, game_object: str, active: bool, material_guids: list[str]):
        self.path = path
        self.game_object = game_object
        self.active = active
        self.material_guids = material_guids


class PrefabInfo:
    def __init__(self, path: str, text: str, guid_to_path: dict[str, str]):
        self.path = path
        self.docs = split_docs(text)
        self.guid_to_path = guid_to_path
        self.game_objects: list[GameObjectInfo] = []
        self.renderers: list[RendererInfo] = []
        self._parse()

    def _parse(self):
        # fileID -> (name, active) for GameObjects; fileID -> parent chain via Transforms
        go_by_id: dict[int, GameObjectInfo] = {}
        transform_go: dict[int, int] = {}
        transform_father: dict[int, int] = {}
        for doc in self.docs:
            if doc.type_name == "GameObject":
                name = doc.field("m_Name") or ""
                active = doc.field("m_IsActive") == "1"
                go = GameObjectInfo(name, active, doc.file_id)
                go_by_id[doc.file_id] = go
                self.game_objects.append(go)
            elif doc.type_name == "Transform":
                go_ref = doc.field("m_GameObject") or "{fileID: 0}"
                father_ref = doc.field("m_Father") or "{fileID: 0}"
                import re
                gm = re.search(r"fileID: (\d+)", go_ref)
                fm = re.search(r"fileID: (\d+)", father_ref)
                if gm:
                    transform_go[doc.file_id] = int(gm.group(1))
                if fm:
                    transform_father[doc.file_id] = int(fm.group(1))
        # path per GameObject: walk father chain
        go_transform: dict[int, int] = {}
        for tid, gid in transform_go.items():
            go_transform[gid] = tid

        def path_of(gid: int) -> str:
            names = []
            seen = set()
            tid = go_transform.get(gid)
            while tid is not None and tid != 0 and tid not in seen:
                seen.add(tid)
                gid2 = transform_go.get(tid)
                if gid2 is None:
                    break
                go = go_by_id.get(gid2)
                if go is None:
                    break
                names.append(go.name)
                tid = transform_father.get(tid)
            return "/".join(reversed(names)) if names else ""

        import re
        for doc in self.docs:
            if doc.type_name != "SkinnedMeshRenderer":
                continue
            go_ref = doc.field("m_GameObject") or "{fileID: 0}"
            gm = re.search(r"fileID: (\d+)", go_ref)
            gid = int(gm.group(1)) if gm else 0
            go = go_by_id.get(gid)
            guids = re.findall(r"guid: ([0-9a-f]{32})", doc.body)
            self.renderers.append(RendererInfo(
                path=path_of(gid) if gid in go_transform else "",
                game_object=go.name if go else "",
                active=go.active if go else True,
                material_guids=guids,
            ))

    @property
    def skinned_mesh_count(self) -> int:
        return len(self.renderers)

    @property
    def inactive_renderers(self) -> list[RendererInfo]:
        return [r for r in self.renderers if not r.active]

    def has_avatar_descriptor_hint(self) -> bool:
        # VRCAvatarDescriptor MonoBehaviours reference the SDK script by external GUID;
        # we can only report whether any MonoBehaviour exists (names are unresolvable offline).
        return any(d.type_name == "MonoBehaviour" for d in self.docs)

    def contains(self, fragment: str) -> bool:
        return any(fragment in d.body for d in self.docs)
