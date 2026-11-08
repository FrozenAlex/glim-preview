using global::Glim;
using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace FrozenAlex.GlimPreview
{
    // Preview owns a separate native library and never invokes Glim's baking lifecycle.
    public static class Bake
    {
        static BakeContext _context;
        static Thread renderThread;
        static volatile bool running, complete, cancelRequested;
        static double nextUpdate;
        static readonly ConcurrentQueue<string> errors = new();
        public static readonly StringBuilder bakeMessages = new();
        public static bool IsPreviewRunning => running;
        public static string LastError { get; private set; }

        static Bake()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
            EditorApplication.quitting += Shutdown;
            EditorApplication.playModeStateChanged += state => {
                if (state == PlayModeStateChange.ExitingEditMode) Shutdown();
            };
        }

        [AOT.MonoPInvokeCallback(typeof(Bindings.ReadbackProbesCallback))]
        public static void OnReadbackLightprobes(Bindings.LightprobesReadbackData data) { }

        [AOT.MonoPInvokeCallback(typeof(Bindings.LogCallback))]
        public static void OnLogCalback(Bindings.LogData data)
        {
            // Never throw across a native callback or touch editor APIs on the render thread.
            if (data.ty == 1) errors.Enqueue(data.message.ToString());
        }

        public static void StopPreview()
        {
            if (!running) return;
            cancelRequested = true;
            Bindings.app_request_cancel();
        }

        static void Shutdown()
        {
            if (running) StopPreview();
            renderThread?.Join();
            Reset();
        }

        static void Reset()
        {
            EditorApplication.update -= Poll;
            PreviewViewport.Detach();
            _context?.DisposePreview();
            _context = null;
            renderThread = null;
            running = complete = cancelRequested = false;
            nextUpdate = 0;
            bakeMessages.Clear();
        }

        static void Poll()
        {
            while (errors.TryDequeue(out var error)) {
                LastError = error;
                Debug.LogError("Glim Live Preview: " + error);
            }
            if (complete) { renderThread?.Join(); Reset(); return; }
            if (!running || cancelRequested || EditorApplication.timeSinceStartup < nextUpdate) return;
            nextUpdate = EditorApplication.timeSinceStartup + 0.1;
            try {
                _context.UpdatePreviewSettings();
                _context.UpdatePreviewLights();
                _context.UpdatePreviewMaterials();
                _context.UpdatePreviewGeometry();
            }
            catch (Exception e) { LastError = e.Message; Debug.LogException(e); StopPreview(); }
        }

        public static void Start(GlimLightmapper baker, Bindings.GlimConfig config, SceneView previewView = null)
        {
            if (!config.is_preview) throw new ArgumentException("The add-on only supports preview rendering.");
            if (running || renderThread != null) throw new InvalidOperationException("Stop the current preview before opening another.");
            if (Application.platform != RuntimePlatform.WindowsEditor &&
                Application.platform != RuntimePlatform.LinuxEditor)
                throw new PlatformNotSupportedException(
                    "Glim Live Preview requires the Windows or Linux editor.");
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Open the preview in Edit mode.");
            if (global::Glim.Bake.IsBaking) throw new InvalidOperationException("Wait for Glim's bake or preview to finish first.");
            if (!baker || !baker.group) throw new ArgumentException("Select a Glim Lightmapper with a lightmap group.");
            if (string.IsNullOrEmpty(baker.gameObject.scene.path)) throw new InvalidOperationException("Save the scene before starting the preview.");
            LastError = null;
            while (errors.TryDequeue(out _)) { }
            bool embedded = previewView != null;
            try {
                _context = new BakeContext(baker, config);
                if (embedded) PreviewViewport.Attach(previewView, baker.previewWidth, baker.previewHeight);
            }
            catch { Reset(); throw; }
            var ctx = _context;
            running = true;
            complete = cancelRequested = false;
            EditorApplication.update += Poll;
            renderThread = new Thread(() => {
                IntPtr app = IntPtr.Zero;
                try {
                    var output = Bindings.FfiString.FromString(ctx.outputDir);
                    app = embedded ? Bindings.app_new_embedded_preview(config, output) : Bindings.app_new(config, output);
                    if (app == IntPtr.Zero) throw new Exception("Could not initialize the preview renderer.");
                    if (cancelRequested) Bindings.app_request_cancel();
                    foreach (var group in ctx.groups)
                    {
                        unsafe
                        {
                            fixed (Color32* albedoPtr = group.albedo)
                            fixed (byte* emissionsPtr = group.emission)
                            {
                                Bindings.app_add_lightmap_group(
                                    app,
                                    group.settings,
                                    (byte*)albedoPtr,
                                    (uint)(group.albedo.Length * 4),
                                    emissionsPtr,
                                    (uint)group.emission.Length
                                );
                            }
                        }
                        group.ClearPixels();
                    }

                    UploadScene(app, ctx);
                    Bindings.app_run(app);
                }
                catch (Exception e) { errors.Enqueue(e.ToString()); }
                finally {
                    if (app != IntPtr.Zero) Bindings.app_destroy(app);
                    complete = true;
                }
            });
            renderThread.SetApartmentState(ApartmentState.STA);
            renderThread.IsBackground = true;
            renderThread.Start();
        }
        static void UploadScene(IntPtr app, BakeContext ctx)
        {
            for (int i = 0; i < ctx.sceneMesh.Count; i++)
            {
                var data = ctx.sceneMesh[i];

                Debug.Assert(data.lightmapUVs.Length == data.mainUVs.Length);

                unsafe
                {
                    fixed (Vector3* vPtr = data.vertices)
                    fixed (Vector3* nPtr = data.normals)
                    fixed (Vector2* lmuvPtr = data.lightmapUVs)
                    fixed (Vector2* mainuvPtr = data.mainUVs)
                    fixed (int* iPtr = data.indices)
                    {
                        var exportedMesh = new Bindings.Mesh
                        {
                            vertices = vPtr,
                            normals = nPtr,
                            lightmapUVs = lmuvPtr,
                            mainUVs = mainuvPtr,
                            indices = (uint*)iPtr,
                            vertices_length = (uint)data.vertices.Length,
                            indices_length = (uint)data.indices.Length,
                            lightmap_group = data.groupIndex,
                            backface_gi = data.backfaceGI,
                            emissive = data.emissive,
                            fix_seams = data.fixSeams,
                            transparent = data.transparent,
                            no_shadows = data.noShadows,
                            alpha_index = data.alphaTextureIndex,
                            cutoff = data.cutoff,
                            alpha_wrap = data.alphaWrap,
                        };

                        Bindings.app_add_mesh(app, exportedMesh);
                    }
                }
            }
            // free
            ctx.sceneMesh = new();

            foreach (var light in ctx.bakeLights)
            {
                Bindings.app_add_light(app, light);
            }

            for (int i = 0; i < ctx.cookies.Count; i++)
            {
                CookiePixelsData cookie = ctx.cookies[i];

                unsafe
                {
                    fixed (Color32* pixels = cookie.pixels)
                    {
                        Bindings.app_add_light_cookie(app, pixels, (uint)cookie.pixels.Length * 4, cookie.width, cookie.height);
                    }
                }

                cookie.pixels = new Color32[0];
                ctx.cookies[i] = cookie;
            }

            for (int i = 0; i < ctx.alphas.Count; i++)
            {
                var a = ctx.alphas[i];

                unsafe
                {
                    fixed (byte* pixels = a.pixels)
                    {
                        Bindings.app_add_alpha_texture(app, pixels, (uint)a.pixels.Length, a.width, a.height);
                    }
                }
                a.pixels = new byte[0];
                ctx.alphas[i] = a;
            }

            foreach (var position in ctx.probePositions)
            {
                Vector3 p = (Vector3)position;
                float r = position.w;
                Bindings.app_add_probe(app, p, r);
            }

            uint size = (uint)Mathf.Sqrt(ctx.skyboxPixels.Length / 6);

            Bindings.app_set_skybox(app, ctx.skyboxPixels, (uint)ctx.skyboxPixels.Length * 4, size, size);
        }

    }
}

