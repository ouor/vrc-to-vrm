using System.Collections.Generic;
using System.IO;
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
        readonly Dictionary<Material, Material> _cache = new Dictionary<Material, Material>();
        public readonly List<string> Log = new List<string>();

        public LilToonToMToon(string textureFolder, bool bakeTextures)
        {
            _textureFolder = textureFolder;
            _bakeTextures = bakeTextures;
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

        Material FromUnknown(Material src)
        {
            var dst = NewMToon(src);
            if (src.HasProperty("_MainTex")) dst.SetTexture("_MainTex", src.GetTexture("_MainTex"));
            if (src.HasProperty("_Color")) dst.SetColor("_Color", src.GetColor("_Color"));
            dst.SetFloat("_LightColorAttenuation", 0);
            dst.SetFloat("_IndirectLightIntensity", 0);
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
                var shadowStrength = F(src, "_ShadowStrength", 1);
                var shadowColor = C(src, "_ShadowColor", Color.white);
                if (T(src, "_ShadowStrengthMask") != null || F(src, "_ShadowMainStrength") != 0)
                {
                    dst.SetColor("_ShadeColor", Color.white);
                    dst.SetTexture("_ShadeTexture", BakeShadowTexture(src, bakedMain));
                }
                else
                {
                    dst.SetColor("_ShadeColor", new Color(
                        1f - (1f - shadowColor.r) * shadowStrength,
                        1f - (1f - shadowColor.g) * shadowStrength,
                        1f - (1f - shadowColor.b) * shadowStrength, 1f));
                    dst.SetTexture("_ShadeTexture", T(src, "_ShadowColorTex") ?? bakedMain);
                }
                dst.SetFloat("_ReceiveShadowRate", 1);
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

            if (F(src, "_UseEmission") == 1 && T(src, "_EmissionMap") != null)
            {
                dst.SetColor("_EmissionColor", C(src, "_EmissionColor", Color.white));
                dst.SetTexture("_EmissionMap", T(src, "_EmissionMap"));
            }

            if (F(src, "_UseRim") == 1)
            {
                dst.SetColor("_RimColor", C(src, "_RimColor", Color.white));
                dst.SetTexture("_RimTexture", T(src, "_RimColorTex"));
                dst.SetFloat("_RimLightingMix", F(src, "_RimEnableLighting", 1));
                float fp = F(src, "_RimFresnelPower", 3), rimBlur = F(src, "_RimBlur", 0.65f), rimBorder = F(src, "_RimBorder", 0.5f);
                dst.SetFloat("_RimFresnelPower", fp / Mathf.Max(0.001f, rimBlur));
                dst.SetFloat("_RimLift", Mathf.Pow(1f - rimBorder, fp) * (1f - rimBlur));
            }
            else
            {
                dst.SetColor("_RimColor", Color.black);
            }

            if (F(src, "_UseMatCap") == 1 && F(src, "_MatCapBlendMode") != 3 && T(src, "_MatCapTex") != null)
            {
                // MToon's sphere-add has no mask and always adds at full strength, so fold blend/color/mask into the texture
                var mask = T(src, "_MatCapBlendMask");
                float maskMean = mask != null ? MeanRed(mask) : 1f;
                var tint = C(src, "_MatCapColor", Color.white);
                float strength = F(src, "_MatCapBlend", 1) * maskMean * tint.a;
                if (strength < MinMatCapStrength)
                {
                    Log.Add(L.Warn("mat.004", src.name, strength));
                }
                else
                {
                    var scale = new Color(tint.r * strength, tint.g * strength, tint.b * strength, 1);
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

        Texture BakeShadowTexture(Material src, Texture bakedMain)
        {
            if (!_bakeTextures || bakedMain == null) return bakedMain;
            try
            {
                var baker = NewBaker(null);
                if (baker == null) return bakedMain;
                var shadowColor = C(src, "_ShadowColor", Color.white);
                var mainRaw = LoadRaw(bakedMain);
                baker.SetColor("_Color", Color.white);
                baker.SetVector("_MainTexHSVG", lilConstants.defaultHSVG);
                baker.SetFloat("_UseMain2ndTex", 1);
                baker.SetFloat("_UseMain3rdTex", 1);
                baker.SetColor("_Color3rd", new Color(1, 1, 1, F(src, "_ShadowMainStrength")));
                baker.SetFloat("_Main3rdTexBlendMode", 3);
                baker.SetColor("_Color2nd", new Color(shadowColor.r, shadowColor.g, shadowColor.b, F(src, "_ShadowStrength", 1)));
                baker.SetFloat("_Main2ndTexBlendMode", 0);
                baker.SetFloat("_Main2ndTexAlphaMode", 0);
                baker.SetTexture("_MainTex", mainRaw);
                baker.SetTexture("_Main3rdTex", mainRaw);
                baker.SetTexture("_Main2ndTex", LoadRaw(T(src, "_ShadowColorTex")) ?? mainRaw);
                var mask = LoadRaw(T(src, "_ShadowStrengthMask")) ?? (Texture)Texture2D.whiteTexture;
                baker.SetTexture("_Main2ndBlendMask", mask);
                baker.SetTexture("_Main3rdBlendMask", mask);
                return RunBake(mainRaw, baker, src.name + "_shade", bakedMain) ?? bakedMain;
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

        static float MeanRed(Texture tex)
        {
            var small = Readable(tex, 64, 64);
            float sum = 0;
            foreach (var c in small.GetPixels32()) sum += c.r;
            Object.DestroyImmediate(small);
            return sum / (64 * 64 * 255f);
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
                int mode = (int)F(src, "_AlphaMaskMode");
                float scale = F(src, "_AlphaMaskScale", 1), value = F(src, "_AlphaMaskValue");
                for (int i = 0; i < colors.Length; i++)
                {
                    float m = Mathf.Clamp01(mask[i].r / 255f * scale + value), a = colors[i].a / 255f;
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

        Texture2D RunBake(Texture2D srcTex, Material baker, string fileName, Texture reference)
        {
            if (srcTex == null) return null;
            Texture2D outTex = null;
            lilToonInspector.RunBake(ref outTex, srcTex, baker);
            return SaveTexture(outTex, fileName, reference);
        }

        /// <summary>
        /// Writes a generated texture as PNG into the output folder and imports it with the reference texture's
        /// importer settings (like lilToon's CopyTextureSetting), keeping the full resolution so the VRM is not downscaled.
        /// Destroys <paramref name="tex"/>.
        /// </summary>
        Texture2D SaveTexture(Texture2D tex, string fileName, Texture reference, TextureWrapMode? wrap = null)
        {
            Directory.CreateDirectory(_textureFolder);
            var path = AssetDatabase.GenerateUniqueAssetPath($"{_textureFolder}/{Sanitize(fileName)}.png");
            int size = Mathf.Max(tex.width, tex.height);
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
                dst.alphaSource = TextureImporterAlphaSource.FromInput; // the reference may be imported without alpha
                dst.maxTextureSize = Mathf.Max(dst.maxTextureSize, Mathf.Min(8192, Mathf.NextPowerOfTwo(size)));
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
