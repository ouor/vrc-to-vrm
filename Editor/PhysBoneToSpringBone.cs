using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using VRC.Dynamics;
using VRM;

namespace VrcToVrm
{
    [Serializable]
    public class SpringConversionSettings
    {
        [Tooltip("stiffnessForce = base + pull*a + stiffness*b")]
        public float StiffnessBase = 0.5f;
        public float StiffnessFromPull = 2.0f;
        public float StiffnessFromStiffness = 2.0f;
        public float DragAtSpring0 = 0.8f;
        public float DragAtSpring1 = 0.4f;
        public float DragFromImmobile = 0.15f;
        public float MaxDrag = 0.9f;
        public float GravityScale = 1.0f;
        public bool MergeIdentical = true;
        public int MaxSpheresPerCapsule = 6;
        public bool DropArmCollidersOnLimited = true;
    }

    /// <summary>VRCPhysBone / VRCPhysBoneCollider -> VRMSpringBone / VRMSpringBoneColliderGroup (VRM 0.x).</summary>
    public static class PhysBoneToSpringBone
    {
        public static void Convert(GameObject root, SpringConversionSettings s, List<string> log)
        {
            var groups = ConvertColliders(root, s, log);

            var physBones = root.GetComponentsInChildren<VRCPhysBoneBase>(true)
                .Where(pb => pb.enabled && pb.gameObject.activeInHierarchy).ToList();
            var skipped = root.GetComponentsInChildren<VRCPhysBoneBase>(true).Length - physBones.Count;
            if (skipped > 0) log.Add(L.Tr("spring.001", skipped));

            var pbRoots = physBones.Select(RootOf).ToList();
            var entries = new List<(string key, float stiff, float grav, float drag, float radius, VRMSpringBoneColliderGroup[] cols, List<Transform> roots)>();
            bool warnedCurves = false;
            var limited = new List<string>();
            int droppedArm = 0;
            var armBones = ArmBones(root);

            foreach (var pb in physBones)
            {
                var pbRoot = RootOf(pb);
                // bones handled by a nested PhysBone are excluded from the outer chain, like PhysBone does
                var ignore = new HashSet<Transform>((pb.ignoreTransforms ?? new List<Transform>()).Where(t => t != null));
                foreach (var other in pbRoots)
                    if (other != pbRoot && other.IsChildOf(pbRoot)) ignore.Add(other);

                var roots = new List<Transform>();
                bool multiChildIgnore = (int)pb.multiChildType == 0 && pbRoot.childCount > 1;
                if (multiChildIgnore)
                    foreach (Transform c in pbRoot) roots.AddRange(CollectRoots(c, ignore));
                else
                    roots.AddRange(CollectRoots(pbRoot, ignore));
                if (roots.Count == 0) continue;

                var (stiff, drag, grav) = Compute(pb, s);
                float radius = pb.radius;

                if (!warnedCurves && (Has(pb.pullCurve) || Has(pb.springCurve) || Has(pb.radiusCurve) || Has(pb.gravityCurve)))
                {
                    log.Add(L.Warn("spring.002"));
                    warnedCurves = true;
                }
                if ((int)pb.limitType != 0) limited.Add(pbRoot.name);

                var cols = (pb.colliders ?? new List<VRCPhysBoneColliderBase>())
                    .Where(c => c != null && groups.ContainsKey(c)).Select(c => groups[c]).Distinct().ToArray();
                if (s.DropArmCollidersOnLimited && (int)pb.limitType != 0)
                {
                    // with PhysBone limits the arm just passes through the hair; without them it would lift the hair
                    var kept = cols.Where(g => !armBones.Any(b => g.transform == b || g.transform.IsChildOf(b))).ToArray();
                    droppedArm += cols.Length - kept.Length;
                    cols = kept;
                }
                var key = $"{stiff:F3}|{grav:F3}|{drag:F3}|{radius:F3}|{string.Join(",", cols.Select(c => c.GetInstanceID()).OrderBy(i => i))}";
                entries.Add((key, stiff, grav, drag, radius, cols, roots));
            }

            if (droppedArm > 0)
                log.Add(L.Tr("spring.003", droppedArm));
            if (limited.Count > 0)
                log.Add(L.Warn("spring.004", limited.Count, string.Join(", ", limited.Take(8)), (limited.Count > 8 ? " …" : "")));

            var secondary = root.transform.Find("secondary") ?? new GameObject("secondary").transform;
            secondary.SetParent(root.transform, false);

            var grouped = s.MergeIdentical
                ? entries.GroupBy(e => e.key).Select(g => (first: g.First(), roots: g.SelectMany(e => e.roots).Distinct().ToList()))
                : entries.Select(e => (first: e, roots: e.roots));
            int count = 0;
            foreach (var (e, roots) in grouped)
            {
                var sb = secondary.gameObject.AddComponent<VRMSpringBone>();
                sb.m_comment = string.Join(",", roots.Take(3).Select(r => r.name)) + (roots.Count > 3 ? "…" : "");
                sb.m_stiffnessForce = e.stiff;
                sb.m_gravityPower = e.grav;
                sb.m_gravityDir = Vector3.down;
                sb.m_dragForce = e.drag;
                sb.m_hitRadius = e.radius;
                sb.RootBones = roots;
                sb.ColliderGroups = e.cols;
                count++;
            }
            log.Add(L.Tr("spring.005", physBones.Count, count, groups.Values.Distinct().Count()));
        }

        /// <summary>PhysBone parameters -> VRM 0.x (stiffnessForce, dragForce, gravityPower).</summary>
        public static (float stiffness, float drag, float gravity) Compute(VRCPhysBoneBase pb, SpringConversionSettings s)
        {
            bool advanced = (int)pb.integrationType == 1;
            float stiff = s.StiffnessBase + pb.pull * s.StiffnessFromPull + (advanced ? pb.stiffness * s.StiffnessFromStiffness : 0);
            // VRM drag 1 cancels all inertia (no sway), so keep it well below 1
            float drag = Mathf.Lerp(s.DragAtSpring0, s.DragAtSpring1, Mathf.Clamp01(pb.spring))
                         + Mathf.Clamp01(pb.immobile) * s.DragFromImmobile;
            return (stiff, Mathf.Clamp(drag, 0, s.MaxDrag), pb.gravity * s.GravityScale);
        }

        static List<Transform> ArmBones(GameObject root)
        {
            var animator = root.GetComponent<Animator>();
            if (animator == null || !animator.isHuman) return new List<Transform>();
            return new[]
            {
                HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
                HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
            }.Select(animator.GetBoneTransform).Where(t => t != null).ToList();
        }

        static bool Has(AnimationCurve c) => c != null && c.length > 0;

        public static Transform RootOf(VRCPhysBoneBase pb) => pb.rootTransform != null ? pb.rootTransform : pb.transform;

        /// <summary>VRM 0.x has no ignore list: split the chain into sub-roots that avoid ignored subtrees.</summary>
        static IEnumerable<Transform> CollectRoots(Transform t, HashSet<Transform> ignore)
        {
            if (ignore.Contains(t)) yield break;
            if (!ignore.Any(i => i != t && i.IsChildOf(t)))
            {
                yield return t;
                yield break;
            }
            foreach (Transform c in t)
                foreach (var r in CollectRoots(c, ignore)) yield return r;
        }

        static Dictionary<VRCPhysBoneColliderBase, VRMSpringBoneColliderGroup> ConvertColliders(GameObject root, SpringConversionSettings s, List<string> log)
        {
            var map = new Dictionary<VRCPhysBoneColliderBase, VRMSpringBoneColliderGroup>();
            foreach (var c in root.GetComponentsInChildren<VRCPhysBoneColliderBase>(true))
            {
                var target = c.rootTransform != null ? c.rootTransform : c.transform;
                var shape = (int)c.shapeType; // 0 Sphere, 1 Capsule, 2 Plane
                if (shape == 2)
                {
                    log.Add(L.Warn("spring.006", target.name));
                    continue;
                }
                if (c.insideBounds)
                {
                    log.Add(L.Warn("spring.007", target.name));
                    continue;
                }

                var spheres = new List<VRMSpringBoneColliderGroup.SphereCollider>();
                if (shape == 0)
                {
                    spheres.Add(new VRMSpringBoneColliderGroup.SphereCollider { Offset = c.position, Radius = c.radius });
                }
                else
                {
                    // PhysBone capsule: height is the full length along local Y including the end caps
                    var axis = c.rotation * Vector3.up;
                    var half = Mathf.Max(0, c.height * 0.5f - c.radius);
                    int n = half <= 0 ? 1 : Mathf.Clamp(Mathf.CeilToInt(2 * half / Mathf.Max(0.001f, c.radius)) + 1, 2, Mathf.Max(2, s.MaxSpheresPerCapsule));
                    for (int i = 0; i < n; i++)
                    {
                        var t = n == 1 ? 0 : Mathf.Lerp(-half, half, i / (float)(n - 1));
                        spheres.Add(new VRMSpringBoneColliderGroup.SphereCollider { Offset = c.position + axis * t, Radius = c.radius });
                    }
                }
                var group = target.gameObject.AddComponent<VRMSpringBoneColliderGroup>();
                group.Colliders = spheres.ToArray();
                map[c] = group;
            }
            return map;
        }
    }
}
