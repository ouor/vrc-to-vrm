using System.Collections.Generic;
using System.IO;
using System.Linq;
using lilToon;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace VrcToVrm
{
    /// <summary>
    /// lilToon -> VRM/MToon (0.x) conversion.
    /// Port of lilToonInspector.CreateMToonMaterial (lilToon 2.3.4) that reads the material directly
    /// instead of inspector state and never shows dialogs.
    /// </summary>
    public class LilToonToMToon
    {
        readonly string _textureFolder;
        readonly bool _bakeTextures;
        readonly bool _keepMatCap;
        readonly bool _smartFallback;
        readonly Dictionary<Material, Material> _cache = new Dictionary<Material, Material>();
        public readonly List<string> Log = new List<string>();

        public LilToonToMToon(string textureFolder, bool bakeTextures, bool keepMatCap = true, bool smartFallback = true)
        {
            _textureFolder = textureFolder;
            _bakeTextures = bakeTextures;
            _keepMatCap = keepMatCap;
            _smartFallback = smartFallback;
        }

        /// <summary>Shader family used by the converter dispatch (for stats/logging). Never null.</summary>
        public static string MaterialFamily(Material m)
        {
            if (m == null) return "null";
            if (IsMToon(m)) return "MToon";
            if (PoiyomiToMToon.IsPoiyomi(m)) return "Poiyomi";
            if (IsLilToon(m)) return "lilToon";
            if (IsFakeShadow(m)) return "FakeShadow";
            return "Other";
        }

        public static bool IsLilToon(Material m) =>
            m != null && m.shader != null && (m.shader.name.Contains("lilToon") || m.HasProperty("_lilToonVersion"));

        public static bool IsFakeShadow(Material m) => m != null && m.shader != null && m.shader.name.Contains("FakeShadow");

        public static bool IsMToon(Material m) => m != null && m.shader != null && m.shader.name == MToon.Utils.ShaderName;

        /// <summary>Returns an in-memory MToon material (cached per source).</summary>
        public Material Convert(Material src)
        {
            if (src == null) return null;
            if (_cache.TryGetValue(src, out var done)) return done;
            Material dst;
            if (IsMToon(src)) dst = src;
            else if (IsFakeShadow(src)) dst = Invisible(src);
            else if (PoiyomiToMToon.IsPoiyomi(src)) dst = FromPoiyomi(src);
            else if (IsLilToon(src)) dst = FromLilToon(src);
            else dst = FromUnknown(src);
            _cache[src] = dst;
            return dst;
        }

        Material NewMToon(Material src)
        {
            var shader = Shader.Find(MToon.Utils.ShaderName);
            if (shader == null) throw new System.Exception(L.Tr("mat.001"));
            return new Material(shader) { name = src.name + "_mtoon" };
        }

        float F(Material m, string p, float d = 0) => m.HasProperty(p) ? m.GetFloat(p) : d;
        Color C(Material m, string p, Color d) => m.HasProperty(p) ? m.GetColor(p) : d;
        Vector4 V(Material m, string p, Vector4 d) => m.HasProperty(p) ? m.GetVector(p) : d;
        Texture T(Material m, string p) => m.HasProperty(p) ? m.GetTexture(p) : null;

        /// <summary>FakeShadow only darkens what is behind it; in MToon it would be a solid shape, so hide it.</summary>
        Material Invisible(Material src)
        {
            var dst = NewMToon(src);
            dst.SetColor("_Color", new Color(1, 1, 1, 0));
            dst.SetFloat("_BlendMode", 2);
            MToon.Utils.ValidateProperties(dst, true);
            Log.Add(L.Warn("mat.002", src.name));
            return dst;
        }

        const float UnknownShade = 0.85f;

        Material FromUnknown(Material src)
        {
            var dst = NewMToon(src);
            var main = T(src, "_MainTex");
            var color = C(src, "_Color", Color.white);
            dst.SetTexture("_MainTex", main);
            dst.SetColor("_Color", color);
            if (_smartFallback && (src.HasProperty("_Cutoff") || src.HasProperty("_Mode") || src.HasProperty("_BumpMap") || src.HasProperty("_EmissionColor")))
            {
                // Unity-Standard-style material: preserve alpha, bump and emission instead of a flat tint
                bool cutout = src.HasProperty("_Mode") ? (int)src.GetFloat("_Mode") == 1 : false;
                if (src.HasProperty("_Cutoff") && F(src, "_Cutoff") > 0 && F(src, "_Cutoff") < 1) cutout = cutout || src.HasProperty("transparentMode") == false;
                if (src.HasProperty("_BumpMap") && T(src, "_BumpMap") != null)
                {
                    dst.SetFloat("_BumpScale", F(src, "_BumpScale", 1));
                    dst.SetTexture("_BumpMap", T(src, "_BumpMap"));
                    dst.EnableKeyword("_NORMALMAP");
                }
                if (src.HasProperty("_EmissionColor") && C(src, "_EmissionColor", Color.black).maxColorComponent > 0.004f)
                {
                    var em = C(src, "_EmissionColor", Color.white);
                    if (src.HasProperty("_EmissionIntensity")) em *= F(src, "_EmissionIntensity", 1);
                    dst.SetColor("_EmissionColor", em);
                    dst.SetTexture("_EmissionMap", T(src, "_EmissionMap"));
                }
                if (cutout || color.a < 0.99f || (main != null && main.name.IndexOf("alpha", System.StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    dst.SetFloat("_Cutoff", F(src, "_Cutoff", 0.5f));
                    dst.SetFloat("_BlendMode", 1);
                }
                Log.Add(L.Tr("mat.013", src.name, src.shader.name));
            }
            // MToon's default shade is a flat pink with no texture; keep the texture and only darken it a little
            // (VRChat's mobile toon shaders shade softly)
            dst.SetTexture("_ShadeTexture", main);
            dst.SetColor("_ShadeColor", new Color(color.r * UnknownShade, color.g * UnknownShade, color.b * UnknownShade, 1));
            dst.SetFloat("_ShadeShift", 0);
            dst.SetFloat("_ShadeToony", 0.9f);
            dst.SetFloat("_LightColorAttenuation", 0);
            dst.SetFloat("_IndirectLightIntensity", 0);
            MToon.Utils.ValidateProperties(dst, true);
            Log.Add(L.Tr("mat.003", src.name, src.shader.name));
            return dst;
        }

        Material FromPoiyomi(Material src)
        {
            var dst = NewMToon(src);
            var main = T(src, "_MainTex");
            var color = C(src, "_Color", Color.white);
            dst.SetTexture("_MainTex", main);
            dst.SetColor("_Color", color);
            dst.SetTextureScale("_MainTex", T(src, "_MainTex") == null ? Vector2.one : src.GetTextureScale("_MainTex"));
            dst.SetTextureOffset("_MainTex", src.GetTextureOffset("_MainTex"));
            int mode = (int)F(src, "_Mode");
            bool forceOpaque = F(src, "_AlphaForceOpaque") > 0.5f;
            int blend = PoiyomiToMToon.BlendModeForMode(mode, forceOpaque, out bool isCutout, out bool isTransparent);
            // bake UDIM discard / dissolve into alpha so the exported variant shows only its own outfit tiles
            {
                if ((PoiyomiToMToon.UdimGrid(src, out int udimRows, out int udimCols) || PoiyomiToMToon.DissolveAlpha(src) > 0f) && main != null)
                {
                    var tiles = PoiyomiToMToon.DiscardedTiles(src);
                    float dissolve = PoiyomiToMToon.DissolveAlpha(src);
                    if (tiles.Count > 0 || dissolve > 0f)
                    {
                        try
                        {
                            var mainRaw = LoadRaw(main);
                            int w = mainRaw.width, h = mainRaw.height;
                            var data = mainRaw.GetPixels32();
                            if (tiles.Count > 0)
                            {
                                foreach (var t in tiles)
                                {
                                    var rc = t.Split(',');
                                    int row = int.Parse(rc[0]), col = int.Parse(rc[1]);
                                    int x0 = w * col / udimCols, x1 = w * (col + 1) / udimCols;
                                    int yBottom = h * row / udimRows, yTop = h * (row + 1) / udimRows;
                                    int y0 = Mathf.Max(0, h - yTop), y1 = Mathf.Min(h, h - yBottom);
                                    for (int y = y0; y < y1; y++)
                                        for (int x = x0; x < x1; x++)
                                        {
                                            int idx = y * w + x;
                                            if (idx >= 0 && idx < data.Length) data[idx].a = 0;
                                        }
                                }
                            }
                            else
                            {
                                // dissolve only: uniform alpha scale
                                float keep = 1f - Mathf.Clamp01(dissolve);
                                for (int i = 0; i < data.Length; i++) data[i].a = (byte)Mathf.Clamp01(data[i].a / 255f * keep * 255f);
                            }
                            var outTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                            outTex.SetPixels32(data);
                            outTex.Apply();
                            dst.SetTexture("_MainTex", SaveTexture(outTex, src.name + "_udim", main) ?? main);
                            if (!isCutout && !isTransparent) { isCutout = true; blend = 1; }
                            Log.Add(L.Tr("mat.014", src.name, tiles.Count, udimRows, udimCols, dissolve));
                        }
                        finally { FreeTemps(); }
                    }
                }
            }
            dst.SetFloat("_BlendMode", blend);
            dst.SetFloat("_Cutoff", F(src, "_Cutoff", 0.5f));
            dst.SetFloat("_Cutoff", isCutout ? Mathf.Clamp(F(src, "_Cutoff", 0.5f), 0.05f, 0.95f) : dst.GetFloat("_Cutoff"));
            // shade: MToon has one shade; map Poiyomi's first shade color (or a softened copy of the base)
            {
                var shade = C(src, "_1st_ShadeColor", Color.white);
                if (shade == Color.white && color != Color.white) shade = new Color(color.r, color.g, color.b, 1);
                var tex = T(src, "_1st_ShadeMap");
                dst.SetTexture("_ShadeTexture", tex != null ? tex : main);
                dst.SetColor("_ShadeColor", new Color(shade.r * 0.85f, shade.g * 0.85f, shade.b * 0.85f, 1));
                dst.SetFloat("_ShadeShift", 0);
                dst.SetFloat("_ShadeToony", 0.9f);
            }
            // bump
            if (T(src, "_BumpMap") != null)
            {
                dst.SetFloat("_BumpScale", F(src, "_BumpScale", 1));
                dst.SetTexture("_BumpMap", T(src, "_BumpMap"));
                dst.EnableKeyword("_NORMALMAP");
            }
            // emission: Poiyomi stacks up to 4 channels; MToon keeps the first (log the rest)
            {
                var emColor = C(src, "_EmissionColor", Color.black);
                float strength = F(src, "_EmissionStrength");
                if (emColor.maxColorComponent > 0.004f && strength > 0.001f)
                {
                    dst.SetColor("_EmissionColor", emColor * strength);
                    var emTex = T(src, "_EmissionMap");
                    var emMask = T(src, "_EmissionMask");
                    if (_bakeTextures && (emTex != null || emMask != null))
                    {
                        try
                        {
                            var refTex = emTex != null ? emTex : emMask;
                            var refRaw = LoadRaw(refTex);
                            var rgb = emTex != null ? Temp(Readable(LoadRaw(emTex), refRaw.width, refRaw.height)).GetPixels32() : null;
                            var maskpx = emMask != null ? Temp(Readable(LoadRaw(emMask), refRaw.width, refRaw.height)).GetPixels32() : null;
                            bool maskSRGB2 = IsSRGB(emMask);
                            var bakedEm = new Color32[refRaw.width * refRaw.height];
                            for (int i = 0; i < bakedEm.Length; i++)
                            {
                                float m = maskpx != null ? Channel(maskpx[i].r, maskSRGB2) : 1f;
                                Color c = rgb != null ? Color.Lerp(Color.black, (Color)rgb[i], 1) : Color.white;
                                c = new Color(c.r, c.g, c.b, 1);
                                bakedEm[i] = (Color32)new Color(c.r * m, c.g * m, c.b * m, 1);
                            }
                            var emOut = new Texture2D(refRaw.width, refRaw.height, TextureFormat.RGBA32, false);
                            emOut.SetPixels32(bakedEm);
                            emOut.Apply();
                            dst.SetTexture("_EmissionMap", SaveTexture(emOut, src.name + "_emission", refTex, maxSizeOf: main as Texture2D, alpha: false) ?? emTex);
                        }
                        finally { FreeTemps(); }
                    }
                    else if (emTex != null) dst.SetTexture("_EmissionMap", emTex);
                    dst.EnableKeyword("_EMISSION_MAP");
                }
                bool extra = F(src, "_EmissionStrength1") > 0.001f || F(src, "_EmissionStrength2") > 0.001f || F(src, "_EmissionStrength3") > 0.001f;
                if (extra) Log.Add(L.Tr("mat.015", src.name));
            }
            // matcap: first non-zero channel as sphere-add (same coverage as lilToon)
            {
                var matcap = T(src, "_Matcap");
                float mcI = F(src, "_MatcapIntensity");
                if (matcap == null) { for (int ch = 0; ch < 5; ch++) { matcap = T(src, "_Matcap" + ch); if (matcap != null) break; } }
                if (matcap != null && mcI > 0.001f && _keepMatCap)
                {
                    var mcColor = C(src, "_MatcapColor", Color.white);
                    dst.SetColor("_SphereAdd", new Color(mcColor.r, mcColor.g, mcColor.b, mcI));
                    dst.SetTexture("_SphereAddTex", matcap);
                    dst.EnableKeyword("_SPHEREADD");
                }
            }
            // rim (first rim only; Poiyomi has two rims + fresnel/aniso that MToon cannot express)
            if (F(src, "_RimStrength") > 0.001f || F(src, "_RimBorder") > 0.001f)
            {
                dst.SetColor("_RimColor", C(src, "_RimColor", Color.white));
                dst.SetFloat("_RimFresnelPower", F(src, "_RimFresnelPower", 5));
                dst.SetFloat("_RimFresnelPowerInv", F(src, "_RimFresnelPowerInv", 5));
                dst.SetFloat("_RimLightingMix", F(src, "_RimLightingMix", 1));
                dst.EnableKeyword("_RIM");
            }
            // outline
            if (src.HasProperty("_OutlineColor") && src.HasProperty("_OutlineWidth") && F(src, "_OutlineWidth") > 0.001f)
            {
                dst.SetColor("_OutlineColor", C(src, "_OutlineColor", Color.white));
                dst.SetFloat("_OutlineWidth", F(src, "_OutlineWidth", 0.5f));
                dst.EnableKeyword("_OUTLINE");
            }
            // alpha mask baked into main alpha when the source is cutout/transparent (Poiyomi _AlphaMask/R + _AlphaTexture)
            if ((isCutout || isTransparent || color.a < 1f) && (T(src, "_AlphaMask") != null || T(src, "_AlphaTexture") != null))
            {
                var mainTex = dst.GetTexture("_MainTex") as Texture2D;
                if (mainTex != null)
                {
                    try
                    {
                        var maskA = T(src, "_AlphaMask") != null ? T(src, "_AlphaMask") : T(src, "_AlphaTexture");
                        var mainRaw2 = LoadRaw(mainTex);
                        var baked = mainRaw2.GetPixels32();
                        var rgba = Temp(Readable(LoadRaw(maskA), mainRaw2.width, mainRaw2.height)).GetPixels32();
                        for (int i = 0; i < baked.Length && i < rgba.Length; i++) baked[i].a = rgba[i].r;
                        var alphaOut = new Texture2D(mainRaw2.width, mainRaw2.height, TextureFormat.RGBA32, false);
                        alphaOut.SetPixels32(baked);
                        alphaOut.Apply();
                        dst.SetTexture("_MainTex", SaveTexture(alphaOut, src.name + "_alpha", mainTex) ?? mainTex);
                    }
                    finally { FreeTemps(); }
                }
            }
            if (mode >= 4 && mode <= 7) Log.Add(L.Tr("mat.016", src.name, PoiyomiToMToon.ModeName(mode)));
            MToon.Utils.ValidateProperties(dst, true);
            Log.Add(L.Tr("mat.003", src.name, src.shader.name));
            return dst;
        }

        Material FromLilToon(Material src)
        {
            var shaderName = src.shader.name;
            var dst = NewMToon(src);

            bool isMulti = shaderName.Contains("Multi");
            bool isOutl = isMulti ? F(src, "_UseOutline") != 0 :lilShaderUtils.IsOutlineShaderName(shaderName);
            bool isCutout = lilShaderUtils.IsCutoutShaderName(shaderName);
            bool isTransparent = lilShaderUtils.IsTransparentShaderName(shaderName) || lilShaderUtils.IsOverlayShaderName(shaderName)
                                 || lilShaderUtils.IsRefractionShaderName(shaderName) || lilShaderUtils.IsFurShaderName(shaderName);
            if (isMulti)
            {
                var tp = F(src, "_TransparentMode");
                isCutout = tp == 1 || tp == 5;                            // Cutout, FurCutout
                isTransparent = tp == 2 || tp == 3 || tp == 4 || tp == 6; // Transparent, Refraction, Fur, Gem
            }

            var mainColor = C(src, "_Color", Color.white);
            dst.SetColor("_Color", new Color(Mathf.Clamp01(mainColor.r), Mathf.Clamp01(mainColor.g), Mathf.Clamp01(mainColor.b), Mathf.Clamp01(mainColor.a)));
            dst.SetFloat("_LightColorAttenuation", 0);
            dst.SetFloat("_IndirectLightIntensity", 0);

            var scroll = V(src, "_MainTex_ScrollRotate", Vector4.zero);
            dst.SetFloat("_UvAnimScrollX", scroll.x);
            dst.SetFloat("_UvAnimScrollY", scroll.y);
            dst.SetFloat("_UvAnimRotation", scroll.w / Mathf.PI * 0.5f);
            dst.SetFloat("_MToonVersion", 35);
            dst.SetFloat("_DebugMode", 0);
            dst.SetFloat("_CullMode", F(src, "_Cull", 2));

            var bakedMain = BakeMainTexture(src);
            // lilToon can take alpha from a separate mask texture; MToon only reads the main texture alpha
            if ((isCutout || isTransparent) && F(src, "_AlphaMaskMode") != 0 && T(src, "_AlphaMask") != null)
                bakedMain = BakeAlphaMask(src, bakedMain);
            if (isCutout || isTransparent) bakedMain = BakeLayerAlpha(src, bakedMain);
            dst.SetTexture("_MainTex", bakedMain);
            dst.SetTextureScale("_MainTex", src.GetTextureScale("_MainTex"));
            dst.SetTextureOffset("_MainTex", src.GetTextureOffset("_MainTex"));

            if (F(src, "_UseBumpMap") == 1 && T(src, "_BumpMap") != null)
            {
                dst.SetFloat("_BumpScale", F(src, "_BumpScale", 1));
                dst.SetTexture("_BumpMap", T(src, "_BumpMap"));
                dst.EnableKeyword("_NORMALMAP");
            }

            if (F(src, "_UseShadow") == 1)
            {
                float border = F(src, "_ShadowBorder", 0.5f), blur = F(src, "_ShadowBlur", 0.1f);
                float shadeShift = (Mathf.Clamp01(border - blur * 0.5f) * 2f) - 1f;
                float shadeToony = shadeShift == 1f ? 1f : (2f - (Mathf.Clamp01(border + blur * 0.5f) * 2f)) / (1f - shadeShift);
                var shade = new ShadeModel(this, src);
                if (shade.NeedsTexture && _bakeTextures && bakedMain != null)
                {
                    dst.SetColor("_ShadeColor", Color.white);
                    dst.SetTexture("_ShadeTexture", BakeShadeTexture(src, shade, bakedMain, mainColor));
                }
                else
                {
                    // without per-pixel inputs the shade is the main color times a constant
                    var k = shade.Shade(Color.white, null, null, null, 1) * mainColor.linear;
                    dst.SetColor("_ShadeColor", (Color)ToSRGB32(k));
                    dst.SetTexture("_ShadeTexture", bakedMain);
                }
                // lilToon only darkens by cast shadows when _ShadowReceive is on (MToon still applies them once)
                dst.SetFloat("_ReceiveShadowRate", F(src, "_ShadowReceive"));
                dst.SetTexture("_ReceiveShadowTexture", null);
                dst.SetFloat("_ShadingGradeRate", 1);
                dst.SetTexture("_ShadingGradeTexture", T(src, "_ShadowBorderMask"));
                dst.SetFloat("_ShadeShift", shadeShift);
                dst.SetFloat("_ShadeToony", shadeToony);
            }
            else
            {
                dst.SetColor("_ShadeColor", Color.white);
                dst.SetTexture("_ShadeTexture", bakedMain);
            }

            if (F(src, "_UseEmission") == 1) ConvertEmission(src, dst, bakedMain, mainColor);

            if (F(src, "_UseRim") == 1 && F(src, "_RimBlendMode", 1) != 3)
            {
                // lilToon uses the color's alpha as strength, fades the rim on the shadowed side (_RimShadowMask)
                // and can keep it to the lit side (_RimDirStrength). MToon draws it everywhere at full color,
                // so fold those into one average strength.
                var rim = C(src, "_RimColor", Color.white);
                float strength = Mathf.Clamp01(rim.a) * (1f - 0.5f * F(src, "_RimShadowMask", 0.5f)) * (1f - 0.5f * F(src, "_RimDirStrength"));
                dst.SetColor("_RimColor", Scaled(rim, strength));
                float rimMain = F(src, "_RimMainStrength");
                var rimTex = T(src, "_RimColorTex");
                dst.SetTexture("_RimTexture", rimTex != null || rimMain > 0
                    ? BakeLayer(rimTex, null, bakedMain, mainColor, rimMain, src.name + "_rim")
                    : null);
                dst.SetFloat("_RimLightingMix", F(src, "_RimEnableLighting", 1));
                var (rimPower, rimLift) = FitRim(F(src, "_RimFresnelPower", 3), F(src, "_RimBorder", 0.5f), F(src, "_RimBlur", 0.65f));
                dst.SetFloat("_RimFresnelPower", rimPower);
                dst.SetFloat("_RimLift", rimLift);
            }
            else
            {
                dst.SetColor("_RimColor", Color.black);
            }

            if (F(src, "_UseMatCap") == 1 && F(src, "_MatCapBlendMode") != 3 && T(src, "_MatCapTex") != null && !_keepMatCap)
            {
                Log.Add(L.Tr("mat.015", src.name));
            }
            else if (F(src, "_UseMatCap") == 1 && F(src, "_MatCapBlendMode") != 3 && T(src, "_MatCapTex") != null)
            {
                // MToon's sphere-add has no mask and always adds at full strength, so fold blend/color/mask into the texture
                var mask = T(src, "_MatCapBlendMask");
                float maskMean = mask != null ? MeanRed(mask, IsSRGB(mask)) : 1f;
                var tint = C(src, "_MatCapColor", Color.white);
                float strength = F(src, "_MatCapBlend", 1) * maskMean * tint.a;
                // spreading a mask that covers only part of the material puts a veil of shine on the rest
                if (strength < MinMatCapStrength || mask != null && maskMean < MinMatCapCoverage)
                {
                    Log.Add(L.Warn("mat.004", src.name, strength));
                }
                else
                {
                    var scale = Scaled(tint, strength); // TintTexture multiplies sRGB texels, i.e. linear × scale.linear
                    dst.SetTexture("_SphereAdd", Approximately(scale, Color.white) ? T(src, "_MatCapTex") : TintTexture(T(src, "_MatCapTex"), scale, src.name + "_matcap"));
                    if (mask != null) Log.Add(L.Warn("mat.005", src.name, strength));
                }
            }

            if (isOutl)
            {
                dst.SetTexture("_OutlineWidthTexture", T(src, "_OutlineWidthMask"));
                dst.SetFloat("_OutlineWidth", F(src, "_OutlineWidth", 0.08f));
                dst.SetColor("_OutlineColor", C(src, "_OutlineColor", Color.black));
                dst.SetFloat("_OutlineLightingMix", 1);
                dst.SetFloat("_OutlineWidthMode", 1);   // WorldCoordinates
                dst.SetFloat("_OutlineColorMode", 1);   // MixedLighting
                dst.SetFloat("_OutlineCullMode", 1);
            }

            var zwrite = F(src, "_ZWrite", 1);
            if (isCutout)
            {
                dst.SetFloat("_Cutoff", F(src, "_Cutoff", 0.5f));
                dst.SetFloat("_BlendMode", 1);
            }
            else if (isTransparent)
            {
                dst.SetFloat("_BlendMode", zwrite == 0 ? 2 : 3);
            }
            else
            {
                dst.SetFloat("_BlendMode", 0);
            }
            MToon.Utils.ValidateProperties(dst, true);

            if (shaderName.Contains("Tessellation")) Log.Add(L.Warn("mat.006", src.name));
            if (lilShaderUtils.IsRefractionShaderName(shaderName) || lilShaderUtils.IsFurShaderName(shaderName))
                Log.Add(L.Warn("mat.007", src.name, shaderName));
            return dst;
        }

        /// <summary>
        /// lilToon's rim ramps (1 - N·V)^fresnel linearly from border - blur/2 to border + blur/2 (lilTooningScale);
        /// MToon's is saturate(1 - N·V + lift)^power. Fit MToon to be full where lilToon's ramp ends and 5% where it starts.
        /// (lilToon's own MToon export uses fresnel / blur, which spreads narrow rims over most of the surface.)
        /// </summary>
        static (float power, float lift) FitRim(float fresnel, float border, float blur)
        {
            fresnel = Mathf.Max(0.01f, fresnel);
            float lo = Mathf.Clamp01(border - blur * 0.5f), hi = Mathf.Clamp01(border + blur * 0.5f);
            float x0 = Mathf.Pow(lo, 1f / fresnel), x1 = Mathf.Pow(Mathf.Max(hi, 0.0001f), 1f / fresnel);
            float lift = 1f - x1;
            float start = x0 + lift;
            if (start <= 0.0001f) return (1f, lift);  // ramp starts at the silhouette's opposite: a soft rim everywhere
            if (start >= 0.999f) return (50f, lift); // no ramp: a hard edge
            return (Mathf.Clamp(Mathf.Log(0.05f) / Mathf.Log(start), 0.5f, 50f), lift);
        }

        /// <summary>
        /// lilToon emission = color × alpha × _EmissionBlend × map × mask (× main color). MToon only has
        /// color × map, so the color strength goes into _EmissionColor and the per-pixel parts are baked into one map.
        /// </summary>
        void ConvertEmission(Material src, Material dst, Texture bakedMain, Color mainColor)
        {
            if (F(src, "_EmissionMap_UVMode") != 0 || F(src, "_EmissionBlendMode", 1) == 3)
            {
                // MToon reads every texture with the main UV and only adds emission
                Log.Add(L.Warn("mat.010", src.name));
                return;
            }
            var color = C(src, "_EmissionColor", Color.white);
            var blink = V(src, "_EmissionBlink", Vector4.zero);
            float strength = Mathf.Clamp01(color.a) * F(src, "_EmissionBlend", 1) * (1f - 0.5f * Mathf.Clamp01(blink.x));
            var emission = Scaled(color, strength);
            if (emission.maxColorComponent < 0.004f) return;

            var map = T(src, "_EmissionMap");
            var mask = T(src, "_EmissionBlendMask");
            float mainStrength = F(src, "_EmissionMainStrength");
            var tex = mask != null || mainStrength > 0
                ? BakeLayer(map, mask, bakedMain, mainColor, mainStrength, src.name + "_emission")
                : map;
            // a null map is fine: MToon's _EmissionMap defaults to white, i.e. the color covers the whole material
            dst.SetColor("_EmissionColor", emission);
            dst.SetTexture("_EmissionMap", tex);
        }

        // ---------------------------------------------------------------- texture baking
        // Every readable copy made while baking is tracked here and destroyed when the bake finishes
        // (full-size 4K copies are ~64 MB each, so leaking them adds up quickly in a batch).

        readonly List<Object> _temps = new List<Object>();

        T Temp<T>(T obj) where T : Object
        {
            if (obj != null && !EditorUtility.IsPersistent(obj)) _temps.Add(obj);
            return obj;
        }

        void FreeTemps()
        {
            foreach (var t in _temps) if (t != null) Object.DestroyImmediate(t);
            _temps.Clear();
        }

        Texture BakeMainTexture(Material src)
        {
            var mainTex = T(src, "_MainTex");
            if (!_bakeTextures) return mainTex;
            var hsvg = V(src, "_MainTexHSVG", lilConstants.defaultHSVG);
            bool use2nd = F(src, "_UseMain2ndTex") != 0, use3rd = F(src, "_UseMain3rdTex") != 0;
            bool nothingToBake = hsvg == lilConstants.defaultHSVG && F(src, "_MainGradationStrength") == 0 && !use2nd && !use3rd;
            if (nothingToBake || mainTex == null) return mainTex;

            try
            {
                var baker = NewBaker(src);
                if (baker == null) return mainTex;
                baker.SetColor("_Color", Color.white);
                var srcTex = LoadRaw(mainTex);
                baker.SetTexture("_MainTex", srcTex);
                var layers = new List<string> { "_MainGradationTex", "_MainColorAdjustMask" };
                if (use2nd) layers.AddRange(new[] { "_Main2ndTex", "_Main2ndBlendMask" });
                if (use3rd) layers.AddRange(new[] { "_Main3rdTex", "_Main3rdBlendMask" });
                foreach (var p in layers)
                    if (src.HasProperty(p)) baker.SetTexture(p, LoadRaw(src.GetTexture(p)) ?? (Texture)Texture2D.whiteTexture);
                var result = RunBake(srcTex, baker, src.name + "_main", mainTex);
                Log.Add(L.Tr("mat.008", src.name));
                return result ?? mainTex;
            }
            finally { FreeTemps(); }
        }

        // lilToon works in linear space (the project is Linear), so strengths and blends are applied to
        // linearised colors and converted back to sRGB for the material / PNG.

        /// <summary>color × strength in linear space, returned as an sRGB material color.</summary>
        static Color Scaled(Color c, float strength)
        {
            var l = c.linear * strength;
            return new Color(l.r, l.g, l.b, 1).gamma;
        }

        /// <summary>True when the GPU linearises the texture's RGB (color textures); masks imported as data are not.</summary>
        static bool IsSRGB(Texture t) =>
            t == null || !(AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(t)) is TextureImporter ti) || ti.sRGBTexture;

        /// <summary>A texel the way the shader reads it: RGB linearised for sRGB textures, alpha always linear.</summary>
        static Color Texel(Color32 c, bool srgb) => srgb ? ((Color)c).linear : (Color)c;

        static float Channel(byte v, bool srgb) => srgb ? Mathf.GammaToLinearSpace(v / 255f) : v / 255f;

        static Color32 ToSRGB32(Color linear) => new Color(Mathf.Clamp01(linear.r), Mathf.Clamp01(linear.g), Mathf.Clamp01(linear.b), 1).gamma;

        /// <summary>
        /// lilToon's shadow (lil_common_frag.hlsl, lilGetShading) reduced to the one shade color MToon has:
        /// the 2nd/3rd shadow colors cover the part of the shadow their border takes up, and the border gradation
        /// (_ShadowBorderColor / _ShadowBorderRange) is applied at its average strength over the shadow.
        /// </summary>
        class ShadeModel
        {
            readonly Color _c1, _c2, _c3, _borderColor;
            readonly float _w2, _w3, _border, _mainStrength, _strength;
            public readonly Texture Tex1, Tex2, Tex3, Mask;

            public ShadeModel(LilToonToMToon o, Material src)
            {
                float b1 = o.F(src, "_ShadowBorder", 0.5f);
                _c1 = o.C(src, "_ShadowColor", Color.white).linear;
                _c2 = o.C(src, "_Shadow2ndColor", Color.clear).linear;
                _c3 = o.C(src, "_Shadow3rdColor", Color.clear).linear;
                _w2 = Mathf.Clamp01(_c2.a) * (b1 > 0 ? Mathf.Clamp01(o.F(src, "_Shadow2ndBorder", 0.5f) / b1) : 0);
                _w3 = Mathf.Clamp01(_c3.a) * (b1 > 0 ? Mathf.Clamp01(o.F(src, "_Shadow3rdBorder", 0.25f) / b1) : 0);
                _mainStrength = o.F(src, "_ShadowMainStrength");
                _strength = o.F(src, "_ShadowStrength", 1);
                _borderColor = o.C(src, "_ShadowBorderColor", Color.black).linear;
                float range = o.F(src, "_ShadowBorderRange");
                // lns.w ramps 0→1 over [b1 - range, b1]; its mean over the shadowed part [0, b1]
                _border = range <= 0 || b1 <= 0 ? 0 : range >= b1 ? 1 - b1 / (2 * range) : 0.5f * range / b1;
                bool lut = o.F(src, "_ShadowColorType") == 1;
                Tex1 = lut ? null : o.T(src, "_ShadowColorTex");
                Tex2 = lut || _w2 <= 0 ? null : o.T(src, "_Shadow2ndColorTex");
                Tex3 = lut || _w3 <= 0 ? null : o.T(src, "_Shadow3rdColorTex");
                // SDF / flat face shadows (mask types 1, 2) use the mask differently; MToon shades those by normals
                Mask = o.F(src, "_ShadowMaskType") == 0 ? o.T(src, "_ShadowStrengthMask") : null;
                MaskIsSRGB = IsSRGB(Mask);
            }

            public readonly bool MaskIsSRGB;

            public bool NeedsTexture => _mainStrength > 0 || Tex1 != null || Tex2 != null || Tex3 != null || Mask != null;

            /// <summary>
            /// Linear shade color for linear albedo <paramref name="a"/> (linear texels, null when absent;
            /// <paramref name="mask"/> as the shader reads it).
            /// </summary>
            public Color Shade(Color a, Color? t1, Color? t2, Color? t3, float mask)
            {
                Color Layer(Color c, Color? t) => (t is Color tc ? Color.Lerp(a, tc, tc.a) : a) * c;
                var ind = Layer(_c1, t1);
                if (_w2 > 0) ind = Color.Lerp(ind, Layer(_c2, t2), _w2);
                if (_w3 > 0) ind = Color.Lerp(ind, Layer(_c3, t3), _w3);
                ind = Color.Lerp(ind, ind * a, _mainStrength);
                ind = new Color(Mathf.Min(ind.r, a.r), Mathf.Min(ind.g, a.g), Mathf.Min(ind.b, a.b));
                ind = new Color(Mathf.Lerp(ind.r, a.r, _border * _borderColor.r), Mathf.Lerp(ind.g, a.g, _border * _borderColor.g),
                    Mathf.Lerp(ind.b, a.b, _border * _borderColor.b));
                var shade = Color.Lerp(a, ind, Mathf.Clamp01(_strength * mask));
                shade.a = 1;
                return shade;
            }
        }

        Texture BakeShadeTexture(Material src, ShadeModel shade, Texture bakedMain, Color mainColor)
        {
            try
            {
                var mainRaw = LoadRaw(bakedMain);
                int w = mainRaw.width, h = mainRaw.height;
                Color32[] Pixels(Texture t) => t == null ? null : Temp(Readable(LoadRaw(t), w, h)).GetPixels32();
                var main = Temp(Readable(mainRaw, w, h)).GetPixels32();
                Color32[] t1 = Pixels(shade.Tex1), t2 = Pixels(shade.Tex2), t3 = Pixels(shade.Tex3), mask = Pixels(shade.Mask);
                var mainLinear = mainColor.linear;
                var outPx = new Color32[w * h];
                for (int i = 0; i < outPx.Length; i++)
                {
                    var c = shade.Shade(((Color)main[i]).linear * mainLinear,
                        t1 != null ? Texel(t1[i], true) : (Color?)null, t2 != null ? Texel(t2[i], true) : (Color?)null,
                        t3 != null ? Texel(t3[i], true) : (Color?)null, mask != null ? Channel(mask[i].r, shade.MaskIsSRGB) : 1);
                    outPx[i] = ToSRGB32(c);
                }
                var outTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                outTex.SetPixels32(outPx);
                outTex.Apply();
                return SaveTexture(outTex, src.name + "_shade", bakedMain, maxSizeOf: bakedMain, alpha: false) ?? bakedMain;
            }
            finally { FreeTemps(); }
        }

        /// <summary>
        /// Bakes tex.rgb·a × mask.rgb·a × lerp(1, main × mainColor, mainStrength) into one opaque texture, the way
        /// lilToon combines them per pixel for emission and rim light. Missing textures count as white.
        /// </summary>
        Texture BakeLayer(Texture tex, Texture mask, Texture main, Color mainColor, float mainStrength, string fileName)
        {
            var sizeRef = main;
            if (mainStrength <= 0) main = null;
            var first = tex != null ? tex : mask != null ? mask : main;
            if (first == null) return null;
            if (!_bakeTextures) return tex;
            try
            {
                var firstRaw = LoadRaw(first);
                int w = firstRaw.width, h = firstRaw.height;
                Color32[] Pixels(Texture t) => t == null ? null : Temp(Readable(LoadRaw(t), w, h)).GetPixels32();
                Color32[] tp = Pixels(tex), mp = Pixels(mask), ap = Pixels(main);
                bool texSRGB = IsSRGB(tex), maskSRGB = IsSRGB(mask);
                var mainLinear = mainColor.linear;
                var outPx = new Color32[w * h];
                for (int i = 0; i < outPx.Length; i++)
                {
                    var c = Color.white;
                    if (tp != null) { var t = Texel(tp[i], texSRGB); c *= t * t.a; }
                    if (mp != null) { var m = Texel(mp[i], maskSRGB); c *= m * m.a; }
                    if (ap != null) c *= Color.Lerp(Color.white, ((Color)ap[i]).linear * mainLinear, mainStrength);
                    outPx[i] = ToSRGB32(c);
                }
                var outTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                outTex.SetPixels32(outPx);
                outTex.Apply();
                return SaveTexture(outTex, fileName, tex != null ? tex : main != null ? main : mask, maxSizeOf: sizeRef, alpha: false) ?? tex;
            }
            finally { FreeTemps(); }
        }

        Material NewBaker(Material copyFrom)
        {
            var shader = Shader.Find("Hidden/ltsother_baker");
            if (shader == null) return null;
            var m = Temp(new Material(shader));
            if (copyFrom != null) m.CopyPropertiesFromMaterial(copyFrom);
            return m;
        }

        const float MinMatCapStrength = 0.35f;
        const float MinMatCapCoverage = 0.7f;

        static bool Approximately(Color a, Color b) =>
            Mathf.Abs(a.r - b.r) < 0.01f && Mathf.Abs(a.g - b.g) < 0.01f && Mathf.Abs(a.b - b.b) < 0.01f;

        /// <summary>Copies a texture into a readable RGBA32 texture of the given size (any import format/size). Caller owns it.</summary>
        static Texture2D Readable(Texture tex, int width, int height)
        {
            var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                var t = new Texture2D(width, height, TextureFormat.RGBA32, false);
                t.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                t.Apply();
                return t;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        static float MeanRed(Texture tex, bool srgb)
        {
            var small = Readable(tex, 64, 64);
            float sum = 0;
            foreach (var c in small.GetPixels32()) sum += Channel(c.r, srgb);
            Object.DestroyImmediate(small);
            return sum / (64 * 64);
        }

        /// <summary>
        /// Loads the original image at full resolution (bypasses import compression / max size, like lilToon does)
        /// without touching the texture asset itself. The result is a temporary owned by this converter.
        /// </summary>
        Texture2D LoadRaw(Texture tex)
        {
            if (tex == null) return null;
            var path = AssetDatabase.GetAssetPath(tex);
            var ext = Path.GetExtension(path ?? "").ToLowerInvariant();
            if ((ext == ".png" || ext == ".jpg" || ext == ".jpeg") && File.Exists(path))
            {
                var t = Temp(new Texture2D(2, 2, TextureFormat.RGBA32, false));
                if (t.LoadImage(File.ReadAllBytes(path))) return t;
            }
            return Temp(Readable(tex, tex.width, tex.height));
        }

        /// <summary>main.alpha = f(mask.r) with lilToon's _AlphaMaskMode / _AlphaMaskScale / _AlphaMaskValue.</summary>
        Texture BakeAlphaMask(Material src, Texture main)
        {
            if (main == null) return null;
            try
            {
                var mainRaw = LoadRaw(main);
                var colors = mainRaw.GetPixels32();
                var mask = Temp(Readable(LoadRaw(T(src, "_AlphaMask")), mainRaw.width, mainRaw.height)).GetPixels32();
                bool maskSRGB = IsSRGB(T(src, "_AlphaMask"));
                int mode = (int)F(src, "_AlphaMaskMode");
                float scale = F(src, "_AlphaMaskScale", 1), value = F(src, "_AlphaMaskValue");
                for (int i = 0; i < colors.Length; i++)
                {
                    float m = Mathf.Clamp01(Channel(mask[i].r, maskSRGB) * scale + value), a = colors[i].a / 255f;
                    a = mode == 1 ? m : mode == 2 ? a * m : mode == 3 ? Mathf.Clamp01(a + m) : mode == 4 ? Mathf.Clamp01(a - m) : a;
                    colors[i].a = (byte)Mathf.RoundToInt(a * 255);
                }
                var outTex = new Texture2D(mainRaw.width, mainRaw.height, TextureFormat.RGBA32, false);
                outTex.SetPixels32(colors);
                outTex.Apply();
                Log.Add(L.Tr("mat.009", src.name));
                return SaveTexture(outTex, src.name + "_alpha", main) ?? main;
            }
            finally { FreeTemps(); }
        }

        /// <summary>
        /// lilToon's 2nd/3rd layers can replace, multiply, add to or subtract from the alpha (_Main2ndTexAlphaMode);
        /// the main-texture bake keeps the main alpha, so apply those modes here.
        /// </summary>
        Texture BakeLayerAlpha(Material src, Texture main)
        {
            var layers = new[] { "2nd", "3rd" }
                .Where(n => F(src, $"_UseMain{n}Tex") != 0 && F(src, $"_Main{n}TexAlphaMode") != 0).ToArray();
            if (main == null || layers.Length == 0) return main;
            try
            {
                var mainRaw = LoadRaw(main);
                int w = mainRaw.width, h = mainRaw.height;
                var colors = mainRaw.GetPixels32();
                foreach (var n in layers)
                {
                    int mode = (int)F(src, $"_Main{n}TexAlphaMode");
                    float colorA = C(src, $"_Color{n}", Color.white).a;
                    var tex = T(src, $"_Main{n}Tex");
                    var mask = T(src, $"_Main{n}BlendMask");
                    var tp = tex != null ? Temp(Readable(LoadRaw(tex), w, h)).GetPixels32() : null;
                    var mp = mask != null ? Temp(Readable(LoadRaw(mask), w, h)).GetPixels32() : null;
                    bool maskSRGB = IsSRGB(mask);
                    for (int i = 0; i < colors.Length; i++)
                    {
                        float la = colorA * (tp != null ? tp[i].a / 255f : 1) * (mp != null ? Channel(mp[i].r, maskSRGB) : 1);
                        float a = colors[i].a / 255f;
                        a = mode == 1 ? la : mode == 2 ? a * la : mode == 3 ? Mathf.Clamp01(a + la) : mode == 4 ? Mathf.Clamp01(a - la) : a;
                        colors[i].a = (byte)Mathf.RoundToInt(a * 255);
                    }
                }
                var outTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                outTex.SetPixels32(colors);
                outTex.Apply();
                Log.Add(L.Tr("mat.012", src.name));
                return SaveTexture(outTex, src.name + "_layeralpha", main) ?? main;
            }
            finally { FreeTemps(); }
        }

        /// <summary>RGB *= tint (HDR tints above 1 are clamped instead of wrapping around).</summary>
        Texture TintTexture(Texture tex, Color tint, string fileName)
        {
            try
            {
                var raw = LoadRaw(tex);
                var px = raw.GetPixels32();
                for (int i = 0; i < px.Length; i++)
                {
                    px[i].r = (byte)Mathf.Min(255f, px[i].r * tint.r);
                    px[i].g = (byte)Mathf.Min(255f, px[i].g * tint.g);
                    px[i].b = (byte)Mathf.Min(255f, px[i].b * tint.b);
                }
                var outTex = new Texture2D(raw.width, raw.height, TextureFormat.RGBA32, false);
                outTex.SetPixels32(px);
                outTex.Apply();
                return SaveTexture(outTex, fileName, tex, TextureWrapMode.Clamp) ?? tex;
            }
            finally { FreeTemps(); }
        }

        Texture2D RunBake(Texture2D srcTex, Material baker, string fileName, Texture reference, Texture maxSizeOf = null, bool alpha = true)
        {
            if (srcTex == null) return null;
            Texture2D outTex = null;
            lilToonInspector.RunBake(ref outTex, srcTex, baker);
            return SaveTexture(outTex, fileName, reference, maxSizeOf: maxSizeOf, alpha: alpha);
        }

        /// <summary>
        /// Writes a generated texture as PNG into the output folder and imports it with the reference texture's
        /// importer settings (like lilToon's CopyTextureSetting), keeping the full resolution so the VRM is not downscaled.
        /// Textures that are only drawn on top of the main texture (shade, rim, emission) pass <paramref name="maxSizeOf"/> so
        /// they are not exported larger than it, and <paramref name="alpha"/> false since MToon ignores their alpha.
        /// Destroys <paramref name="tex"/>.
        /// </summary>
        Texture2D SaveTexture(Texture2D tex, string fileName, Texture reference, TextureWrapMode? wrap = null,
            Texture maxSizeOf = null, bool alpha = true)
        {
            Directory.CreateDirectory(_textureFolder);
            var path = AssetDatabase.GenerateUniqueAssetPath($"{_textureFolder}/{Sanitize(fileName)}.png");
            int size = Mathf.Max(tex.width, tex.height);
            if (maxSizeOf != null) size = Mathf.Min(size, Mathf.Max(maxSizeOf.width, maxSizeOf.height));
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);

            if (AssetImporter.GetAtPath(path) is TextureImporter dst)
            {
                if (reference != null && AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(reference)) is TextureImporter srcImporter)
                {
                    var settings = new TextureImporterSettings();
                    srcImporter.ReadTextureSettings(settings);
                    dst.SetTextureSettings(settings);
                    dst.SetPlatformTextureSettings(srcImporter.GetDefaultPlatformTextureSettings());
                }
                dst.textureType = TextureImporterType.Default; // baked results are color textures even if the reference was not
                // the reference may be imported without alpha
                dst.alphaSource = alpha ? TextureImporterAlphaSource.FromInput : TextureImporterAlphaSource.None;
                var fit = Mathf.Min(8192, Mathf.NextPowerOfTwo(size));
                dst.maxTextureSize = maxSizeOf != null ? fit : Mathf.Max(dst.maxTextureSize, fit);
                if (wrap != null) dst.wrapMode = wrap.Value;
                dst.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        public static string Sanitize(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }
    }
}
