using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.Dynamics;
using VRM;
using Object = UnityEngine.Object;

namespace VrcToVrm
{
    public class VrcToVrmWindow : EditorWindow
    {
        const string PrefsPrefix = "VrcToVrm.";

        enum Tab { Expression, Spring, Meta, Options, Result }
        static string[] TabLabels => new[] { L.Tr("ui.tab.expression"), L.Tr("ui.tab.spring"), L.Tr("ui.tab.meta"), L.Tr("ui.tab.options"), L.Tr("ui.tab.result") };

        [SerializeField] GameObject _source;
        [SerializeField] Tab _tab;
        [SerializeField] bool _rememberMeta = true;

        // ConversionOptions serializes everything except the candidate references (Emotions/Customs),
        // which are saved by clip name below and re-attached after the post-reload analysis.
        [SerializeField] ConversionOptions _o = new ConversionOptions();
        [SerializeField] bool _hasChoices;
        [SerializeField] bool _initialized;
        [SerializeField] List<string> _savedEmotionPresets = new List<string>();
        [SerializeField] List<string> _savedEmotionClips = new List<string>();
        [SerializeField] List<string> _savedCustoms = new List<string>();

        // UI state worth keeping across reloads
        [SerializeField] bool _showShapeDetails;
        [SerializeField] bool _springAdvanced;
        [SerializeField] bool _springTable;
        [SerializeField] bool _optionsAdvanced;
        [SerializeField] bool _showNotes;
        [SerializeField] bool _showLog;

        VrcAvatarAnalysis _analysis;
        ConversionResult _result;
        string[] _faceShapes = new string[0];
        string _facePath;
        List<VRCPhysBoneBase> _physBones = new List<VRCPhysBoneBase>();
        readonly ExpressionPreview _preview = new ExpressionPreview();
        Vector2 _scroll;
        string _customFilter = "";

        static GUIStyle _chip, _sectionTitle, _rowName, _badge, _numBold, _hint, _bigTitle;

        [MenuItem("Tools/VRC → VRM/Converter", false, 0)]
        static void Open()
        {
            var w = GetWindow<VrcToVrmWindow>();
            w.minSize = new Vector2(420, 480);
        }

        /// <summary>Opens the window with the given avatar selected.</summary>
        public static void ShowFor(GameObject avatar)
        {
            var w = GetWindow<VrcToVrmWindow>();
            w.minSize = new Vector2(420, 480);
            w.SetSource(avatar);
            w.Focus();
        }

        [MenuItem("GameObject/VRC → VRM…", false, 49)]
        [MenuItem("Assets/VRC → VRM…", false, 2000)]
        static void OpenForSelection() => ShowFor(Selection.activeGameObject);

        [MenuItem("GameObject/VRC → VRM…", true)]
        [MenuItem("Assets/VRC → VRM…", true)]
        static bool CanOpenForSelection() =>
            Selection.activeGameObject != null && Selection.activeGameObject.GetComponentInChildren<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>(true) != null;

        void OnEnable()
        {
            titleContent = new GUIContent("VRC → VRM", EditorGUIUtility.IconContent("Avatar Icon").image);
            if (!_initialized) LoadMetaPrefs(); // first open only: later reloads keep the serialized values
            _initialized = true;
            if (_source != null) Analyze(restoreChoices: _hasChoices);
            EditorSceneManager.sceneSaving += OnSceneSaving;
        }

        void OnDisable()
        {
            EditorSceneManager.sceneSaving -= OnSceneSaving;
            _preview.Clear();
            SaveChoices();
            if (_rememberMeta) SaveMetaPrefs();
        }

        // preview weights must never end up saved into the user's scene
        void OnSceneSaving(UnityEngine.SceneManagement.Scene scene, string path) => _preview.Clear();

        void SaveChoices()
        {
            _hasChoices = _analysis != null;
            _savedEmotionPresets.Clear();
            _savedEmotionClips.Clear();
            _savedCustoms.Clear();
            if (!_hasChoices) return;
            foreach (var kv in _o.Emotions)
            {
                _savedEmotionPresets.Add(kv.Key.ToString());
                _savedEmotionClips.Add(kv.Value.Name);
            }
            _savedCustoms.AddRange(_o.Customs.Select(c => c.Name));
        }

        void RestoreChoices()
        {
            var byName = _analysis.Candidates.GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First());
            _o.Emotions = new Dictionary<BlendShapePreset, ExpressionCandidate>();
            for (int i = 0; i < _savedEmotionPresets.Count && i < _savedEmotionClips.Count; i++)
                if (Enum.TryParse(_savedEmotionPresets[i], out BlendShapePreset preset) && byName.TryGetValue(_savedEmotionClips[i], out var c))
                    _o.Emotions[preset] = c;
            _o.Customs = new HashSet<ExpressionCandidate>(_savedCustoms.Where(byName.ContainsKey).Select(n => byName[n]));
        }

        void OnGUI()
        {
            // the scene object (or its avatar root) was deleted or its scene was closed
            if (_analysis != null && (_source == null || _analysis.Root == null))
            {
                _preview.Clear();
                _analysis = null;
                _result = null;
                _source = null;
            }
            InitStyles();
            // translated labels vary a lot in length; give the label column a share of the window instead of a fixed width
            EditorGUIUtility.labelWidth = Mathf.Max(150, position.width * 0.4f);
            DrawHeader();
            if (_analysis == null)
            {
                EditorGUILayout.HelpBox(L.Tr("ui.001"), MessageType.Info);
                return;
            }

            EditorGUILayout.Space(2);
            var labels = TabLabels.Select((t, i) => i == (int)Tab.Result && _result != null ? (_result.Success ? L.Tr("ui.002") : L.Tr("ui.003")) : t).ToArray();
            _tab = (Tab)GUILayout.Toolbar((int)_tab, labels, GUILayout.Height(24));

            // no horizontal scrolling: every row is built to shrink to the window width
            _scroll = EditorGUILayout.BeginScrollView(_scroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUIStyle.none);
            EditorGUILayout.Space(4);
            switch (_tab)
            {
                case Tab.Expression: DrawExpressionTab(); break;
                case Tab.Spring: DrawSpringTab(); break;
                case Tab.Meta: DrawMetaTab(); break;
                case Tab.Options: DrawOptionsTab(); break;
                case Tab.Result: DrawResultTab(); break;
            }
            EditorGUILayout.Space(8);
            EditorGUILayout.EndScrollView();

            DrawFooter();
        }

        // ================================================================ header

        void DrawHeader()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    EditorGUI.BeginChangeCheck();
                    var lang = (UiLanguage)EditorGUILayout.Popup((int)L.Language, L.LanguageNames, EditorStyles.miniPullDown, GUILayout.Width(90));
                    if (EditorGUI.EndChangeCheck()) SetLanguage(lang);
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    var picked = (GameObject)EditorGUILayout.ObjectField(L.Tr("ui.004"), _source, typeof(GameObject), true);
                    if (EditorGUI.EndChangeCheck()) SetSource(picked);

                    var sel = Selection.activeGameObject;
                    using (new EditorGUI.DisabledScope(sel == null || sel == _source))
                        if (GUILayout.Button(new GUIContent(L.Tr("ui.005"), L.Tr("ui.006")), EditorStyles.miniButton, GUILayout.Width(70)))
                            SetSource(sel);
                    if (GUILayout.Button(new GUIContent(EditorGUIUtility.IconContent("Refresh").image, L.Tr("ui.007")), EditorStyles.miniButton, GUILayout.Width(28)) && _source != null)
                        Analyze();
                }

                if (_analysis != null) DrawChips();
            }
        }

        /// <summary>Switches the UI language; analysis texts are rebuilt in the new language, user picks are kept.</summary>
        void SetLanguage(UiLanguage lang)
        {
            L.Language = lang;
            if (_analysis != null)
            {
                SaveChoices();
                Analyze(restoreChoices: true);
            }
            Repaint();
        }

        void SetSource(GameObject go)
        {
            if (go == _source) return;
            _preview.Clear();
            _source = go;
            if (_source == null) { _analysis = null; return; }
            Analyze();
        }

        /// <param name="restoreChoices">true after a domain reload: keep the user's picks instead of re-guessing.</param>
        void Analyze(bool restoreChoices = false)
        {
            _preview.Clear();
            _result = null;
            _analysis = VrcAvatarAnalysis.Analyze(_source);
            var a = _analysis;
            if (restoreChoices)
            {
                RestoreChoices();
            }
            else
            {
                _o.Title = _source.name;
                _o.Blink = a.BlinkShape;
                _o.BlinkLeft = a.BlinkLeftShape;
                _o.BlinkRight = a.BlinkRightShape;
                _o.LookUp = a.LookUpShape;
                _o.LookDown = a.LookDownShape;
                _o.Emotions = ShapeNameHeuristics.GuessEmotions(a.Candidates);
                // every face clip not used as an emotion becomes a custom expression by default
                _o.Customs = new HashSet<ExpressionCandidate>(a.Candidates.Where(c => !_o.Emotions.ContainsValue(c)));
            }
            var mesh = a.EyelidMesh != null ? a.EyelidMesh.sharedMesh : null;
            _faceShapes = mesh == null ? new string[0] : Enumerable.Range(0, mesh.blendShapeCount).Select(mesh.GetBlendShapeName).ToArray();
            _facePath = a.EyelidMesh != null ? AnimationUtility.CalculateTransformPath(a.EyelidMesh.transform, AvatarRoot) : null;
            _physBones = a.Root.GetComponentsInChildren<VRCPhysBoneBase>(true).ToList();
        }

        /// <summary>
        /// Status chips. Checks (✓ / ⚠) say whether something VRM needs was found; plain chips are just counts.
        /// Every chip explains itself on hover and opens the tab where it can be changed.
        /// </summary>
        void DrawChips()
        {
            var a = _analysis;
            bool humanoid = a.Animator != null && a.Animator.avatar != null && a.Animator.avatar.isHuman;
            int hidden = a.InactiveRendererCount;
            var chips = new List<(bool? ok, string text, string tip, Tab tab)>
            {
                (humanoid, "Humanoid", humanoid ? L.Tr("ui.008") : L.Tr("ui.009"), Tab.Expression),
                (a.Vowels.Count == 5, L.Tr("ui.010", a.Vowels.Count), L.Tr("ux.chip.lipsync", a.LipSyncSource), Tab.Expression),
                (!string.IsNullOrEmpty(_o.Blink), L.Tr("ui.011"), string.IsNullOrEmpty(_o.Blink) ? L.Tr("ux.chip.blinkMissing") : _o.Blink, Tab.Expression),
                (a.LeftEye && a.RightEye, L.Tr("ui.012"), a.LeftEye && a.RightEye ? $"{a.LeftEye.name} / {a.RightEye.name}" : L.Tr("ui.013"), Tab.Expression),
                (a.Candidates.Count > 0, L.Tr("ui.014", a.Candidates.Count), a.Fx ? L.Tr("ux.chip.expressions", a.Fx.name) : L.Tr("ui.015"), Tab.Expression),
                (null, L.Tr("ux.chip.spring", a.PhysBoneCount), L.Tr("ux.chip.springTip", a.PhysBoneCount, a.ColliderCount), Tab.Spring),
                (null, hidden > 0 ? L.Tr("ux.chip.meshesHidden", a.RendererCount - hidden, hidden) : L.Tr("ux.chip.meshes", a.RendererCount),
                    hidden > 0 ? L.Tr("ux.chip.meshesTip", hidden) : L.Tr("ux.chip.meshesAllTip"), Tab.Options),
            };

            EditorGUILayout.Space(2);
            float maxWidth = position.width - 24, x = 0;
            EditorGUILayout.BeginHorizontal();
            foreach (var (ok, text, tip, tab) in chips)
            {
                var content = ok == null
                    ? new GUIContent(text, tip)
                    : new GUIContent(" " + text, EditorGUIUtility.IconContent(ok.Value ? "TestPassed" : "console.warnicon.sml").image, tip);
                var w = _chip.CalcSize(content).x;
                if (x > 0 && x + w > maxWidth)
                {
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.BeginHorizontal();
                    x = 0;
                }
                if (GUILayout.Button(content, _chip, GUILayout.Width(w))) _tab = tab;
                x += w + 4;
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField(L.Tr("ux.chip.legend"), _hint);

            foreach (var w in a.Warnings) EditorGUILayout.HelpBox(w, MessageType.Warning);
            foreach (var n in a.Notes) EditorGUILayout.HelpBox(n, MessageType.Info);
        }

        // ================================================================ expression tab

        Transform AvatarRoot => (_analysis?.Root != null ? _analysis.Root : _source).transform;

        const float PreviewRowHeight = 26;

        bool CanPreview => _source != null && !EditorUtility.IsPersistent(_source);

        void DrawExpressionTab()
        {
            var a = _analysis;
            // one fixed-height row for every preview state, so starting/stopping a preview never shifts the layout
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox, GUILayout.Height(PreviewRowHeight)))
            {
                if (!CanPreview)
                {
                    GUILayout.Label(L.Tr("ux.preview.prefab"), EditorStyles.miniLabel, GUILayout.MinWidth(0));
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(new GUIContent(L.Tr("ux.preview.place"), L.Tr("ux.preview.placeTip")), EditorStyles.miniButton, GUILayout.ExpandWidth(false)))
                        PlaceInScene();
                }
                else
                {
                    if (_preview.Current == null)
                        GUILayout.Label(L.Tr("ui.020"), EditorStyles.miniLabel, GUILayout.MinWidth(0));
                    else
                        GUILayout.Label(L.Tr("ui.021", _preview.Current), EditorStyles.boldLabel, GUILayout.MinWidth(0));
                    GUILayout.FlexibleSpace();
                    using (new EditorGUI.DisabledScope(_preview.Current == null))
                        if (GUILayout.Button(L.Tr("ui.022"), EditorStyles.miniButton, GUILayout.ExpandWidth(false))) { _preview.Clear(); SceneView.RepaintAll(); }
                }
            }
            _showShapeDetails = EditorGUILayout.ToggleLeft(L.Tr("ux.expr.showDetails"), _showShapeDetails, EditorStyles.miniLabel);

            // ---- basic
            Section(L.Tr("ui.023"), L.Tr("ui.024"));
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(new GUIContent(L.Tr("ui.025"), a.LipSyncSource));
                if (a.Vowels.Count > 0)
                {
                    foreach (var (preset, _) in VrcAvatarAnalysis.VowelVisemes)
                    {
                        a.Vowels.TryGetValue(preset, out var shapes);
                        var tip = shapes == null ? L.Tr("analysis.002") : string.Join("\n", shapes.Select(sv => $"{sv.ShapeName} {sv.Weight:0}"));
                        using (new EditorGUI.DisabledScope(shapes == null))
                            if (GUILayout.Button(new GUIContent(preset.ToString(), tip), EditorStyles.miniButtonMid, GUILayout.MinWidth(20)) && CanPreview)
                                PreviewShapes(preset.ToString(), shapes);
                    }
                }
                else GUILayout.Label(L.Tr("ui.026"), EditorStyles.miniLabel);
            }
            if (_showShapeDetails) EditorGUILayout.LabelField(" ", L.Tr("ui.027", a.LipSyncSource), EditorStyles.miniLabel);
            ShapeRow("Blink", _o.Blink, v => _o.Blink = v);
            ShapeRow("Blink_L", _o.BlinkLeft, v => _o.BlinkLeft = v);
            ShapeRow("Blink_R", _o.BlinkRight, v => _o.BlinkRight = v);
            ShapeRow("LookUp", _o.LookUp, v => _o.LookUp = v);
            ShapeRow("LookDown", _o.LookDown, v => _o.LookDown = v);

            // ---- emotions
            Section(L.Tr("ui.028"), L.Tr("ui.029"));
            var labels = new[] { SearchableDropdown.NoneLabel }.Concat(a.Candidates.Select(c => c.ShortLabel)).ToArray();
            foreach (var preset in ShapeNameHeuristics.EmotionWords.Keys)
            {
                _o.Emotions.TryGetValue(preset, out var cur);
                using (new EditorGUILayout.HorizontalScope())
                {
                    var idx = cur == null ? 0 : a.Candidates.IndexOf(cur) + 1;
                    var next = EditorGUILayout.Popup(preset.ToString(), idx, labels, GUILayout.MinWidth(80));
                    if (next != idx)
                    {
                        if (next == 0) _o.Emotions.Remove(preset);
                        else _o.Emotions[preset] = a.Candidates[next - 1];
                    }
                    PreviewButton(cur?.Name, cur?.Shapes);
                }
                if (cur == null) EditorGUILayout.LabelField(" ", L.Tr("ux.expr.emptyEmotion"), _hint);
                else if (_showShapeDetails) ShapeSummary(cur.Shapes);
            }

            // ---- customs
            Section(L.Tr("ui.030"), L.Tr("ux.expr.customHint"));
            using (new EditorGUILayout.HorizontalScope())
            {
                _customFilter = EditorGUILayout.TextField(_customFilter, EditorStyles.toolbarSearchField, GUILayout.MinWidth(60));
                if (GUILayout.Button(L.Tr("ui.032"), EditorStyles.miniButtonLeft, GUILayout.ExpandWidth(false))) _o.Customs.UnionWith(Filtered());
                if (GUILayout.Button(L.Tr("ui.033"), EditorStyles.miniButtonMid, GUILayout.ExpandWidth(false))) _o.Customs.ExceptWith(Filtered());
                if (GUILayout.Button(L.Tr("ui.034"), EditorStyles.miniButtonRight, GUILayout.ExpandWidth(false)))
                {
                    _o.Customs.ExceptWith(Filtered());
                    _o.Customs.UnionWith(Filtered().Where(c => c.IsGesture));
                }
            }
            EditorGUILayout.LabelField(L.Tr("ui.035", _o.Customs.Count, a.Candidates.Count), EditorStyles.centeredGreyMiniLabel);
            foreach (var c in Filtered())
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    var on = _o.Customs.Contains(c);
                    var next = EditorGUILayout.Toggle(on, GUILayout.Width(16));
                    if (next != on) { if (next) _o.Customs.Add(c); else _o.Customs.Remove(c); }
                    using (new EditorGUILayout.VerticalScope())
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            GUILayout.Label(new GUIContent(c.Name, string.Join("\n", c.Sources)), _rowName, GUILayout.ExpandWidth(false));
                            GUILayout.Label(c.IsGesture ? L.Tr("ui.036") : L.Tr("ui.037"), _badge);
                            var asEmotion = _o.Emotions.Where(kv => kv.Value == c).Select(kv => kv.Key.ToString()).FirstOrDefault();
                            if (asEmotion != null) GUILayout.Label(new GUIContent(L.Tr("ui.038") + asEmotion, L.Tr("ux.expr.usedAsEmotionTip")), _badge);
                            GUILayout.FlexibleSpace();
                        }
                        if (_showShapeDetails)
                        {
                            EditorGUILayout.LabelField(new GUIContent(string.Join(" · ", c.Sources.Take(2)) + (c.Sources.Count > 2 ? " …" : ""), string.Join("\n", c.Sources)), EditorStyles.wordWrappedMiniLabel, GUILayout.MinWidth(60));
                            ShapeSummary(c.Shapes);
                        }
                    }
                    PreviewButton(c.Name, c.Shapes);
                }
            }
        }

        /// <summary>Puts the selected prefab into the open scene so expressions can be previewed on it (undoable).</summary>
        void PlaceInScene()
        {
            if (_source == null || !EditorUtility.IsPersistent(_source)) return;
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(_source);
            if (instance == null) return;
            Undo.RegisterCreatedObjectUndo(instance, "Place avatar for preview");
            Selection.activeGameObject = instance;
            SaveChoices();
            _source = instance;
            Analyze(restoreChoices: true);
        }

        IEnumerable<ExpressionCandidate> Filtered() =>
            _analysis.Candidates.Where(c => string.IsNullOrEmpty(_customFilter)
                || c.Name.IndexOf(_customFilter, StringComparison.OrdinalIgnoreCase) >= 0
                || c.Sources.Any(s => s.IndexOf(_customFilter, StringComparison.OrdinalIgnoreCase) >= 0));

        void ShapeRow(string label, string current, Action<string> set)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (_faceShapes.Length == 0)
                    set(EditorGUILayout.TextField(label, current));
                else
                    SearchableDropdown.Field(new GUIContent(label), current, _faceShapes, v => { set(v); Repaint(); });
                PreviewButton(label, string.IsNullOrEmpty(current) || _facePath == null ? null
                    : new List<ShapeValue> { new ShapeValue { RelativePath = _facePath, ShapeName = current, Weight = 100 } });
            }
        }

        void PreviewButton(string name, IList<ShapeValue> shapes)
        {
            bool usable = CanPreview && shapes != null && shapes.Count > 0;
            bool active = usable && _preview.Current == name;
            using (new EditorGUI.DisabledScope(!usable))
            {
                var tip = CanPreview ? (active ? L.Tr("ui.039") : L.Tr("ui.040")) : L.Tr("ui.041");
                if (GUILayout.Button(new GUIContent(active ? "■" : "▶", tip), EditorStyles.miniButton, GUILayout.Width(24)))
                {
                    if (active) _preview.Clear();
                    else PreviewShapes(name, shapes);
                    SceneView.RepaintAll();
                }
            }
        }

        void PreviewShapes(string name, IEnumerable<ShapeValue> shapes)
        {
            if (!CanPreview) return;
            _preview.Show(AvatarRoot.gameObject, name, shapes);
            SceneView.RepaintAll();
        }

        static void ShapeSummary(IList<ShapeValue> shapes)
        {
            var text = string.Join(", ", shapes.Take(5).Select(s => $"{s.ShapeName} {s.Weight:0}")) + (shapes.Count > 5 ? L.Tr("ui.042", shapes.Count - 5) : "");
            var content = new GUIContent(text, string.Join("\n", shapes.Select(s => $"{s.RelativePath} : {s.ShapeName} = {s.Weight:0.#}")));
            EditorGUILayout.LabelField(content, EditorStyles.wordWrappedMiniLabel, GUILayout.MinWidth(60));
        }

        // ================================================================ spring tab

        void DrawSpringTab()
        {
            var s = _o.Spring;
            EditorGUILayout.HelpBox(L.Tr("ux.spring.intro"), MessageType.Info);

            EditorGUILayout.Space(4);
            _springTable = EditorGUILayout.Foldout(_springTable, L.Tr("ui.056", _physBones.Count), true);
            if (_springTable) DrawSpringTable(s);

            EditorGUILayout.Space(8);
            _springAdvanced = EditorGUILayout.Foldout(_springAdvanced, L.Tr("ux.advanced"), true);
            if (!_springAdvanced) return;
            using (new EditorGUI.IndentLevelScope())
            {
                Section(L.Tr("ui.043"), null);
                EditorGUILayout.HelpBox(L.Tr("ui.044") + L.Tr("ui.045") + "gravityPower = gravity × g", MessageType.None);
                s.StiffnessBase = EditorGUILayout.FloatField("stiffness base", s.StiffnessBase);
                s.StiffnessFromPull = EditorGUILayout.FloatField("a (pull)", s.StiffnessFromPull);
                s.StiffnessFromStiffness = EditorGUILayout.FloatField("b (stiffness)", s.StiffnessFromStiffness);
                s.DragAtSpring0 = EditorGUILayout.Slider(new GUIContent("drag@0", L.Tr("ui.046")), s.DragAtSpring0, 0, 1);
                s.DragAtSpring1 = EditorGUILayout.Slider(new GUIContent("drag@1", L.Tr("ui.047")), s.DragAtSpring1, 0, 1);
                s.DragFromImmobile = EditorGUILayout.Slider("m (immobile)", s.DragFromImmobile, 0, 1);
                s.MaxDrag = EditorGUILayout.Slider("max drag", s.MaxDrag, 0, 1);
                s.GravityScale = EditorGUILayout.FloatField("g (gravity)", s.GravityScale);

                Section(L.Tr("ui.048"), null);
                s.MaxSpheresPerCapsule = EditorGUILayout.IntSlider(new GUIContent(L.Tr("ui.049"), L.Tr("ui.050")), s.MaxSpheresPerCapsule, 2, 12);
                s.DropArmCollidersOnLimited = EditorGUILayout.Toggle(new GUIContent(L.Tr("ui.051"), L.Tr("ui.052")), s.DropArmCollidersOnLimited);
                s.MergeIdentical = EditorGUILayout.Toggle(new GUIContent(L.Tr("ui.053"), L.Tr("ui.054")), s.MergeIdentical);
                EditorGUILayout.Space(4);
                if (GUILayout.Button(L.Tr("ui.055"), GUILayout.ExpandWidth(false))) _o.Spring = new SpringConversionSettings();
            }
        }

        /// <summary>Per-PhysBone preview: VRChat values on the left, resulting VRM values on the right.</summary>
        void DrawSpringTable(SpringConversionSettings s)
        {
            EditorGUILayout.LabelField(L.Tr("ux.spring.legend"), _hint);
            EditorGUILayout.LabelField(L.Tr("ui.057"), _hint);
            const float num = 46;
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("Root", EditorStyles.miniBoldLabel, GUILayout.MinWidth(80));
                foreach (var h in new[] { "pull", "spring", "immob", "→stiff", "→drag", "→grav" })
                    GUILayout.Label(h, EditorStyles.miniBoldLabel, GUILayout.Width(num));
            }
            foreach (var pb in _physBones)
            {
                if (pb == null) continue;
                bool active = pb.enabled && VrcAvatarAnalysis.IsActive(pb.gameObject, AvatarRoot.gameObject);
                var (st, dr, gr) = PhysBoneToSpringBone.Compute(pb, s);
                using (new EditorGUI.DisabledScope(!active))
                using (new EditorGUILayout.HorizontalScope())
                {
                    var root = PhysBoneToSpringBone.RootOf(pb);
                    if (GUILayout.Button(new GUIContent(root.name, AnimationUtility.CalculateTransformPath(pb.transform, AvatarRoot)), EditorStyles.label, GUILayout.MinWidth(80)))
                        EditorGUIUtility.PingObject(pb);
                    foreach (var v in new[] { pb.pull, pb.spring, pb.immobile })
                        GUILayout.Label(v.ToString("0.00"), EditorStyles.miniLabel, GUILayout.Width(num));
                    foreach (var v in new[] { st, dr, gr })
                        GUILayout.Label(v.ToString("0.00"), _numBold, GUILayout.Width(num));
                }
            }

        }

        // ================================================================ meta tab

        void DrawMetaTab()
        {
            EditorGUILayout.LabelField(L.Tr("ux.meta.optional"), _hint);

            Section(L.Tr("ui.058"), null);
            _o.Title = EditorGUILayout.TextField("Title", _o.Title);
            _o.Version = EditorGUILayout.TextField("Version", _o.Version);
            _o.Thumbnail = (Texture2D)EditorGUILayout.ObjectField("Thumbnail", _o.Thumbnail, typeof(Texture2D), false);

            Section(L.Tr("ui.059"), null);
            AuthorField();
            EditorGUILayout.LabelField(" ", L.Tr("ux.meta.authorHelp"), _hint);
            _o.ContactInformation = EditorGUILayout.TextField("Contact", _o.ContactInformation);
            _o.Reference = EditorGUILayout.TextField(new GUIContent("Reference", L.Tr("ux.meta.referenceTip")), _o.Reference);

            Section(L.Tr("ui.061"), L.Tr("ui.062"));
            EditorGUILayout.LabelField(L.Tr("ux.meta.termsNote"), _hint);
            _o.AllowedUser = (AllowedUser)EditorGUILayout.Popup(L.Tr("ui.063"), (int)_o.AllowedUser,
                new[] { L.Tr("ux.allowed.onlyAuthor"), L.Tr("ux.allowed.licensed"), L.Tr("ux.allowed.everyone") });
            var usage = new[] { L.Tr("ux.usage.disallow"), L.Tr("ux.usage.allow") };
            _o.ViolentUssage = (UssageLicense)EditorGUILayout.Popup(L.Tr("ui.064"), (int)_o.ViolentUssage, usage);
            _o.SexualUssage = (UssageLicense)EditorGUILayout.Popup(L.Tr("ui.065"), (int)_o.SexualUssage, usage);
            _o.CommercialUssage = (UssageLicense)EditorGUILayout.Popup(L.Tr("ui.066"), (int)_o.CommercialUssage, usage);
            _o.OtherPermissionUrl = EditorGUILayout.TextField(L.Tr("ui.067"), _o.OtherPermissionUrl);
            _o.LicenseType = (LicenseType)EditorGUILayout.Popup(L.Tr("ui.068"), (int)_o.LicenseType, new[]
            {
                L.Tr("ux.license.prohibited"), "CC0", "CC BY", "CC BY-NC", "CC BY-SA", "CC BY-NC-SA", "CC BY-ND", "CC BY-NC-ND", L.Tr("ux.license.other"),
            });
            if (_o.LicenseType == LicenseType.Other)
                _o.OtherLicenseUrl = EditorGUILayout.TextField(L.Tr("ui.069"), _o.OtherLicenseUrl);

            EditorGUILayout.Space(6);
            _rememberMeta = EditorGUILayout.ToggleLeft(new GUIContent(L.Tr("ui.070"), L.Tr("ui.071")), _rememberMeta);
        }

        /// <summary>The only required input; drawn in the Meta tab and above the convert button.</summary>
        void AuthorField()
        {
            var prev = GUI.backgroundColor;
            if (string.IsNullOrWhiteSpace(_o.Author)) GUI.backgroundColor = new Color(1f, 0.55f, 0.55f);
            _o.Author = EditorGUILayout.TextField(new GUIContent(L.Tr("ux.author"), L.Tr("ux.meta.authorHelp")), _o.Author);
            GUI.backgroundColor = prev;
        }

        void LoadMetaPrefs()
        {
            string S(string k, string d) => EditorPrefs.GetString(PrefsPrefix + k, d);
            int I(string k, int d) => EditorPrefs.GetInt(PrefsPrefix + k, d);
            _o.Author = S("Author", _o.Author);
            _o.ContactInformation = S("Contact", _o.ContactInformation);
            _o.Reference = S("Reference", _o.Reference);
            _o.OtherPermissionUrl = S("OtherPermissionUrl", _o.OtherPermissionUrl);
            _o.OtherLicenseUrl = S("OtherLicenseUrl", _o.OtherLicenseUrl);
            _o.AllowedUser = (AllowedUser)I("AllowedUser", (int)_o.AllowedUser);
            _o.ViolentUssage = (UssageLicense)I("Violent", (int)_o.ViolentUssage);
            _o.SexualUssage = (UssageLicense)I("Sexual", (int)_o.SexualUssage);
            _o.CommercialUssage = (UssageLicense)I("Commercial", (int)_o.CommercialUssage);
            _o.LicenseType = (LicenseType)I("License", (int)_o.LicenseType);
            _o.OutputFolder = S("OutputFolder", _o.OutputFolder);
        }

        void SaveMetaPrefs()
        {
            EditorPrefs.SetString(PrefsPrefix + "Author", _o.Author ?? "");
            EditorPrefs.SetString(PrefsPrefix + "Contact", _o.ContactInformation ?? "");
            EditorPrefs.SetString(PrefsPrefix + "Reference", _o.Reference ?? "");
            EditorPrefs.SetString(PrefsPrefix + "OtherPermissionUrl", _o.OtherPermissionUrl ?? "");
            EditorPrefs.SetString(PrefsPrefix + "OtherLicenseUrl", _o.OtherLicenseUrl ?? "");
            EditorPrefs.SetInt(PrefsPrefix + "AllowedUser", (int)_o.AllowedUser);
            EditorPrefs.SetInt(PrefsPrefix + "Violent", (int)_o.ViolentUssage);
            EditorPrefs.SetInt(PrefsPrefix + "Sexual", (int)_o.SexualUssage);
            EditorPrefs.SetInt(PrefsPrefix + "Commercial", (int)_o.CommercialUssage);
            EditorPrefs.SetInt(PrefsPrefix + "License", (int)_o.LicenseType);
            EditorPrefs.SetString(PrefsPrefix + "OutputFolder", _o.OutputFolder ?? "");
        }

        // ================================================================ options tab

        void DrawOptionsTab()
        {
            EditorGUILayout.LabelField(L.Tr("ux.options.optional"), _hint);

            Section(L.Tr("ui.072"), null);
            using (new EditorGUILayout.HorizontalScope())
            {
                _o.OutputFolder = EditorGUILayout.TextField(L.Tr("ui.073"), _o.OutputFolder);
                if (GUILayout.Button("…", EditorStyles.miniButton, GUILayout.Width(24)))
                {
                    var abs = EditorUtility.OpenFolderPanel(L.Tr("ui.073"), _o.OutputFolder, "");
                    var root = Path.GetFullPath(Application.dataPath + "/..").Replace('\\', '/') + "/";
                    abs = abs.Replace('\\', '/');
                    if (!string.IsNullOrEmpty(abs))
                    {
                        if (abs.StartsWith(root + "Assets")) _o.OutputFolder = abs.Substring(root.Length);
                        else EditorUtility.DisplayDialog("VRC → VRM", L.Tr("ui.074"), L.Tr("ui.075"));
                    }
                }
            }
            EditorGUILayout.LabelField(" ", L.Tr("ui.077", _o.OutputFolder.TrimEnd('/'), (_source ? LilToonToMToon.Sanitize(_source.name) : L.Tr("ui.076"))), EditorStyles.miniLabel);

            Section(L.Tr("ux.options.result"), null);
            _o.ForceTPose = EditorGUILayout.ToggleLeft(new GUIContent(L.Tr("ui.079"), L.Tr("ui.080")), _o.ForceTPose);
            _o.RemoveInactive = EditorGUILayout.ToggleLeft(new GUIContent(L.Tr("ui.081"), L.Tr("ui.082")), _o.RemoveInactive);
            _o.BakeTextures = EditorGUILayout.ToggleLeft(new GUIContent(L.Tr("ui.087"), L.Tr("ui.088")), _o.BakeTextures);

            EditorGUILayout.Space(8);
            _optionsAdvanced = EditorGUILayout.Foldout(_optionsAdvanced, L.Tr("ux.advanced"), true);
            if (!_optionsAdvanced) return;
            using (new EditorGUI.IndentLevelScope())
            {
                _o.ReduceBlendshape = EditorGUILayout.ToggleLeft(new GUIContent(L.Tr("ui.083"), L.Tr("ui.084")), _o.ReduceBlendshape);
                EditorGUILayout.LabelField(L.Tr("ui.084"), _hint);
                _o.UseSparseAccessor = EditorGUILayout.ToggleLeft(new GUIContent("Sparse Accessor", L.Tr("ui.085")), _o.UseSparseAccessor);
                EditorGUILayout.LabelField(L.Tr("ui.085"), _hint);
                _o.KeepWorkingCopy = EditorGUILayout.ToggleLeft(new GUIContent(L.Tr("ui.090"), L.Tr("ui.091")), _o.KeepWorkingCopy);
                EditorGUILayout.LabelField(L.Tr("ui.091"), _hint);
            }
        }

        // ================================================================ result tab

        void DrawResultTab()
        {
            if (_result == null)
            {
                EditorGUILayout.HelpBox(L.Tr("ui.092"), MessageType.None);
                return;
            }

            var errors = _result.Log.Where(l => LevelOf(l) == MessageType.Error).ToList();
            var notes = _result.Log.Where(l => LevelOf(l) == MessageType.Warning).ToList();

            if (_result.Success)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label(EditorGUIUtility.IconContent("TestPassed"), GUILayout.Width(20), GUILayout.Height(22));
                        GUILayout.Label(L.Tr("ux.result.ready"), _bigTitle);
                    }
                    long size = File.Exists(_result.VrmPath) ? new FileInfo(_result.VrmPath).Length : 0;
                    EditorGUILayout.LabelField(L.Tr("ux.result.file", Path.GetFileName(_result.VrmPath), size / 1048576f), EditorStyles.wordWrappedLabel);
                    EditorGUILayout.LabelField(Path.GetDirectoryName(_result.VrmPath)?.Replace('\\', '/'), EditorStyles.miniLabel);
                    EditorGUILayout.Space(4);
                    if (GUILayout.Button(L.Tr("ui.098"), GUILayout.Height(28))) EditorUtility.RevealInFinder(_result.VrmPath);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button(L.Tr("ui.095"), EditorStyles.miniButtonLeft)) Ping(_result.VrmPath);
                        if (GUILayout.Button(new GUIContent(L.Tr("ui.096"), L.Tr("ui.097")), EditorStyles.miniButtonRight)) Ping(_result.PrefabPath);
                    }
                    EditorGUILayout.LabelField(L.Tr("ux.result.next"), _hint);
                }
            }
            else
            {
                EditorGUILayout.HelpBox(L.Tr("ui.094"), MessageType.Error);
            }

            // errors always visible; everything else is folded away
            foreach (var e in errors) LogLine(e, MessageType.Error);

            if (notes.Count > 0)
            {
                EditorGUILayout.Space(6);
                _showNotes = EditorGUILayout.Foldout(_showNotes, L.Tr("ux.result.notes", notes.Count), true);
                if (_showNotes)
                {
                    EditorGUILayout.LabelField(L.Tr("ux.result.notesHint"), _hint);
                    foreach (var n in notes) LogLine(n.Substring(n.StartsWith(L.WarnMark) ? L.WarnMark.Length : 0), MessageType.Warning);
                }
            }

            EditorGUILayout.Space(6);
            using (new EditorGUILayout.HorizontalScope())
            {
                _showLog = EditorGUILayout.Foldout(_showLog, L.Tr("ux.result.log", _result.Log.Count), true);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(L.Tr("ui.099"), EditorStyles.miniButton, GUILayout.ExpandWidth(false)))
                    EditorGUIUtility.systemCopyBuffer = string.Join("\n", _result.Log);
            }
            if (_showLog)
                foreach (var line in _result.Log) LogLine(line, LevelOf(line));
        }

        static void LogLine(string line, MessageType level)
        {
            var icon = level == MessageType.Error ? "console.erroricon.sml" : level == MessageType.Warning ? "console.warnicon.sml" : "console.infoicon.sml";
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(EditorGUIUtility.IconContent(icon), GUILayout.Width(18), GUILayout.Height(18));
                EditorGUILayout.LabelField(line, EditorStyles.wordWrappedMiniLabel);
            }
        }

        static MessageType LevelOf(string line)
        {
            if (line.StartsWith("[Error]") || line.Contains("Validate:Error") || line.Contains("Validate:Critical")) return MessageType.Error;
            if (line.StartsWith(L.WarnMark) || line.Contains("Validate:Warning")) return MessageType.Warning;
            return MessageType.Info;
        }

        static void Ping(string path)
        {
            var obj = AssetDatabase.LoadAssetAtPath<Object>(path);
            if (obj == null) return;
            Selection.activeObject = obj;
            EditorGUIUtility.PingObject(obj);
        }

        // ================================================================ footer

        void DrawFooter()
        {
            var a = _analysis;
            var problems = new List<string>();
            if (a.Animator == null || a.Animator.avatar == null || !a.Animator.avatar.isHuman) problems.Add(L.Tr("conv.001"));
            if (string.IsNullOrWhiteSpace(_o.OutputFolder) || !_o.OutputFolder.StartsWith("Assets")) problems.Add(L.Tr("ui.103"));
            bool needsAuthor = string.IsNullOrWhiteSpace(_o.Author);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                foreach (var p in problems)
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label(EditorGUIUtility.IconContent("console.erroricon.sml"), GUILayout.Width(18), GUILayout.Height(18));
                        GUILayout.Label(p, EditorStyles.wordWrappedMiniLabel);
                    }
                // the one required input sits right next to the button it unlocks
                if (_tab != Tab.Meta)
                {
                    AuthorField();
                    if (needsAuthor) EditorGUILayout.LabelField(" ", L.Tr("ux.footer.authorNeeded"), _hint);
                }
                else if (needsAuthor) EditorGUILayout.LabelField(L.Tr("ux.footer.authorAbove"), _hint);
                using (new EditorGUI.DisabledScope(problems.Count > 0 || needsAuthor))
                {
                    var label = _result != null && _result.Success ? L.Tr("ux.footer.again") : L.Tr("ui.104");
                    if (GUILayout.Button(new GUIContent(label, L.Tr("ux.footer.vrm0")), GUILayout.Height(30)))
                        RunConvert();
                }
                EditorGUILayout.LabelField(L.Tr("ux.footer.vrm0"), EditorStyles.centeredGreyMiniLabel);
            }
        }

        void RunConvert()
        {
            _preview.Clear();
            if (_rememberMeta) SaveMetaPrefs();
            try
            {
                EditorUtility.DisplayProgressBar("VRC → VRM", L.Tr("ui.105", _source.name), 0.5f);
                _result = VrcToVrmConverter.Convert(_source, _analysis, _o);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            _tab = Tab.Result;
            _showNotes = false;
            _showLog = false;
            _scroll = Vector2.zero;
            if (_result.Success) Ping(_result.VrmPath);
            GUIUtility.ExitGUI();
        }

        // ================================================================ styles

        void Section(string title, string hint)
        {
            EditorGUILayout.Space(6);
            GUILayout.Label(title, _sectionTitle);
            var r = GUILayoutUtility.GetRect(1, 1, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(r, new Color(0.5f, 0.5f, 0.5f, 0.35f));
            if (!string.IsNullOrEmpty(hint)) EditorGUILayout.LabelField(hint, EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(2);
        }

        static void InitStyles()
        {
            if (_chip != null) return;
            _chip = new GUIStyle(EditorStyles.miniButton)
            {
                alignment = TextAnchor.MiddleLeft,
                fixedHeight = 20,
                padding = new RectOffset(6, 8, 2, 2),
                imagePosition = ImagePosition.ImageLeft,
            };
            _sectionTitle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 12 };
            _bigTitle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 14 };
            _rowName = new GUIStyle(EditorStyles.boldLabel);
            _numBold = new GUIStyle(EditorStyles.miniLabel) { fontStyle = FontStyle.Bold };
            _hint = new GUIStyle(EditorStyles.wordWrappedMiniLabel) { normal = { textColor = new Color(0.5f, 0.5f, 0.5f) } };
            _badge = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = EditorGUIUtility.isProSkin ? new Color(0.55f, 0.8f, 1f) : new Color(0.1f, 0.35f, 0.7f) },
            };
        }
    }
}
