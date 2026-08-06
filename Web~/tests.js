// What the harness actually asserts.
//
// Each test is a claim about shaping that would be false if HarfBuzz were not
// really running: a count, a reordering, a substitution, a kern. "It drew
// something" is not evidence: a font with no shaping at all still draws
// something. So the assertions are the ones that separate a shaper from a
// glyph-per-character loop.

import { HB_OT_TAG_GSUB } from './hb.js';

export const FONT_DIR = '../Tests/CoverageFonts~/';

export const FONTS = {
  arabic:     'NotoSansArabic-Regular.ttf',
  devanagari: 'NotoSansDevanagari-Regular.ttf',
  latin:      'NotoSans-Regular.ttf',
  emoji:      'NotoColorEmoji.ttf',
};

const cps = (s) => [...s].length;

export function runShapingTests(hb, faces) {
  const tests = [];
  const t = (name, fn) => {
    try {
      const r = fn();
      tests.push({ name, pass: r.pass, ...r });
    } catch (e) {
      tests.push({ name, pass: false, error: String(e && e.stack || e) });
    }
  };

  // -- Arabic: contextual joining forms ------------------------------------
  //
  // The tempting assertion here is "fewer glyphs than codepoints", and against
  // this font it is simply false, twice over. Arabic joining is a one-to-one
  // substitution, not a ligature, so it cannot reduce the count; and Noto Sans
  // Arabic decomposes dotted letters through `ccmp` into a dotless skeleton
  // plus a zero-advance dot mark, so five letters come back as *six* glyphs.
  // (Shaping "ب" alone returns two glyphs for that reason.)
  //
  // So the count is not the evidence. Three other things are, and none of them
  // can happen without a shaper:
  //   * the glyph ids are not what cmap returns for the bare codepoints,
  //     every letter has been swapped for an initial/medial/final form;
  //   * exactly one glyph has a zero advance, which is the `ccmp` dot;
  //   * the clusters run downward, 4 3 3 2 1 0, because the run came back in
  //     visual order, right to left.
  t('arabic/joining-and-rtl-order', () => {
    const text = 'مرحبا';                       // marhaba
    const glyphs = hb.shape(faces.arabic.font, text);
    const nominal = [...text].map((ch) => hb.nominalGlyph(faces.arabic.font, ch.codePointAt(0)));
    const shaped = glyphs.map((g) => g.glyph);
    const substituted = shaped.filter((g) => !nominal.includes(g)).length;
    const clusters = glyphs.map((g) => g.cluster);
    const descending = clusters.every((c, i) => i === 0 || c <= clusters[i - 1]);
    const marks = glyphs.filter((g) => g.xAdvance === 0).length;
    return {
      pass: substituted >= 4 && descending && marks === 1 &&
            glyphs.filter((g) => g.xAdvance > 0).length === cps(text),
      text, codepoints: cps(text), glyphCount: glyphs.length,
      nominalGlyphs: nominal, shapedGlyphs: shaped,
      contextualSubstitutions: substituted,
      clusters, visualOrderRTL: descending, zeroAdvanceMarks: marks,
      advances: glyphs.map((g) => g.xAdvance),
      note: 'count rises, not falls: ccmp splits dots off as marks',
    };
  });

  // -- Arabic: lam-alef, which this font builds out of two pieces -----------
  //
  // Both Noto Arabic faces render lam+alef as two glyphs rather than the one
  // precomposed ligature, so a glyph-count assertion would fail here too. The
  // check that does hold, and is stronger: the two pieces together measure
  // exactly what the precomposed presentation form U+FEFB measures. Getting
  // that total right means the substitution and the positioning both ran.
  t('arabic/lam-alef-metrics', () => {
    const glyphs = hb.shape(faces.arabic.font, 'لا');           // U+0644 U+0627
    const ligature = hb.shape(faces.arabic.font, 'ﻻ');     // precomposed form
    const width = glyphs.reduce((a, g) => a + g.xAdvance, 0);
    const ligWidth = ligature.reduce((a, g) => a + g.xAdvance, 0);
    const nominal = [hb.nominalGlyph(faces.arabic.font, 0x0644),
                     hb.nominalGlyph(faces.arabic.font, 0x0627)];
    const shaped = glyphs.map((g) => g.glyph);
    return {
      pass: width === ligWidth && ligature.length === 1 &&
            shaped.every((g, i) => g !== nominal[i]),
      pairGlyphs: shaped, nominalGlyphs: nominal,
      pairWidth: width, precomposedWidth: ligWidth,
      precomposedGlyph: ligature.map((g) => g.glyph),
    };
  });

  // -- Arabic: the GSUB features that do the joining exist -------------------
  t('arabic/gsub-features', () => {
    const tags = hb.featureTags(faces.arabic.face, HB_OT_TAG_GSUB);
    const need = ['init', 'medi', 'fina'];
    return {
      pass: need.every((f) => tags.includes(f)),
      required: need, found: tags.slice(0, 24), featureCount: tags.length,
    };
  });

  // -- Devanagari: conjunct formation --------------------------------------
  //
  // क्षत्रिय is eight codepoints. Each virama fuses its neighbours into a
  // conjunct, and eight codepoints come back as four glyphs. This is the case
  // where "fewer glyphs than codepoints" is genuinely the right assertion.
  t('devanagari/conjuncts', () => {
    const text = 'क्षत्रिय';
    const glyphs = hb.shape(faces.devanagari.font, text);
    return {
      pass: glyphs.length === 4 && glyphs.length < cps(text) &&
            glyphs.every((g) => g.xAdvance > 0),
      text, codepoints: cps(text), glyphCount: glyphs.length,
      shapedGlyphs: glyphs.map((g) => g.glyph),
      clusters: glyphs.map((g) => g.cluster),
      advances: glyphs.map((g) => g.xAdvance),
    };
  });

  // -- Devanagari: the i-matra moves in front of its consonant --------------
  //
  // Reordering cannot be caught by watching cluster numbers: HarfBuzz merges
  // the cluster when it moves a glyph, so क्षत्रिय comes back with clusters
  // 0 3 3 7: rising, and no evidence of anything. "कि" shows it plainly
  // instead. It is ka followed by the i-matra, and it comes back matra first,
  // with ka's glyph id untouched in second place. The matra is not even the
  // glyph cmap names: it has been swapped for the width-matched variant that
  // fits ka's shoulder.
  t('devanagari/pre-base-reordering', () => {
    const glyphs = hb.shape(faces.devanagari.font, 'कि');   // U+0915 U+093F
    const ka = hb.nominalGlyph(faces.devanagari.font, 0x0915);
    const matra = hb.nominalGlyph(faces.devanagari.font, 0x093F);
    const shaped = glyphs.map((g) => g.glyph);
    return {
      pass: glyphs.length === 2 && shaped[1] === ka && shaped[0] !== ka &&
            shaped[0] !== matra,
      shapedGlyphs: shaped, nominalKa: ka, nominalIMatra: matra,
      matraCameFirst: shaped[1] === ka,
      matraIsVariant: shaped[0] !== matra,
      advances: glyphs.map((g) => g.xAdvance),
    };
  });

  // -- Latin: GPOS kerning -------------------------------------------------
  //
  // Shape a pair, then shape each letter alone. If the pair is narrower than
  // the sum, a kern was applied, which no per-character loop would produce.
  t('latin/kerning', () => {
    const font = faces.latin.font;
    const width = (s) => hb.shape(font, s).reduce((a, g) => a + g.xAdvance, 0);
    const pairs = ['AV', 'Ta', 'Wa', 'AT', 'Yo'].map((p) => ({
      pair: p, delta: width(p) - (width(p[0]) + width(p[1])),
    }));
    const kerned = pairs.filter((p) => p.delta !== 0);
    return {
      pass: kerned.length > 0 && kerned.every((p) => p.delta < 0),
      pairs, kernedPairs: kerned.length,
    };
  });

  // -- Emoji: a ZWJ sequence is one glyph ----------------------------------
  t('emoji/zwj-sequence', () => {
    const text = '\u{1F468}‍\u{1F469}‍\u{1F467}';  // family: man, woman, girl
    const glyphs = hb.shape(faces.emoji.font, text);
    return {
      pass: glyphs.length === 1 && glyphs.length < cps(text),
      text, codepoints: cps(text), utf16Units: text.length,
      glyphCount: glyphs.length, shapedGlyphs: glyphs.map((g) => g.glyph),
      hasPng: !!hb.colorHasPng(faces.emoji.face),
      hasLayers: !!hb.colorHasLayers(faces.emoji.face),
      paletteCount: hb.colorPaletteGetCount(faces.emoji.face),
    };
  });

  // -- hb-draw: outlines come back with real curves -------------------------
  t('draw/outlines', () => {
    const per = {};
    let total = 0, curves = 0;
    for (const key of ['latin', 'arabic', 'devanagari']) {
      const g = hb.shape(faces[key].font, key === 'latin' ? 'Ag'
                        : key === 'arabic' ? 'مرحبا' : 'क्षत्रिय');
      let cmds = 0, cv = 0;
      for (const gl of g) {
        const p = hb.drawGlyph(faces[key].font, gl.glyph);
        cmds += p.cmds.length;
        cv += p.counts.quadTo + p.counts.cubicTo;
      }
      per[key] = { glyphs: g.length, pathCommands: cmds, curveCommands: cv };
      total += cmds; curves += cv;
    }
    return { pass: total > 0 && curves > 0, per, totalPathCommands: total,
             totalCurveCommands: curves };
  });

  // -- font metrics --------------------------------------------------------
  t('font/h-extents', () => {
    const e = hb.fontHExtents(faces.latin.font);
    return { pass: e.ok && e.ascender > 0 && e.descender < 0, ...e,
             upem: faces.latin.upem };
  });

  // -- subsetting is present in this build too ------------------------------
  //
  // harfbuzz-subset is a separate library in HarfBuzz's build, so a wasm
  // archive can shape perfectly and have no subsetting at all. Docs/NATIVES.md
  // checks the symbol on every other platform; here it is checked by running.
  t('subset/or-fail', () => {
    const src = faces.latin.bytes.length;
    const r = hb.subsetToCodepoints(faces.latin.face,
      [...'Hello'].map((c) => c.codePointAt(0)));
    return { pass: !!r && r.size > 0 && r.size < src,
             sourceBytes: src, subsetBytes: r ? r.size : 0 };
  });

  return tests;
}

/** The lines both renderers draw, each already shaped through wasm HarfBuzz. */
export function buildScenes(hb, faces) {
  const line = (id, key, text, px, y, color, opts) => {
    const f = faces[key];
    return {
      id, label: text, fontKey: key, face: f, upem: f.upem, px,
      originX: 28, originY: y, color,
      hasPng: !!hb.colorHasPng(f.face),
      glyphs: hb.shape(f.font, text, opts || {}),
    };
  };
  return [
    line('ar', 'arabic',     'مرحبا',        56,  74, [0.55, 0.85, 1.0, 1]),
    line('dv', 'devanagari', 'क्षत्रिय',       56, 150, [1.0, 0.78, 0.45, 1]),
    line('la', 'latin',      'AVATar Wave',  46, 218, [0.92, 0.94, 0.98, 1]),
    // The family sequence is three people and two ZWJs collapsing into one
    // glyph; the smiley and heart after it are there because Noto draws the
    // family as a grey silhouette on purpose, and a grey emoji on its own
    // looks like a colour path that failed.
    line('em', 'emoji',      '\u{1F468}‍\u{1F469}‍\u{1F467}\u{1F600}❤️',
                                             52, 288, [1, 1, 1, 1]),
  ];
}
