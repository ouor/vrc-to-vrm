using System.Collections.Generic;
using UnityEngine;

namespace VrcToVrm
{
    /// <summary>
    /// Temporarily applies blendshape weights to a scene avatar so expressions can be checked before export.
    /// Original weights are remembered and restored on Clear().
    /// </summary>
    public class ExpressionPreview
    {
        readonly Dictionary<(SkinnedMeshRenderer smr, int index), float> _original = new Dictionary<(SkinnedMeshRenderer, int), float>();
        public string Current { get; private set; }

        public void Show(GameObject root, string name, IEnumerable<ShapeValue> shapes)
        {
            Clear();
            if (root == null) return;
            foreach (var s in shapes)
            {
                var smr = root.transform.Find(s.RelativePath)?.GetComponent<SkinnedMeshRenderer>();
                if (smr == null || smr.sharedMesh == null) continue;
                var i = smr.sharedMesh.GetBlendShapeIndex(s.ShapeName);
                if (i < 0) continue;
                if (!_original.ContainsKey((smr, i))) _original[(smr, i)] = smr.GetBlendShapeWeight(i);
                smr.SetBlendShapeWeight(i, s.Weight);
            }
            Current = name;
        }

        public void Clear()
        {
            foreach (var kv in _original)
                if (kv.Key.smr != null) kv.Key.smr.SetBlendShapeWeight(kv.Key.index, kv.Value);
            _original.Clear();
            Current = null;
        }
    }
}
