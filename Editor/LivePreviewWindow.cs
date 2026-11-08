using System;
using Glim;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FrozenAlex.GlimPreview
{
    public sealed class LivePreviewWindow : EditorWindow
    {
        [SerializeField] GlimLightmapper lightmapper;
        string error;

        [MenuItem("Tools/Glim/Live Preview")]
        public static void Open() => GetWindow<LivePreviewWindow>("Glim Live Preview");

        [MenuItem("CONTEXT/GlimLightmapper/Live Preview")]
        static void OpenForComponent(MenuCommand command)
        {
            var window = GetWindow<LivePreviewWindow>("Glim Live Preview");
            window.lightmapper = command.context as GlimLightmapper;
        }

        void OnEnable()
        {
            minSize = new Vector2(340, 320);
            FindLightmapper();
        }

        void FindLightmapper()
        {
            if (lightmapper) return;
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects()) {
                lightmapper = root.GetComponentInChildren<GlimLightmapper>();
                if (lightmapper) break;
            }
        }

        void OnInspectorUpdate() => Repaint();

        void OnGUI()
        {
            EditorGUILayout.LabelField("Glim Live Preview", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Preview directly in Scene View with the scene's color correction. Uses the selected Glim Lightmapper's preview settings.", MessageType.Info);
            using (new EditorGUI.DisabledScope(Bake.IsPreviewRunning))
                lightmapper = (GlimLightmapper)EditorGUILayout.ObjectField("Lightmapper", lightmapper, typeof(GlimLightmapper), true);

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
            EditorGUILayout.HelpBox("Lights, base colors/textures, emission, and existing object transforms update live. Stop and reopen after adding, removing, disabling, or replacing meshes. The scene must be saved before starting.", MessageType.None);
            if (!string.IsNullOrEmpty(error ?? Bake.LastError)) EditorGUILayout.HelpBox(error ?? Bake.LastError, MessageType.Error);
        }

        static float Positive(float value, float fallback) => float.IsNaN(value) || float.IsInfinity(value) ? fallback : Mathf.Max(0.000001f, value);

        internal static string CompatibilityError()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor && Application.platform != RuntimePlatform.LinuxEditor)
                return "This add-on supports the Windows x64 and Linux x64 editors.";
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(GlimLightmapper).Assembly);
            if (package == null || !package.version.StartsWith("0.12.", StringComparison.Ordinal))
                return "This add-on supports Glim 0.12.x. Install a matching add-on version after upgrading Glim.";
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
                Bake.Start(lightmapper, CreateConfig(lightmapper, sceneView.camera), inScene ? sceneView : null);
            }
            catch (Exception e) { error = e.Message; Debug.LogException(e); }
        }

        internal static Bindings.GlimConfig CreateConfig(GlimLightmapper baker, Camera camera) => new(
            Bindings.CoordinateSystem.Unity, baker.previewSamples, baker.previewSamples, baker.previewSamples,
            baker.previewBounces, true, baker.hardwareRayTracing, baker.previewThrottle,
            new Bindings.LightmapSettings(baker.previewWidth, baker.previewHeight, false, false, false, false),
            camera.transform.position, camera.transform.forward, baker.lightProbeSamples,
            baker.lightFalloff, baker.multipleImportanceSampling, baker.lightmapMode,
            RenderSettings.ambientIntensity, baker.indirectMultiplier, 0, baker.emissiveMultiplier,
            baker.ambientOcclusion, baker.ambientOcclusionRange, baker.coverageMaxSamples,
            baker.coverageMinThreshold, baker.antiAliasing, baker.leakDetection);
    }
}
