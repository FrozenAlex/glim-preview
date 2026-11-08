Shader "Hidden/FrozenAlexGlimPreview/PreviewComposite"
{
    Properties
    {
        _MainTex ("Preview", 2D) = "black" {}
        _Opacity ("Opacity", Float) = 1
        _DisplayMode ("Display mode", Float) = 0
        _ExposureEV ("Exposure (stops)", Float) = 0
        _ReferenceGray ("Reference gray", Float) = 0.18
        _HighlightLimit ("Highlight limit", Float) = 1
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        CGINCLUDE
        #include "UnityCG.cginc"
        sampler2D _MainTex;
        float _Opacity, _DisplayMode, _ExposureEV, _ReferenceGray, _HighlightLimit;

        float Luminance(float3 color) { return dot(max(color, 0), float3(0.2126, 0.7152, 0.0722)); }

        // Display colors, indexed by stops relative to the selected linear gray.
        float3 FalseColor(float ev)
        {
            if (ev < -6) return lerp(float3(0,0,0), float3(0.2,0,0.5), saturate((ev + 10) / 4));
            if (ev < -3) return lerp(float3(0.2,0,0.5), float3(0,0.2,1), (ev + 6) / 3);
            if (ev < -1) return lerp(float3(0,0.2,1), float3(0,1,1), (ev + 3) / 2);
            if (ev < -0.25) return lerp(float3(0,1,1), float3(0.5,0.5,0.5), (ev + 1) / 0.75);
            if (ev <= 0.25) return float3(0.5,0.5,0.5);
            if (ev < 2) return lerp(float3(0.5,0.5,0.5), float3(1,1,0), (ev - 0.25) / 1.75);
            if (ev < 4) return lerp(float3(1,1,0), float3(1,0,0), (ev - 2) / 2);
            return lerp(float3(1,0,0), float3(1,1,1), saturate((ev - 4) / 2));
        }

        float4 Composite(v2f_img i) : SV_Target
        {
            // Vulkan readback rows start at the top; Unity texture rows start at the bottom.
            float3 color = tex2D(_MainTex, float2(i.uv.x, 1 - i.uv.y)).rgb * exp2(_ExposureEV);
            if (_DisplayMode > 0.5)
            {
                float3 diagnostic;
                if (_DisplayMode < 1.5)
                    diagnostic = FalseColor(log2(max(Luminance(color), 1e-8) / max(_ReferenceGray, 1e-6)));
                else
                {
                    float luma = Luminance(color);
                    diagnostic = LinearToGammaSpace((luma / (1 + luma) * 0.35).xxx);
                    // The threshold is a user-selected HDR level, not a claim
                    // that values above display white have clipped in the renderer.
                    if (max(color.r, max(color.g, color.b)) >= _HighlightLimit)
                    {
                        float stripe = step(0.5, frac((i.pos.x + i.pos.y) / 12));
                        diagnostic = lerp(float3(0.45,0,0), float3(1,0.12,0.04), stripe);
                    }
                }
                #ifndef UNITY_COLORSPACE_GAMMA
                    diagnostic = GammaToLinearSpace(diagnostic);
                #endif
                return float4(diagnostic, 1);
            }
            #ifdef UNITY_COLORSPACE_GAMMA
                color = LinearToGammaSpace(color);
            #endif
            return float4(color, _Opacity);
        }

        ENDCG

        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment Composite
            #pragma target 3.0
            ENDCG
        }
    }
}
