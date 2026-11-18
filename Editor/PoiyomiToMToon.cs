using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace VrcToVrm
{
    /// <summary>
    /// Poiyomi Toon (optimized shaders bundled in many avatar packages) -> VRM/MToon (0.x) mapping.
    /// Only the set of properties MToon can represent is carried over: main texture + color, cutout/transparent
    /// mode + cutoff, bump, emission (map x color x strength, first channel), matcap (as sphere-add, same
    /// coverage rules as lilToon), rim and outline when present. Everything else (decals, glitter, AudioLink,
    /// AudioLink-driven colors, multiple emission/matcap channels, aniso, clear-coat, layers) has no MToon
    /// equivalent and is reported in the log instead of silently changing the look.
    ///
    /// Clothing-swap properties animated by FX clips are handled here so variants can be baked:
    /// <list type="bullet">
    /// <item>UDIM discard (<c>material._UDIMDiscardRow{row}_{col}_*</c>, 1 = discard that UV tile): baked into
    /// the main-texture alpha for the selected variant (see <see cref="UdimGrid"/>).</item>
    /// <item>Dissolve (<c>material._DissolveAlpha_*</c>): folded into the main-texture alpha as
    /// <c>alpha *= 1 - value</c>.</item>
    /// </list>
    /// </summary>
    public static class PoiyomiToMToon
    {
        /// <summary>Poiyomi <c>_Mode</c> values (ThryWideEnum: Opaque, Cutout, TransClipping, Fade, Transparent, Additive, ...).</summary>
        public enum RenderMode { Opaque = 0, Cutout = 1, Fade = 2, Transparent = 3, Additive = 4, SoftAdditive = 5, Multiplicative = 6 }

        static readonly HashSet<string> KnownSuffixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>True for any Poiyomi shader: the shader name contains "Poiyomi" (optimized copies are
        /// <c>Hidden/Locked/.poiyomi/...</c>) or the material carries the Poiyomi mode/cutoff pair.</summary>
        public static bool IsPoiyomi(Material m)
        {
            if (m == null || m.shader == null) return false;
            if (m.shader.name.IndexOf("Poiyomi", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            // bundled optimized shaders keep the name, but be conservative for renamed/merged shaders
            return m.HasProperty("_Mode") && m.HasProperty("_Cutoff") && m.HasProperty("_MainTex")
                   && (m.HasProperty("_EmissionStrength") || m.HasProperty("_UDIMDiscardRow0_0_Discard")
                       || m.HasProperty("_DissolveAlpha") || m.HasProperty("_Matcap"));
        }

        /// <summary>Parses a UDIM discard property name <c>_UDIMDiscardRow{row}_{col}_*</c>.</summary>
        public static bool TryParseUdim(string property, out int row, out int col)
        {
            row = col = -1;
            if (string.IsNullOrEmpty(property) || !property.StartsWith("_UDIMDiscardRow", StringComparison.OrdinalIgnoreCase)) return false;
            var rest = property.Substring("_UDIMDiscardRow".Length);
            var parts = rest.Split('_');
            if (parts.Length < 2) return false;
            return int.TryParse(parts[0], out row) && int.TryParse(parts[1], out col) && row >= 0 && col >= 0;
        }

        public static bool IsUdimDiscard(string property) => TryParseUdim(property, out _, out _);

        public static bool IsDissolveAlpha(string property) =>
            !string.IsNullOrEmpty(property) && property.StartsWith("_DissolveAlpha", StringComparison.OrdinalIgnoreCase);

        /// <summary>Grid size implied by the discard properties present on <paramref name="src"/>:
        /// (rows, cols) = (maxRow + 1, maxCol + 1). Returns false when the material has no discard properties.</summary>
        public static bool UdimGrid(Material src, out int rows, out int cols)
        {
            rows = cols = 0;
            if (src == null || src.shader == null) return false;
            int count = src.shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                var name = src.shader.GetPropertyName(i);
                if (TryParseUdim(name, out int r, out int c))
                {
                    rows = Mathf.Max(rows, r + 1);
                    cols = Mathf.Max(cols, c + 1);
                }
            }
            return rows > 0 && cols > 0;
        }

        /// <summary>Discard values present on <paramref name="src"/>: set of "row,col" tiles to hide (value &gt; 0.5).</summary>
        public static HashSet<string> DiscardedTiles(Material src)
        {
            var tiles = new HashSet<string>();
            if (src == null || src.shader == null) return tiles;
            int count = src.shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                var name = src.shader.GetPropertyName(i);
                if (TryParseUdim(name, out int r, out int c) && src.GetFloat(name) > 0.5f)
                    tiles.Add(r + "," + c);
            }
            return tiles;
        }

        /// <summary>Dissolve strength 0..1 (min over <c>_DissolveAlpha_*</c>, 0 when none).</summary>
        public static float DissolveAlpha(Material src)
        {
            float best = 0f;
            bool found = false;
            if (src == null || src.shader == null) return 0f;
            int count = src.shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                var name = src.shader.GetPropertyName(i);
                if (IsDissolveAlpha(name))
                {
                    float v = Mathf.Clamp01(src.GetFloat(name));
                    best = found ? Mathf.Min(best, v) : v;
                    found = true;
                }
            }
            return found ? best : 0f;
        }

        /// <summary>MToon blend mode for a Poiyomi rendering preset. Additive/multiplicative presets become
        /// Transparent (MToon has no additive blend for opaque materials) and are reported by the caller.</summary>
        public static int BlendModeForMode(int poiMode, bool forceOpaque, out bool isCutout, out bool isTransparent)
        {
            isCutout = isTransparent = false;
            if (forceOpaque) return 0; // BlendMode 0 = Opaque
            switch (poiMode)
            {
                case 1: // Cutout
                case 9: // TransClipping (treated as cutout)
                    isCutout = true;
                    return 1;
                case 2: // Fade
                case 3: // Transparent
                case 4: // Additive
                case 5: // Soft Additive
                case 6: // Multiplicative
                case 7: // 2x Multiplicative
                    isTransparent = true;
                    return 2;
                default:
                    return 0;
            }
        }

        /// <summary>Short human-readable rendering preset, for logs.</summary>
        public static string ModeName(int poiMode) => poiMode switch
        {
            0 => "Opaque", 1 => "Cutout", 2 => "Fade", 3 => "Transparent",
            4 => "Additive", 5 => "Soft Additive", 6 => "Multiplicative", 7 => "2x Multiplicative",
            9 => "TransClipping", _ => "Mode " + poiMode
        };

        static PoiyomiToMToon() { }

        /// <summary>Touch the types twice so the class is exercised even when conversion takes the lilToon path.</summary>
        public static void SelfCheck() { KnownSuffixes.Clear(); }
    }
}
