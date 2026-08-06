#!/usr/bin/env bash
#
# Builds HarfBuzz to WebAssembly for Unity's Web (WebGL) player, plus a
# standalone module for the browser test harness in `Web~/`.
#
# Two outputs, from one compile:
#
#   Runtime/Plugins/WebGL/libHarfBuzzSharp.a   Unity links this into the player.
#   Web~/onetext-hb.{js,wasm}                  MODULARIZE build, for Web~/index.html.
#
# Unity links plugin archives with its own bundled Emscripten, and LLVM does not
# promise object compatibility across compiler versions, so the archive has to
# be built with an Emscripten close to the editor's. Unity 2023.2 and later,
# which is every Unity 6 version, document "Emscripten 3.1.38-unity"; the
# 6000.0.77f1 install this was verified against actually reports 3.1.39-git.
# Upstream 3.1.38 links against it: both are clang 17.0.0, and a real Web
# player has been built and run from this archive. If a future editor moves
# further, change EMSDK_VERSION and rebuild; do not ship the old .a.
#
# Everything else here is the same HarfBuzz release the other platforms get from
# the HarfBuzzSharp NuGet packages (see Docs/NATIVES.md), so that no script
# shapes differently on the web than it does anywhere else.
#
# Usage:  Tools/build_webgl_natives.sh [--clean]

set -euo pipefail

HB_VERSION=14.2.1        # matches HarfBuzzSharp.NativeAssets.* 14.2.1.1
EMSDK_VERSION=3.1.38     # nearest upstream to Unity 6's bundled Emscripten

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="${ONETEXT_WEBGL_WORK:-${TMPDIR:-/tmp}/onetext-webgl}"
SRC="$WORK/harfbuzz-$HB_VERSION"
BUILD="$WORK/build-$HB_VERSION-em$EMSDK_VERSION"
OUT_UNITY="$REPO/Runtime/Plugins/WebGL"
OUT_WEB="$REPO/Web~"

[ "${1:-}" = "--clean" ] && rm -rf "$BUILD"

# ---------------------------------------------------------------- toolchain --

EMSDK_DIR="${EMSDK:-$HOME/emsdk}"
if [ ! -f "$EMSDK_DIR/emsdk_env.sh" ]; then
  echo "error: no emsdk at $EMSDK_DIR" >&2
  echo "  git clone https://github.com/emscripten-core/emsdk ~/emsdk" >&2
  echo "  cd ~/emsdk && ./emsdk install $EMSDK_VERSION && ./emsdk activate $EMSDK_VERSION" >&2
  exit 1
fi
# shellcheck disable=SC1091
source "$EMSDK_DIR/emsdk_env.sh" >/dev/null 2>&1

have=$(emcc --version | head -1 | sed -n 's/.* \([0-9][0-9.]*\) (.*/\1/p')
if [ "$have" != "$EMSDK_VERSION" ]; then
  echo "warning: emcc is $have, expected $EMSDK_VERSION." >&2
  echo "  A Unity Web build links this archive with the editor's own Emscripten;" >&2
  echo "  a mismatch shows up as an LLVM object version error at link time." >&2
fi

# ------------------------------------------------------------------ sources --

mkdir -p "$WORK" "$BUILD"
if [ ! -d "$SRC" ]; then
  echo "==> fetching HarfBuzz $HB_VERSION"
  git clone --depth 1 --branch "$HB_VERSION" \
      https://github.com/harfbuzz/harfbuzz.git "$SRC"
fi

# Five of HarfBuzz's public functions are shadowed by function-like macros of
# the same name: hb_color_get_{alpha,red,green,blue} in hb-common.h and
# hb_glyph_info_get_glyph_flags in hb-buffer.h. The rename below works by
# #define, and a function-like macro in a header beats an object-like one that
# arrived earlier, so these five alone would keep their original names and
# collide with Unity's HarfBuzz.
#
# They are also the one place the headers cannot be touched. The macro has to
# keep its name, because the declaration above it would otherwise be eaten by
# it; and the definitions read `return hb_color_get_blue (color);`, written on
# the assumption that the macro catches that call, so removing the macro turns
# all five into infinite recursion.
#
# What can be moved is the definition. Each is written `(hb_color_get_blue)
# (hb_color_t color)`, parenthesised precisely so the macro cannot expand it,
# so prefixing the name there renames the symbol and touches nothing else. The
# declaration is left behind with no definition, which is legal and unused.
# Idempotent: once rewritten the pattern no longer matches.
sed -i '' -E \
  's/^\((hb_color_get_(alpha|red|green|blue)|hb_glyph_info_get_glyph_flags)\)/(onetext_\1)/' \
  "$SRC/src/hb-common.cc" "$SRC/src/hb-buffer.cc"

# Those five definitions now have no matching declaration, and hb.hh turns
# -Wmissing-prototypes into an error from inside the source, where a command
# line -Wno- cannot reach it. Downgraded to a warning; nothing else in the
# diagnostic set is touched.
sed -i '' -E \
  's/^#pragma GCC diagnostic error( +)"-Wmissing-prototypes"/#pragma GCC diagnostic ignored\1"-Wmissing-prototypes"/' \
  "$SRC/src/hb.hh"

# ------------------------------------------------- one translation unit ------
#
# The archive is a single object, compiled from HarfBuzz's own amalgamation
# `src/harfbuzz-subset.cc`. That is not a compile-speed choice; it is the fix
# for a real link failure, and the reason is worth keeping written down.
#
# The obvious build is HarfBuzz's CMake, which produces libharfbuzz.a and
# libharfbuzz-subset.a, merged into one archive because Unity imports one file
# per plugin. Those two overlap. libharfbuzz.a is a single object holding the
# whole core (harfbuzz.cc.o), while libharfbuzz-subset.a separately compiles
# core files that the amalgamation already contains: hb-static.cc,
# hb-number.cc, hb-ot-cff1-table.cc, hb-ot-cff2-table.cc and
# graph/gsubgpos-context.cc. Merge them and both copies live in one archive;
# Unity's link pulls both and wasm-ld stops with "duplicate symbol:
# _hb_NullPool", "duplicate symbol: hb_blob_destroy", and dozens more. No
# browser harness finds this, because a harness links the archive itself and
# never asks Unity to.
#
# `harfbuzz-subset.cc` is the superset amalgamation: core plus subsetting in one
# translation unit. The only files `harfbuzz.cc` has that it lacks are the
# platform integrations (CoreText, DirectWrite, FreeType, GDI, glib, graphite2,
# Uniscribe), none of which exist on Web or are enabled here. One object cannot
# collide with itself, so the whole failure mode is gone by construction rather
# than by a careful merge.

# --------------------------------------------------------------- trim flags --
#
# HarfBuzz's own tiny-build presets are HB_TINY -> HB_LEAN + HB_MINI. We cannot
# use them. HB_LEAN defines HB_NO_DRAW, HB_NO_COLOR, HB_NO_VAR, HB_NO_BITMAP and
# HB_NO_METRICS, and HarfBuzzApi.cs calls into every one of those:
# hb_font_draw_glyph (outlines are how the engine gets glyphs at all),
# hb_ot_color_* (COLRv0 layers, CPAL palettes, CBDT/sbix PNGs), hb_ot_var_* and
# hb_font_set_variations, and hb_font_get_h_extents. HB_MINI adds HB_NO_AAT and
# HB_NO_LEGACY, which do not remove API; they remove *shaping behaviour*, so
# the web player would shape morx fonts and legacy-cmap symbol fonts differently
# from every other platform. That is the one failure this package is built to
# avoid.
#
# So the trim is done by hand instead, and the rule is: remove entry points
# HarfBuzzApi.cs never names, and infrastructure a wasm player cannot use. Never
# remove anything that changes what hb_shape returns.

# Infrastructure that does not exist, or is not reachable, in a browser player.
TRIM_INFRA=(
  -DHB_NDEBUG -DNDEBUG          # no assertion machinery in a shipped player
  -DHB_NO_MT                    # Unity Web is single-threaded unless the project
                                # opts into pthreads; see the note at the bottom
  -DHB_NO_ATEXIT
  -DHB_NO_ERRNO
  -DHB_NO_GETENV                # no environment to read shaping overrides from
  -DHB_NO_SETLOCALE
  -DHB_NO_MMAP                  # no mmap
  -DHB_NO_OPEN                  # hb_blob_create_from_file: no filesystem
  -DHB_DISABLE_DEPRECATED
)

# Public API HarfBuzzApi.cs does not name. Each of these is an entry point, not
# a shaping rule: removing them cannot change a shaped result.
TRIM_API=(
  -DHB_NO_BUFFER_MESSAGE        # hb_buffer_set_message_func
  -DHB_NO_BUFFER_SERIALIZE      # hb_buffer_serialize_glyphs
  -DHB_NO_BUFFER_VERIFY
  -DHB_NO_LAYOUT_COLLECT_GLYPHS
  -DHB_NO_LAYOUT_FEATURE_PARAMS # cvXX/ssXX parameter records
  -DHB_NO_MATH                  # MATH table
  -DHB_NO_META                  # 'meta' table
  -DHB_NO_STYLE                 # hb_style_get_value
  -DHB_NO_SVG                   # OT-SVG colour glyphs; we read COLR/CPAL + PNG
)

# Size knobs. HB_OPTIMIZE_SIZE is the one HB_TINY sets that costs nothing we
# need; HB_OPTIMIZE_SIZE_MORE and HB_MINIMIZE_MEMORY_USAGE are deliberately not
# set, because they trade shaping speed for bytes and this is a text engine.
TRIM_SIZE=(-DHB_OPTIMIZE_SIZE)

# -Wno-missing-prototypes: the five colour accessors renamed above now define a
# symbol their header never declares, and HarfBuzz builds with -Werror. The
# declaration is deliberately left alone (see the note by the sed), so the
# warning is expected and is the only one suppressed.
CFLAGS=(-I "$SRC/src" -Oz -fno-exceptions -fno-rtti -Wno-missing-prototypes
        "${TRIM_INFRA[@]}" "${TRIM_API[@]}" "${TRIM_SIZE[@]}")

# ------------------------------------------------------ symbol renaming ------
#
# Unity's Web player already contains a HarfBuzz. `TextRenderingModule` links
# one statically, 8.0.1 in editor 6000.0.77f1, and it is not optional: any
# Canvas pulls in UnityEngine.UI, which references Font, which keeps the module
# alive, so engine-code stripping does not remove it. Two HarfBuzzes in one
# link is 591 duplicate strong symbols and no player.
#
# Renaming ours is the only way to keep 14.2.1 and subsetting on Web. It has to
# happen in the compiler: llvm-objcopy has no symbol-table support for wasm
# objects ("only flags for section dumping, removal, and addition are
# supported"), so there is nothing to rewrite afterwards.
#
# The rename is done by identifier, not by finished symbol name, because that
# is what also fixes the C++ ones. Rename the identifier hb_parse_int and the
# mangled _Z12hb_parse_int... becomes _Z20onetext_hb_parse_int... by itself;
# rename the type hb_font_t and every mangled name that mentions it changes too.
# Identifiers are recovered from the length prefixes in the mangled names rather
# than by regex, which would run them together and rename nothing.

echo "==> pass 1: compiling to collect symbols"
emcc -c "$SRC/src/harfbuzz-subset.cc" -o "$BUILD/harfbuzz-plain.o" "${CFLAGS[@]}"

NM="$EMSDK_DIR/upstream/bin/llvm-nm"
[ -x "$NM" ] || NM="$(dirname "$(command -v emcc)")/llvm-nm"

# hb_-prefixed names that HarfBuzz itself defines as macros. Defining them here
# too is a redefinition the header wins, so the rename would not take.
find "$SRC/src" \( -name '*.h' -o -name '*.hh' \) -print0 |
  xargs -0 grep -hoE '^#[[:space:]]*define[[:space:]]+_?hb_[A-Za-z0-9_]*' |
  awk '{print $NF}' | sort -u > "$BUILD/hb-macros.txt"

"$NM" --defined-only "$BUILD/harfbuzz-plain.o" | awk 'NF==3 {print $3}' | sort -u \
  > "$BUILD/hb-symbols.txt"

# Written out rather than piped: `python3 - ... < symbols <<'PY'` would give the
# heredoc and the symbol list the same stdin, and the heredoc wins; Python
# reads its program from stdin, sys.stdin is then empty, and the rename header
# comes out with nothing in it but the hand-written extras.
cat > "$BUILD/genrename.py" <<'PY'
import re, sys

PREFIX = "onetext_"

# Colliding symbols that are not hb_-prefixed and so cannot be caught by the
# pattern: HarfBuzz's per-shaper data callbacks, two CFF file-scope globals, and
# the namespaces whose contents would otherwise keep plain names. Renaming a
# namespace renames everything inside it, which is what handles OT::cff1's
# lookup_* tables.
EXTRA = {
    "endchar_str", "minus_1",
    "cff1", "cff2", "graph",
    "data_create_arabic", "data_destroy_arabic",
    "data_create_indic", "data_destroy_indic",
    "data_create_khmer", "data_destroy_khmer",
    "data_create_myanmar", "data_destroy_myanmar",
    "data_create_use", "data_destroy_use",
    "data_create_hangul", "data_destroy_hangul",
    "data_create_thai", "data_destroy_thai",
    "data_create_hebrew", "data_destroy_hebrew",
}

def components(sym):
    """Source-name components of an Itanium-mangled symbol, by length prefix."""
    out, i, n = [], 0, len(sym)
    while i < n:
        if sym[i].isdigit() and (i == 0 or not sym[i - 1].isdigit()):
            j = i
            while j < n and sym[j].isdigit():
                j += 1
            length = int(sym[i:j])
            if 0 < length <= n - j:
                out.append(sym[j:j + length])
                i = j + length
                continue
        i += 1
    return out

macros = {l.strip() for l in open(sys.argv[1]) if l.strip()}
idents = set()
for sym in (l.strip() for l in sys.stdin if l.strip()):
    if sym.startswith("_Z"):
        idents.update(components(sym))
    else:
        idents.add(sym)

keep = ({i for i in idents if re.fullmatch(r"_?hb_[A-Za-z0-9_]*", i)} | EXTRA) - macros

print("/* Generated by Tools/build_webgl_natives.sh -- do not edit. */")
print("#pragma once")
for i in sorted(keep):
    print("#define %s %s%s" % (i, PREFIX, i))
sys.stderr.write("    %d identifiers renamed\n" % len(keep))
PY

python3 "$BUILD/genrename.py" "$BUILD/hb-macros.txt" \
  < "$BUILD/hb-symbols.txt" > "$BUILD/hb-rename.h"
grep -c '^#define' "$BUILD/hb-rename.h" > /dev/null || {
  echo "rename header is empty" >&2; exit 1; }

echo "==> pass 2: compiling with the rename header"
emcc -c "$SRC/src/harfbuzz-subset.cc" -o "$BUILD/harfbuzz-all.o" \
  -include "$BUILD/hb-rename.h" "${CFLAGS[@]}"

echo "==> archiving libHarfBuzzSharp.a"
mkdir -p "$OUT_UNITY"
rm -f "$OUT_UNITY/libHarfBuzzSharp.a"
emar crs "$OUT_UNITY/libHarfBuzzSharp.a" "$BUILD/harfbuzz-all.o"

# ------------------------------------------------------------------ verify ---
#
# The same questions Docs/NATIVES.md asks of every vendored binary, asked of an
# archive of wasm objects instead of a shared library, plus a duplicate-symbol
# check, which is here because its absence is what let a broken archive reach a
# Unity build once already.

syms="$("$NM" --defined-only "$OUT_UNITY/libHarfBuzzSharp.a" 2>/dev/null | awk 'NF==3 {print $3}')"
strong="$("$NM" --defined-only "$OUT_UNITY/libHarfBuzzSharp.a" 2>/dev/null |
          awk 'NF==3 && $2 ~ /^[TDBR]$/ {print $3}' | sort -u)"

fail=0

# Every entry point HarfBuzzApi.cs names, under its renamed name.
for s in hb_shape hb_font_draw_glyph hb_draw_funcs_create hb_ot_color_glyph_get_layers \
         hb_ot_color_glyph_reference_png hb_ot_var_get_axis_infos hb_font_set_variations \
         hb_font_get_h_extents hb_ot_layout_table_get_feature_tags \
         hb_buffer_add_utf16 hb_subset_or_fail hb_version_string; do
  grep -qx "onetext_$s" <<<"$syms" || { echo "MISSING: onetext_$s" >&2; fail=1; }
done

n_subset=$(grep -c '^onetext_hb_subset_' <<<"$syms" || true)
[ "$n_subset" -ge 31 ] ||
  { echo "only $n_subset onetext_hb_subset_* symbols, expected >= 31" >&2; fail=1; }

# Nothing may still answer to a plain HarfBuzz name.
leftover=$(grep -E '^_?hb_' <<<"$strong" || true)
if [ -n "$leftover" ]; then
  echo "these strong symbols kept their original HarfBuzz names:" >&2
  head -20 <<<"$leftover" >&2
  fail=1
fi

# Any symbol defined twice is a Unity link error waiting to happen.
dupes=$(sort <<<"$syms" | uniq -d)
if [ -n "$dupes" ]; then
  echo "duplicate symbols in the archive; Unity's wasm-ld will refuse this:" >&2
  head -20 <<<"$dupes" >&2
  fail=1
fi

# The real test, when the editor is here to run it: compare against the actual
# HarfBuzz inside Unity's TextRenderingModule. This is the exact comparison
# wasm-ld will make during a Web build, so a clean result here is the thing that
# says the player will link.
UNITY_TRM=$(ls -1 "/Applications/Unity/Hub/Editor/"*"/PlaybackEngines/WebGLSupport/BuildTools/lib/modules/"*TextRenderingModule*.a 2>/dev/null | head -1 || true)
if [ -n "$UNITY_TRM" ]; then
  clash=$(comm -12 <(echo "$strong") \
                   <("$NM" --defined-only "$UNITY_TRM" 2>/dev/null |
                     awk 'NF==3 && $2 ~ /^[TDBR]$/ {print $3}' | sort -u))
  if [ -n "$clash" ]; then
    echo "collides with Unity's bundled HarfBuzz ($(wc -l <<<"$clash") symbols):" >&2
    head -10 <<<"$clash" >&2
    fail=1
  else
    echo "    ok: no strong symbol collides with $(basename "$UNITY_TRM")"
  fi
else
  echo "    note: no Unity Web module found, skipped the collision check"
fi

[ "$fail" = 0 ] || exit 1
echo "    ok: $(grep -cE '^onetext_hb_' <<<"$syms") onetext_hb_* symbols, $n_subset subset, no duplicates"

# --------------------------------------------------- harness side module -----
#
# The same archive, linked into a MODULARIZE module so Web~/index.html can drive
# it from JavaScript. The export list is exactly the P/Invoke surface of
# HarfBuzzApi.cs, so the harness exercises the entry points Unity will, and not
# some easier subset of them. ALLOW_TABLE_GROWTH is what lets the harness pass
# JS functions to hb_draw_funcs_set_*_func, which is how C# passes its delegates.

EXPORTS=$(cat <<'EOF'
_hb_version_string,
_hb_blob_create,_hb_blob_destroy,_hb_blob_get_data,_hb_blob_get_length,
_hb_face_create,_hb_face_destroy,_hb_face_get_upem,_hb_face_make_immutable,
_hb_face_is_immutable,_hb_face_reference_blob,
_hb_ot_layout_table_get_feature_tags,
_hb_ot_color_has_png,_hb_ot_color_has_layers,_hb_ot_color_has_palettes,
_hb_ot_color_glyph_reference_png,_hb_ot_color_glyph_get_layers,
_hb_ot_color_palette_get_colors,_hb_ot_color_palette_get_count,
_hb_subset_input_create_or_fail,_hb_subset_input_destroy,
_hb_subset_input_unicode_set,_hb_subset_input_get_flags,
_hb_subset_input_set_flags,_hb_subset_or_fail,
_hb_set_create,_hb_set_destroy,_hb_set_add,_hb_set_add_range,
_hb_font_create,_hb_font_destroy,_hb_font_get_h_extents,
_hb_font_get_nominal_glyph,_hb_font_get_glyph_extents,
_hb_ot_var_has_data,_hb_ot_var_get_axis_count,_hb_ot_var_get_axis_infos,
_hb_font_set_variations,
_hb_buffer_create,_hb_buffer_destroy,_hb_buffer_reset,_hb_buffer_add_utf16,
_hb_buffer_guess_segment_properties,_hb_buffer_set_direction,
_hb_buffer_set_language,_hb_language_from_string,_hb_language_to_string,
_hb_shape,_hb_buffer_get_glyph_infos,_hb_buffer_get_glyph_positions,
_hb_draw_funcs_create,_hb_draw_funcs_destroy,
_hb_draw_funcs_set_move_to_func,_hb_draw_funcs_set_line_to_func,
_hb_draw_funcs_set_quadratic_to_func,_hb_draw_funcs_set_cubic_to_func,
_hb_draw_funcs_set_close_path_func,_hb_font_draw_glyph,
_malloc,_free
EOF
)
EXPORTS=$(tr -d ' \n' <<<"$EXPORTS")

echo "==> linking Web~/onetext-hb.js"
mkdir -p "$OUT_WEB"
# Built from the *unrenamed* pass-1 object on purpose. The harness links its own
# module and never meets Unity's HarfBuzz, so it has nothing to collide with,
# and keeping the plain names means Web~/hb.js reads like HarfBuzzApi.cs instead
# of like this build script's private naming scheme.
emcc "$BUILD/harfbuzz-plain.o" -Oz \
  -o "$OUT_WEB/onetext-hb.js" \
  -sMODULARIZE=1 \
  -sEXPORT_NAME=createHarfBuzz \
  -sENVIRONMENT=web \
  -sALLOW_MEMORY_GROWTH=1 \
  -sALLOW_TABLE_GROWTH=1 \
  -sSTACK_SIZE=4MB \
  -sFILESYSTEM=0 \
  -sEXPORTED_FUNCTIONS="$EXPORTS" \
  -sEXPORTED_RUNTIME_METHODS=ccall,cwrap,addFunction,removeFunction,getValue,setValue,UTF8ToString,stringToUTF8,lengthBytesUTF8
# (HEAPU8/HEAP32/HEAPF32 are on the module object already at this Emscripten
#  version; naming them in EXPORTED_RUNTIME_METHODS is a warning, not an export.)

# ------------------------------------------------------------------ report ---

echo
echo "Emscripten $have, HarfBuzz $HB_VERSION"
ls -l "$OUT_UNITY/libHarfBuzzSharp.a" "$OUT_WEB/onetext-hb.js" "$OUT_WEB/onetext-hb.wasm" |
  awk '{printf "  %9d  %s\n", $5, $NF}'
echo
echo "Note: built without -pthread. Unity Web is single-threaded by default; if a"
echo "project enables pthreads the whole player is compiled -pthread and this"
echo "archive will not link against it. Rebuild with -pthread on both lines above."
