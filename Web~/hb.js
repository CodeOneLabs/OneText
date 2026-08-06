// A thin JavaScript mirror of Runtime/Core/Native/HarfBuzzApi.cs.
//
// Every call here is one of the entry points the C# P/Invoke surface declares,
// used the same way and in the same order, so that a green run of this harness
// says something about the archive Unity will link, not about a friendlier
// wrapper written around it. The draw callbacks in particular go through
// Module.addFunction, which is the JavaScript equivalent of handing HarfBuzz a
// managed delegate: the same hb_draw_funcs_set_*_func calls, the same
// signatures, the same float arguments.

export const HB_MEMORY_MODE_READONLY = 1;
export const HB_DIRECTION_LTR = 4;
export const HB_DIRECTION_RTL = 5;

export const HB_OT_TAG_GSUB = tag('GSUB');
export const HB_OT_TAG_GPOS = tag('GPOS');

export function tag(s) {
  return ((s.charCodeAt(0) << 24) | (s.charCodeAt(1) << 16) |
          (s.charCodeAt(2) << 8) | s.charCodeAt(3)) >>> 0;
}

export function tagToString(t) {
  return String.fromCharCode((t >>> 24) & 0xff, (t >>> 16) & 0xff,
                             (t >>> 8) & 0xff, t & 0xff);
}

export class HarfBuzz {
  constructor(module) {
    this.m = module;
    const c = (name, ret, args) => module.cwrap(name, ret, args);

    this.version        = () => module.UTF8ToString(module._hb_version_string());

    this.blobCreate     = c('hb_blob_create', 'number',
                            ['number', 'number', 'number', 'number', 'number']);
    this.blobDestroy    = c('hb_blob_destroy', null, ['number']);
    this.blobGetLength  = c('hb_blob_get_length', 'number', ['number']);
    this.blobGetData    = c('hb_blob_get_data', 'number', ['number', 'number']);

    this.faceCreate     = c('hb_face_create', 'number', ['number', 'number']);
    this.faceDestroy    = c('hb_face_destroy', null, ['number']);
    this.faceGetUpem    = c('hb_face_get_upem', 'number', ['number']);
    this.faceMakeImmutable = c('hb_face_make_immutable', null, ['number']);
    this.faceIsImmutable   = c('hb_face_is_immutable', 'number', ['number']);
    this.faceReferenceBlob = c('hb_face_reference_blob', 'number', ['number']);

    this.fontCreate     = c('hb_font_create', 'number', ['number']);
    this.fontDestroy    = c('hb_font_destroy', null, ['number']);
    this._fontHExtents  = c('hb_font_get_h_extents', 'number', ['number', 'number']);
    this._nominalGlyph  = c('hb_font_get_nominal_glyph', 'number',
                            ['number', 'number', 'number']);
    this._glyphExtents  = c('hb_font_get_glyph_extents', 'number',
                            ['number', 'number', 'number']);

    this.bufferCreate   = c('hb_buffer_create', 'number', []);
    this.bufferDestroy  = c('hb_buffer_destroy', null, ['number']);
    this.bufferReset    = c('hb_buffer_reset', null, ['number']);
    this._addUtf16      = c('hb_buffer_add_utf16', null,
                            ['number', 'number', 'number', 'number', 'number']);
    this.guessSegmentProperties =
                          c('hb_buffer_guess_segment_properties', null, ['number']);
    this.bufferSetDirection = c('hb_buffer_set_direction', null, ['number', 'number']);
    this.bufferSetLanguage  = c('hb_buffer_set_language', null, ['number', 'number']);
    this.languageFromString = c('hb_language_from_string', 'number',
                                ['string', 'number']);
    this._languageToString  = c('hb_language_to_string', 'number', ['number']);

    this._shape         = c('hb_shape', null, ['number', 'number', 'number', 'number']);
    this._glyphInfos    = c('hb_buffer_get_glyph_infos', 'number', ['number', 'number']);
    this._glyphPositions= c('hb_buffer_get_glyph_positions', 'number',
                            ['number', 'number']);

    this.drawFuncsCreate  = c('hb_draw_funcs_create', 'number', []);
    this.drawFuncsDestroy = c('hb_draw_funcs_destroy', null, ['number']);
    this._setMoveTo   = c('hb_draw_funcs_set_move_to_func', null,
                          ['number', 'number', 'number', 'number']);
    this._setLineTo   = c('hb_draw_funcs_set_line_to_func', null,
                          ['number', 'number', 'number', 'number']);
    this._setQuadTo   = c('hb_draw_funcs_set_quadratic_to_func', null,
                          ['number', 'number', 'number', 'number']);
    this._setCubicTo  = c('hb_draw_funcs_set_cubic_to_func', null,
                          ['number', 'number', 'number', 'number']);
    this._setClose    = c('hb_draw_funcs_set_close_path_func', null,
                          ['number', 'number', 'number', 'number']);
    this._drawGlyph   = c('hb_font_draw_glyph', null,
                          ['number', 'number', 'number', 'number']);

    this.colorHasPng      = c('hb_ot_color_has_png', 'number', ['number']);
    this.colorHasLayers   = c('hb_ot_color_has_layers', 'number', ['number']);
    this.colorHasPalettes = c('hb_ot_color_has_palettes', 'number', ['number']);
    this.colorGlyphReferencePng =
                            c('hb_ot_color_glyph_reference_png', 'number',
                              ['number', 'number']);
    this.colorPaletteGetCount = c('hb_ot_color_palette_get_count', 'number', ['number']);

    this.varHasData     = c('hb_ot_var_has_data', 'number', ['number']);
    this.varAxisCount   = c('hb_ot_var_get_axis_count', 'number', ['number']);

    this.subsetInputCreate  = c('hb_subset_input_create_or_fail', 'number', []);
    this.subsetInputDestroy = c('hb_subset_input_destroy', null, ['number']);
    this.subsetInputUnicodeSet = c('hb_subset_input_unicode_set', 'number', ['number']);
    this.subsetOrFail   = c('hb_subset_or_fail', 'number', ['number', 'number']);
    this.setAdd         = c('hb_set_add', null, ['number', 'number']);

    this._layoutFeatureTags = c('hb_ot_layout_table_get_feature_tags', 'number',
                                ['number', 'number', 'number', 'number', 'number']);

    this._dfuncs = null;
    this._sink = null;
  }

  // --- faces and fonts ---

  /** Copies font bytes into the wasm heap and builds a face + font from them. */
  createFace(bytes) {
    const m = this.m;
    const ptr = m._malloc(bytes.length);
    m.HEAPU8.set(bytes, ptr);
    const blob = this.blobCreate(ptr, bytes.length, HB_MEMORY_MODE_READONLY, 0, 0);
    const face = this.faceCreate(blob, 0);
    this.blobDestroy(blob);
    this.faceMakeImmutable(face);
    return { face, font: this.fontCreate(face), dataPtr: ptr, upem: this.faceGetUpem(face) };
  }

  destroyFace(f) {
    this.fontDestroy(f.font);
    this.faceDestroy(f.face);
    this.m._free(f.dataPtr);
  }

  /** hb_font_extents_t: ascender, descender, line_gap, then nine private ints. */
  fontHExtents(font) {
    const m = this.m, p = m._malloc(12 * 4);
    const ok = this._fontHExtents(font, p);
    const r = { ok: !!ok, ascender: m.HEAP32[p >> 2], descender: m.HEAP32[(p >> 2) + 1],
                lineGap: m.HEAP32[(p >> 2) + 2] };
    m._free(p);
    return r;
  }

  /** The glyph a codepoint maps to through cmap alone, before any shaping. */
  nominalGlyph(font, cp) {
    const m = this.m, p = m._malloc(4);
    const ok = this._nominalGlyph(font, cp, p);
    const g = m.HEAPU32[p >> 2];
    m._free(p);
    return ok ? g : 0;
  }

  glyphExtents(font, glyph) {
    const m = this.m, p = m._malloc(4 * 4);
    const ok = this._glyphExtents(font, glyph, p);
    const i = p >> 2;
    const r = { ok: !!ok, xBearing: m.HEAP32[i], yBearing: m.HEAP32[i + 1],
                width: m.HEAP32[i + 2], height: m.HEAP32[i + 3] };
    m._free(p);
    return r;
  }

  featureTags(face, tableTag) {
    const m = this.m, cp = m._malloc(4), max = 64, arr = m._malloc(max * 4);
    m.HEAPU32[cp >> 2] = max;
    this._layoutFeatureTags(face, tableTag, 0, cp, arr);
    const n = Math.min(m.HEAPU32[cp >> 2], max);
    const out = [];
    for (let i = 0; i < n; i++) out.push(tagToString(m.HEAPU32[(arr >> 2) + i]));
    m._free(cp); m._free(arr);
    return out;
  }

  // --- shaping ---

  /**
   * Shapes a JS string. The string goes in as UTF-16 through
   * hb_buffer_add_utf16, which is what C# does with its native char buffer,
   * so clusters come back as indices into the same UTF-16 string the caller
   * passed, with no re-encoding step to disagree about.
   */
  shape(font, text, { direction = 0, language = null } = {}) {
    const m = this.m;
    const buf = this.bufferCreate();

    const units = text.length;
    const tp = m._malloc(units * 2);
    for (let i = 0; i < units; i++) m.HEAPU16[(tp >> 1) + i] = text.charCodeAt(i);
    this._addUtf16(buf, tp, units, 0, units);

    this.guessSegmentProperties(buf);
    if (direction) this.bufferSetDirection(buf, direction);
    if (language) this.bufferSetLanguage(buf, this.languageFromString(language, -1));

    this._shape(font, buf, 0, 0);

    const np = m._malloc(4);
    const infos = this._glyphInfos(buf, np);
    const n = m.HEAPU32[np >> 2];
    const positions = this._glyphPositions(buf, np);

    const glyphs = [];
    for (let i = 0; i < n; i++) {
      const gi = (infos + i * 20) >> 2;      // hb_glyph_info_t: 5 x uint32
      const gp = (positions + i * 20) >> 2;  // hb_glyph_position_t: 5 x int32
      glyphs.push({
        glyph: m.HEAPU32[gi],
        cluster: m.HEAPU32[gi + 2],
        xAdvance: m.HEAP32[gp],
        yAdvance: m.HEAP32[gp + 1],
        xOffset: m.HEAP32[gp + 2],
        yOffset: m.HEAP32[gp + 3],
      });
    }

    m._free(np); m._free(tp);
    this.bufferDestroy(buf);
    return glyphs;
  }

  // --- outlines ---

  /**
   * hb_draw_funcs_t wired to JavaScript callbacks through addFunction, which
   * is the same shape as the DrawMoveToFunc/DrawCubicToFunc delegates in
   * HarfBuzzApi.cs, hence the float arguments in every signature.
   */
  _ensureDrawFuncs() {
    if (this._dfuncs) return this._dfuncs;
    const m = this.m;
    const sink = () => this._sink;

    const moveTo = m.addFunction((_d, _dd, _st, x, y, _u) => sink().moveTo(x, y), 'viiiffi');
    const lineTo = m.addFunction((_d, _dd, _st, x, y, _u) => sink().lineTo(x, y), 'viiiffi');
    const quadTo = m.addFunction((_d, _dd, _st, cx, cy, x, y, _u) =>
      sink().quadTo(cx, cy, x, y), 'viiiffffi');
    const cubicTo = m.addFunction((_d, _dd, _st, c1x, c1y, c2x, c2y, x, y, _u) =>
      sink().cubicTo(c1x, c1y, c2x, c2y, x, y), 'viiiffffffi');
    const close = m.addFunction((_d, _dd, _st, _u) => sink().close(), 'viiii');

    const df = this.drawFuncsCreate();
    this._setMoveTo(df, moveTo, 0, 0);
    this._setLineTo(df, lineTo, 0, 0);
    this._setQuadTo(df, quadTo, 0, 0);
    this._setCubicTo(df, cubicTo, 0, 0);
    this._setClose(df, close, 0, 0);
    this._dfuncs = df;
    return df;
  }

  /**
   * Runs hb_font_draw_glyph and returns the path as a flat command list, in
   * font units with y upward: exactly what HarfBuzz handed over, with no
   * transform applied. Callers scale it.
   */
  drawGlyph(font, glyph) {
    const df = this._ensureDrawFuncs();
    const cmds = [];
    let counts = { moveTo: 0, lineTo: 0, quadTo: 0, cubicTo: 0, close: 0 };
    this._sink = {
      moveTo: (x, y) => { counts.moveTo++; cmds.push(['M', x, y]); },
      lineTo: (x, y) => { counts.lineTo++; cmds.push(['L', x, y]); },
      quadTo: (cx, cy, x, y) => { counts.quadTo++; cmds.push(['Q', cx, cy, x, y]); },
      cubicTo: (a, b, c, d, x, y) => { counts.cubicTo++; cmds.push(['C', a, b, c, d, x, y]); },
      close: () => { counts.close++; cmds.push(['Z']); },
    };
    this._drawGlyph(font, glyph, df, 0);
    this._sink = null;
    return { cmds, counts };
  }

  /** The CBDT/sbix PNG for a glyph, or null. Ownership follows the C# path: the blob is released here. */
  glyphPng(font, glyph) {
    const blob = this.colorGlyphReferencePng(font, glyph);
    if (!blob) return null;
    const len = this.blobGetLength(blob);
    if (!len) { this.blobDestroy(blob); return null; }
    const m = this.m, lp = m._malloc(4);
    const data = this.blobGetData(blob, lp);
    const bytes = m.HEAPU8.slice(data, data + m.HEAPU32[lp >> 2]);
    m._free(lp);
    this.blobDestroy(blob);
    return bytes;
  }

  /** hb_subset_or_fail over a codepoint set: the check that subsetting is real here too. */
  subsetToCodepoints(face, codepoints) {
    const input = this.subsetInputCreate();
    if (!input) return null;
    const set = this.subsetInputUnicodeSet(input);
    for (const cp of codepoints) this.setAdd(set, cp);
    const out = this.subsetOrFail(face, input);
    this.subsetInputDestroy(input);
    if (!out) return null;
    const blob = this.faceReferenceBlob(out);
    const size = this.blobGetLength(blob);
    this.blobDestroy(blob);
    this.faceDestroy(out);
    return { size };
  }
}
