using global::Glim;
using System;
using System.Runtime.InteropServices;
using System.Text;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;

namespace FrozenAlex.GlimPreview
{
    public static class Bindings
    {
        public enum CoordinateSystem : uint
        {
            Default = 0,
            Unity = 1,
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct GlimConfig
        {
            public readonly CoordinateSystem coordinate_system;

            [MarshalAs(UnmanagedType.I1)] public readonly bool is_preview;
            [MarshalAs(UnmanagedType.I1)] public readonly bool vulkan_validation_layers;
            [MarshalAs(UnmanagedType.I1)] public readonly bool seams_debug;
            [MarshalAs(UnmanagedType.I1)] public readonly bool hardware_rt;
            public readonly uint throttle_preview_ms;
            public readonly LightmapSettings preview_settings;

            public readonly Vector3 camera_position;
            public readonly Vector3 camera_forward;

            public readonly LogCallback log_callback;
            public readonly ReadbackProbesCallback lightprobes_read_callback;

            public readonly uint probe_samples;
            public readonly uint light_falloff;

            [MarshalAs(UnmanagedType.I1)] public readonly bool mis;

            public readonly uint direct_light_samples;
            public readonly uint direct_emission_samples;
            public readonly uint indirect_samples;
            public readonly uint bounce_count;
            public readonly LightmapMode lightmap_mode;

            public readonly float skybox_intensity;
            public readonly float indirect_intensity;
            public readonly float lightprobe_deringing;
            public readonly float emissive_multiplier;

            public readonly float ao_intensity;
            public readonly float ao_range;

            public readonly uint coverage_max_samples;
            public readonly float coverage_min_threshold;
            [MarshalAs(UnmanagedType.I1)] public readonly bool antialiasing;
            [MarshalAs(UnmanagedType.I1)] public readonly bool leak_detection;

            public GlimConfig(CoordinateSystem coordinate_system,
                               uint direct_light_samples,
                               uint direct_emission_samples,
                               uint indirect_samples,
                               uint bounce_count,
                               bool is_preview,
                               bool hardware_rt,
                               uint throttle_preview_ms,
                               LightmapSettings preview_settings,
                               Vector3 camera_position,
                               Vector3 camera_forward,
                               uint probe_samples,
                               LightFalloffType falloff,
                               bool mis,
                               LightmapMode lightmap_mode,
                               float skybox_intensity,
                               float indirect_intensity,
                               float lightprobe_deringing,
                               float emissive_multiplier,
                               float ao_intensity,
                               float ao_range,
                               uint coverage_max_samples,
                               float coverage_min_threshold,
                               bool antialiasing,
                               bool leak_detection
                               )
            {
                this.coordinate_system = coordinate_system;
                this.is_preview = is_preview;
                this.throttle_preview_ms = throttle_preview_ms;
                this.preview_settings = preview_settings;
                this.camera_position = camera_position;
                this.camera_forward = camera_forward;
                this.log_callback = Bake.OnLogCalback;
                this.lightprobes_read_callback = Bake.OnReadbackLightprobes;
                this.probe_samples = probe_samples;
                this.direct_light_samples = direct_light_samples;
                this.direct_emission_samples = direct_emission_samples;
                this.indirect_samples = indirect_samples;
                this.bounce_count = bounce_count;
                this.hardware_rt = hardware_rt;
#if GLIM_DEV
                this.vulkan_validation_layers = true;
#else
                this.vulkan_validation_layers = false;
#endif
                this.seams_debug = false;
                this.mis = mis;
                this.lightmap_mode = lightmap_mode;
                this.lightprobe_deringing = lightprobe_deringing;

                this.skybox_intensity = skybox_intensity;
                this.indirect_intensity = indirect_intensity;
                this.emissive_multiplier = emissive_multiplier;

                this.ao_intensity = ao_intensity;
                this.ao_range = ao_range;

                this.coverage_max_samples = coverage_max_samples;
                this.coverage_min_threshold = coverage_min_threshold;
                this.antialiasing = antialiasing;
                this.leak_detection = leak_detection;

                var currentPipeline = GraphicsSettings.currentRenderPipeline;
                uint autoFalloff = 0;
                if (currentPipeline == null) // BuiltIn
                {
                    autoFalloff = 1;
                }

                this.light_falloff = falloff switch
                {
                    LightFalloffType.Auto => autoFalloff,
                    LightFalloffType.InverseSquare => 0,
                    LightFalloffType.LegacyBIRP => 1,
                    _ => 0,
                };
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LightmapSettings
        {
            public readonly uint width;
            public readonly uint height;

            [MarshalAs(UnmanagedType.I1)] public readonly bool dilate;
            [MarshalAs(UnmanagedType.I1)] public readonly bool denoise;
            [MarshalAs(UnmanagedType.I1)] public readonly bool fix_seams;
            [MarshalAs(UnmanagedType.I1)] public readonly bool discard;

            public LightmapSettings(uint width, uint height, bool dilate, bool denoise, bool fix_seams, bool discard)
            {
                this.width = width;
                this.height = height;
                this.dilate = dilate;
                this.denoise = denoise;
                this.fix_seams = fix_seams;
                this.discard = discard;
            }

            public LightmapSettings(GlimLightmapGroup group) :
                this(group.Width, group.Height, group.dilate, group.denoise, group.fixSeams, group.discard)
            {
                return;
            }

        }

#if UNITY_EDITOR_WIN
        const string DLL_NAME = "glim_preview_addon_012_v1";
#else
        const string DLL_NAME = "glim";
#endif

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr app_new(GlimConfig config, FfiString output_dir);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr app_new_embedded_preview(GlimConfig config, FfiString output_dir);

        [StructLayout(LayoutKind.Sequential)]
        public struct PreviewView
        {
            public Vector3 origin, originDx, originDy, direction, directionDx, directionDy;
            public uint width, height, revision;
        }

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool app_update_preview_view(PreviewView view);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool app_update_preview_emission(uint group, [In] byte[] pixels, uint length);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint app_copy_preview_frame([Out] byte[] pixels, uint capacity, uint revision);

        [DllImport(DLL_NAME)]
        public static extern uint app_copy_preview_frame_hdr([Out] byte[] pixels, uint capacity, uint revision, out ulong generation);
        [DllImport(DLL_NAME)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool app_set_preview_denoise([MarshalAs(UnmanagedType.I1)] bool enabled);
        [DllImport(DLL_NAME)]
        public static extern uint app_preview_denoise_status();
        [DllImport(DLL_NAME)]
        public static extern uint app_copy_preview_denoised([Out] byte[] pixels, uint capacity, uint revision, ulong generation);

        [StructLayout(LayoutKind.Sequential)]
        public struct PreviewTransformState
        {
            public uint meshId;
            public Vector4 position0, position1, position2;
            public Vector4 normal0, normal1, normal2;
            public uint flipWinding;
        }

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool app_update_preview_transforms([In] PreviewTransformState[] states, uint count);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern ulong app_preview_scene_revision();
        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern void app_run(IntPtr app);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern void app_request_cancel();

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern void app_destroy(IntPtr app);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern void app_add_mesh(IntPtr app, Mesh mesh);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern void app_add_probe(IntPtr app, Vector3 position, float radius);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern void app_add_light(IntPtr app, Light light);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool app_update_preview_lights([In] PreviewLightState[] states, uint count);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool app_update_preview_indirect_multiplier(float multiplier);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static unsafe extern void app_add_light_cookie(IntPtr app, Color32* pixels, uint pixels_length, uint width, uint height);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static unsafe extern void app_add_alpha_texture(IntPtr app, byte* pixels, uint pixels_length, uint width, uint height);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void app_set_skybox(IntPtr app, Color[] pixels, uint pixels_length, uint width, uint height);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static unsafe extern void app_add_lightmap_group(IntPtr app, LightmapSettings settings, byte* albedoPixels, uint albedoPixelsLength, byte* emissionPixels, uint emissionPixelsLength);

        [StructLayout(LayoutKind.Sequential)]
        public unsafe struct Mesh
        {
            public Vector3* vertices;
            public Vector3* normals;
            public Vector2* lightmapUVs;
            public Vector2* mainUVs;
            public uint* indices;

            public uint vertices_length;
            public uint indices_length;
            public uint lightmap_group;

            [MarshalAs(UnmanagedType.I1)] public bool backface_gi;
            [MarshalAs(UnmanagedType.I1)] public bool emissive;
            [MarshalAs(UnmanagedType.I1)] public bool fix_seams;

            [MarshalAs(UnmanagedType.I1)] public bool transparent;
            [MarshalAs(UnmanagedType.I1)] public bool no_shadows;

            public int alpha_index;
            [MarshalAs(UnmanagedType.I1)] public bool alpha_wrap;
            public float cutoff;
        }

        public enum LightType : byte
        {
            Directional = 0,
            Point = 1,
            Spot = 2,
            Area = 3,
            Disc = 4,
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PreviewLightState
        {
            public Vector3 color;
            public Vector3 position;
            public Vector3 direction;
            public Vector3 up;
            public float range;
            public float spotInnerPercent;
            public float spotOuter;
            public Vector2 areaSize;
            public uint type;
            public uint castShadows;
            public uint mixed;
            public uint cookie;
            public float shadowRadiusOrAngle;

            public static PreviewLightState FromLight(Light light) => new PreviewLightState
            {
                color = light.color, position = light.position, direction = light.direction, up = light.up,
                range = light.range, spotInnerPercent = light.spot_inner_percent, spotOuter = light.spot_outer,
                areaSize = light.area_or_cookie_size, type = (uint)light.ty, castShadows = light.cast_shadows,
                mixed = light.mixed, cookie = light.cookie, shadowRadiusOrAngle = light.shadow_radius_or_angle,
            };

            public bool Matches(PreviewLightState other) =>
                color.Equals(other.color) && position.Equals(other.position) &&
                direction.Equals(other.direction) && up.Equals(other.up) && range.Equals(other.range) &&
                spotInnerPercent.Equals(other.spotInnerPercent) && spotOuter.Equals(other.spotOuter) &&
                areaSize.Equals(other.areaSize) && type == other.type && castShadows == other.castShadows &&
                mixed == other.mixed && cookie == other.cookie && shadowRadiusOrAngle.Equals(other.shadowRadiusOrAngle);
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Light
        {
            public Vector3 position;
            public LightType ty;
            public byte cast_shadows;
            public byte mixed;
            public sbyte occlusion_channel;

            public Vector3 direction;
            public float range;

            public Vector3 color;
            public float shadow_radius_or_angle;

            public float spot_inner_percent;
            public float spot_outer;
            public Vector2 area_or_cookie_size;

            public Vector3 up;
            public uint cookie;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 16)]
        public struct SHProbe
        {
            public Vector3 Position;
            public uint Pad0;

            // L0
            public Vector3 L0;
            public uint Pad1;

            // L1
            public Vector3 L1_1;
            public uint Pad2;
            public Vector3 L10;
            public uint Pad3;
            public Vector3 L11;
            public uint Pad4;

            // L2
            public Vector3 L2_2;
            public uint Pad5;
            public Vector3 L2_1;
            public uint Pad6;
            public Vector3 L20;
            public uint Pad7;
            public Vector3 L21;
            public uint Pad8;
            public Vector3 L22;
            public uint Pad9;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LightprobesReadbackData
        {
            public IntPtr probes;
            public uint probes_count;

            public unsafe SHProbe[] GetProbes()
            {
                int count = (int)probes_count;
                if (count == 0 || probes == IntPtr.Zero)
                {
                    return Array.Empty<SHProbe>();
                }

                SHProbe[] managedArray = new SHProbe[count];

                int structSize = UnsafeUtility.SizeOf<SHProbe>();
                long byteCount = count * structSize;

                fixed (SHProbe* destPtr = managedArray)
                {
                    Buffer.MemoryCopy((void*)probes, destPtr, byteCount, byteCount);
                }

                return managedArray;
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ReadbackProbesCallback(LightprobesReadbackData data);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void LogCallback(LogData data);

        [StructLayout(LayoutKind.Sequential)]
        public struct LogData
        {
            public uint ty;
            public float progress;
            public FfiString message;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FfiString
        {
            public IntPtr raw;
            public uint length;

            public override string ToString()
            {
                if (raw == IntPtr.Zero || length == 0)
                {
                    return string.Empty;
                }

                return Marshal.PtrToStringAnsi(raw, (int)length);
            }

            public static FfiString FromString(string value)
            {
                if (string.IsNullOrEmpty(value))
                {
                    return new FfiString
                    {
                        raw = IntPtr.Zero,
                        length = 0
                    };
                }

                IntPtr ptr = Marshal.StringToCoTaskMemUTF8(value);

                return new FfiString
                {
                    raw = ptr,
                    length = (uint)Encoding.UTF8.GetByteCount(value)
                };
            }
        }
    }
}
