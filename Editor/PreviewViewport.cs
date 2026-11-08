using global::Glim;
using System;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

namespace FrozenAlex.GlimPreview
{
    internal enum PreviewDisplayMode { Normal, FalseColor, Highlights }

    [InitializeOnLoad]
    internal static class PreviewViewport
    {
        static SceneView view;
        static Camera camera;
        static CommandBuffer composite;
        static Material material;
        static Texture2D frame;
        static Texture2D denoisedFrame;
        static ulong frameGeneration, denoisedGeneration;
        static byte[] pixels;
        static Bindings.PreviewView lastView;
        static uint revision, samples;
        static bool hasView, hasFrame;
        static int maxWidth, maxHeight;
        static double nextRead;
        static CameraEvent injectionEvent;
        static bool originalImageEffects, originalFxEnabled;
        internal static bool UseSceneEffects = true;
        internal static bool Active => view != null;
        internal static bool Shown = true;
        internal static float Opacity = 1;
        internal static PreviewDisplayMode DisplayMode;
        internal static float ExposureEV;
        internal static float ReferenceGray = 0.18f;
        internal static float HighlightLimit = 1;
        internal static bool Denoise;
        internal static uint DenoiseStatus;
        internal static bool Diagnostic => DisplayMode != PreviewDisplayMode.Normal;
        internal static uint Samples => samples;

        static PreviewViewport()
        {
            EditorApplication.update += Update;
            Camera.onPreCull += BeforeCamera;
            SceneView.beforeSceneGui += BeforeSceneGUI;
            AssemblyReloadEvents.beforeAssemblyReload += Detach;
            EditorApplication.quitting += Detach;
        }

        internal static void Attach(SceneView sceneView, uint width, uint height)
        {
            Detach();
            view = sceneView;
            originalImageEffects = view.sceneViewState.showImageEffects;
            originalFxEnabled = view.sceneViewState.fxEnabled;
            if (UseSceneEffects) { view.sceneViewState.showImageEffects = true; view.sceneViewState.fxEnabled = true; }
            maxWidth = (int)Math.Min(2048u, Math.Max(64u, width));
            maxHeight = (int)Math.Min(2048u, Math.Max(64u, height));
            var shader = AssetDatabase.LoadAssetAtPath<Shader>("Packages/com.frozenalex.glim-preview/Editor/PreviewComposite.shader");
            material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            composite = new CommandBuffer { name = "Glim Scene View Preview" };
            Shown = true;
            view.Repaint();
        }

        internal static void Detach()
        {
            if (view) Bindings.app_set_preview_denoise(false);
            if (camera && composite != null) camera.RemoveCommandBuffer(injectionEvent, composite);
            composite?.Release();
            composite = null;
            camera = null;
            if (frame) UnityEngine.Object.DestroyImmediate(frame);
            ReleaseDenoisedFrame();
            if (material) UnityEngine.Object.DestroyImmediate(material);
            frame = null; material = null; pixels = null;
            if (view)
            {
                view.sceneViewState.showImageEffects = originalImageEffects;
                view.sceneViewState.fxEnabled = originalFxEnabled;
                view.Repaint();
            }
            view = null;
            hasView = hasFrame = false;
            revision = samples = 0;
            frameGeneration = denoisedGeneration = 0;
            DenoiseStatus = 0;
        }

        static Vector3 Native(Vector3 v) => new Vector3(v.x, v.y, -v.z);

        // Near-plane origins also support orthographic cameras and rolled Scene views.
        internal static Bindings.PreviewView CaptureView(Camera source, int width, int height)
        {
            float near = source.nearClipPlane;
            Vector3 a = source.ViewportToWorldPoint(new Vector3(0, 1, near));
            Vector3 b = source.ViewportToWorldPoint(new Vector3(1, 1, near));
            Vector3 c = source.ViewportToWorldPoint(new Vector3(0, 0, near));
            Vector3 da = source.ViewportToWorldPoint(new Vector3(0, 1, near + 1)) - a;
            Vector3 db = source.ViewportToWorldPoint(new Vector3(1, 1, near + 1)) - b;
            Vector3 dc = source.ViewportToWorldPoint(new Vector3(0, 0, near + 1)) - c;
            return new Bindings.PreviewView {
                origin = Native(a), originDx = Native(b - a), originDy = Native(c - a),
                direction = Native(da), directionDx = Native(db - da), directionDy = Native(dc - da),
                width = (uint)width, height = (uint)height,
            };
        }

        static bool SameView(Bindings.PreviewView a, Bindings.PreviewView b) =>
            a.width == b.width && a.height == b.height &&
            (a.origin - b.origin).sqrMagnitude < 1e-10f &&
            (a.originDx - b.originDx).sqrMagnitude < 1e-10f &&
            (a.originDy - b.originDy).sqrMagnitude < 1e-10f &&
            (a.direction - b.direction).sqrMagnitude < 1e-10f &&
            (a.directionDx - b.directionDx).sqrMagnitude < 1e-10f &&
            (a.directionDy - b.directionDy).sqrMagnitude < 1e-10f;

        static void BeforeSceneGUI(SceneView sceneView)
        {
            if (sceneView == view) BeforeCamera(sceneView.camera);
        }

        static void BeforeCamera(Camera renderingCamera)
        {
            if (!view || renderingCamera != view.camera || composite == null) return;
            var desiredEvent = UseSceneEffects && !Diagnostic ? CameraEvent.BeforeImageEffects : CameraEvent.AfterImageEffects;
            if (camera != renderingCamera || injectionEvent != desiredEvent)
            {
                if (camera) camera.RemoveCommandBuffer(injectionEvent, composite);
                camera = renderingCamera;
                injectionEvent = desiredEvent;
            }
            // PPv2 also uses BeforeImageEffects. Our HDR image must be the input
            // to its command buffers, so insert first and preserve their order.
            var buffers = camera.GetCommandBuffers(injectionEvent);
            if (buffers.Length == 0 || buffers[0].name != composite.name)
            {
                camera.RemoveCommandBuffers(injectionEvent);
                camera.AddCommandBuffer(injectionEvent, composite);
                foreach (var buffer in buffers)
                    if (buffer.name != composite.name) camera.AddCommandBuffer(injectionEvent, buffer);
            }
            float scale = Mathf.Min(1, Mathf.Min(maxWidth / (float)Mathf.Max(1, camera.pixelWidth),
                maxHeight / (float)Mathf.Max(1, camera.pixelHeight)));
            var state = CaptureView(camera, Mathf.Max(1, Mathf.RoundToInt(camera.pixelWidth * scale)),
                Mathf.Max(1, Mathf.RoundToInt(camera.pixelHeight * scale)));
            if (!hasView || !SameView(state, lastView))
            {
                state.revision = revision + 1;
                if (state.revision == 0) state.revision = 1;
                if (Bindings.app_update_preview_view(state))
                {
                    revision = state.revision;
                    lastView = state;
                    hasView = true;
                    hasFrame = false; // Never paste an old camera frame over moved geometry.
                    samples = 0;
                    ReleaseDenoisedFrame();
                }
            }
            RebuildComposite();
        }

        static void RebuildComposite()
        {
            if (composite == null) return;
            composite.Clear();
            if (!Shown || !hasFrame || !frame) return;
            material.SetFloat("_Opacity", Opacity);
            material.SetFloat("_DisplayMode", (float)DisplayMode);
            material.SetFloat("_ExposureEV", ExposureEV);
            material.SetFloat("_ReferenceGray", ReferenceGray);
            material.SetFloat("_HighlightLimit", HighlightLimit);
            Texture display = !Diagnostic && Denoise && denoisedFrame && denoisedGeneration == frameGeneration
                ? denoisedFrame : (Texture)frame;
            composite.Blit(display, BuiltinRenderTextureType.CameraTarget, material, 0);
        }

        static void ReleaseDenoisedFrame()
        {
            if (!denoisedFrame) return;
            UnityEngine.Object.DestroyImmediate(denoisedFrame);
            denoisedFrame = null;
            denoisedGeneration = 0;
        }

        static void UpdateDenoiseRequest()
        {
            bool enabled = Denoise && !Diagnostic;
            Bindings.app_set_preview_denoise(enabled);
            DenoiseStatus = Bindings.app_preview_denoise_status();
            if (!enabled || DenoiseStatus == 1 || DenoiseStatus == 4) ReleaseDenoisedFrame();
        }

        static void Update()
        {
            if (composite == null) return;
            if (!view) { Bake.StopPreview(); Detach(); return; }
            if (!Bake.IsPreviewRunning) { Detach(); return; }
            if (!hasView) { view.Repaint(); return; }
            if (EditorApplication.timeSinceStartup < nextRead) return;
            nextRead = EditorApplication.timeSinceStartup + 1.0 / 15;
            UpdateDenoiseRequest();
            int length = checked((int)(lastView.width * lastView.height * 16));
            if (pixels == null || pixels.Length != length) pixels = new byte[length];
            bool changed = false;
            uint received = Bindings.app_copy_preview_frame_hdr(pixels, (uint)length, revision, out ulong generation);
            if (received != 0)
            {
                if (generation != frameGeneration) ReleaseDenoisedFrame();
                frameGeneration = generation;
                if (!frame || frame.width != lastView.width || frame.height != lastView.height)
                {
                    if (frame) UnityEngine.Object.DestroyImmediate(frame);
                    frame = new Texture2D((int)lastView.width, (int)lastView.height, TextureFormat.RGBAFloat, false, true)
                    { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                }
                frame.LoadRawTextureData(pixels);
                frame.Apply(false, false);
                samples = received;
                hasFrame = true;
                changed = true;
            }
            if (Denoise && !Diagnostic && hasFrame &&
                Bindings.app_copy_preview_denoised(pixels, (uint)length, revision, frameGeneration) != 0)
            {
                if (!denoisedFrame)
                    denoisedFrame = new Texture2D((int)lastView.width, (int)lastView.height, TextureFormat.RGBAFloat, false, true)
                    { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                denoisedFrame.LoadRawTextureData(pixels);
                denoisedFrame.Apply(false, false);
                denoisedGeneration = frameGeneration;
                changed = true;
            }
            if (changed)
            {
                RebuildComposite();
                view.Repaint();
            }
        }

        internal static void Repaint()
        {
            if (view)
            {
                view.sceneViewState.showImageEffects = (UseSceneEffects && !Diagnostic) || originalImageEffects;
                view.sceneViewState.fxEnabled = (UseSceneEffects && !Diagnostic) || originalFxEnabled;
                UpdateDenoiseRequest();
                BeforeCamera(view.camera);
                view.Repaint();
            }
        }
    }

    [Overlay(typeof(SceneView), "Glim Preview", true)]
    internal sealed class GlimPreviewOverlay : Overlay, ITransientOverlay
    {
        public bool visible => PreviewViewport.Active;
        public override VisualElement CreatePanelContent()
        {
            var root = new VisualElement();
            root.style.minWidth = 250;
            var status = new Label("Starting previewвЂ¦");
            root.Add(status);
            status.schedule.Execute(() => status.text = PreviewViewport.Samples == 0 ? "Updating previewвЂ¦" :
                $"Glim В· {PreviewViewport.Samples} samples").Every(200);
            var show = new Toggle("Show preview") { value = PreviewViewport.Shown };
            show.RegisterValueChangedCallback(e => { PreviewViewport.Shown = e.newValue; PreviewViewport.Repaint(); });
            root.Add(show);
            var opacity = new Slider("Opacity", 0, 1) { value = PreviewViewport.Opacity };
            opacity.RegisterValueChangedCallback(e => { PreviewViewport.Opacity = e.newValue; PreviewViewport.Repaint(); });
            root.Add(opacity);
            var effects = new Toggle("Scene color correction / effects") { value = PreviewViewport.UseSceneEffects };
            effects.tooltip = "Apply the Scene view camera's existing post-processing to the HDR preview.";
            effects.RegisterValueChangedCallback(e => { PreviewViewport.UseSceneEffects = e.newValue; PreviewViewport.Repaint(); });
            root.Add(effects);
            var display = new EnumField("Display", PreviewViewport.DisplayMode);
            display.tooltip = "False Color maps HDR luminance to exposure bands. Highlights marks RGB channels above the selected HDR limit.";
            root.Add(display);
            var exposure = new Slider("Exposure (EV)", -12, 12) { value = PreviewViewport.ExposureEV, showInputField = true };
            exposure.tooltip = "Display exposure only: each +1 EV doubles brightness. Original HDR samples and baked lighting are unchanged.";
            exposure.RegisterValueChangedCallback(e => { PreviewViewport.ExposureEV = Mathf.Clamp(e.newValue, -12, 12); PreviewViewport.Repaint(); });
            root.Add(exposure);
            var reference = new FloatField("Reference gray") { value = PreviewViewport.ReferenceGray };
            reference.tooltip = "Linear luminance for the gray (0 EV) band. Default: 0.18. Material color and emission also affect measured brightness.";
            reference.RegisterValueChangedCallback(e => {
                PreviewViewport.ReferenceGray = FinitePositive(e.newValue, 0.18f);
                reference.SetValueWithoutNotify(PreviewViewport.ReferenceGray); PreviewViewport.Repaint();
            });
            root.Add(reference);
            var limit = new FloatField("HDR highlight limit") { value = PreviewViewport.HighlightLimit };
            limit.tooltip = "Warn when any exposed RGB channel reaches this value. HDR values above 1 are valid; this is your comparison threshold.";
            limit.RegisterValueChangedCallback(e => {
                PreviewViewport.HighlightLimit = FinitePositive(e.newValue, 1);
                limit.SetValueWithoutNotify(PreviewViewport.HighlightLimit); PreviewViewport.Repaint();
            });
            root.Add(limit);
            var legend = new VisualElement();
            var bands = new VisualElement(); bands.style.flexDirection = FlexDirection.Row;
            string[] labels = { "в€’3", "в€’1", "0", "+2", "+4", "+6" };
            Color[] colors = { new Color(0,0.2f,1), Color.cyan, Color.gray, Color.yellow, Color.red, Color.white };
            for (int i = 0; i < labels.Length; i++)
            {
                var swatch = new Label(labels[i]) { tooltip = labels[i] + " stops relative to reference gray" };
                swatch.style.backgroundColor = colors[i];
                swatch.style.color = i == 0 ? Color.white : Color.black;
                swatch.style.flexGrow = 1; swatch.style.unityTextAlign = TextAnchor.MiddleCenter;
                bands.Add(swatch);
            }
            legend.Add(bands); legend.Add(new Label("Exposure bands (EV)")); root.Add(legend);
            var diagnosticNote = new Label("Raw HDR В· grading and denoising bypassed");
            diagnosticNote.style.whiteSpace = WhiteSpace.Normal;
            root.Add(diagnosticNote);
            var denoise = new Toggle("Surface denoising (OIDN)") { value = PreviewViewport.Denoise };
            denoise.tooltip = "Open Image Denoise filters surface lighting using albedo and normal guides, then restores the original material color and emission. Runs in the background; raw HDR remains available.";
            denoise.RegisterValueChangedCallback(e => { PreviewViewport.Denoise = e.newValue; PreviewViewport.Repaint(); });
            root.Add(denoise);
            var denoiseStatus = new Label();
            denoiseStatus.style.whiteSpace = WhiteSpace.Normal;
            denoiseStatus.schedule.Execute(() => {
                denoiseStatus.text = PreviewViewport.DenoiseStatus switch {
                    1 => "OIDN: waiting for surface samples",
                    2 => "OIDN: denoising surface lightingвЂ¦",
                    3 => "OIDN: ready",
                    4 => "OIDN unavailable вЂ” showing raw lighting. See Console.",
                    _ => ""
                };
            }).Every(200);
            root.Add(denoiseStatus);
            void UpdateControls()
            {
                bool diagnostic = PreviewViewport.Diagnostic;
                effects.SetEnabled(!diagnostic); opacity.SetEnabled(!diagnostic);
                denoise.SetEnabled(!diagnostic);
                reference.style.display = legend.style.display = PreviewViewport.DisplayMode == PreviewDisplayMode.FalseColor ? DisplayStyle.Flex : DisplayStyle.None;
                limit.style.display = PreviewViewport.DisplayMode == PreviewDisplayMode.Highlights ? DisplayStyle.Flex : DisplayStyle.None;
                diagnosticNote.style.display = diagnostic ? DisplayStyle.Flex : DisplayStyle.None;
            }
            display.RegisterValueChangedCallback(e => { PreviewViewport.DisplayMode = (PreviewDisplayMode)e.newValue; UpdateControls(); PreviewViewport.Repaint(); });
            denoise.RegisterValueChangedCallback(_ => UpdateControls());
            UpdateControls();
            root.Add(new Button(() => { Bake.StopPreview(); PreviewViewport.Detach(); }) { text = "Stop Preview" });
            return root;
        }

        static float FinitePositive(float value, float fallback) =>
            float.IsNaN(value) || float.IsInfinity(value) ? fallback : Mathf.Clamp(value, 0.000001f, 65504);
    }
}
