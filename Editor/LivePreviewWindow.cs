using System;
using Glim;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FrozenAlex.GlimPreview
{
    public sealed class LivePreviewWindow : EditorWindow
    {
        [SerializeField] GlimLightmapper lightmapper;
        [SerializeField] string lightmapperId;
        [SerializeField] bool selectionCleared;
#if GLIM_013_OR_NEWER
        [SerializeField] int previewWidth = 1024;
        [SerializeField] int previewHeight = 1024;
        [SerializeField] int previewSamples = 512;
        [SerializeField] int previewBounces = 2;
        [SerializeField] bool fastMetaCapture;
#endif
        double nextRestore;
        string error;

        [MenuItem("Tools/Glim/Live Preview")]
        public static void Open() => GetWindow<LivePreviewWindow>("Glim Live Preview");

        [MenuItem("CONTEXT/GlimLightmapper/Live Preview")]
        static void OpenForComponent(MenuCommand command)
        {
            var window = GetWindow<LivePreviewWindow>("Glim Live Preview");
            window.SetLightmapper(command.context as GlimLightmapper);
        }

        void OnEnable()
        {
#if GLIM_013_OR_NEWER
            minSize = new Vector2(340, 400);
#else
            minSize = new Vector2(340, 320);
#endif
            EditorSceneManager.sceneClosing += OnSceneClosing;
            EditorSceneManager.sceneOpened += OnSceneOpened;
            RememberLightmapper();
            RestoreLightmapper();
        }

        void OnDisable()
        {
            EditorSceneManager.sceneClosing -= OnSceneClosing;
            EditorSceneManager.sceneOpened -= OnSceneOpened;
        }

        internal void SetLightmapper(GlimLightmapper value)
        {
            lightmapper = value;
            selectionCleared = !value;
            lightmapperId = null;
            RememberLightmapper();
            error = null;
        }

        void RememberLightmapper()
        {
            if (lightmapper && !EditorApplication.isPlayingOrWillChangePlaymode && !string.IsNullOrEmpty(lightmapper.gameObject.scene.path))
                lightmapperId = GlobalObjectId.GetGlobalObjectIdSlow(lightmapper).ToString();
        }

        void OnSceneClosing(Scene scene, bool removingScene)
        {
            // Glim closes the source scene while creating its lighting data, then
            // reopens it. Save the persistent ID before that destroys this instance.
            if (lightmapper && lightmapper.gameObject.scene == scene) RememberLightmapper();
        }

        void OnSceneOpened(Scene scene, OpenSceneMode mode)
        {
            RestoreLightmapper();
            Repaint();
        }

        void RestoreLightmapper()
        {
            if (lightmapper || selectionCleared || EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (!string.IsNullOrEmpty(lightmapperId))
            {
                if (GlobalObjectId.TryParse(lightmapperId, out var id))
                {
                    string path = AssetDatabase.GUIDToAssetPath(id.assetGUID.ToString());
                    if (SceneManager.GetSceneByPath(path).isLoaded)
                        lightmapper = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id) as GlimLightmapper;
                }
                // Never substitute a different component from the temporary bake
                // scene, or the first component in a scene with several lightmappers.
                return;
            }
            var activeScene = SceneManager.GetActiveScene();
            if (!activeScene.IsValid() || !activeScene.isLoaded) return;
            foreach (var root in activeScene.GetRootGameObjects()) {
                var candidate = root.GetComponentInChildren<GlimLightmapper>();
                if (candidate) { SetLightmapper(candidate); break; }
            }
        }

        void OnInspectorUpdate()
        {
            // Scene-open callbacks may precede object restoration. Retry only a
            // missing reference; ordinary repaint/selection changes leave it alone.
            if (!lightmapper && EditorApplication.timeSinceStartup >= nextRestore)
            {
                nextRestore = EditorApplication.timeSinceStartup + 0.5;
                RestoreLightmapper();
            }
            Repaint();
        }

        void OnGUI()
        {
            EditorGUILayout.LabelField("Glim Live Preview", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Preview directly in Scene View with the scene's color correction.", MessageType.Info);
            using (new EditorGUI.DisabledScope(Bake.IsPreviewRunning))
            {
                EditorGUI.BeginChangeCheck();
                var selected = (GlimLightmapper)EditorGUILayout.ObjectField("Lightmapper", lightmapper, typeof(GlimLightmapper), true);
                if (EditorGUI.EndChangeCheck()) SetLightmapper(selected);
#if GLIM_013_OR_NEWER
                fastMetaCapture = EditorGUILayout.Toggle(new GUIContent("Faster META capture",
                    "Updates mesh emission colors faster during preview, with slightly lower quality around UV edges."), fastMetaCapture);
                previewWidth = Mathf.Clamp(EditorGUILayout.DelayedIntField("Preview width", previewWidth), 64, 2048);
                previewHeight = Mathf.Clamp(EditorGUILayout.DelayedIntField("Preview height", previewHeight), 64, 2048);
                previewSamples = Mathf.Clamp(EditorGUILayout.DelayedIntField("Preview samples", previewSamples), 1, 65536);
                previewBounces = Mathf.Clamp(EditorGUILayout.DelayedIntField("Preview bounces", previewBounces), 0, 127);
#endif
            }

            EditorGUI.BeginChangeCheck();
            PreviewViewport.UseSceneEffects = EditorGUILayout.Toggle("Scene color correction / effects", PreviewViewport.UseSceneEffects);
            PreviewViewport.DisplayMode = (PreviewDisplayMode)EditorGUILayout.EnumPopup("Display", PreviewViewport.DisplayMode);
            PreviewViewport.ExposureEV = EditorGUILayout.Slider("Exposure (EV)", PreviewViewport.ExposureEV, -12, 12);
            if (PreviewViewport.DisplayMode == PreviewDisplayMode.FalseColor)
                PreviewViewport.ReferenceGray = Positive(EditorGUILayout.FloatField("Reference gray", PreviewViewport.ReferenceGray), 0.18f);
            if (PreviewViewport.DisplayMode == PreviewDisplayMode.Highlights)
                PreviewViewport.HighlightLimit = Positive(EditorGUILayout.FloatField("HDR highlight limit", PreviewViewport.HighlightLimit), 1);
            PreviewViewport.Denoise = EditorGUILayout.Toggle("Surface denoising (OIDN)", PreviewViewport.Denoise);
            if (EditorGUI.EndChangeCheck()) PreviewViewport.Repaint();
            EditorGUILayout.LabelField("Display controls apply to the Scene View preview.", EditorStyles.wordWrappedMiniLabel);

            string compatibility = CompatibilityError();
            if (compatibility != null) EditorGUILayout.HelpBox(compatibility, MessageType.Warning);
            using (new EditorGUI.DisabledScope(Bake.IsPreviewRunning || compatibility != null || !lightmapper || EditorApplication.isPlayingOrWillChangePlaymode)) {
                if (GUILayout.Button("Preview in Scene View", GUILayout.Height(30))) StartPreview(true);
                if (GUILayout.Button("Open Separate Preview Window")) StartPreview(false);
            }
            using (new EditorGUI.DisabledScope(!Bake.IsPreviewRunning))
                if (GUILayout.Button("Stop Preview")) Bake.StopPreview();
            if (Bake.IsPreviewRunning)
                EditorGUILayout.LabelField(PreviewViewport.Active ? $"Scene View В· {PreviewViewport.Samples} samples" : "Separate preview window is running.");
            EditorGUILayout.HelpBox("Lights, skybox, base colors/textures, emission, and existing object transforms update live. Stop and reopen after adding, removing, disabling, or replacing meshes. The scene must be saved before starting.", MessageType.None);
            if (!string.IsNullOrEmpty(error ?? Bake.LastError)) EditorGUILayout.HelpBox(error ?? Bake.LastError, MessageType.Error);
        }

        static float Positive(float value, float fallback) => float.IsNaN(value) || float.IsInfinity(value) ? fallback : Mathf.Max(0.000001f, value);

        internal static string CompatibilityError()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor && Application.platform != RuntimePlatform.LinuxEditor)
                return "This add-on supports the Windows x64 and Linux x64 editors.";
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(GlimLightmapper).Assembly);
            if (package == null || !(package.version.StartsWith("0.12.", StringComparison.Ordinal) ||
                                    package.version.StartsWith("0.13.", StringComparison.Ordinal)))
                return "This add-on supports Glim 0.12.x and 0.13.x.";
            return null;
        }

        void StartPreview(bool inScene)
        {
            error = null;
            try {
                var sceneView = SceneView.lastActiveSceneView;
                if (!sceneView) sceneView = GetWindow<SceneView>();
                if (lightmapper.gameObject.scene != SceneManager.GetActiveScene())
                    throw new InvalidOperationException("Select a lightmapper in the active scene.");
#if GLIM_013_OR_NEWER
                var config = CreateConfig(lightmapper, sceneView.camera, (uint)previewWidth, (uint)previewHeight,
                    (uint)previewSamples, (uint)previewBounces, global::Glim.PreviewWindow.ThrottleMs);
                Bake.Start(lightmapper, config, inScene ? sceneView : null, fastMetaCapture);
#else
                var config = CreateConfig(lightmapper, sceneView.camera, lightmapper.previewWidth, lightmapper.previewHeight,
                    lightmapper.previewSamples, lightmapper.previewBounces, lightmapper.previewThrottle);
                Bake.Start(lightmapper, config, inScene ? sceneView : null);
#endif
            }
            catch (Exception e) { error = e.Message; Debug.LogException(e); }
        }

        internal static Bindings.GlimConfig CreateConfig(GlimLightmapper baker, Camera camera, uint width, uint height,
            uint samples, uint bounces, uint throttle) => new(
            Bindings.CoordinateSystem.Unity, samples, samples, samples, bounces, true, baker.hardwareRayTracing, throttle,
            new Bindings.LightmapSettings(width, height, false, false, false, false),
            camera.transform.position, camera.transform.forward, baker.lightProbeSamples,
#if GLIM_013_OR_NEWER
            // The independent preview renderer still uses the Glim 0.12.2 ABI.
            baker.lightFalloff, false, baker.lightmapMode,
#else
            baker.lightFalloff, baker.multipleImportanceSampling, baker.lightmapMode,
#endif
            RenderSettings.ambientIntensity, baker.indirectMultiplier, 0, baker.emissiveMultiplier,
            baker.ambientOcclusion, baker.ambientOcclusionRange, baker.coverageMaxSamples,
            baker.coverageMinThreshold, baker.antiAliasing, baker.leakDetection);
    }
}
