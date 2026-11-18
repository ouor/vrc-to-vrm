using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VrcToVrm
{
    /// <summary>Which part of the avatar a clothing clip changes. The value only groups variants for the UI;</summary>
    /// the actual visibility/material/blendshape curves carry the data.
    public enum ClothingCategory { Top, Bottom, Hair, Face, Body, Feet, Extra, Color, Other }

    /// <summary>One curve of a clothing clip, sampled at its last key (clothing clips are hold-pose toggles,
    /// exactly like the expression clips read by <c>VrcAvatarAnalysis</c>).</summary>
    public class ClothingCurve
    {
        public string RelativePath;
        public string PropertyName;   // m_IsActive, m_Enabled, material._X, blendShape.Y
        public float Value;
        /// <summary>"Active", "Enabled", "Material", "BlendShape" or "Other".</summary>
        public string Kind = "Other";
    }

    /// <summary>One FX clip that changes what the avatar wears (or how its clothing looks).</summary>
    public class ClothingClip
    {
        public string Name;
        public AnimationClip Clip;
        public ClothingCategory Category = ClothingCategory.Other;
        public List<string> Sources = new List<string>();
        public List<ClothingCurve> Curves = new List<ClothingCurve>();

        public bool HasActiveToggles => Curves.Any(c => c.Kind == "Active");
        public bool HasMaterialProps => Curves.Any(c => c.Kind == "Material");
        public bool HasBlendShapes => Curves.Any(c => c.Kind == "BlendShape");
        public string ShortLabel => Sources.Count > 0 ? $"{Name}  [{Sources[0]}]" : Name;
    }

    /// <summary>A set of mutually exclusive clothing choices (e.g. the clips that share the same toggled
    /// objects / material tiles). Exactly one of <see cref="Clips"/> (or none = the baked default) should
    /// be applied for a conversion.</summary>
    public class ClothingGroup
    {
        public string Key;                    // stable id, e.g. "Top"
        public ClothingCategory Category;
        public List<ClothingClip> Clips = new List<ClothingClip>();
        public string DisplayName => Key;
    }

    /// <summary>
    /// Detects clothing variations driven by the FX layer and can apply one variant to a converter copy.
    /// Covers the mechanisms seen in the wild:
    /// <list type="bullet">
    /// <item>GameObject toggles (<c>m_IsActive</c>) and Renderer/Behaviour toggles (<c>m_Enabled</c>),
    /// e.g. per-outfit objects (Hoodie / Top / Shorts ...).</item>
    /// <item>Material property swaps (<c>material._*</c>): Poiyomi UDIM discard tiles and dissolve
    /// (<c>material._UDIMDiscardRow*</c>, <c>material._DissolveAlpha_*</c>), decal blend alpha for
    /// black/white and color swaps, cutoff and hue/brightness props.</item>
    /// <item>Blendshape morphs on non-face meshes (bangs, pigtails, chest, ...).</item>
    /// </list>
    /// Grouping is by shared curve targets (union-find over "path + property"), so a top mesh toggled from
    /// several clips becomes one group with one option per clip, whatever the author's naming scheme is.
    /// </summary>
    public static class ClothingVariants
    {
        static readonly Dictionary<ClothingCategory, string[]> CategoryWords = new Dictionary<ClothingCategory, string[]>
        {
            { ClothingCategory.Top, new[] { "top", "dress", "bodysuit", "corset", "tube", "hoodie", "onesie", "shirt", "bikini top", "bustier", "vest", "jacket", "coat", "sweater", "blouse" } },
            { ClothingCategory.Bottom, new[] { "bottom", "pants", "shorts", "skirt", "bikini bottom", "bottoms", "sweats", "sweatpants", "leggings", "thong", "bikini" } },
            { ClothingCategory.Hair, new[] { "hair", "bangs", "pigtail", "ponytail", "back long", "back short", "fronts", "bang " } },
            { ClothingCategory.Feet, new[] { "feet", "shoe", "sneaker", "heels", "slipon", "socks", "stocking", "legfishnet", "fishnet", "armfishnet", "boots", "warmers" } },
            { ClothingCategory.Face, new[] { "blindfold", "makeup", "lipstick", "lashes", "sclera", "fangs", "piercing", "tattoo", "blush", "freckles" } },
            { ClothingCategory.Body, new[] { "body ", "skin", "tan", "stripe", "tights", "abbs", "chest ", "splatter" } },
            { ClothingCategory.Color, new[] { "color", "colour", "hue", "saturation", "black", "white", "swap", "rgb", "greyscale", "grayscale", "outfit hue", "outfit sat", "hair hue", "hair sat", "dress color" } },
            { ClothingCategory.Extra, new[] { "choker", "belt", "bracelet", "necklace", "ring", "garter", "wings", "tail", "earring", "claws", "bag", "fan", "umbrella", "accs", "lore", "extras", "sleeves", "cuffs", "batbra" } },
        };

        /// <summary>Name-based category guess; returns Other when nothing matches.</summary>
        public static ClothingCategory GuessCategory(string name, IEnumerable<ClothingCurve> curves)
        {
            var haystack = (name ?? "") + " " + string.Join(" ", curves.Select(c => c.RelativePath + " " + c.PropertyName));
            // color swaps often share the outfit name, so check Color after Top/Bottom to avoid stealing them,
            // but a pure color clip ("color swap hue") has no outfit object toggles.
            foreach (var cat in new[] { ClothingCategory.Top, ClothingCategory.Bottom, ClothingCategory.Hair, ClothingCategory.Feet, ClothingCategory.Face, ClothingCategory.Body, ClothingCategory.Extra, ClothingCategory.Color })
                if (CategoryWords.TryGetValue(cat, out var words) && words.Any(w => haystack.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0))
                    return cat;
            return ClothingCategory.Other;
        }

        /// <summary>True for curves that change clothing/appearance rather than face expressions.</summary>
        public static bool IsClothingCurve(EditorCurveBinding b) =>
            b.propertyName == "m_IsActive" || b.propertyName == "m_Enabled"
            || b.propertyName.StartsWith("material.") || b.propertyName.StartsWith("blendShape.");

        /// <summary>Reads every curve of <paramref name="clip"/> as hold-pose values (last key), the same way
        /// expression clips are read. Material swaps beyond the last key (animated ramps) keep their end value.
        /// </summary>
        public static List<ClothingCurve> ReadCurves(AnimationClip clip)
        {
            var result = new List<ClothingCurve>();
            if (clip == null) return result;
            foreach (var b in AnimationUtility.GetCurveBindings(clip))
            {
                if (!IsClothingCurve(b)) continue;
                var curve = AnimationUtility.GetEditorCurve(clip, b);
                if (curve == null || curve.length == 0) continue;
                result.Add(new ClothingCurve
                {
                    RelativePath = b.path,
                    PropertyName = b.propertyName,
                    Value = curve.keys[curve.length - 1].value,
                    Kind = b.propertyName == "m_IsActive" ? "Active"
                        : b.propertyName == "m_Enabled" ? "Enabled"
                        : b.propertyName.StartsWith("material.") ? "Material"
                        : b.propertyName.StartsWith("blendShape.") ? "BlendShape" : "Other",
                });
            }
            return result;
        }

        /// <summary>Groups <paramref name="clips"/> by shared targets. Clips that touch the same path+property
        /// (directly or transitively) end up in one group; isolated on/off toggles get their own group.</summary>
        public static List<ClothingGroup> GroupClips(IEnumerable<ClothingClip> clips)
        {
            var list = clips.Where(c => c != null && c.Curves.Count > 0).ToList();
            var parent = list.Select((c, i) => i).ToArray();
            int Find(int x) => parent[x] == x ? x : parent[x] = Find(parent[x]);
            void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[b] = a; }

            var byTarget = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < list.Count; i++)
            {
                foreach (var target in list[i].Curves.Select(c => c.RelativePath + "\0" + c.PropertyName).Distinct())
                {
                    if (byTarget.TryGetValue(target, out int j)) Union(i, j);
                    else byTarget[target] = i;
                }
            }
            var groups = new List<ClothingGroup>();
            var byRoot = new Dictionary<int, ClothingGroup>();
            for (int i = 0; i < list.Count; i++)
            {
                int root = Find(i);
                if (!byRoot.TryGetValue(root, out var g))
                {
                    g = new ClothingGroup { Category = list[i].Category };
                    byRoot[root] = g;
                    groups.Add(g);
                }
                g.Clips.Add(list[i]);
            }
            foreach (var g in groups)
            {
                g.Clips = g.Clips.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
                // key = most common category in the group + size, so logs/UI stay stable across runs
                g.Key = $"{g.Category} [{g.Clips.Count}]";
                if (g.Clips.Count == 1) g.Key = $"{g.Category}: {g.Clips[0].Name}";
            }
            return groups.OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        }

        /// <summary>Applies one clothing clip to <paramref name="copyRoot"/> (a converter working copy).
        /// Material properties are set on cloned materials, so the source prefab's assets are never modified.
        /// Returns the number of curves that could not be resolved (missing object / shape).</summary>
        public static int Apply(GameObject copyRoot, ClothingClip clip, List<string> log)
        {
            if (copyRoot == null || clip == null) return 0;
            int missing = 0;
            var cloned = new Dictionary<SkinnedMeshRenderer, Material[]>();
            foreach (var curve in clip.Curves)
            {
                var target = copyRoot.transform.Find(curve.RelativePath);
                if (target == null)
                {
                    missing++;
                    continue;
                }
                switch (curve.Kind)
                {
                    case "Active":
                        target.gameObject.SetActive(curve.Value > 0.5f);
                        break;
                    case "Enabled":
                    {
                        var renderer = target.GetComponent<Renderer>();
                        if (renderer != null) renderer.enabled = curve.Value > 0.5f;
                        else
                        {
                            var behaviour = target.GetComponent<Behaviour>();
                            if (behaviour != null) behaviour.enabled = curve.Value > 0.5f;
                            else missing++;
                        }
                        break;
                    }
                    case "Material":
                    {
                        var smr = target.GetComponent<SkinnedMeshRenderer>();
                        if (smr == null) { missing++; break; }
                        var prop = curve.PropertyName.Substring("material.".Length);
                        if (!cloned.TryGetValue(smr, out var mats))
                        {
                            mats = smr.sharedMaterials.Select(m => m != null ? new Material(m) : null).ToArray();
                            cloned[smr] = mats;
                            smr.sharedMaterials = mats;
                        }
                        bool set = false;
                        foreach (var m in mats)
                            if (m != null && m.HasProperty(prop)) { m.SetFloat(prop, curve.Value); set = true; }
                        if (!set) missing++;
                        break;
                    }
                    case "BlendShape":
                    {
                        var smr = target.GetComponent<SkinnedMeshRenderer>();
                        var shape = curve.PropertyName.Substring("blendShape.".Length);
                        if (smr == null || smr.sharedMesh == null || smr.sharedMesh.GetBlendShapeIndex(shape) < 0) { missing++; break; }
                        smr.SetBlendShapeWeight(smr.sharedMesh.GetBlendShapeIndex(shape), Mathf.Clamp(curve.Value, 0f, 100f));
                        break;
                    }
                }
            }
            if (log != null && missing > 0) log.Add(L.Tr("clothing.001", clip.Name, missing));
            return missing;
        }
    }
}
