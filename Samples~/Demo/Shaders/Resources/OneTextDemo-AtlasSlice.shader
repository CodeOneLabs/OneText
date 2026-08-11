// Draws one layer of a Texture2DArray into a uGUI RawImage.
//
// The atlas is an array, not a texture, and uGUI's default UI shader samples
// sampler2D. Worse than wrong output: `RawImage.texture = someTexture2DArray`
// trips a native assert (`texture->GetDimension() == kTexDim2D`) the moment
// the CanvasRenderer binds it, because a canvas renderer's texture slot is 2D
// by construction. So the array does not travel through the RawImage at all.
// It is set on the material as `_AtlasTex`, the RawImage keeps whatever plain
// 2D texture uGUI wants to bind to `_MainTex`, and this shader ignores that
// one entirely.
//
// Two atlases, two channel layouts. The ordinary SDF atlas is single-channel
// R8: broadcast red to RGB or the preview comes out red. The precise atlas is
// RGBA multi-channel SDF, and showing its three channels as they are is not a
// debug affordance but the point — the colour fringing you can see IS the
// median-of-three encoding that keeps corners sharp.
//
// Alpha is forced opaque either way. An atlas viewer that let empty texels
// show through would be hiding the exact thing it exists to show: how much of
// the sheet is packed and how much is air.
Shader "OneText/Demo/Atlas Slice"
{
    Properties
    {
        _AtlasTex ("Atlas array", 2DArray) = "" {}
        _Slice ("Layer", Float) = 0
        _Broadcast ("Broadcast red to RGB", Float) = 1
        // 0 shows the sheet as it is. 1, 2 and 3 isolate one channel of a
        // multi-channel field and show it as greyscale. Isolating is the only
        // way to make the encoding legible: all three at once is the fringed
        // picture people have seen and not understood, whereas one at a time
        // shows a channel carrying one edge of a corner while its neighbour
        // carries the other — which is what the median in the real shader is
        // reading.
        _Channel ("Isolate channel (0 none, 1 R, 2 G, 3 B)", Float) = 0
        // Sub-rectangle of the sheet to show: xy origin, zw size, in uv. The
        // whole sheet at panel size is a thumbnail in which a 32-pixel glyph is
        // four pixels, and four pixels cannot show anybody what a distance
        // field looks like. Zooming into a corner of it can.
        _UvRect ("Region (x, y, w, h)", Vector) = (0,0,1,1)
        _Color ("Tint", Color) = (1,1,1,1)

        // uGUI writes these on every UI material whether the shader reads them
        // or not; declaring them keeps the RawImage from logging about a
        // material it cannot configure.
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
            "CanUseSpriteAtlas" = "True"
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
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma require 2darray
            #include "UnityCG.cginc"

            UNITY_DECLARE_TEX2DARRAY(_AtlasTex);
            float _Slice;
            float _Broadcast;
            float _Channel;
            float4 _UvRect;
            fixed4 _Color;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.color = v.color * _Color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // v flipped: the packer fills shelves from the top of the
                // sheet, and uGUI's uv origin is the bottom, so an unflipped
                // preview shows an empty atlas with the glyphs hiding off the
                // bottom edge.
                float2 uv = float2(i.uv.x, 1.0 - i.uv.y);
                uv = _UvRect.xy + uv * _UvRect.zw;
                fixed4 texel = UNITY_SAMPLE_TEX2DARRAY(_AtlasTex, float3(uv, _Slice));
                fixed3 rgb = lerp(texel.rgb, texel.rrr, _Broadcast);

                // Branchless because the channel is a uniform and every
                // fragment in the draw takes the same path anyway; a step per
                // channel keeps the shader readable next to the comment above.
                fixed picked = texel.r * step(0.5, _Channel) * step(_Channel, 1.5)
                             + texel.g * step(1.5, _Channel) * step(_Channel, 2.5)
                             + texel.b * step(2.5, _Channel);
                rgb = lerp(rgb, picked.xxx, step(0.5, _Channel));

                return fixed4(rgb * i.color.rgb, i.color.a);
            }
            ENDCG
        }
    }

    Fallback Off
}
