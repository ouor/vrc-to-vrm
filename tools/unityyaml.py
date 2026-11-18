"""Minimal Unity YAML (prefab / animation / material / controller) parsing.

Unity YAML is a sequence of documents `--- !u!<classID> &<fileID>( stripped)?` followed
by a type name line, then indented body text. We never parse the full tree: each
helper only extracts what the converter cares about, with regexes over the body text.
"""
from __future__ import annotations

import re

DOC_RE = re.compile(r"--- !u!(\d+) &(\d+)( stripped)?\n([^\n]+):\n")


class Doc:
    def __init__(self, class_id: int, file_id: int, type_name: str, body: str, header: str):
        self.class_id = class_id
        self.file_id = file_id
        self.type_name = type_name
        self.body = body
        self.header = header

    def field(self, name: str) -> str | None:
        m = re.search(rf"^  {re.escape(name)}: (.*)$", self.body, re.M)
        return m.group(1).strip() if m else None


def split_docs(text: str) -> list[Doc]:
    matches = list(DOC_RE.finditer(text))
    docs = []
    for i, m in enumerate(matches):
        start = m.end()
        end = matches[i + 1].start() if i + 1 < len(matches) else len(text)
        docs.append(Doc(int(m.group(1)), int(m.group(2)), m.group(4), text[start:end], text[m.start():m.end()]))
    return docs


def ref_guids(text: str) -> set[str]:
    return set(re.findall(r"guid: ([0-9a-f]{32})", text))


def guids_in_slice(text: str, start: int, end: int) -> set[str]:
    return set(re.findall(r"guid: ([0-9a-f]{32})", text[start:end]))
