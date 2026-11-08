using global::Glim;
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace FrozenAlex.GlimPreview
{
    // Captures META output only when the corresponding material inputs change.
    // Base color/texture inputs are tracked separately from emission. Unity and GPU
    // readback operations stay on the editor thread.
    internal sealed class PreviewMaterials : IDisposable
    {
        readonly List<BakeContextGroup> groups;
        readonly Dictionary<Material, int> materialHashes = new();
        readonly Dictionary<Material, int> albedoHashes = new();
        readonly Dictionary<Shader, (int revision, int[] colors, int[] textures)> albedoInputs = new();
        readonly List<Material> materials = new();
        readonly List<int> textureProperties = new();
        readonly int[] submittedHashes;
        MetaTexture.AtlasType pendingType;
        internal int AlbedoCaptureCount { get; private set; }
        internal int EmissionCaptureCount { get; private set; }
        readonly MaterialPropertyBlock properties = new();
        MetaTexture capture;
        AsyncGPUReadbackRequest request;
        int pendingGroup = -1, pendingHash, nextSlot;
        byte[] pendingPixels;
        bool readbackFinished, readbackFailed, disposed;
        double nextScan;

        public PreviewMaterials(List<BakeContextGroup> groups)
        {
            this.groups = groups;
            submittedHashes = new int[groups.Count * 2];
            for (int i = 0; i < submittedHashes.Length; i++)
                submittedHashes[i] = GroupHash(groups[i / 2], (MetaTexture.AtlasType)(i % 2));
        }

        internal static List<MeshRenderer> CloneRenderers(List<MeshRenderer> sources, List<MeshRenderer> owned, List<int> copyIds)
        {
            var result = new List<MeshRenderer>(sources.Count);
            foreach (var source in sources)
            {
                var go = new GameObject("Glim preview: " + source.name) { hideFlags = HideFlags.HideAndDontSave };
                copyIds.Add(PreviewRendererCopy.Register(go));
                // Parenting with an identity transform preserves shear and nonuniform scale.
                go.transform.SetParent(source.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = source.GetComponent<MeshFilter>().sharedMesh;
                var clone = go.AddComponent<MeshRenderer>();
                owned.Add(clone);
                EditorUtility.CopySerialized(source, clone);
                var block = new MaterialPropertyBlock();
                source.GetPropertyBlock(block);
                clone.SetPropertyBlock(block);
                clone.enabled = false; // Exported explicitly, never drawn by the Scene camera.
                result.Add(clone);
            }
            return result;
        }

        int MaterialHash(Material material)
        {
            if (!material) return 0;
            if (materialHashes.TryGetValue(material, out int cached)) return cached;
            unchecked
            {
                int hash = material.ComputeCRC();
                hash = hash * 31 + (int)material.globalIlluminationFlags;
                hash = hash * 31 + EditorUtility.GetDirtyCount(material);
                if (material.shader) hash = hash * 31 + EditorUtility.GetDirtyCount(material.shader);
                textureProperties.Clear();
                material.GetTexturePropertyNameIDs(textureProperties);
                foreach (int property in textureProperties)
                {
                    var texture = material.GetTexture(property);
                    if (!texture) continue;
                    hash = hash * 31 + texture.GetInstanceID();
                    hash = hash * 31 + (int)texture.updateCount;
                    hash = hash * 31 + EditorUtility.GetDirtyCount(texture);
                    hash = hash * 31 + texture.imageContentsHash.GetHashCode();
                }
                materialHashes[material] = hash;
                return hash;
            }
        }

        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");

        (int revision, int[] colors, int[] textures) AlbedoInputs(Shader shader)
        {
            int revision = EditorUtility.GetDirtyCount(shader);
            if (albedoInputs.TryGetValue(shader, out var cached) && cached.revision == revision) return cached;
            var colors = new List<int>();
            var textures = new List<int>();
            for (int i = 0; i < shader.GetPropertyCount(); i++)
            {
                int id = shader.GetPropertyNameId(i);
                var flags = shader.GetPropertyFlags(i);
                var type = shader.GetPropertyType(i);
                if (type == ShaderPropertyType.Color &&
                    (id == ColorId || id == BaseColorId || (flags & ShaderPropertyFlags.MainColor) != 0)) colors.Add(id);
                else if (type == ShaderPropertyType.Texture &&
                    (id == MainTexId || id == BaseMapId || (flags & ShaderPropertyFlags.MainTexture) != 0)) textures.Add(id);
            }
            return albedoInputs[shader] = (revision, colors.ToArray(), textures.ToArray());
        }

        int AlbedoHash(Material material)
        {
            if (!material || !material.shader) return 0;
            if (albedoHashes.TryGetValue(material, out int cached)) return cached;
            unchecked
            {
                var shader = material.shader;
                var inputs = AlbedoInputs(shader);
                int hash = shader.GetInstanceID() * 31 + inputs.revision;
                // Use Unity's MainColor/MainTexture annotations as well as the usual
                // built-in/SRP names. Emission edits and material dirty counts do not
                // invalidate this atlas. Custom secondary shader inputs need restart.
                foreach (int id in inputs.colors)
                    hash = hash * 31 + material.GetColor(id).GetHashCode();
                foreach (int id in inputs.textures)
                {
                    hash = hash * 31 + material.GetTextureScale(id).GetHashCode();
                    hash = hash * 31 + material.GetTextureOffset(id).GetHashCode();
                    var texture = material.GetTexture(id);
                    hash = hash * 31 + (texture ? texture.GetInstanceID() : 0);
                    if (!texture) continue;
                    hash = hash * 31 + (int)texture.updateCount;
                    hash = hash * 31 + EditorUtility.GetDirtyCount(texture);
                    hash = hash * 31 + texture.imageContentsHash.GetHashCode();
                }
                albedoHashes[material] = hash;
                return hash;
            }
        }

        int GroupHash(BakeContextGroup group, MetaTexture.AtlasType type)
        {
            if (group.sourceRenderers == null) return 0; // Terrain is exported separately.
            unchecked
            {
                int hash = 17;
                foreach (var renderer in group.sourceRenderers)
                {
                    if (!renderer) { hash *= 31; continue; }
                    renderer.GetSharedMaterials(materials);
                    hash = hash * 31 + materials.Count;
                    foreach (var material in materials)
                    {
                        // Non-emissive materials draw no pixels in an emission atlas.
                        bool included = type == MetaTexture.AtlasType.Albedo || MetaTexture.IsMaterialEmissive(material);
                        hash = hash * 31 + (included && material ? material.GetInstanceID() : 0);
                        hash = hash * 31 + (included ? (type == MetaTexture.AtlasType.Albedo ? AlbedoHash(material) : MaterialHash(material)) : 0);
                    }
                }
                return hash;
            }
        }

        void SyncMaterials(BakeContextGroup group)
        {
            for (int i = 0; i < group.sourceRenderers.Length; i++)
            {
                var source = group.sourceRenderers[i];
                var clone = group.previewRenderers[i];
                if (!source || !clone) continue;
                source.GetSharedMaterials(materials);
                clone.sharedMaterials = materials.ToArray();
                source.GetPropertyBlock(properties);
                clone.SetPropertyBlock(properties);
            }
        }

        static bool CanCapture(BakeContextGroup group)
        {
            if (group.sourceRenderers == null) return false;
            // The preview keeps its original geometry until it is reopened.
            // A missing copy or replaced mesh cannot reproduce that atlas; keep
            // its last pixels instead of clearing part of the snapshot.
            for (int i = 0; i < group.sourceRenderers.Length; i++)
            {
                var source = group.sourceRenderers[i];
                var clone = group.previewRenderers[i];
                if (!source || !clone) return false;
                var filter = source.GetComponent<MeshFilter>();
                if (!filter || filter.sharedMesh != clone.GetComponent<MeshFilter>().sharedMesh) return false;
            }
            return true;
        }

        public void Update()
        {
            if (pendingGroup >= 0)
            {
                if (!CanCapture(groups[pendingGroup]))
                {
                    // Let an already-issued readback finish without blocking
                    // the editor, then discard it if its export is obsolete.
                    if (!readbackFinished) return;
                    FinishCapture();
                    return;
                }
                if (pendingPixels == null)
                {
                    if (!readbackFinished) return;
                    if (readbackFailed)
                    {
                        Debug.LogWarning($"Glim: {pendingType} atlas readback failed; it will be retried.");
                        FinishCapture();
                        nextScan = EditorApplication.timeSinceStartup + 2;
                        return;
                    }
                }
                if (capture != null) { capture.Dispose(); capture = null; }
                bool accepted = pendingType == MetaTexture.AtlasType.Albedo
                    ? Bindings.app_update_preview_albedo((uint)pendingGroup, pendingPixels, (uint)pendingPixels.Length)
                    : Bindings.app_update_preview_emission((uint)pendingGroup, pendingPixels, (uint)pendingPixels.Length);
                if (!accepted) return; // Initial native scene export may still be uploading.
                submittedHashes[pendingGroup * 2 + (int)pendingType] = pendingHash;
                FinishCapture();
            }
            if (EditorApplication.timeSinceStartup < nextScan) return;
            nextScan = EditorApplication.timeSinceStartup + 0.35;
            materialHashes.Clear();
            albedoHashes.Clear();
            for (int n = 0; n < submittedHashes.Length; n++)
            {
                int slot = (nextSlot + n) % submittedHashes.Length;
                int i = slot / 2;
                var type = (MetaTexture.AtlasType)(slot % 2);
                var group = groups[i];
                int hash = GroupHash(group, type);
                if (hash == submittedHashes[slot] || !CanCapture(group)) continue;
                SyncMaterials(group);
                capture = new MetaTexture((int)group.settings.width, type);
                pendingType = type;
                if (type == MetaTexture.AtlasType.Albedo) AlbedoCaptureCount++;
                else EmissionCaptureCount++;
                pendingGroup = i;
                pendingHash = hash;
                readbackFinished = readbackFailed = false;
                request = capture.CreateAtlas(group.previewRenderers, type, completed =>
                {
                    if (disposed) return;
                    // Unity retains completed readback data for only one frame.
                    // Copy in the completion callback, not in the throttled poll.
                    readbackFailed = completed.hasError;
                    if (!readbackFailed) pendingPixels = completed.GetData<byte>().ToArray();
                    readbackFinished = true;
                });
                nextSlot = (slot + 1) % submittedHashes.Length;
                break; // At most one atlas readback in flight; coalesce slider changes.
            }
        }

        void FinishCapture()
        {
            capture?.Dispose();
            capture = null;
            pendingPixels = null;
            pendingGroup = -1;
        }

        public void Dispose()
        {
            disposed = true;
            if (capture != null && !readbackFinished) request.WaitForCompletion();
            FinishCapture();
        }
    }
}
