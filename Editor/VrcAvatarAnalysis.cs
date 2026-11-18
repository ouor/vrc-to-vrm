using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRM;

namespace VrcToVrm
{
    /// <summary>One blendshape weight driven by an FX animation clip.</summary>
    public struct ShapeValue
    {
        public string RelativePath;
        public string ShapeName;
        public float Weight;
    }

    /// <summary>An FX animation clip that can become a VRM expression.</summary>
    public class ExpressionCandidate
    {
        public string Name;
        public AnimationClip Clip;
        public List<string> Sources = new List<string>();   // e.g. "Left Hand / Open (GestureLeft=2)"
        public bool IsGesture;
        public bool TouchesFace;
        public List<ShapeValue> Shapes = new List<ShapeValue>();

        // '/' would turn into nested submenus in EditorGUILayout.Popup
        string SafeName => Name.Replace('/', '\u2215');

        public string Label => $"{SafeName}  [{string.Join(", ", Sources.Take(2))}{(Sources.Count > 2 ? " …" : "")}]";

        /// <summary>Compact label: clip name + hand gesture(s) or "toggle".</summary>
        public string ShortLabel
        {
            get
            {
                var gestures = Sources.Select(s => System.Text.RegularExpressions.Regex.Match(s, @"=(\w+)\)$"))
                    .Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct().ToList();
                return gestures.Count > 0 ? $"{SafeName}  ({string.Join(" · ", gestures)})" : L.Tr("analysis.001", SafeName);
            }
        }
    }

    /// <summary>Read-only snapshot of everything VRM needs from a VRChat avatar.</summary>
    public class VrcAvatarAnalysis
    {
        public static readonly string[] GestureNames = { "Neutral", "Fist", "Open", "Point", "Peace", "RockNRoll", "Gun", "ThumbsUp" };

        public GameObject Root;
        public VRCAvatarDescriptor Descriptor;
        public Animator Animator;

        public Vector3 ViewPosition;
        public SkinnedMeshRenderer VisemeMesh;
        public string[] VisemeShapes = new string[0];

        /// <summary>A/I/U/E/O -> blendshapes (paths relative to Root). Several shapes per vowel when the FX mixes them.</summary>
        public Dictionary<BlendShapePreset, List<ShapeValue>> Vowels = new Dictionary<BlendShapePreset, List<ShapeValue>>();
        public string LipSyncSource = L.Tr("analysis.002");

        // VRChat viseme index of each VRM vowel (sil PP FF TH DD kk CH SS nn RR aa E ih oh ou)
        public static readonly (BlendShapePreset preset, int viseme)[] VowelVisemes =
        {
            (BlendShapePreset.A, 10), (BlendShapePreset.I, 12), (BlendShapePreset.U, 14),
            (BlendShapePreset.E, 11), (BlendShapePreset.O, 13),
        };
        readonly Dictionary<int, List<ShapeValue>> _fxVisemes = new Dictionary<int, List<ShapeValue>>();

        public SkinnedMeshRenderer EyelidMesh;
        public string BlinkShape, LookUpShape, LookDownShape;
        public string BlinkLeftShape, BlinkRightShape;

        public Transform LeftEye, RightEye;
        public float EyeUpDeg = 10, EyeDownDeg = 10, EyeOuterDeg = 10, EyeInnerDeg = 10;

        public RuntimeAnimatorController Fx;
        public List<ExpressionCandidate> Candidates = new List<ExpressionCandidate>();
        public List<ClothingClip> ClothingClips = new List<ClothingClip>();
        public List<ClothingGroup> ClothingGroups = new List<ClothingGroup>();
        public List<string> Warnings = new List<string>();
        /// <summary>Informational messages (nothing for the user to fix).</summary>
        public List<string> Notes = new List<string>();

        public int PhysBoneCount, ColliderCount, RendererCount, InactiveRendererCount;

        public static VrcAvatarAnalysis Analyze(GameObject root)
        {
            var a = new VrcAvatarAnalysis { Root = root };
            a.Descriptor = root.GetComponent<VRCAvatarDescriptor>();
            if (a.Descriptor == null)
            {
                // some prefabs wrap the avatar in an extra parent (e.g. Telmy/Telmy01): the descriptor object is the avatar
                var nested = root.GetComponentInChildren<VRCAvatarDescriptor>(true);
                if (nested != null)
                {
                    a.Descriptor = nested;
                    a.Root = root = nested.gameObject;
                    a.Notes.Add(L.Tr("analysis.003", root.name));
                }
            }
            a.Animator = root.GetComponent<Animator>();
            if (a.Descriptor == null) a.Warnings.Add(L.Tr("analysis.004"));
            if (a.Animator == null || a.Animator.avatar == null || !a.Animator.avatar.isHuman)
                a.Warnings.Add(L.Tr("analysis.005"));

            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                a.RendererCount++;
                if (!IsActive(r.gameObject, root) || !r.enabled) a.InactiveRendererCount++;
            }
            a.PhysBoneCount = root.GetComponentsInChildren<VRC.Dynamics.VRCPhysBoneBase>(true).Length;
            a.ColliderCount = root.GetComponentsInChildren<VRC.Dynamics.VRCPhysBoneColliderBase>(true).Length;

            if (a.Descriptor != null)
            {
                a.ReadDescriptor();
                a.ReadFx();
                a.ResolveLipSync();
            }
            return a;
        }

        /// <summary>activeInHierarchy that also works for prefab assets (which are never "in hierarchy").</summary>
        public static bool IsActive(GameObject go, GameObject root)
        {
            for (var t = go.transform; t != null; t = t == root.transform ? null : t.parent)
                if (!t.gameObject.activeSelf) return false;
            return true;
        }

        void ReadDescriptor()
        {
            var d = Descriptor;
            ViewPosition = d.ViewPosition;

            // lipSync: 2 = JawFlapBlendShape, 3 = VisemeBlendShape, 4 = VisemeParameterOnly (FX drives the mouth)
            VisemeMesh = d.VisemeSkinnedMesh;
            int mode = (int)d.lipSync;
            if (mode == 3 && VisemeMesh != null && d.VisemeBlendShapes != null && d.VisemeBlendShapes.Length >= 15)
            {
                VisemeShapes = d.VisemeBlendShapes;
                var path = PathOf(VisemeMesh.transform);
                foreach (var (preset, v) in VowelVisemes)
                    if (!string.IsNullOrEmpty(VisemeShapes[v]))
                        Vowels[preset] = new List<ShapeValue> { new ShapeValue { RelativePath = path, ShapeName = VisemeShapes[v], Weight = 100 } };
                LipSyncSource = L.Tr("analysis.006");
            }
            else if (mode == 2 && VisemeMesh != null && !string.IsNullOrEmpty(d.MouthOpenBlendShapeName))
            {
                // jaw flap only opens the mouth with volume: no vowels, so it becomes A
                Vowels[BlendShapePreset.A] = new List<ShapeValue> { new ShapeValue { RelativePath = PathOf(VisemeMesh.transform), ShapeName = d.MouthOpenBlendShapeName, Weight = 100 } };
                LipSyncSource = L.Tr("analysis.007");
            }

            var eye = d.customEyeLookSettings;
            LeftEye = eye.leftEye;
            RightEye = eye.rightEye;
            if (Animator != null && Animator.isHuman)
            {
                if (LeftEye == null) LeftEye = Animator.GetBoneTransform(HumanBodyBones.LeftEye);
                if (RightEye == null) RightEye = Animator.GetBoneTransform(HumanBodyBones.RightEye);
            }
            if (d.enableEyeLook)
            {
                EyeUpDeg = AngleOrDefault(eye.eyesLookingUp?.left, 10);
                EyeDownDeg = AngleOrDefault(eye.eyesLookingDown?.left, 10);
                // looking left: left eye moves outward, right eye inward
                EyeOuterDeg = AngleOrDefault(eye.eyesLookingLeft?.left, 10);
                EyeInnerDeg = AngleOrDefault(eye.eyesLookingLeft?.right, EyeOuterDeg);
            }

            // eyelidType 2 = Blendshapes; indices are [blink, lookingUp, lookingDown]
            if ((int)eye.eyelidType == 2 && eye.eyelidsSkinnedMesh != null && eye.eyelidsSkinnedMesh.sharedMesh != null)
            {
                EyelidMesh = eye.eyelidsSkinnedMesh;
                var mesh = EyelidMesh.sharedMesh;
                var idx = eye.eyelidsBlendshapes ?? new int[0];
                string ShapeAt(int i) => i < idx.Length && idx[i] >= 0 && idx[i] < mesh.blendShapeCount ? mesh.GetBlendShapeName(idx[i]) : null;
                BlinkShape = ShapeAt(0);
                LookUpShape = ShapeAt(1);
                LookDownShape = ShapeAt(2);
            }

            var faceMesh = EyelidMesh != null ? EyelidMesh : VisemeMesh;
            if (faceMesh == null)
            {
                // descriptor names no face mesh (e.g. lip sync by FX, no eyelids): the mesh with the most blendshapes is the face
                faceMesh = Root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                    .Where(r => r.sharedMesh != null && r.sharedMesh.blendShapeCount > 0)
                    .OrderByDescending(r => r.sharedMesh.blendShapeCount).FirstOrDefault();
                if (faceMesh != null) Notes.Add(L.Tr("analysis.008", faceMesh.name));
            }
            if (faceMesh != null && faceMesh.sharedMesh != null)
            {
                if (EyelidMesh == null) EyelidMesh = faceMesh;
                var names = Enumerable.Range(0, faceMesh.sharedMesh.blendShapeCount).Select(faceMesh.sharedMesh.GetBlendShapeName).ToList();
                if (BlinkShape == null) BlinkShape = ShapeNameHeuristics.FindBlink(names);
                ShapeNameHeuristics.FindWinks(names, BlinkShape, out BlinkLeftShape, out BlinkRightShape);
            }
            if (BlinkShape == null) Warnings.Add(L.Tr("analysis.009"));
        }

        static float AngleOrDefault(Quaternion? q, float fallback)
        {
            if (q == null) return fallback;
            var v = q.Value;
            if (v.x == 0 && v.y == 0 && v.z == 0 && v.w == 0) return fallback;
            var deg = Quaternion.Angle(Quaternion.identity, v);
            return deg < 1f ? fallback : deg;
        }

        void ReadFx()
        {
            // VRChat ignores the layer list unless "Customize Animation Layers" is on
            var layers = Descriptor.customizeAnimationLayers ? Descriptor.baseAnimationLayers ?? new VRCAvatarDescriptor.CustomAnimLayer[0] : new VRCAvatarDescriptor.CustomAnimLayer[0];
            var fxLayer = layers.FirstOrDefault(l => (int)l.type == 5); // AnimLayerType.FX
            Fx = fxLayer.isDefault ? null : fxLayer.animatorController;
            if (Fx == null)
            {
                Warnings.Add(L.Tr("analysis.010"));
                return;
            }

            // unwrap (possibly nested) override controllers; the outermost override wins
            var overrides = new Dictionary<AnimationClip, AnimationClip>();
            var rac = Fx;
            while (rac is AnimatorOverrideController aoc)
            {
                var list = new List<KeyValuePair<AnimationClip, AnimationClip>>();
                aoc.GetOverrides(list);
                foreach (var kv in list)
                    if (kv.Key != null && kv.Value != null && !overrides.ContainsKey(kv.Key)) overrides[kv.Key] = kv.Value;
                rac = aoc.runtimeAnimatorController;
            }
            var controller = rac as AnimatorController;
            if (controller == null)
            {
                Warnings.Add(L.Tr("analysis.011", Fx.name));
                return;
            }

            var faceRoots = new HashSet<string>();
            foreach (var smr in new[] { VisemeMesh, EyelidMesh })
                if (smr != null) faceRoots.Add(AnimationUtility.CalculateTransformPath(smr.transform, Root.transform));

            var byClip = new Dictionary<AnimationClip, ExpressionCandidate>();
            foreach (var layer in controller.layers)
            {
                foreach (var (state, machine) in AllStates(layer.stateMachine))
                {
                    var clip = PickClip(state.motion);
                    if (clip == null) continue;
                    if (overrides.TryGetValue(clip, out var o)) clip = o;

                    // lip-sync driven by the Viseme parameter (VisemeParameterOnly avatars): these are mouth shapes, not expressions
                    var viseme = ParameterEquals(state, layer.stateMachine, "Viseme");
                    if (viseme != null)
                    {
                        if (!_fxVisemes.ContainsKey(viseme.Value)) _fxVisemes[viseme.Value] = ReadShapes(clip);
                        continue;
                    }

                    var gesture = GestureCondition(state, layer.stateMachine);
                    if (!byClip.TryGetValue(clip, out var cand))
                    {
                        cand = new ExpressionCandidate { Name = clip.name, Clip = clip, Shapes = ReadShapes(clip) };
                        if (cand.Shapes.Count == 0) continue;
                        cand.TouchesFace = cand.Shapes.Any(s => faceRoots.Contains(s.RelativePath));
                        byClip[clip] = cand;
                    }
                    cand.IsGesture |= gesture != null;
                    cand.Sources.Add($"{layer.name} / {state.name}{(gesture != null ? $" ({gesture})" : "")}");
                }
            }
            Candidates = byClip.Values
                .Where(c => c.TouchesFace)
                .OrderByDescending(c => c.IsGesture)
                .ThenBy(c => c.Name)
                .ToList();
            // clothing variants: every FX clip that toggles objects, swaps material properties or morphs a
            // non-face mesh. Face candidates keep their existing meaning; a clip may be both.
            var clipSources = new Dictionary<AnimationClip, List<string>>();
            AddSources(scc, "", clipSources);
            AddSources(fx?.gesture, "Gesture", clipSources);
            AddSources(fx?.action, "Action", clipSources);
            AddSources(fx?.sitting, "Sitting", clipSources);
            var clothingList = new List<ClothingClip>();
            foreach (var kv in byClip)
            {
                var clip = kv.Key;
                var clothingCurves = ClothingVariants.ReadCurves(clip);
                if (clothingCurves.Count == 0) continue;
                bool onlyFace = clothingCurves.All(c => c.Kind == "BlendShape") && (faceRoots.Count == 0 || clothingCurves.All(c => faceRoots.Contains(c.RelativePath)));
                if (onlyFace) continue;
                var cc = new ClothingClip { Name = clip.name, Clip = clip, Curves = clothingCurves };
                cc.Category = ClothingVariants.GuessCategory(clip.name, clothingCurves);
                cc.Sources = clipSources.TryGetValue(clip, out var src2) ? src2 : new List<string>();
                clothingList.Add(cc);
            }
            ClothingClips = clothingList.OrderBy(c => c.Name, System.StringComparer.OrdinalIgnoreCase).ToList();
            ClothingGroups = ClothingVariants.GroupClips(ClothingClips);
            if (ClothingClips.Count > 0) Notes.Add(L.Tr("analysis.016", ClothingClips.Count, ClothingGroups.Count));

        }

        /// <summary>
        /// Fills A/I/U/E/O when the descriptor did not: FX states gated by the Viseme parameter first,
        /// then well-known blendshape names on the face mesh. Finally drops lip-sync-only clips from the expression candidates.
        /// </summary>
        void ResolveLipSync()
        {
            if (Vowels.Count == 0 && _fxVisemes.Count > 0)
            {
                foreach (var (preset, v) in VowelVisemes)
                    if (_fxVisemes.TryGetValue(v, out var shapes) && shapes.Count > 0) Vowels[preset] = shapes;
                if (Vowels.Count > 0) LipSyncSource = L.Tr("analysis.012");
            }

            if (Vowels.Count == 0)
            {
                // largest-shape-count meshes first: that is nearly always the face
                var meshes = new[] { VisemeMesh, EyelidMesh }
                    .Concat(Root.GetComponentsInChildren<SkinnedMeshRenderer>(true).OrderByDescending(r => r.sharedMesh ? r.sharedMesh.blendShapeCount : 0))
                    .Where(r => r != null && r.sharedMesh != null && r.sharedMesh.blendShapeCount > 0)
                    .Distinct().Take(4);
                foreach (var smr in meshes)
                {
                    var names = Enumerable.Range(0, smr.sharedMesh.blendShapeCount).Select(smr.sharedMesh.GetBlendShapeName).ToList();
                    foreach (var (preset, _) in VowelVisemes)
                    {
                        if (Vowels.ContainsKey(preset)) continue;
                        var shape = ShapeNameHeuristics.FindVowel(names, preset);
                        if (shape != null)
                            Vowels[preset] = new List<ShapeValue> { new ShapeValue { RelativePath = PathOf(smr.transform), ShapeName = shape, Weight = 100 } };
                    }
                    if (Vowels.Count > 0) { LipSyncSource = L.Tr("analysis.013", smr.name); break; }
                }
            }

            if (Vowels.Count == 0) Warnings.Add(L.Tr("analysis.014"));
            else if (Vowels.Count < 5) Warnings.Add(L.Tr("analysis.015", string.Join(", ", Vowels.Keys), LipSyncSource));

            // lip-sync clips that only drive vowel shapes are already covered by A/I/U/E/O
            var vowelShapes = new HashSet<string>(
                Vowels.Values.SelectMany(v => v).Select(s => s.ShapeName).Concat(VisemeShapes.Where(v => !string.IsNullOrEmpty(v))),
                System.StringComparer.OrdinalIgnoreCase);
            Candidates = Candidates.Where(c => !c.Shapes.All(s => vowelShapes.Contains(s.ShapeName))).ToList();
        }

        string PathOf(Transform t) => AnimationUtility.CalculateTransformPath(t, Root.transform);

        /// <summary>Returns N when a transition into the state requires "parameter == N".</summary>
        static int? ParameterEquals(AnimatorState state, AnimatorStateMachine layerRoot, string parameter)
        {
            foreach (var t in IncomingTransitions(state, layerRoot))
                foreach (var c in t.conditions)
                    if (c.parameter == parameter && c.mode == AnimatorConditionMode.Equals)
                        return (int)c.threshold;
            return null;
        }

        /// <summary>
        /// Every transition in the layer that can enter the state: Any State, state-to-state, Entry,
        /// and transitions into a sub-state machine whose default state it is.
        /// </summary>
        static IEnumerable<AnimatorTransitionBase> IncomingTransitions(AnimatorState state, AnimatorStateMachine layerRoot)
        {
            var machines = AllMachines(layerRoot).ToList();
            var enteredVia = new HashSet<AnimatorStateMachine>(machines.Where(m => m.defaultState == state));
            bool Into(AnimatorTransitionBase t) =>
                t != null && (t.destinationState == state || (t.destinationStateMachine != null && enteredVia.Contains(t.destinationStateMachine)));
            foreach (var m in machines)
            {
                foreach (var t in m.anyStateTransitions) if (Into(t)) yield return t;
                foreach (var t in m.entryTransitions) if (Into(t)) yield return t;
                foreach (var s in m.states)
                    foreach (var t in s.state.transitions) if (Into(t)) yield return t;
                foreach (var child in m.stateMachines)
                    foreach (var t in m.GetStateMachineTransitions(child.stateMachine)) if (Into(t)) yield return t;
            }
        }

        static IEnumerable<AnimatorStateMachine> AllMachines(AnimatorStateMachine sm)
        {
            yield return sm;
            foreach (var child in sm.stateMachines)
                foreach (var x in AllMachines(child.stateMachine)) yield return x;
        }

        static IEnumerable<(AnimatorState, AnimatorStateMachine)> AllStates(AnimatorStateMachine sm)
        {
            foreach (var s in sm.states) yield return (s.state, sm);
            foreach (var child in sm.stateMachines)
                foreach (var x in AllStates(child.stateMachine)) yield return x;
        }

        /// <summary>For gesture-weight blend trees pick the child clip that drives the most shapes.</summary>
        static AnimationClip PickClip(Motion motion)
        {
            if (motion is AnimationClip clip) return clip;
            if (motion is BlendTree tree)
                return tree.children.Select(c => PickClip(c.motion)).Where(c => c != null)
                    .OrderByDescending(c => AnimationUtility.GetCurveBindings(c).Length).FirstOrDefault();
            return null;
        }

        static string GestureCondition(AnimatorState state, AnimatorStateMachine layerRoot)
        {
            foreach (var t in IncomingTransitions(state, layerRoot))
                foreach (var c in t.conditions)
                    if ((c.parameter == "GestureLeft" || c.parameter == "GestureRight") && c.mode == AnimatorConditionMode.Equals)
                    {
                        var i = (int)c.threshold;
                        var g = i >= 0 && i < GestureNames.Length ? GestureNames[i] : i.ToString();
                        return $"{c.parameter}={g}";
                    }
            return null;
        }

        static List<ShapeValue> ReadShapes(AnimationClip clip)
        {
            var result = new List<ShapeValue>();
            foreach (var b in AnimationUtility.GetCurveBindings(clip))
            {
                if (b.type != typeof(SkinnedMeshRenderer) || !b.propertyName.StartsWith("blendShape.")) continue;
                var curve = AnimationUtility.GetEditorCurve(clip, b);
                if (curve == null || curve.length == 0) continue;
                var w = curve.keys[curve.length - 1].value;
                if (Mathf.Abs(w) < 0.01f) continue;
                result.Add(new ShapeValue { RelativePath = b.path, ShapeName = b.propertyName.Substring("blendShape.".Length), Weight = w });
            }
            return result;
        }
    }
}
