using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRM;
using Object = UnityEngine.Object;

namespace VrcToVrm
{
    [Serializable]
    public class ConversionOptions
    {
        public string OutputFolder = "Assets/VRMConverted";
        /// <summary>Optional output folder/file name; defaults to the avatar name (batch uses it to keep names unique).</summary>
        public string OutputName;
        public bool ForceTPose = true;
        public bool RemoveInactive = true;
        public bool BakeTextures = true;
        public bool KeepMatCap = true;
        public bool ReduceBlendshape = true;
        public bool UseSparseAccessor = true;
        public bool KeepWorkingCopy = false;
        public SpringConversionSettings Spring = new SpringConversionSettings();

        // meta
        public string Title = "";
        public string Version = "1.0";
        public string Author = "";
        public string ContactInformation = "";
        public string Reference = "";
        public Texture2D Thumbnail;
        public AllowedUser AllowedUser = AllowedUser.OnlyAuthor;
        public UssageLicense ViolentUssage = UssageLicense.Disallow;
        public UssageLicense SexualUssage = UssageLicense.Disallow;
        public UssageLicense CommercialUssage = UssageLicense.Disallow;
        public string OtherPermissionUrl = "";
        public LicenseType LicenseType = LicenseType.Redistribution_Prohibited;
        public string OtherLicenseUrl = "";

        // expressions (blendshape names on the face mesh; candidates from FX)
        public string Blink, BlinkLeft, BlinkRight, LookUp, LookDown;
        public Dictionary<BlendShapePreset, ExpressionCandidate> Emotions = new Dictionary<BlendShapePreset, ExpressionCandidate>();
        public HashSet<ExpressionCandidate> Customs = new HashSet<ExpressionCandidate>();
    }

    public class ConversionResult
    {
        public string VrmPath;
        public string PrefabPath;
        public bool Success;
        public List<string> Log = new List<string>();
    }

    /// <summary>VRChat avatar (lilToon / PhysBone / FX expressions) -> VRM 0.x.</summary>
    public static class VrcToVrmConverter
    {

        public static ConversionResult Convert(GameObject source, VrcAvatarAnalysis analysis, ConversionOptions o)
        {
            var result = new ConversionResult();
            if (analysis.Root != null) source = analysis.Root; // descriptor may sit on a child of the selected prefab
            var log = result.Log;
            var name = LilToonToMToon.Sanitize(string.IsNullOrEmpty(o.OutputName) ? source.name : o.OutputName);
            var folder = $"{o.OutputFolder.TrimEnd('/')}/{name}";
            if (!PrepareOutputFolder(folder, source, log)) return result;

            var copy = (GameObject)Object.Instantiate(source);
            copy.name = name;
            copy.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            if (!copy.activeSelf) copy.SetActive(true);
            try
            {
                Build(copy, source, analysis, o, folder, result);
            }
            catch (Exception e)
            {
                log.Add("[Error] " + e);
                Debug.LogException(e);
            }
            finally
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                if (!o.KeepWorkingCopy) Object.DestroyImmediate(copy);
            }
            return result;
        }

        static void Build(GameObject copy, GameObject source, VrcAvatarAnalysis a, ConversionOptions o, string folder, ConversionResult result)
        {
            var log = result.Log;
            var animator = copy.GetComponent<Animator>();
            if (animator == null || animator.avatar == null || !animator.avatar.isHuman)
                throw new Exception(L.Tr("conv.001"));
            var head = animator.GetBoneTransform(HumanBodyBones.Head);

            // resolve everything that refers to the source hierarchy while the copy is still identical
            Transform Map(Transform t) => t == null ? null : copy.transform.Find(AnimationUtility.CalculateTransformPath(t, source.transform));
            SkinnedMeshRenderer MapSmr(SkinnedMeshRenderer s) => s == null ? null : Map(s.transform)?.GetComponent<SkinnedMeshRenderer>();
            var eyelidMesh = MapSmr(a.EyelidMesh);
            var leftEye = Map(a.LeftEye) ?? animator.GetBoneTransform(HumanBodyBones.LeftEye);
            var rightEye = Map(a.RightEye) ?? animator.GetBoneTransform(HumanBodyBones.RightEye);
            List<(SkinnedMeshRenderer smr, string shape, float weight)> Resolve(IEnumerable<ShapeValue> shapes) => shapes
                .Select(s => (copy.transform.Find(s.RelativePath)?.GetComponent<SkinnedMeshRenderer>(), s.ShapeName, s.Weight))
                .ToList();
            var usedCandidates = o.Emotions.Values.Concat(o.Customs).Distinct().ToList();
            var resolved = usedCandidates.ToDictionary(c => c, c => Resolve(c.Shapes));
            var vowels = a.Vowels.ToDictionary(kv => kv.Key, kv => Resolve(kv.Value));
            var viewWorld = copy.transform.TransformPoint(a.ViewPosition);

            // 1. dynamics (needs VRC components, so before stripping)
            PhysBoneToSpringBone.Convert(copy, o.Spring, log);

            // 2. hidden objects: VRM has no toggles, so drop what is not visible
            if (o.RemoveInactive) RemoveInactive(copy, animator, log);

            // 3. strip VRChat / tool components
            StripComponents(copy, animator, log);

            // 4. materials
            TrimMaterialSlots(copy, log);
            var conv = new LilToonToMToon(folder + "/Textures", o.BakeTextures, o.KeepMatCap);
            var saved = new Dictionary<Material, Material>();
            foreach (var r in copy.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials.Select(m =>
                {
                    var dst = conv.Convert(m);
                    if (dst == null || EditorUtility.IsPersistent(dst)) return dst;
                    if (!saved.TryGetValue(dst, out var asset))
                    {
                        var path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/Materials/{LilToonToMToon.Sanitize(dst.name)}.mat");
                        AssetDatabase.CreateAsset(dst, path);
                        saved[dst] = asset = dst;
                    }
                    return asset;
                }).ToArray();
                r.sharedMaterials = mats;
            }
            log.AddRange(conv.Log.Distinct());
            log.Add(L.Tr("conv.002", saved.Count));

            // 5. unique names (VRM exporter renames duplicates, which would break blendshape paths)
            MakeNamesUnique(copy, animator);

            // 6. expressions
            var avatar = ScriptableObject.CreateInstance<BlendShapeAvatar>();
            var clips = new List<BlendShapeClip>();
            BlendShapeClip Clip(BlendShapePreset preset, string clipName, IEnumerable<(SkinnedMeshRenderer smr, string shape, float weight)> values)
            {
                var c = ScriptableObject.CreateInstance<BlendShapeClip>();
                c.BlendShapeName = clipName;
                c.Preset = preset;
                c.name = "BlendShape." + clipName;
                var bindings = new List<BlendShapeBinding>();
                foreach (var (smr, shape, weight) in values)
                {
                    if (string.IsNullOrEmpty(shape)) continue;
                    if (smr == null || smr.sharedMesh == null)
                    {
                        log.Add(L.Warn("conv.003", clipName, shape));
                        continue;
                    }
                    var idx = smr.sharedMesh.GetBlendShapeIndex(shape);
                    if (idx < 0) { log.Add(L.Warn("conv.004", clipName, smr.name, shape)); continue; }
                    bindings.Add(new BlendShapeBinding
                    {
                        RelativePath = AnimationUtility.CalculateTransformPath(smr.transform, copy.transform),
                        Index = idx,
                        Weight = Mathf.Clamp(weight, 0, 100),
                    });
                }
                c.Values = bindings.ToArray();
                clips.Add(c);
                return c;
            }
            IEnumerable<(SkinnedMeshRenderer, string, float)> One(SkinnedMeshRenderer smr, string shape) =>
                string.IsNullOrEmpty(shape) ? Enumerable.Empty<(SkinnedMeshRenderer, string, float)>() : new[] { (smr, shape, 100f) };

            Clip(BlendShapePreset.Neutral, "Neutral", Enumerable.Empty<(SkinnedMeshRenderer, string, float)>());
            foreach (var (preset, _) in VrcAvatarAnalysis.VowelVisemes)
                Clip(preset, preset.ToString(), vowels.TryGetValue(preset, out var vs) ? vs : new List<(SkinnedMeshRenderer, string, float)>());
            Clip(BlendShapePreset.Blink, "Blink", One(eyelidMesh, o.Blink));
            foreach (var preset in ShapeNameHeuristics.EmotionWords.Keys)
                Clip(preset, preset.ToString(), o.Emotions.TryGetValue(preset, out var cand) && cand != null ? resolved[cand] : Enumerable.Empty<(SkinnedMeshRenderer, string, float)>());
            Clip(BlendShapePreset.LookUp, "LookUp", One(eyelidMesh, o.LookUp));
            Clip(BlendShapePreset.LookDown, "LookDown", One(eyelidMesh, o.LookDown));
            Clip(BlendShapePreset.LookLeft, "LookLeft", Enumerable.Empty<(SkinnedMeshRenderer, string, float)>());
            Clip(BlendShapePreset.LookRight, "LookRight", Enumerable.Empty<(SkinnedMeshRenderer, string, float)>());
            Clip(BlendShapePreset.Blink_L, "Blink_L", One(eyelidMesh, o.BlinkLeft));
            Clip(BlendShapePreset.Blink_R, "Blink_R", One(eyelidMesh, o.BlinkRight));

            var taken = new HashSet<string>(clips.Select(c => c.BlendShapeName), StringComparer.OrdinalIgnoreCase);
            foreach (var cand in o.Customs.OrderBy(c => c.Name))
            {
                var n = cand.Name;
                for (int i = 2; taken.Contains(n); i++) n = $"{cand.Name}_{i}";
                taken.Add(n);
                Clip(BlendShapePreset.Unknown, n, resolved[cand]);
            }
            foreach (var c in clips)
                AssetDatabase.CreateAsset(c, AssetDatabase.GenerateUniqueAssetPath($"{folder}/BlendShapes/{LilToonToMToon.Sanitize(c.name)}.asset"));
            avatar.Clips = clips;
            AssetDatabase.CreateAsset(avatar, $"{folder}/BlendShapes/BlendShape.asset");
            copy.AddComponent<VRMBlendShapeProxy>().BlendShapeAvatar = avatar;
            log.Add(L.Tr("conv.005", clips.Count(c => c.Preset != BlendShapePreset.Unknown && c.Values.Length > 0), clips.Count(c => c.Preset == BlendShapePreset.Unknown)));

            // 7. first person + look at
            var fp = copy.AddComponent<VRMFirstPerson>();
            fp.FirstPersonBone = head;
            fp.FirstPersonOffset = viewWorld - head.position;
            fp.TraverseRenderers();

            if (leftEye != null && rightEye != null)
            {
                copy.AddComponent<VRMLookAtHead>().Head = head;
                var ap = copy.AddComponent<VRMLookAtBoneApplyer>();
                ap.LeftEye = OffsetOnTransform.Create(leftEye);
                ap.RightEye = OffsetOnTransform.Create(rightEye);
                ap.HorizontalOuter = new CurveMapper(90, a.EyeOuterDeg);
                ap.HorizontalInner = new CurveMapper(90, a.EyeInnerDeg);
                ap.VerticalDown = new CurveMapper(90, a.EyeDownDeg);
                ap.VerticalUp = new CurveMapper(90, a.EyeUpDeg);
            }
            else log.Add(L.Warn("conv.006"));

            // 8. meta
            var meta = ScriptableObject.CreateInstance<VRMMetaObject>();
            meta.Title = string.IsNullOrEmpty(o.Title) ? copy.name : o.Title;
            meta.Version = o.Version;
            meta.Author = o.Author;
            meta.ContactInformation = o.ContactInformation;
            meta.Reference = o.Reference;
            meta.Thumbnail = o.Thumbnail;
            meta.AllowedUser = o.AllowedUser;
            meta.ViolentUssage = o.ViolentUssage;
            meta.SexualUssage = o.SexualUssage;
            meta.CommercialUssage = o.CommercialUssage;
            meta.OtherPermissionUrl = o.OtherPermissionUrl;
            meta.LicenseType = o.LicenseType;
            meta.OtherLicenseUrl = o.OtherLicenseUrl;
            AssetDatabase.CreateAsset(meta, $"{folder}/Meta.asset");
            copy.AddComponent<VRMMeta>().Meta = meta;

            // 9. prefab (for manual tweaks + re-export through UniVRM's own exporter)
            AssetDatabase.SaveAssets();
            result.PrefabPath = $"{folder}/{copy.name}_Setup.prefab";
            PrefabUtility.SaveAsPrefabAsset(copy, result.PrefabPath);

            // 10. validate + export
            foreach (var v in VRMExporterValidator.Validate(copy).Concat(meta.Validate(copy)))
                log.Add($"[Validate:{v.ErrorLevel}] {v.Message}");
            var settings = ScriptableObject.CreateInstance<VRMExportSettings>();
            settings.ForceTPose = o.ForceTPose;
            settings.PoseFreeze = true;
            settings.FreezeMeshUseCurrentBlendShapeWeight = true;
            settings.ReduceBlendshape = o.ReduceBlendshape;
            settings.UseSparseAccessor = o.UseSparseAccessor;
            settings.ReduceBlendshapeClip = false;
            // UniGLTF switches every exported texture asset to "Uncompressed" and reimports it;
            // put the user's own textures back so exporting never changes their VRChat avatar.
            var importers = CaptureTextureCompression(copy, meta, folder);
            byte[] bytes;
            try { bytes = VRMEditorExporter.Export(copy, meta, settings); }
            finally
            {
                RestoreTextureCompression(importers, log);
                Object.DestroyImmediate(settings);
            }
            result.VrmPath = $"{folder}/{copy.name}.vrm";
            File.WriteAllBytes(result.VrmPath, bytes);
            result.Success = true;
            log.Add($"[Export] {result.VrmPath} ({bytes.Length / 1024:N0} KB)");
        }

        static void RemoveInactive(GameObject root, Animator animator, List<string> log)
        {
            var visibleBones = new HashSet<Transform>();
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!smr.gameObject.activeInHierarchy || !smr.enabled) continue;
                visibleBones.Add(smr.transform); // keeps the mesh's own parents even if they hold hidden renderers
                foreach (var b in smr.bones) if (b != null) visibleBones.Add(b);
                if (smr.rootBone != null) visibleBones.Add(smr.rootBone);
            }
            foreach (var mr in root.GetComponentsInChildren<MeshRenderer>(true))
                if (mr.gameObject.activeInHierarchy && mr.enabled) visibleBones.Add(mr.transform);

            // springs that only move bones of hidden meshes are useless in VRM
            int prunedSprings = 0;
            foreach (var sb in root.GetComponentsInChildren<VRMSpringBone>(true))
            {
                sb.RootBones = sb.RootBones.Where(r => r != null && visibleBones.Any(b => b == r || b.IsChildOf(r))).ToList();
                if (sb.RootBones.Count == 0) { Object.DestroyImmediate(sb); prunedSprings++; }
            }

            var used = new HashSet<Transform>(visibleBones);
            foreach (HumanBodyBones hb in Enum.GetValues(typeof(HumanBodyBones)))
            {
                if (hb == HumanBodyBones.LastBone) continue;
                var t = animator.GetBoneTransform(hb);
                if (t != null) used.Add(t);
            }
            foreach (var sb in root.GetComponentsInChildren<VRMSpringBone>(true))
            {
                foreach (var r in sb.RootBones) used.Add(r);
                foreach (var g in sb.ColliderGroups ?? new VRMSpringBoneColliderGroup[0]) if (g != null) used.Add(g.transform);
            }
            bool Needed(Transform t) => used.Any(u => u == t || u.IsChildOf(t));

            int removedRenderers = 0, removedObjects = 0;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue; // already destroyed together with a hidden parent
                if (r.gameObject.activeInHierarchy && r.enabled) continue;
                removedRenderers++;
                if (!Needed(r.transform) && r.transform != root.transform) Object.DestroyImmediate(r.gameObject);
                else Object.DestroyImmediate(r);
            }
            foreach (var t in root.GetComponentsInChildren<Transform>(true).Reverse())
            {
                if (t == null || t.gameObject.activeSelf) continue;
                if (Needed(t)) t.gameObject.SetActive(true);
                else { Object.DestroyImmediate(t.gameObject); removedObjects++; }
            }
            foreach (var sb in root.GetComponentsInChildren<VRMSpringBone>(true))
                sb.ColliderGroups = sb.ColliderGroups.Where(g => g != null).ToArray();
            log.Add(L.Tr("conv.007", removedRenderers, removedObjects, prunedSprings));
        }

        /// <summary>
        /// VRM keeps one material per submesh: extra slots (multi-pass tricks such as lilToon FakeShadow)
        /// would only add unused materials, and a mesh drawn purely with FakeShadow would become an opaque blob.
        /// </summary>
        static void TrimMaterialSlots(GameObject root, List<string> log)
        {
            int extra = 0, fakeOnly = 0, noMaterial = 0;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                r.TryGetComponent<MeshFilter>(out var meshFilter);
                var mesh = r is SkinnedMeshRenderer smr ? smr.sharedMesh : meshFilter != null ? meshFilter.sharedMesh : null;
                if (mesh == null) continue;
                var mats = r.sharedMaterials;
                if (mats.All(m => m == null))
                {
                    // no material = invisible in Unity (e.g. gimmick colliders); exporting it only produces a broken mesh
                    Object.DestroyImmediate(r);
                    if (meshFilter != null) Object.DestroyImmediate(meshFilter);
                    noMaterial++;
                    continue;
                }
                if (mats.Length > mesh.subMeshCount)
                {
                    extra += mats.Length - mesh.subMeshCount;
                    mats = mats.Take(mesh.subMeshCount).ToArray();
                    r.sharedMaterials = mats;
                }
                if (mats.Length > 0 && mats.All(LilToonToMToon.IsFakeShadow))
                {
                    Object.DestroyImmediate(r);
                    if (meshFilter != null) Object.DestroyImmediate(meshFilter);
                    fakeOnly++;
                }
            }
            if (extra > 0) log.Add(L.Warn("conv.008", extra));
            if (fakeOnly > 0) log.Add(L.Warn("conv.009", fakeOnly));
            if (noMaterial > 0) log.Add(L.Tr("conv.010", noMaterial));
        }

        static void StripComponents(GameObject root, Animator rootAnimator, List<string> log)
        {
            var removed = new Dictionary<string, int>();
            foreach (var go in root.GetComponentsInChildren<Transform>(true).Select(t => t.gameObject).ToList())
            {
                GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);
                // reverse order: components added later usually depend on earlier ones
                for (int pass = 0; pass < 2; pass++)
                foreach (var c in go.GetComponents<Component>().Reverse())
                {
                    if (c == null || c is Transform || c == rootAnimator) continue;
                    if (c is SkinnedMeshRenderer || c is MeshRenderer || c is MeshFilter) continue;
                    if (c.GetType().Namespace == "VRM") continue;
                    var key = c.GetType().Name;
                    Object.DestroyImmediate(c);
                    if (c == null) removed[key] = removed.TryGetValue(key, out var n) ? n + 1 : 1; // alive = another component depends on it
                }
            }
            if (removed.Count > 0)
                log.Add(L.Tr("conv.011") + string.Join(", ", removed.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}×{kv.Value}")));
        }

        static void MakeNamesUnique(GameObject root, Animator animator)
        {
            var human = new HashSet<Transform>(Enum.GetValues(typeof(HumanBodyBones)).Cast<HumanBodyBones>()
                .Where(b => b != HumanBodyBones.LastBone).Select(animator.GetBoneTransform).Where(t => t != null));
            var names = new HashSet<string>(human.Select(t => t.name));
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == root.transform || human.Contains(t)) continue;
                var n = t.name;
                for (int i = 2; names.Contains(n); i++) n = $"{t.name}_{i}";
                t.name = n;
                names.Add(n);
            }
        }

        const string MarkerFile = ".vrctovrm"; // dot-files are ignored by Unity (no .meta); marks folders this tool owns

        /// <summary>
        /// Creates a clean output folder. An existing folder is only replaced when this tool created it,
        /// so a mistyped output path can never delete the user's own assets.
        /// </summary>
        static bool PrepareOutputFolder(string folder, GameObject source, List<string> log)
        {
            if (!folder.StartsWith("Assets/"))
            {
                log.Add(L.Tr("conv.012", folder));
                return false;
            }
            var sourcePath = AssetDatabase.GetAssetPath(source);
            if (string.IsNullOrEmpty(sourcePath)) sourcePath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(source); // scene instance
            if (!string.IsNullOrEmpty(sourcePath) && sourcePath.StartsWith(folder + "/"))
            {
                log.Add(L.Tr("conv.013", folder, sourcePath));
                return false;
            }
            if (AssetDatabase.IsValidFolder(folder))
            {
                bool ours = File.Exists(Path.Combine(folder, MarkerFile))
                            // folders written by versions before the marker existed
                            || (Directory.GetFiles(folder, "*_Setup.prefab").Length > 0 && File.Exists(Path.Combine(folder, "Meta.asset")));
                bool empty = !Directory.EnumerateFileSystemEntries(folder).Any();
                if (!ours && !empty)
                {
                    log.Add(L.Tr("conv.014", folder));
                    return false;
                }
                // start clean so re-runs don't pile up *_1 assets; trash rather than delete so it can be recovered
                if (ours && !AssetDatabase.MoveAssetToTrash(folder)) AssetDatabase.DeleteAsset(folder);
            }
            EnsureFolder(folder);
            File.WriteAllText(Path.Combine(folder, MarkerFile), "Generated by VrcToVrm. This folder is replaced on every conversion.");
            EnsureFolder(folder + "/Materials");
            EnsureFolder(folder + "/Textures");
            EnsureFolder(folder + "/BlendShapes");
            return true;
        }

        /// <summary>Remembers the compression of every texture asset the export will read, except our own outputs.</summary>
        static Dictionary<string, TextureImporterCompression> CaptureTextureCompression(GameObject root, VRMMetaObject meta, string folder)
        {
            var textures = new HashSet<Texture>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    foreach (var prop in m.GetTexturePropertyNames())
                    {
                        var t = m.GetTexture(prop);
                        if (t != null) textures.Add(t);
                    }
                }
            if (meta.Thumbnail != null) textures.Add(meta.Thumbnail);

            var result = new Dictionary<string, TextureImporterCompression>();
            foreach (var t in textures)
            {
                var path = AssetDatabase.GetAssetPath(t);
                if (string.IsNullOrEmpty(path) || path.StartsWith(folder + "/")) continue;
                if (AssetImporter.GetAtPath(path) is TextureImporter ti) result[path] = ti.textureCompression;
            }
            return result;
        }

        static void RestoreTextureCompression(Dictionary<string, TextureImporterCompression> before, List<string> log)
        {
            int restored = 0;
            foreach (var kv in before)
            {
                if (!(AssetImporter.GetAtPath(kv.Key) is TextureImporter ti) || ti.textureCompression == kv.Value) continue;
                ti.textureCompression = kv.Value;
                ti.SaveAndReimport();
                restored++;
            }
            if (restored > 0) log.Add(L.Tr("conv.015", restored));
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
