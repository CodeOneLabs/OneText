// Atlas construction and the two renderers.
//
// The split matters for what this harness is trying to prove. Everything about
// *what to draw*, which glyphs, where they sit, what shape they are, comes
// out of the wasm HarfBuzz: hb_shape for the glyph run, hb_font_draw_glyph for
// the outline, hb_ot_color_glyph_reference_png for colour bitmaps. Neither
// renderer knows it is drawing text. Both are handed the same array of
// positioned textured quads and the same atlas image, and the only difference
// between them is which graphics API puts those quads on screen.
//
// The glyph rasteriser fills paths that HarfBuzz produced, command by command.
// It never asks the browser to lay out or shape a string; Path2D here is a
// polygon filler being handed explicit move/line/quad/cubic coordinates.

const ATLAS_SIZE = 1024;
const RASTER_PX = 72;   // em size the atlas is rasterised at
const PAD = 2;

/**
 * Rasterises every glyph a scene needs into one RGBA atlas.
 * Outline glyphs come out white with the coverage in alpha, so a per-quad tint
 * can colour them; colour bitmaps go in as-is and are tinted with white.
 */
export async function buildAtlas(hb, scenes) {
  const canvas = new OffscreenCanvas(ATLAS_SIZE, ATLAS_SIZE);
  const ctx = canvas.getContext('2d', { willReadFrequently: false });
  ctx.clearRect(0, 0, ATLAS_SIZE, ATLAS_SIZE);
  ctx.fillStyle = '#fff';

  const map = new Map();
  let penX = PAD, penY = PAD, rowH = 0;
  let drawCommands = 0, bitmaps = 0;

  const alloc = (w, h) => {
    if (penX + w + PAD > ATLAS_SIZE) { penX = PAD; penY += rowH + PAD; rowH = 0; }
    if (penY + h + PAD > ATLAS_SIZE) throw new Error('glyph atlas full');
    const slot = { x: penX, y: penY, w, h };
    penX += w + PAD;
    rowH = Math.max(rowH, h);
    return slot;
  };

  for (const scene of scenes) {
    const { face, upem } = scene;
    const s = RASTER_PX / upem;

    for (const g of scene.glyphs) {
      const key = `${scene.id}/${g.glyph}`;
      if (map.has(key)) continue;

      // Colour fonts first: a CBDT/sbix glyph has a PNG and no outline at all.
      if (scene.hasPng) {
        const png = hb.glyphPng(face.font, g.glyph);
        if (png) {
          const bmp = await createImageBitmap(new Blob([png], { type: 'image/png' }));
          const h = RASTER_PX, w = Math.round(bmp.width * (h / bmp.height));
          const slot = alloc(w, h);
          ctx.drawImage(bmp, slot.x, slot.y, w, h);
          bmp.close();
          bitmaps++;
          map.set(key, {
            ...uv(slot), w, h,
            // Bitmap emoji sit on the baseline with a small drop, and CBDT
            // strikes carry no per-glyph bearing worth trusting here.
            left: 0, top: h * 0.85, color: [1, 1, 1, 1], bitmap: true,
          });
          continue;
        }
      }

      const ext = hb.glyphExtents(face.font, g.glyph);
      const path = hb.drawGlyph(face.font, g.glyph);
      drawCommands += path.cmds.length;

      if (!ext.ok || ext.width === 0 || ext.height === 0 || path.cmds.length === 0) {
        map.set(key, { u0: 0, v0: 0, u1: 0, v1: 0, w: 0, h: 0, left: 0, top: 0,
                       color: [1, 1, 1, 1], empty: true, commands: path.cmds.length });
        continue;
      }

      // Font units, y up. Height is negative: the box runs down from the bearing.
      const w = Math.ceil(ext.width * s) + PAD * 2;
      const h = Math.ceil(-ext.height * s) + PAD * 2;
      const slot = alloc(w, h);

      const p = new Path2D();
      const X = (x) => (x - ext.xBearing) * s + PAD;
      const Y = (y) => (ext.yBearing - y) * s + PAD;
      for (const c of path.cmds) {
        switch (c[0]) {
          case 'M': p.moveTo(X(c[1]), Y(c[2])); break;
          case 'L': p.lineTo(X(c[1]), Y(c[2])); break;
          case 'Q': p.quadraticCurveTo(X(c[1]), Y(c[2]), X(c[3]), Y(c[4])); break;
          case 'C': p.bezierCurveTo(X(c[1]), Y(c[2]), X(c[3]), Y(c[4]),
                                    X(c[5]), Y(c[6])); break;
          case 'Z': p.closePath(); break;
        }
      }
      ctx.save();
      ctx.translate(slot.x, slot.y);
      ctx.fill(p, 'nonzero');
      ctx.restore();

      map.set(key, {
        ...uv(slot), w, h,
        left: ext.xBearing * s - PAD,
        top: ext.yBearing * s + PAD,
        color: scene.color || [1, 1, 1, 1],
        commands: path.cmds.length,
      });
    }
  }

  return { bitmap: await createImageBitmap(canvas), map, drawCommands, bitmaps };
}

function uv(slot) {
  return {
    u0: slot.x / ATLAS_SIZE, v0: slot.y / ATLAS_SIZE,
    u1: (slot.x + slot.w) / ATLAS_SIZE, v1: (slot.y + slot.h) / ATLAS_SIZE,
  };
}

/**
 * Turns shaped runs into a flat vertex buffer of textured quads: two triangles
 * per glyph, 8 floats per vertex (x, y, u, v, r, g, b, a). Positions come
 * straight from hb_glyph_position_t, scaled out of font units.
 */
export function buildQuads(scenes, atlas, viewport) {
  const verts = [];
  let quads = 0;

  for (const scene of scenes) {
    const s = scene.px / scene.upem;
    let x = scene.originX, y = scene.originY;

    for (const g of scene.glyphs) {
      const a = atlas.map.get(`${scene.id}/${g.glyph}`);
      if (a && !a.empty && a.w > 0) {
        const scaleToPx = scene.px / RASTER_PX;
        const gx = x + (g.xOffset * s) + a.left * scaleToPx;
        const gy = y - (g.yOffset * s) - a.top * scaleToPx;
        const gw = a.w * scaleToPx, gh = a.h * scaleToPx;
        const c = a.bitmap ? [1, 1, 1, 1] : (scene.color || [1, 1, 1, 1]);
        push(verts, gx, gy, gw, gh, a, c);
        quads++;
      }
      x += g.xAdvance * s;
      y -= g.yAdvance * s;
    }
    scene.measuredWidth = x - scene.originX;
  }

  // Pixels to clip space, done on the CPU so both renderers get identical data
  // and a difference on screen can only come from the graphics API.
  const out = new Float32Array(verts.length);
  for (let i = 0; i < verts.length; i += 8) {
    out[i]     = (verts[i] / viewport[0]) * 2 - 1;
    out[i + 1] = 1 - (verts[i + 1] / viewport[1]) * 2;
    for (let k = 2; k < 8; k++) out[i + k] = verts[i + k];
  }
  return { data: out, count: out.length / 8, quads };
}

function push(v, x, y, w, h, a, c) {
  const q = [
    [x, y, a.u0, a.v0], [x + w, y, a.u1, a.v0], [x, y + h, a.u0, a.v1],
    [x + w, y, a.u1, a.v0], [x + w, y + h, a.u1, a.v1], [x, y + h, a.u0, a.v1],
  ];
  for (const [px, py, u, t] of q) v.push(px, py, u, t, c[0], c[1], c[2], c[3]);
}

// ------------------------------------------------------------------ WebGL2 --

export function renderWebGL2(canvas, atlasBitmap, mesh, clear) {
  const gl = canvas.getContext('webgl2', { antialias: false, alpha: false,
                                           preserveDrawingBuffer: true });
  if (!gl) throw new Error('webgl2 context unavailable');

  const prog = link(gl, `#version 300 es
    in vec2 a_pos; in vec2 a_uv; in vec4 a_color;
    out vec2 v_uv; out vec4 v_color;
    void main() { v_uv = a_uv; v_color = a_color; gl_Position = vec4(a_pos, 0.0, 1.0); }`,
  `#version 300 es
    precision highp float;
    in vec2 v_uv; in vec4 v_color; out vec4 fragColor;
    uniform sampler2D u_atlas;
    void main() {
      vec4 t = texture(u_atlas, v_uv);
      fragColor = vec4(t.rgb * v_color.rgb, t.a * v_color.a);
    }`);

  const tex = gl.createTexture();
  gl.bindTexture(gl.TEXTURE_2D, tex);
  gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false);
  gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, atlasBitmap);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);

  const vao = gl.createVertexArray();
  gl.bindVertexArray(vao);
  const vbo = gl.createBuffer();
  gl.bindBuffer(gl.ARRAY_BUFFER, vbo);
  gl.bufferData(gl.ARRAY_BUFFER, mesh.data, gl.STATIC_DRAW);
  const stride = 8 * 4;
  bind(gl, prog, 'a_pos', 2, stride, 0);
  bind(gl, prog, 'a_uv', 2, stride, 8);
  bind(gl, prog, 'a_color', 4, stride, 16);

  gl.viewport(0, 0, canvas.width, canvas.height);
  gl.clearColor(clear[0], clear[1], clear[2], 1);
  gl.clear(gl.COLOR_BUFFER_BIT);
  gl.enable(gl.BLEND);
  gl.blendFuncSeparate(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA, gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
  gl.useProgram(prog);
  gl.uniform1i(gl.getUniformLocation(prog, 'u_atlas'), 0);
  gl.activeTexture(gl.TEXTURE0);
  gl.bindTexture(gl.TEXTURE_2D, tex);
  gl.drawArrays(gl.TRIANGLES, 0, mesh.count);
  gl.finish();

  const err = gl.getError();
  if (err !== gl.NO_ERROR) throw new Error('gl error 0x' + err.toString(16));
  return { api: 'webgl2', renderer: describeGL(gl), vertices: mesh.count, quads: mesh.quads };
}

function bind(gl, prog, name, size, stride, offset) {
  const loc = gl.getAttribLocation(prog, name);
  gl.enableVertexAttribArray(loc);
  gl.vertexAttribPointer(loc, size, gl.FLOAT, false, stride, offset);
}

function link(gl, vsSrc, fsSrc) {
  const sh = (type, src) => {
    const s = gl.createShader(type);
    gl.shaderSource(s, src); gl.compileShader(s);
    if (!gl.getShaderParameter(s, gl.COMPILE_STATUS))
      throw new Error('shader: ' + gl.getShaderInfoLog(s));
    return s;
  };
  const p = gl.createProgram();
  gl.attachShader(p, sh(gl.VERTEX_SHADER, vsSrc));
  gl.attachShader(p, sh(gl.FRAGMENT_SHADER, fsSrc));
  gl.linkProgram(p);
  if (!gl.getProgramParameter(p, gl.LINK_STATUS))
    throw new Error('link: ' + gl.getProgramInfoLog(p));
  return p;
}

function describeGL(gl) {
  const d = gl.getExtension('WEBGL_debug_renderer_info');
  return d ? gl.getParameter(d.UNMASKED_RENDERER_WEBGL) : gl.getParameter(gl.RENDERER);
}

// ------------------------------------------------------------------ WebGPU --

export async function renderWebGPU(canvas, atlasBitmap, mesh, clear) {
  if (!navigator.gpu) throw new Error('navigator.gpu is undefined');
  const adapter = await navigator.gpu.requestAdapter();
  if (!adapter) throw new Error('requestAdapter() returned null');
  const device = await adapter.requestDevice();

  const errors = [];
  device.addEventListener?.('uncapturederror', (e) => errors.push(String(e.error)));

  const ctx = canvas.getContext('webgpu');
  const format = navigator.gpu.getPreferredCanvasFormat();
  ctx.configure({ device, format, alphaMode: 'opaque' });

  const tex = device.createTexture({
    size: [atlasBitmap.width, atlasBitmap.height],
    format: 'rgba8unorm',
    usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST |
           GPUTextureUsage.RENDER_ATTACHMENT,
  });
  device.queue.copyExternalImageToTexture(
    { source: atlasBitmap }, { texture: tex }, [atlasBitmap.width, atlasBitmap.height]);

  const sampler = device.createSampler({ magFilter: 'linear', minFilter: 'linear' });

  const shader = device.createShaderModule({ code: `
    struct VSOut {
      @builtin(position) pos : vec4f,
      @location(0) uv : vec2f,
      @location(1) color : vec4f,
    };
    @vertex fn vs(@location(0) pos : vec2f,
                  @location(1) uv : vec2f,
                  @location(2) color : vec4f) -> VSOut {
      var o : VSOut;
      o.pos = vec4f(pos, 0.0, 1.0);
      o.uv = uv;
      o.color = color;
      return o;
    }
    @group(0) @binding(0) var samp : sampler;
    @group(0) @binding(1) var atlas : texture_2d<f32>;
    @fragment fn fs(in : VSOut) -> @location(0) vec4f {
      let t = textureSample(atlas, samp, in.uv);
      return vec4f(t.rgb * in.color.rgb, t.a * in.color.a);
    }` });

  const pipeline = device.createRenderPipeline({
    layout: 'auto',
    vertex: {
      module: shader, entryPoint: 'vs',
      buffers: [{
        arrayStride: 8 * 4,
        attributes: [
          { shaderLocation: 0, offset: 0,  format: 'float32x2' },
          { shaderLocation: 1, offset: 8,  format: 'float32x2' },
          { shaderLocation: 2, offset: 16, format: 'float32x4' },
        ],
      }],
    },
    fragment: {
      module: shader, entryPoint: 'fs',
      targets: [{
        format,
        blend: {
          color: { srcFactor: 'src-alpha', dstFactor: 'one-minus-src-alpha' },
          alpha: { srcFactor: 'one', dstFactor: 'one-minus-src-alpha' },
        },
      }],
    },
    primitive: { topology: 'triangle-list' },
  });

  const vbo = device.createBuffer({
    size: mesh.data.byteLength,
    usage: GPUBufferUsage.VERTEX | GPUBufferUsage.COPY_DST,
  });
  device.queue.writeBuffer(vbo, 0, mesh.data);

  const bindGroup = device.createBindGroup({
    layout: pipeline.getBindGroupLayout(0),
    entries: [{ binding: 0, resource: sampler },
              { binding: 1, resource: tex.createView() }],
  });

  const enc = device.createCommandEncoder();
  const pass = enc.beginRenderPass({
    colorAttachments: [{
      view: ctx.getCurrentTexture().createView(),
      clearValue: { r: clear[0], g: clear[1], b: clear[2], a: 1 },
      loadOp: 'clear', storeOp: 'store',
    }],
  });
  pass.setPipeline(pipeline);
  pass.setBindGroup(0, bindGroup);
  pass.setVertexBuffer(0, vbo);
  pass.draw(mesh.count);
  pass.end();
  device.queue.submit([enc.finish()]);
  await device.queue.onSubmittedWorkDone();

  const info = adapter.info || (adapter.requestAdapterInfo
    ? await adapter.requestAdapterInfo() : null);
  return {
    api: 'webgpu',
    renderer: info ? [info.vendor, info.architecture, info.description]
      .filter(Boolean).join(' ') || 'unknown' : 'unknown',
    vertices: mesh.count, quads: mesh.quads,
    errors,
  };
}
