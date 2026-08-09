// Minimal SDF text shader for uGUI: samples the R8 glyph atlas
// (Texture2DArray, layer in uv.z) and does screen-space antialiased
// coverage from the 0.5 isoline.
// A label with `precise` on samples the RGBA multi-channel atlas instead and
// takes the median of three channels, which keeps corners sharp; same
// material either way, so the two batch together.
//
// The two uGUI masks are two different mechanisms and this shader needs both.
// The Stencil block below serves `Mask`, which is stencil-based. `RectMask2D`
// is not: it drives CanvasRenderer.EnableRectClipping, which sets _ClipRect
// and turns on UNITY_UI_CLIP_RECT per renderer, and a shader that ignores
// those clips nothing. (An earlier version of this comment claimed the stencil
// block covered RectMask2D too. It did not, and text inside every ScrollRect
// was hanging over the edge of its viewport as a result.)
Shader "OneText/SDF"
{
    Properties
    {
        _GlyphTex ("Glyph Atlas", 2DArray) = "" {}
        _ColorTex ("Colour Atlas", 2DArray) = "" {}
        _MsdfTex ("Precise Glyph Atlas", 2DArray) = "" {}
        _GlyphTexelSize ("Glyph Atlas Texel Size", Vector) = (0, 0, 0, 0)
        _MsdfTexelSize ("Precise Atlas Texel Size", Vector) = (0, 0, 0, 0)

        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15

        // The rect a RectMask2D is clipping to, in root-canvas space, written
        // by the CanvasRenderer rather than by anything of ours. The default is
        // the one uGUI's own shaders use: wide enough to be no rect at all, so
        // a material inspected outside a mask reads as unclipped.
        _ClipRect ("Clip Rect", Vector) = (-32767, -32767, 32767, 32767)

        // Set by StencilMaterial when this label is a `Mask`'s own graphic.
        // Nothing reads the float — the keyword beside it is what the shader
        // acts on — but uGUI writes it, and a Property it can write without
        // warning is cheaper than explaining the warning.
        _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
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
        // it itself; see OneTextProofScene.
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            // multi_compile and not shader_feature, and the distinction is the
            // whole correctness of this feature in a player build. Nothing ever
            // enables UNITY_UI_CLIP_RECT on a *material*: the CanvasRenderer
            // turns it on at draw time, and OneText's material is built at
            // runtime and HideAndDontSave besides. shader_feature strips
            // variants no built material asks for, so the clipping variant would
            // exist in the editor and be gone from the build — mask works when
            // you test it, mask silently stops working when you ship it.
            // _local keeps it out of the global keyword table, which is a fixed
            // budget the whole project shares.
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            // The stencil half of masking, and multi_compile for the same
            // reason: StencilMaterial builds the material that carries this
            // keyword at runtime, so no serialized material asks for the
            // variant and shader_feature would strip it.
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP
            #include "UnityCG.cginc"

            // Every return in the fragment goes through this.
            //
            // A `Mask` writes its stencil wherever its graphic produces a
            // fragment, and "produces a fragment" for a text shader means the
            // whole glyph quad, most of which is the transparent margin around
            // a letter. Without the discard, a line of text used as a mask
            // reveals its children through a row of rectangles instead of
            // through the letters, which is the one arrangement where being
            // text rather than an image was the entire point.
            //
            // A discard costs real money on a tile-based mobile GPU, which is
            // why it is behind a keyword rather than always on: it is paid only
            // by a label that is actually somebody's mask, and the ordinary
            // millions of text pixels never reach it. With the keyword off this
            // is `return c` and nothing else.
            //
            // At the end, deliberately: the face coverage is read through
            // fwidth, and a fragment killed before its quad-mates take their
            // derivative would make that derivative undefined.
            #ifdef UNITY_UI_ALPHACLIP
                #define ONETEXT_RETURN(c) { fixed4 shaded = (c); clip(shaded.a - 0.001); return shaded; }
            #else
                #define ONETEXT_RETURN(c) return (c);
            #endif

            UNITY_DECLARE_TEX2DARRAY(_GlyphTex);
            // Colour glyphs (emoji, sprites) live in a second array: a
            // Texture2DArray has one format for all its slices, so colour
            // cannot share the R8 SDF array, and the coverage maths below would
            // binarize a colour image into a silhouette anyway. Same material
            // and same draw call regardless; the choice rides in a vertex
            // channel, not in a second pass.
            UNITY_DECLARE_TEX2DARRAY(_ColorTex);
            // Multi-channel fields (the label's `precise` option) live in a
            // third array for the same reason colour does: RGBA cannot share an
            // R8 array. Still one material and one draw call; which of the
            // three a tile came from rides in TEXCOORD2.w, exactly as the
            // colour flag already did.
            UNITY_DECLARE_TEX2DARRAY(_MsdfTex);

            // Set from C# beside _GlyphTex rather than relied on from Unity's
            // automatic _TexelSize, which is documented for 2D textures and not
            // for arrays. The shadow offset is the only thing that reads it.
            float4 _GlyphTexelSize;
            float4 _MsdfTexelSize;

            #ifdef UNITY_UI_CLIP_RECT
            // All three are set per renderer by the canvas, never by us.
            // _UIMaskSoftness* is the RectMask2D `Softness` field, and it is a
            // uniform rather than a Property for that reason: a Property would
            // suggest a material somewhere holds the value, and none does.
            //
            // Everything in this #ifdef — the uniforms, the interpolator, the
            // vertex maths — is absent from the variant compiled with the
            // keyword off, which is the variant every unmasked label in the
            // project draws with. Not "cheap when unused": not present.
            float4 _ClipRect;
            float _UIMaskSoftnessX;
            float _UIMaskSoftnessY;
            #endif

            // One reach (how far outside the ink the field still knows
            // anything) in texels. Must equal GlyphRasterizer.SpreadPixels,
            // which is also GlyphRasterizer.Padding; a disagreement here draws
            // shadows at the wrong distance with nothing to catch it.
            #define REACH_TEXELS 4.0
            // The same reach in field values: the rasterizer encodes
            // 0.5 - signedDistance / (2 * spread), so one reach is half of the
            // 0..1 range.
            #define REACH_FIELD 0.5

            // Decoration parameters ride in channels the mesh already carried:
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
                float4 bounds : TEXCOORD2; // x = tile v-max, yz = tile u-min/u-max, w = which atlas
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
                #ifdef UNITY_UI_CLIP_RECT
                // xy: this vertex's distance from the clip rect's centre,
                // doubled, so the frag can compare it against the rect's size
                // without halving anything. zw: one over the softness band in
                // those same doubled units, stored pre-divided because a
                // reciprocal per vertex is cheaper than one per pixel.
                //
                // Derived in vert from the position, NOT read from a mesh
                // channel: the vertex-budget contract in OneTextLabel.AddVert
                // ends at TEXCOORD3 and this does not extend it. Nothing needs
                // to be added to the canvas's additionalShaderChannels.
                half4 mask : TEXCOORD4;
                #endif
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

                #ifdef UNITY_UI_CLIP_RECT
                // v.vertex is already in root-canvas space — the canvas batcher
                // bakes every graphic's mesh into that space on the CPU — and
                // _ClipRect is given in the same space, so there is no matrix
                // here and a RectMask2D inside a world-space canvas works
                // exactly as one in a screen-space canvas.
                //
                // pixelSize is one screen pixel measured in canvas units, and it
                // is what makes a softness of zero come out as a one-pixel
                // antialiased edge rather than a stair-step: without it, text
                // would meet the mask boundary a pixel differently from every
                // Image beside it under the same mask. The projection matrix's
                // scale carries clip space to NDC and _ScreenParams carries NDC
                // to pixels; w undoes the perspective divide that has not
                // happened yet.
                float2 pixelSize = o.pos.w
                    / abs(mul((float2x2)UNITY_MATRIX_P, _ScreenParams.xy));
                // Guarding the subtraction below against a degenerate rect: an
                // infinity here would come back out of it as a NaN, and a NaN
                // in the mask makes the whole label vanish rather than clip
                // wrongly. The default ±32767 rect needs no help — its two
                // terms cancel to zero, which is why "no mask" costs nothing
                // even in the clipping variant.
                float4 clipped = clamp(_ClipRect, -2e10, 2e10);
                o.mask = half4(v.vertex.xy * 2 - clipped.xy - clipped.zw,
                    0.25 / (0.25 * float2(_UIMaskSoftnessX, _UIMaskSoftnessY) + pixelSize));
                #endif

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
            // tile the field reads exactly 0: the right answer, not a fallback.
            float Field(float2 uv, float2 tileMin, float2 tileMax, float layer)
            {
                float3 s = float3(clamp(uv, tileMin, tileMax), layer);
                return UNITY_SAMPLE_TEX2DARRAY(_GlyphTex, s).r;
            }

            /// The multi-channel field, reconstructed. Each channel holds the
            /// distance to a subset of the glyph's edges, chosen so that the two
            /// edges meeting at a corner share exactly one channel; the median
            /// of the three is then the intersection of their half-planes, and
            /// the corner survives the bilinear sampler that rounds a
            /// single-channel cone off.
            float Median(float3 d)
            {
                return max(min(d.r, d.g), min(max(d.r, d.g), d.b));
            }

            float MsdfField(float2 uv, float2 tileMin, float2 tileMax, float layer)
            {
                float3 s = float3(clamp(uv, tileMin, tileMax), layer);
                return Median(UNITY_SAMPLE_TEX2DARRAY(_MsdfTex, s).rgb);
            }

            /// The median and the true distance from one fetch: x is what the
            /// coverage threshold reads, y is what its WIDTH is measured on.
            /// They have to be different fields, and that is not an optimisation
            /// but the whole reason a corner renders correctly; see the frag.
            float2 MsdfFieldAndWidth(float2 uv, float2 tileMin, float2 tileMax, float layer)
            {
                float3 s = float3(clamp(uv, tileMin, tileMax), layer);
                float4 field = UNITY_SAMPLE_TEX2DARRAY(_MsdfTex, s);
                return float2(Median(field.rgb), field.a);
            }

            /// Alpha of a precise tile: the ordinary single-channel field, which
            /// the rasterizer gets for free (every edge is in two channels, so
            /// the nearest edge overall is the nearest in some channel).
            float MsdfTrueField(float2 uv, float2 tileMin, float2 tileMax, float layer)
            {
                float3 s = float3(clamp(uv, tileMin, tileMax), layer);
                return UNITY_SAMPLE_TEX2DARRAY(_MsdfTex, s).a;
            }

            /// Premultiplied source over premultiplied destination.
            float4 Over(float4 src, float4 dst)
            {
                return src + dst * (1.0 - src.a);
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // The RectMask2D factor, once. Every one of the four returns
                // below needs it and exactly one of them runs, so computing it
                // here costs one evaluation per pixel and no more.
                //
                // This is the softness-aware form uGUI's own shaders use rather
                // than UnityGet2DClipping, which is a hard step and would make
                // RectMask2D.Softness silently do nothing. mask.xy is the
                // doubled distance from the rect's centre and (zw - xy) its
                // doubled size, so their difference is the doubled distance
                // still to go before the edge; scaling by the pre-divided band
                // and saturating turns that into coverage, per axis.
                #ifdef UNITY_UI_CLIP_RECT
                half2 inside = saturate(
                    (_ClipRect.zw - _ClipRect.xy - abs(i.mask.xy)) * i.mask.zw);
                half clipA = inside.x * inside.y;
                #else
                // A literal 1, so every `* clipA` below is constant-folded out
                // of the unmasked variant rather than left as a multiply.
                half clipA = 1.0;
                #endif

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
                // 0 the single-channel atlas, 1 the colour one, 2 the
                // multi-channel one. A branch, not a keyword: a shader variant
                // per atlas would be a material per atlas, and one material is
                // what keeps a line of text with an emoji and a precise word in
                // it inside one draw call.
                // 3: no atlas at all. The bar under a <u>, through an <s> or
                // behind a <mark> is flat colour; there is no field to
                // threshold and no picture to sample, so it is answered before
                // anything else and never reaches a sampler. A fourth value of
                // the discriminator rather than a second material, for the same
                // reason colour and precise are: an underlined word has to
                // batch with the sentence around it.
                if (i.bounds.w > 2.5)
                {
                    fixed4 bar = i.color;
                    bar.a *= clipA;
                    ONETEXT_RETURN(bar);
                }

                float precise = step(1.5, i.bounds.w);
                if (i.bounds.w > 0.5 && precise < 0.5)
                {
                    float3 s = float3(i.uv.x, clamp(i.uv.y, i.uv.w, i.bounds.x), i.uv.z);
                    // Alpha only. Under SrcAlpha/OneMinusSrcAlpha the source
                    // contributes rgb*a, so attenuating a is the whole of
                    // clipping; scaling rgb as well would darken the tile
                    // towards the mask edge instead of fading it.
                    fixed4 tile = UNITY_SAMPLE_TEX2DARRAY(_ColorTex, s) * i.color;
                    tile.a *= clipA;
                    ONETEXT_RETURN(tile);
                }

                float2 tileMin = float2(i.bounds.y, i.uv.w);
                float2 tileMax = float2(i.bounds.z, i.bounds.x);
                // x thresholds, y sets the width of the band it is thresholded
                // over. For the single-channel field they are the same number.
                float2 field = precise > 0.5
                    ? MsdfFieldAndWidth(i.uv.xy, tileMin, tileMax, i.uv.z)
                    : Field(i.uv.xy, tileMin, tileMax, i.uv.z).xx;
                float d = field.x;

                // The antialiasing width comes off the TRUE distance, never off
                // the median, and this is the difference between a sharp corner
                // and a whisker hanging off it.
                //
                // A distance field has unit gradient, so one screen pixel is a
                // fixed step in field value and fwidth recovers it. The median
                // does not: it is the larger of two half-planes outside a
                // corner, so along the bisector of a twenty-degree vertex it
                // falls at about sin(10 deg) — a sixth of the rate — and it has
                // a kink exactly on the bisector where the two swap. fwidth is
                // a two-by-two quad difference, so on the kink it reports
                // several times the real gradient, the band widens by that
                // much, and because the field is also falling slowly there the
                // widened band reaches far down the bisector. That is the thin
                // tapering spike below the point of an 'A' or a 'W': not ink
                // the field ever claimed — every texel of it is within half a
                // texel of the outline — but antialiasing spread along a
                // direction the median is flat in.
                //
                // Alpha is the ordinary single-channel field over the same
                // encoding, so its fwidth is the right screen-space width for
                // the median's isoline as well, and it is smooth where the
                // median kinks. It costs nothing: it arrived in the same fetch.
                float aa = max(fwidth(field.y), 1e-4);
                float faceA = i.color.a * smoothstep(0.5 - aa, 0.5 + aa, d);

                // Every byte a decoration could be visible through is the low
                // half of one of these three floats, so their sum is zero for
                // undecorated text and the whole of the work below is skipped
                // for the overwhelmingly common case.
                if (i.decoA.y + i.decoA.w + i.decoB.y < 0.5)
                {
                    fixed4 plain = i.color;
                    plain.a = faceA * clipA;
                    ONETEXT_RETURN(plain);
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
                //
                // A precise tile is read through its alpha here (the plain
                // distance) rather than through the median. The displaced
                // sample lands where the three channels disagree, which is
                // exactly where the median's reconstruction is a claim about a
                // corner this pixel does not belong to; and a shadow is offset
                // and usually softened, so nobody can inspect its corners
                // anyway. The face, the outline and the glow all keep the
                // median, which is the whole point of the option.
                float2 texel = precise > 0.5 ? _MsdfTexelSize.xy : _GlyphTexelSize.xy;
                float2 shadowUv = i.uv.xy - float2(Signed(offset.x), Signed(offset.y))
                    * REACH_TEXELS * texel;
                float shadowD = precise > 0.5
                    ? MsdfTrueField(shadowUv, tileMin, tileMax, i.uv.z)
                    : Field(shadowUv, tileMin, tileMax, i.uv.z);
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
                // antialiasing band because the field saturates at one reach;
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

                // Clipping applies HERE, to the finished premultiplied stack,
                // and not to i.color.a on the way in. Scaling the four layers'
                // alphas individually and then compositing is not the same
                // thing, because Over is not linear in alpha: with an opaque
                // shadow under an opaque face and a factor of one half, the
                // per-layer route yields acc.a = 0.75 and lets a quarter of the
                // shadow bleed into what should be pure face colour. Scaling a
                // premultiplied result uniformly is the one operation that does
                // commute with Over — it is a change of exposure, not of
                // composition — so the colours come out of the divide below
                // untouched and only the alpha is attenuated.
                //
                // With a hard step the distinction never shows, since the factor
                // is 0 or 1. Softness is what makes it visible, and softness is
                // exactly what the formula above supports.
                acc *= clipA;

                // Back to straight alpha for SrcAlpha/OneMinusSrcAlpha, which is
                // the blend every other uGUI graphic in the batch is using.
                ONETEXT_RETURN(fixed4(acc.rgb / max(acc.a, 1e-4), acc.a));
            }
            ENDCG
        }
    }
}
