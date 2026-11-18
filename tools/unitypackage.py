"""Read Unity .unitypackage files (gzip tar of <guid>/asset, <guid>/asset.meta,
<guid>/pathname, <guid>/preview.png) without extracting everything to disk.

A .unitypackage can be several hundred MB; callers should index pathnames
first (small) and extract only the assets they need.
"""
from __future__ import annotations

import tarfile
from pathlib import Path


class UnityPackage:
    def __init__(self, path: str | Path):
        self.path = Path(path)
        self._guid_to_path: dict[str, str] | None = None
        self._path_to_guid: dict[str, str] | None = None

    def _index(self):
        if self._guid_to_path is not None:
            return
        guid_to_path: dict[str, str] = {}
        with tarfile.open(self.path, "r:gz") as tf:
            for member in tf:
                if member.name.endswith("/pathname"):
                    f = tf.extractfile(member)
                    if f is not None:
                        guid = member.name.split("/")[0]
                        guid_to_path[guid] = f.read().decode("utf-8", errors="replace").strip()
        self._guid_to_path = guid_to_path
        self._path_to_guid = {v: k for k, v in guid_to_path.items()}

    def guid_to_path(self) -> dict[str, str]:
        self._index()
        assert self._guid_to_path is not None
        return dict(self._guid_to_path)

    def path_to_guid(self) -> dict[str, str]:
        self._index()
        assert self._path_to_guid is not None
        return dict(self._path_to_guid)

    def paths(self) -> list[str]:
        return sorted(self.guid_to_path().values())

    def find(self, suffix: str) -> list[str]:
        return sorted(p for p in self.paths() if p.endswith(suffix))

    def find_containing(self, fragment: str) -> list[str]:
        return sorted(p for p in self.paths() if fragment in p)

    def read_asset(self, asset_path: str) -> bytes:
        """Return the raw `asset` bytes for a project path. Raises KeyError if unknown."""
        guid = self.path_to_guid().get(asset_path)
        if guid is None:
            raise KeyError(asset_path)
        with tarfile.open(self.path, "r:gz") as tf:
            member = tf.getmember(guid + "/asset")
            f = tf.extractfile(member)
            return f.read() if f is not None else b""

    def read_text(self, asset_path: str) -> str:
        return self.read_asset(asset_path).decode("utf-8", errors="replace")

    def read_many(self, asset_paths) -> dict:
        """Read many assets in ONE archive pass (a per-asset open rescans the whole .gz)."""
        paths = list(asset_paths)
        p2g = self.path_to_guid()
        want = {}
        for path in paths:
            guid = p2g.get(path)
            if guid is not None:
                want[guid + "/asset"] = path
        out = {}
        if not want:
            return out
        with tarfile.open(self.path, "r:gz") as tf:
            for member in tf:
                if member.name in want:
                    f = tf.extractfile(member)
                    out[want[member.name]] = f.read() if f is not None else b""
                    if len(out) == len(want):
                        break
        return out

    def read_text_many(self, asset_paths) -> dict:
        raw = self.read_many(asset_paths)
        return {k: v.decode("utf-8", errors="replace") for k, v in raw.items()}

    def size_of(self, asset_path: str) -> int:
        guid = self.path_to_guid().get(asset_path)
        if guid is None:
            raise KeyError(asset_path)
        with tarfile.open(self.path, "r:gz") as tf:
            return tf.getmember(guid + "/asset").size
