"""Offline agent harness for VrcToVrm: inspect .unitypackage files, detect clothing
variants, and dry-run the converter without Unity.

See AGENTS.md and docs/CLOTHING.md. These tools mirror the C# editor logic
(Editor/PoiyomiToMToon.cs, Editor/ClothingVariants.cs) so changes can be tested
in CI without a Unity license.
"""
__all__ = ["unitypackage", "unityyaml", "prefab", "anims", "materials", "clothing", "dryrun"]
