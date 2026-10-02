using System;
using System.Collections.Generic;
using System.Linq;
using VRM;

namespace VrcToVrm
{
    /// <summary>Name-based guesses for blink shapes and VRM emotion presets.</summary>
    public static class ShapeNameHeuristics
    {
        static readonly string[] BlinkWords = { "blink", "brink", "まばたき", "瞬き", "eye_close", "eyeclose", "eyes_closed", "目閉じ", "close_eye" };
        static readonly string[] WinkWords = { "wink", "ウインク", "ウィンク", "ｳｨﾝｸ", "ｳｲﾝｸ", "blink", "brink", "eye_close", "eyeclose", "まばたき" };
        static readonly string[] LeftMarks = { "_l", ".l", "-l", " l", "left", "左" };
        static readonly string[] RightMarks = { "_r", ".r", "-r", " r", "right", "右" };

        // Keywords per emotion; clip/state names weigh more than blendshape names.
        public static readonly Dictionary<BlendShapePreset, string[]> EmotionWords = new Dictionary<BlendShapePreset, string[]>
        {
            { BlendShapePreset.Joy,    new[] { "joy", "smile", "happy", "laugh", "egao", "笑", "喜", "にこ", "ニコ", "嬉" } },
            { BlendShapePreset.Angry,  new[] { "angry", "anger", "ikari", "okori", "怒", "jito", "ジト", "tsuri", "doya", "ドヤ" } },
            { BlendShapePreset.Sorrow, new[] { "sorrow", "sad", "cry", "tear", "namida", "悲", "泣", "困", "komaru", "uruuru", "うるうる" } },
            { BlendShapePreset.Fun,    new[] { "fun", "relax", "tanoshi", "楽", "ω", "wink", "ウインク", "smile", "にやり" } },
        };

        static bool HasAny(string s, IEnumerable<string> words) => words.Any(w => s.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
        static bool EndsWithMark(string s, IEnumerable<string> marks) => marks.Any(m => s.EndsWith(m, StringComparison.OrdinalIgnoreCase) || s.Contains(m + "_") || s.Contains(m + "."));
        static bool IsLeft(string s) => EndsWithMark(s, LeftMarks.Where(m => m != "左")) || s.Contains("左");
        static bool IsRight(string s) => EndsWithMark(s, RightMarks.Where(m => m != "右")) || s.Contains("右");

        // Common lip-sync blendshape names, most specific first (VRChat, MMD, VRoid, generic).
        static readonly Dictionary<BlendShapePreset, string[]> VowelNames = new Dictionary<BlendShapePreset, string[]>
        {
            { BlendShapePreset.A, new[] { "vrc.v_aa", "vrc.v.aa", "vrc_v_aa", "v_aa", "vrc_fcl_lipsynch_a", "fcl_mth_a", "mth_a", "mouth_a", "mouth_a_1", "あ", "a" } },
            { BlendShapePreset.I, new[] { "vrc.v_ih", "vrc.v.ih", "vrc_v_ih", "v_ih", "vrc_fcl_lipsynch_i", "fcl_mth_i", "mth_i", "mouth_i", "mouth_i_1", "い", "i" } },
            { BlendShapePreset.U, new[] { "vrc.v_ou", "vrc.v.ou", "vrc_v_ou", "v_ou", "vrc_fcl_lipsynch_u", "fcl_mth_u", "mth_u", "mouth_u", "mouth_u_1", "う", "u" } },
            { BlendShapePreset.E, new[] { "vrc.v_e", "vrc.v.e", "vrc.v_ee", "vrc_v_e", "v_e", "vrc_fcl_lipsynch_e", "fcl_mth_e", "mth_e", "mouth_e", "mouth_e_1", "え", "e" } },
            { BlendShapePreset.O, new[] { "vrc.v_oh", "vrc.v.oh", "vrc_v_oh", "v_oh", "vrc_fcl_lipsynch_o", "fcl_mth_o", "mth_o", "mouth_o", "mouth_o_1", "お", "o" } },
        };

        /// <summary>Exact (case-insensitive) match against well-known vowel shape names.</summary>
        public static string FindVowel(IList<string> names, BlendShapePreset vowel)
        {
            if (!VowelNames.TryGetValue(vowel, out var candidates)) return null;
            foreach (var c in candidates)
            {
                var hit = names.FirstOrDefault(n => string.Equals(n.Trim(), c, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit;
            }
            return null;
        }

        public static string FindBlink(IList<string> names)
        {
            // prefer both-eye shapes without side markers, then shortest name
            return names.Where(n => HasAny(n, BlinkWords) && !IsLeft(n) && !IsRight(n))
                .OrderBy(n => n.StartsWith("vrc.", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(n => n.Length).FirstOrDefault();
        }

        // (right marker, left marker); "右" -> "" covers ウインク / ウインク右 where the left side has no marker
        static readonly (string r, string l)[] SidePairs =
        {
            ("_R", "_L"), ("_r", "_l"), (".R", ".L"), (".r", ".l"), ("_right", "_left"), ("_Right", "_Left"),
            ("right", "left"), ("Right", "Left"), ("右", "左"), ("右", ""),
        };

        /// <summary>Finds a left/right pair whose names differ only by a side marker.</summary>
        public static void FindWinks(IList<string> names, string blink, out string left, out string right)
        {
            left = right = null;
            var set = new HashSet<string>(names);
            var pairs = new List<(string l, string r, string stem)>();
            foreach (var r in names.Where(n => n != blink && HasAny(n, WinkWords)))
                foreach (var (rm, lm) in SidePairs)
                {
                    var i = r.LastIndexOf(rm, StringComparison.Ordinal);
                    if (i < 0) continue;
                    var l = r.Substring(0, i) + lm + r.Substring(i + rm.Length);
                    if (l != r && set.Contains(l)) { pairs.Add((l, r, r.Remove(i, rm.Length))); break; }
                }
            // prefer real blink shapes over "wink" expressions, then plain names over vrc.* helpers, then short stems
            var best = pairs.OrderBy(p => HasAny(p.stem, BlinkWords) ? 0 : 1)
                .ThenBy(p => p.stem.StartsWith("vrc.", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(p => p.stem.Length).FirstOrDefault();
            if (best.r == null) return;
            left = best.l;
            right = best.r;
        }

        public static float EmotionScore(ExpressionCandidate c, BlendShapePreset preset)
        {
            var words = EmotionWords[preset];
            float score = 0;
            if (HasAny(c.Name, words)) score += 3;
            foreach (var src in c.Sources) if (HasAny(src, words)) { score += 1; break; }
            foreach (var s in c.Shapes)
                if (HasAny(s.ShapeName, words)) score += s.Weight / 100f;
            return score;
        }

        public static bool LooksLikeBlinkOnly(ExpressionCandidate c) =>
            c.Shapes.Count > 0 && c.Shapes.All(s => HasAny(s.ShapeName, BlinkWords));

        /// <summary>Greedy one-to-one assignment of FX clips to Joy/Angry/Sorrow/Fun.</summary>
        public static Dictionary<BlendShapePreset, ExpressionCandidate> GuessEmotions(IList<ExpressionCandidate> candidates)
        {
            var pool = candidates.Where(c => !LooksLikeBlinkOnly(c)).ToList();
            var pairs = from p in EmotionWords.Keys
                        from c in pool
                        let s = EmotionScore(c, p) + (c.IsGesture ? 0.25f : 0)
                        where s >= 1f
                        orderby s descending
                        select (p, c);
            var result = new Dictionary<BlendShapePreset, ExpressionCandidate>();
            var used = new HashSet<ExpressionCandidate>();
            foreach (var (p, c) in pairs)
            {
                if (result.ContainsKey(p) || used.Contains(c)) continue;
                result[p] = c;
                used.Add(c);
            }
            return result;
        }
    }
}
