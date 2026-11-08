using global::Glim;
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace FrozenAlex.GlimPreview
{
    // Tracks only meshes in the original export. New topology needs a new preview,
    // whereas transforms can retain packed UVs and all material captures.
    internal sealed class PreviewGeometry
    {
        sealed class Entry
        {
            internal uint meshId;
            internal Transform source;
            internal Matrix4x4 initialInverse, submitted;
        }
        readonly List<Entry> entries = new();
        readonly List<Bindings.PreviewTransformState> changes = new();
        readonly List<Entry> changedEntries = new();
        readonly List<Matrix4x4> changedMatrices = new();
        static readonly Matrix4x4 NativeSpace = Matrix4x4.Scale(new Vector3(1, 1, -1));
        double nextUpdate;

        internal void Track(uint meshId, Transform source)
        {
            var initial = source.localToWorldMatrix;
            if (!Usable(initial))
            {
                Debug.LogWarning("Glim: a mesh with a zero or invalid initial scale cannot move in this preview. Reopen after fixing its scale.", source);
                return;
            }
            entries.Add(new Entry { meshId = meshId, source = source,
                initialInverse = initial.inverse, submitted = initial });
        }

        static bool Usable(Matrix4x4 matrix)
        {
            for (int i = 0; i < 16; i++)
                if (float.IsNaN(matrix[i]) || float.IsInfinity(matrix[i])) return false;
            return Mathf.Abs(matrix.determinant) > 1e-12f;
        }

        internal static Bindings.PreviewTransformState MakeState(uint id, Matrix4x4 current, Matrix4x4 initialInverse)
        {
            Matrix4x4 delta = NativeSpace * current * initialInverse * NativeSpace;
            Matrix4x4 normal = delta.inverse.transpose;
            return new Bindings.PreviewTransformState {
                meshId = id,
                position0 = delta.GetRow(0), position1 = delta.GetRow(1), position2 = delta.GetRow(2),
                normal0 = normal.GetRow(0), normal1 = normal.GetRow(1), normal2 = normal.GetRow(2),
                flipWinding = delta.determinant < 0 ? 1u : 0u,
            };
        }

        public void Update()
        {
            if (EditorApplication.timeSinceStartup < nextUpdate) return;
            nextUpdate = EditorApplication.timeSinceStartup + 0.2;
            changes.Clear(); changedEntries.Clear(); changedMatrices.Clear();
            foreach (var entry in entries)
            {
                if (!entry.source) continue;
                var current = entry.source.localToWorldMatrix;
                if (current == entry.submitted || !Usable(current)) continue;
                var state = MakeState(entry.meshId, current, entry.initialInverse);
                // A near-singular inverse can overflow even if its input was finite.
                bool valid = true;
                foreach (var row in new[] { state.position0, state.position1, state.position2, state.normal0, state.normal1, state.normal2 })
                    for (int j = 0; j < 4; j++) valid &= !float.IsNaN(row[j]) && !float.IsInfinity(row[j]);
                if (!valid) continue;
                changes.Add(state); changedEntries.Add(entry); changedMatrices.Add(current);
            }
            if (changes.Count == 0) return;
            var snapshot = changes.ToArray();
            if (!Bindings.app_update_preview_transforms(snapshot, (uint)snapshot.Length)) return;
            for (int i = 0; i < changedEntries.Count; i++) changedEntries[i].submitted = changedMatrices[i];
        }
    }
}
