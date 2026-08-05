// Minimal SDF text shader for uGUI: samples the R8 glyph atlas
// (Texture2DArray, layer in uv.z) and does screen-space antialiased
// coverage from the 0.5 isoline. Stencil block keeps RectMask/Mask working.
Shader "OneText/SDF"
{
    Properties
    {
        _GlyphTex ("Glyph Atlas", 2DArray) = "" {}
        _ColorTex ("Colour Atlas", 2DArray) = "" {}
        _GlyphTexelSize ("Glyph Atlas Texel Size", Vector) = (0, 0, 0, 0)

        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
        }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        Lighting Off
        ZWrite Off
        // The canvas system drives this global: overlay canvases get Always,
        // world-space canvases get LEqual so text can be occluded correctly.
        // Code that renders a canvas by hand (editor/headless capture) must set
        // it itself — see OneTextProofScene.
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"

            UNITY_DECLARE_TEX2DARRAY(_GlyphTex);
            // Colour glyphs (emoji, sprites) live in a second array: a
            // Texture2DArray has one format for all its slices, so colour
            // cannot share the R8 SDF array, and the coverage maths below would
            // binarize a colour image into a silhouette anyway. Same material
            // and same draw call regardless — the choice rides in a vertex
            // channel, not in a second pass.
            UNITY_DECLARE_TEX2DARRAY(_ColorTex);

            // Set from C# beside _GlyphTex rather than relied on from Unity's
            // automatic _TexelSize, which is documented for 2D textures and not
            // for arrays. The shadow offset is the only thing that reads it.
            float4 _GlyphTexelSize;

            // One reach — how far outside the ink the field still knows
            // anything — in texels. Must equal GlyphRasterizer.SpreadPixels,
            // which is also GlyphRasterizer.Padding; a disagreement here draws
            // shadows at the wrong distance with nothing to catch it.
            #define REACH_TEXELS 4.0
            // The same reach in field values: the rasterizer encodes
            // 0.5 - signedDistance / (2 * spread), so one reach is half of the
            // 0..1 range.
            #define REACH_FIELD 0.5

            // Decoration parameters ride in channels the mesh already carried —
            // TEXCOORD1, TEXCOORD3 and TEXCOORD2.yz used to be the second and
            // third sweep-line samples, dead since joints moved inside the
            // field with cluster-union rasterization. The full budget, and why
            // it is two bytes per float and not three, is written above
            // OneTextLabel.AddVert; this end of the contract only unpacks it.
            struct appdata
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float4 uv : TEXCOORD0;    // xy = tile uv, z = layer, w = tile v-min
                float4 decoA : TEXCOORD1; // outline R|G, outline B|width, shadow R|G, shadow B|A
                float4 bounds : TEXCOORD2; // x = tile v-max, yz = tile u-min/u-max, w = 1 for colour
                float4 decoB : TEXCOORD3; // glow R|G, glow B|A, shadow dx|dy, shadow soft|glow radius
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                fixed4 color : COLOR;
                float4 uv : TEXCOORD0;
                float4 decoA : TEXCOORD1;
                float4 bounds : TEXCOORD2;
                float4 decoB : TEXCOORD3;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.uv = v.uv;
                o.decoA = v.decoA;
                o.bounds = v.bounds;
                o.decoB = v.decoB;
                return o;
            }

            // Two bytes back out of one float. The round() is the whole point:
            // the packed value is an exact integer below 65536, so an
            // interpolator that returns it off by a fraction is snapped back
            // before the high byte can borrow from the low one.
            float2 Unpack(float packed)
            {
                float v = floor(packed + 0.5);
                float hi = floor(v * (1.0 / 256.0));
                return float2(hi, v - hi * 256.0) * (1.0 / 255.0);
            }

            /// A byte with 128 meaning zero, back to -1..1.
            float Signed(float unit)
            {
                return (unit * 255.0 - 128.0) * (1.0 / 127.0);
            }

            // Clamping into the tile is what keeps an offset sample from reading
            // the glyph the atlas shelf packed next door and drawing it as this
            // one's shadow. The ring the clamp lands on is padding the
            // rasterizer guarantees is a full reach from the ink, so outside the
            // tile the field reads exactly 0 — the right answer, not a fallback.
            float Field(float2 uv, float2 tileMin, float2 tileMax, float layer)
            {
                float3 s = float3(clamp(uv, tileMin, tileMax), layer);
                return UNITY_SAMPLE_TEX2DARRAY(_GlyphTex, s).r;
            }

            /// Premultiplied source over premultiplied destination.
            float4 Over(float4 src, float4 dst)
            {
                return src + dst * (1.0 - src.a);
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // A colour tile is a picture, not a distance field: sample it
                // straight and tint by the vertex colour, which is what lets a
                // label fade its emoji out with the rest of its text.
                //
                // Decorations are skipped here rather than approximated. There
                // is no distance to threshold, so an outline would have to be
                // guessed from an alpha edge and a glow from a blur nobody has
                // budgeted; and the colour atlas packs its tiles without the
                // padding ring the SDF one has, so an offset sample has no
                // guaranteed-empty margin to land in. Two different wrong
                // pictures, and drawing a bad halo round an emoji is worse than
                // drawing none.
                if (i.bounds.w > 0.5)
                {
                    float3 s = float3(i.uv.x, clamp(i.uv.y, i.uv.w, i.bounds.x), i.uv.z);
                    return UNITY_SAMPLE_TEX2DARRAY(_ColorTex, s) * i.color;
                }

                float2 tileMin = float2(i.bounds.y, i.uv.w);
                float2 tileMax = float2(i.bounds.z, i.bounds.x);
                float d = Field(i.uv.xy, tileMin, tileMax, i.uv.z);
                float aa = max(fwidth(d), 1e-4);
                float faceA = i.color.a * smoothstep(0.5 - aa, 0.5 + aa, d);

                // Every byte a decoration could be visible through is the low
                // half of one of these three floats, so their sum is zero for
                // undecorated text and the whole of the work below is skipped
                // for the overwhelmingly common case.
                if (i.decoA.y + i.decoA.w + i.decoB.y < 0.5)
                {
                    fixed4 plain = i.color;
                    plain.a = faceA;
                    return plain;
                }

                float2 outlineRG = Unpack(i.decoA.x);
                float2 outlineBW = Unpack(i.decoA.y);
                float2 shadowRG = Unpack(i.decoA.z);
                float2 shadowBA = Unpack(i.decoA.w);
                float2 glowRG = Unpack(i.decoB.x);
                float2 glowBA = Unpack(i.decoB.y);
                float2 offset = Unpack(i.decoB.z);
                float2 softRadius = Unpack(i.decoB.w);

                // The shadow: one sample of the same field, taken from where
                // this pixel would have been had the glyph sat at the offset.
                // Softness widens the antialiasing band instead of blurring
                // anything, which is what makes it free.
                float2 shadowUv = i.uv.xy - float2(Signed(offset.x), Signed(offset.y))
                    * REACH_TEXELS * _GlyphTexelSize.xy;
                float shadowD = Field(shadowUv, tileMin, tileMax, i.uv.z);
                float soft = max(aa, softRadius.x * REACH_FIELD);
                float shadowA = shadowBA.y * i.color.a
                    * smoothstep(0.5 - soft, 0.5 + soft, shadowD);

                // The glow: a falloff on the distance itself, so it costs no
                // sample at all. Full inside the ink, where the face covers it.
                float outward = saturate((0.5 - d) * (1.0 / REACH_FIELD));
                float glowA = glowBA.y * i.color.a
                    * saturate(1.0 - outward / max(softRadius.y, 1e-3));

                // The outline: the same field read at a second threshold, one
                // width further out. The threshold is held above the
                // antialiasing band because the field saturates at one reach —
                // past that an outline stops thickening and starts fringing the
                // whole background, and stopping is the honest answer.
                float outlineT = max(0.5 - REACH_FIELD * outlineBW.y, aa * 1.5);
                float outlineA = step(0.5 / 255.0, outlineBW.y) * i.color.a
                    * smoothstep(outlineT - aa, outlineT + aa, d);

                float3 shadowRgb = float3(shadowRG, shadowBA.x);
                float3 glowRgb = float3(glowRG, glowBA.x);
                float3 outlineRgb = float3(outlineRG, outlineBW.x);

                float4 acc = float4(shadowRgb * shadowA, shadowA);
                acc = Over(float4(glowRgb * glowA, glowA), acc);
                acc = Over(float4(outlineRgb * outlineA, outlineA), acc);
                acc = Over(float4(i.color.rgb * faceA, faceA), acc);

                // Back to straight alpha for SrcAlpha/OneMinusSrcAlpha, which is
                // the blend every other uGUI graphic in the batch is using.
                return fixed4(acc.rgb / max(acc.a, 1e-4), acc.a);
            }
            ENDCG
        }
    }
}
