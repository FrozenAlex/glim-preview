using global::Glim;
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;

namespace FrozenAlex.GlimPreview
{
    public class MetaTexture : IDisposable
    {
        RenderTexture _rt;

        int _resolution;

        public MetaTexture(int resolution, AtlasType type)
        {
            _resolution = resolution;

            // todo limit resolution
            var desc = new RenderTextureDescriptor
            {
                autoGenerateMips = false,
                width = resolution,
                height = resolution,
                useMipMap = false,
                mipCount = 1,
                colorFormat = type == AtlasType.Albedo ? RenderTextureFormat.ARGB32 : RenderTextureFormat.RGB111110Float,
                sRGB = false,
                volumeDepth = 1,
                msaaSamples = 1,
                dimension = TextureDimension.Tex2D
            };

            _rt = new RenderTexture(desc);
            _rt.filterMode = FilterMode.Point;
        }

        public enum AtlasType
        {
            Albedo,
            Emission,
        }

        public AsyncGPUReadbackRequest CreateAtlas(IList<Renderer> renderers, AtlasType type, Action<AsyncGPUReadbackRequest> onComplete = null
#if GLIM_013_OR_NEWER
            , ConservativeMeshCache conservativeMeshes = null, bool fastCapture = false
#endif
        )
        {
            using var cmd = new CommandBuffer();
            cmd.SetRenderTarget(_rt);

            if (type == AtlasType.Albedo)
            {
                cmd.ClearRenderTarget(true, true, Color.gray);
            }
            else
            {
                cmd.ClearRenderTarget(true, true, Color.black);
            }

            RenderMeta(renderers, type, cmd
#if GLIM_013_OR_NEWER
                , conservativeMeshes, fastCapture
#endif
            );

            Graphics.ExecuteCommandBuffer(cmd);

            var format = type == AtlasType.Albedo ? GraphicsFormat.R8G8B8A8_UNorm : GraphicsFormat.B10G11R11_UFloatPack32;

            return AsyncGPUReadback.Request(_rt, 0, format, onComplete);
        }

        static int _Cutoff = Shader.PropertyToID("_Cutoff");
        static int _MainTex = Shader.PropertyToID("_MainTex");
        static int _BaseMap = Shader.PropertyToID("_BaseMap");
        static int _Color = Shader.PropertyToID("_Color");
        static int _BaseColor = Shader.PropertyToID("_BaseColor");

        Color GetMaterialColor(Material mat)
        {
            if (mat.HasProperty(_Color))
            {
                return mat.GetColor(_Color);
            }
            if (mat.HasProperty(_BaseColor))
            {
                return mat.GetColor(_BaseColor);
            }
            return Color.white;
        }

        static Texture GetMaterialAlbedo(Material mat)
        {
            if (mat.HasProperty(_MainTex))
            {
                return mat.GetTexture(_MainTex);
            }
            if (mat.HasProperty(_BaseMap))
            {
                return mat.GetTexture(_BaseMap);
            }
            return Texture2D.whiteTexture;
        }

        public static Texture2D GetMaterialAlpha(Material mat)
        {
            Texture tex = GetMaterialAlbedo(mat);

            if (tex is Texture2D tex2d)
            {
                int components = (int)GraphicsFormatUtility.GetComponentCount(tex2d.graphicsFormat);
                bool alpha8 = tex2d.format == TextureFormat.Alpha8;

                if (alpha8 || components == 4)
                {
                    return tex2d;
                }
            }

            return Texture2D.whiteTexture;
        }

        Vector2 GetMaterialAlbedoScale(Material mat)
        {
            if (mat.HasProperty(_MainTex))
            {
                return mat.GetTextureScale(_MainTex);
            }
            if (mat.HasProperty(_BaseMap))
            {
                return mat.GetTextureScale(_BaseMap);
            }
            return Vector2.one;
        }

        Vector2 GetMaterialAlbedoOffset(Material mat)
        {
            if (mat.HasProperty(_MainTex))
            {
                return mat.GetTextureOffset(_MainTex);
            }
            if (mat.HasProperty(_BaseMap))
            {
                return mat.GetTextureOffset(_BaseMap);
            }
            return Vector2.zero;
        }

        public static float GetMaterialCutoff(Material source)
        {
            string renderType = source.GetTag("RenderType", false, "");
            if (source.HasProperty(_Cutoff) && renderType == "TransparentCutout")
            {
                return source.GetFloat(_Cutoff);
            }
            else
            {
                return 0.5f;
            }
        }

        void RenderMeta(IList<Renderer> renderers, AtlasType type, CommandBuffer cmd
#if GLIM_013_OR_NEWER
            , ConservativeMeshCache conservativeMeshes, bool fastCapture
#endif
        )
        {
            float near = 0.01f;
            float far = 100f;

            // Ortho projection matrix
            Matrix4x4 proj = Matrix4x4.Ortho(0, 1, 0, 1, near, far);
            // View matrix (like a top-down or front view)
            Vector3 camPos = new Vector3(0, 0, -10);
            Vector3 target = Vector3.zero;
            Vector3 up = Vector3.up;
            Matrix4x4 view = Matrix4x4.LookAt(camPos, target, up);
            cmd.SetViewProjectionMatrices(view, proj);

            cmd.SetGlobalVector("unity_MetaVertexControl", new Vector4(1, 0, 0, 0));
            cmd.SetGlobalFloat("unity_OneOverOutputBoost", 1.0f);
            cmd.SetGlobalFloat("unity_UseLinearSpace", 1.0f);

            if (type == AtlasType.Albedo)
            {
                cmd.SetGlobalVector("unity_MetaFragmentControl", new Vector4(1, 0, 0, 0));
                cmd.SetGlobalFloat("unity_MaxOutputValue", 1.0f);
            }
            else if (type == AtlasType.Emission)
            {
                cmd.SetGlobalVector("unity_MetaFragmentControl", new Vector4(0, 1, 0, 0));
                cmd.SetGlobalFloat("unity_MaxOutputValue", 100.0f);
            }

            cmd.SetGlobalFloat("unity_VisualizationMode", -1);

            cmd.SetGlobalVector("unity_LightmapST", new Vector4(1f, 1f, 0, 0));

            // https://ndotl.wordpress.com/2018/08/29/baking-artifact-free-lightmaps/#raster
            Vector4[] uvOffset = new Vector4[]
            {
                    new (1f, 1f, -2, -2f),
                    new (1f, 1f, 2, -2f),
                    new (1f, 1f, -2, 2f),
                    new (1f, 1f, 2f, 2f),
                    new (1f, 1f, -1f, -2f),
                    new (1f, 1f, 1f, -2f),
                    new (1f, 1f, -2f, -1f),
                    new (1f, 1f, 2f, -1f),
                    new (1f, 1f, -2f, 1f),
                    new (1f, 1f, 2f, 1f),
                    new (1f, 1f, -1f, 2f),
                    new (1f, 1f, 1f, 2f),
                    new (1f, 1f, -2f, 0f),
                    new (1f, 1f, 2f, 0f),
                    new (1f, 1f, 0f, -2f),
                    new (1f, 1f, 0f, 2f),
                    new (1f, 1f, -1f, -1f),
                    new (1f, 1f, 1f, -1f),
                    new (1f, 1f, -1f, 0f),
                    new (1f, 1f, 1f, 0f),
                    new (1f, 1f, -1f, 1f),
                    new (1f, 1f, 1f, 1f),
                    new (1f, 1f, 0f, -1f),
                    new (1f, 1f, 0f, 1f),
                    new (1f, 1f, 0f, 0f)
            };

            float halfTexelSize = (1.0f / _resolution) * 0.5f;
            for (int i = 0; i < uvOffset.Length; i++)
            {
                uvOffset[i].z *= halfTexelSize;
                uvOffset[i].w *= halfTexelSize;
            }

            var unity_LightmapST = Shader.PropertyToID("unity_LightmapST");

            bool flipY = !SystemInfo.graphicsUVStartsAtTop;


            var items = new List<MetaDrawItem>(renderers.Count);
            foreach (var renderer in renderers)
            {
                if (!renderer || !renderer.TryGetComponent<MeshFilter>(out var filter)) continue;
                var mesh = filter.sharedMesh;
                if (!mesh) continue;
                if (renderer is MeshRenderer mr)
                {
                    var stream = mr.enlightenVertexStream;
                    if (!stream) stream = mr.additionalVertexStreams;
                    if (stream && stream.HasVertexAttribute(VertexAttribute.TexCoord1)) mesh = stream;
                }

                var materials = renderer.sharedMaterials;
                var passes = new int[Mathf.Min(materials.Length, mesh.subMeshCount)];
                bool draws = false;
                for (int i = 0; i < passes.Length; i++)
                {
                    var mat = materials[i];
                    passes[i] = mat && (type == AtlasType.Albedo || IsMaterialEmissive(mat))
                        ? mat.FindPass("META") : -1;
                    draws |= passes[i] >= 0;
                }
                if (!draws) continue;

                var block = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(block);
                var lightmapSo = renderer.lightmapScaleOffset;
                var drawSo = lightmapSo;
                if (flipY)
                {
                    drawSo.y = -drawSo.y;
                    drawSo.w = 1.0f - drawSo.w;
                }
                items.Add(new MetaDrawItem
                {
                    mesh = mesh,
                    localToWorld = renderer.localToWorldMatrix,
                    properties = block,
                    materials = materials,
                    passes = passes,
                    lightmapSo = lightmapSo,
                    drawSo = drawSo,
                });
            }

#if GLIM_013_OR_NEWER
            if (conservativeMeshes != null)
            {
                var requests = new List<(Mesh, Vector4)>(items.Count);
                foreach (var item in items) requests.Add((item.mesh, item.lightmapSo));
                conservativeMeshes.Build(requests);
                // Fill texels touched by triangle edges before regular META draws overwrite them.
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    var expanded = conservativeMeshes.Get(item.mesh, item.lightmapSo);
                    if (!expanded) continue;
                    cmd.SetGlobalVector(unity_LightmapST, item.drawSo);
                    DrawMetaSubmeshes(cmd, expanded, item);
                    item.centerOnly = fastCapture;
                    items[i] = item;
                }
            }
#endif
            foreach (var item in items)
            {
#if GLIM_013_OR_NEWER
                if (item.centerOnly)
                {
                    cmd.SetGlobalVector(unity_LightmapST, item.drawSo);
                    DrawMetaSubmeshes(cmd, item.mesh, item);
                    continue;
                }
#endif
                foreach (var offset in uvOffset)
                {
                    var so = item.drawSo;
                    so.z += offset.z;
                    so.w += offset.w;
                    cmd.SetGlobalVector(unity_LightmapST, so);
                    DrawMetaSubmeshes(cmd, item.mesh, item);
                }
            }
        }

        struct MetaDrawItem
        {
            public Mesh mesh;
            public Matrix4x4 localToWorld;
            public MaterialPropertyBlock properties;
            public Material[] materials;
            public int[] passes;
#if GLIM_013_OR_NEWER
            public bool centerOnly;
#endif
            public Vector4 lightmapSo, drawSo;
        }

        static void DrawMetaSubmeshes(CommandBuffer cmd, Mesh mesh, in MetaDrawItem item)
        {
            int count = Mathf.Min(item.passes.Length, mesh.subMeshCount);
            for (int i = 0; i < count; i++)
                if (item.passes[i] >= 0)
                    cmd.DrawMesh(mesh, item.localToWorld, item.materials[i], i, item.passes[i], item.properties);
        }

        public static bool IsMaterialEmissive(Material mat)
        {
            if (!mat) return false;
            if (!mat.shader) return false;

            return mat.globalIlluminationFlags.HasFlag(MaterialGlobalIlluminationFlags.BakedEmissive);
        }


        public static readonly HashSet<string> TransparentTags = new()
        {
            "Transparent",
            "TransparentCutout",
            "TreeTransparentCutout",
            "GrassBillboard",
            "Grass",
            "TreeLeaf"
        };

        public static bool IsMaterialTransparent(Material mat)
        {
            if (!mat) return false;
            if (!mat.shader) return false;

            {
                string type = mat.GetTag("SurfaceType", false, "");
                if (TransparentTags.Contains(type)) return true;
            }

            {
                string type = mat.GetTag("RenderType", false, "");
                if (TransparentTags.Contains(type)) return true;
            }

            // if (mat.renderQueue >= (int)RenderQueue.AlphaTest)
            // {
            //     return true;
            // }

            return false;
        }

        public static int GetMaterialAlbedoProperty(Material mat)
        {
            if (mat.HasProperty(_MainTex)) return _MainTex;
            if (mat.HasProperty(_BaseMap)) return _BaseMap;
            return -1;
        }

        public static void GetMaterialAlbedoScaleOffset(Material mat, out Vector2 scale, out Vector2 offset)
        {
            int prop = GetMaterialAlbedoProperty(mat);
            if (prop >= 0)
            {
                scale = mat.GetTextureScale(prop);
                offset = mat.GetTextureOffset(prop);
            }
            else
            {
                scale = Vector2.one;
                offset = Vector2.zero;
            }
        }

        public static float GetMaterialColorAlpha(Material mat)
        {
            if (mat.HasProperty(_BaseColor)) return mat.GetColor(_BaseColor).a;
            if (mat.HasProperty(_Color)) return mat.GetColor(_Color).a;
            return 1.0f;
        }

        public void Dispose()
        {
            if (_rt)
            {
                Editor.DestroyImmediate(_rt);
            }
        }
    }
}