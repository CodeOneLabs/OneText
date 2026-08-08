#!/usr/bin/env bash
#
# Builds HarfBuzz to a Linux x86_64 shared library for the Linux editor and the
# StandaloneLinux64 player.
#
#   Runtime/Plugins/Linux/x86_64/libHarfBuzzSharp.so
#
# This is the second binary in this package that is not vendored from the
# HarfBuzzSharp NuGet family, and the first one that had to stop being
# vendored. The NuGet build is a correct HarfBuzz; it is linked in a way that
# cannot survive being loaded into a process that already has one, and the
# Linux editor is exactly that process. Docs/NATIVES.md records the crash.
#
# The short version, because it is the reason every flag below is what it is:
# ELF resolves an exported function through the global symbol scope, first
# definition wins, and a library's calls to its *own* exported functions are no
# exception. Unity's editor already defines hb_* for TextCore, so our
# hb_font_create called Unity's hb_font_set_var_coords_normalized, which walked
# a different hb_font_t and freed a pointer that was never a pointer. Mach-O's
# two-level namespace binds each library to its own definitions, which is why
# 508 EditMode tests pass on macOS and the first shaping call on Linux is a
# SIGSEGV.
#
# Usage:  Tools/build_linux_natives.sh [--clean]
#
# Meant to run inside an old-glibc container; the runner's own glibc becomes
# the floor of every machine the result can load on, and ubuntu-latest's is
# far above the oldest Unity image this package supports. See
# .github/workflows/build-natives.yml, which is where it actually runs.

set -euo pipefail

HB_VERSION=14.2.1        # matches HarfBuzzSharp.NativeAssets.* 14.2.1.1
GLIBC_CEILING="${ONETEXT_GLIBC_CEILING:-2.17}"   # manylinux2014; every Unity Linux image is newer

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="${ONETEXT_LINUX_WORK:-${TMPDIR:-/tmp}/onetext-linux}"
SRC="${HB_SRC:-$WORK/harfbuzz-$HB_VERSION}"
BUILD="$WORK/build-$HB_VERSION"
OUT="$REPO/Runtime/Plugins/Linux/x86_64"
API="$REPO/Runtime/Core/Native/HarfBuzzApi.cs"

[ "${1:-}" = "--clean" ] && rm -rf "$BUILD"

mkdir -p "$WORK" "$BUILD"
if [ ! -d "$SRC" ]; then
  echo "==> fetching HarfBuzz $HB_VERSION"
  git clone --depth 1 --branch "$HB_VERSION" \
      https://github.com/harfbuzz/harfbuzz.git "$SRC"
fi

# ------------------------------------------------------------------ exports --
#
# HarfBuzzApi.cs is the list. Reading it here rather than keeping a copy is the
# only way the two stay in step: an entry point added there and forgotten here
# would link, ship, and throw EntryPointNotFoundException on Linux alone.
#
# hb_subset_* is added as a pattern instead of by name. Only six of them are
# P/Invoked, but Docs/NATIVES.md asserts of every binary in this package that
# all 31 are present, because a shaper that silently has no subsetter is the
# kind of per-platform drift that file exists to prevent, and a symbol nobody
# exports cannot be counted.

mapfile -t ENTRY < <(
  grep -oE 'EntryPoint = HbPrefix \+ "hb_[a-z0-9_]+"' "$API" |
  grep -oE '"hb_[a-z0-9_]+"' | tr -d '"' | sort -u
)
[ "${#ENTRY[@]}" -ge 50 ] || { echo "read only ${#ENTRY[@]} entry points from HarfBuzzApi.cs" >&2; exit 1; }

# An anonymous version node, deliberately. A named one would stamp every export
# as hb_shape@@SOMETHING, which dlsym still resolves but which is a second
# difference from the vendored binary for no gain; `local: *` alone does the
# whole job of hiding the C++ internals from the dynamic symbol table.
#
# -fvisibility=hidden is *not* used with it. A symbol the compiler marked
# STV_HIDDEN is localised before the version script is applied, so the two
# together would quietly un-export half the list; the script's `local: *` gets
# the same dynamic table by itself.
{
  echo "{"
  echo "  global:"
  printf '    %s;\n' "${ENTRY[@]}"
  echo "    hb_subset_*;"
  echo "  local:"
  echo "    *;"
  echo "};"
} > "$BUILD/hb-exports.map"

# ------------------------------------------------- one translation unit ------
#
# HarfBuzz's own amalgamation, for the reason the Web build gives at length:
# libharfbuzz.a and libharfbuzz-subset.a overlap, and harfbuzz-subset.cc is the
# superset that contains both without the overlap. The files it lacks are the
# platform integrations (CoreText, DirectWrite, FreeType, GDI, glib, graphite2,
# Uniscribe), and the vendored NuGet binary had none of them either: its NEEDED
# list is libstdc++, libpthread, libm, libc and nothing else. So this is the
# same HarfBuzz, shaping the same way, and the check at the bottom of this file
# is what keeps that claim honest.
#
# None of the HB_NO_* trims the Web build uses are set here. Those exist
# because a browser has no filesystem and no threads and the archive had to fit
# in a download; the Linux editor has all of it, and every define that removes
# a code path is a way for Linux to shape differently from macOS.

CFLAGS=(
  -I "$SRC/src"
  -O2 -fPIC -shared
  -fno-exceptions -fno-rtti -fno-threadsafe-statics
  -DHB_NDEBUG -DNDEBUG
  -pthread
)

# -Wl,-Bsymbolic is the fix. It resolves every reference this library makes to
# a symbol this library defines at link time, against the local definition, so
# the calls never reach the PLT and never meet Unity's HarfBuzz.
#
# The version script above would have stopped the one crash that was actually
# observed, because the symbol in frame 3 of it is not one of the 59 and is
# local now. That is not a reason to drop this line, it is the reason to keep
# it: 59 hb_* are still exported, HarfBuzz calls its own public API internally
# throughout, and the next crash would have read identically with a different
# name in that frame. This flag covers the 59 as well.
#
# -Bsymbolic rather than -Bsymbolic-functions, which would leave data
# references to be resolved through the global scope. Everything exported here
# happens to be a function, so today the two are the same binary; there is no
# reason to ship the one whose guarantee is narrower than the problem.
#
# --no-undefined turns a missing symbol into a link error here rather than a
# DllNotFoundException on a user's machine: dlopen resolves everything eagerly
# enough that one unresolved reference fails the whole load, with no message
# saying which.
LDFLAGS=(
  -Wl,-Bsymbolic
  -Wl,--version-script="$BUILD/hb-exports.map"
  -Wl,--no-undefined
  -Wl,-soname,libHarfBuzzSharp.so
  -static-libstdc++ -static-libgcc
)

echo "==> compiling libHarfBuzzSharp.so"
SO="$BUILD/libHarfBuzzSharp.so"
${CXX:-g++} "$SRC/src/harfbuzz-subset.cc" -o "$SO" "${CFLAGS[@]}" "${LDFLAGS[@]}"
strip --strip-unneeded "$SO"

# ------------------------------------------------------------------ verify ---
#
# Everything below fails the build rather than warning. The vendored binary
# passed every check this package had and still crashed the editor, so the
# checks are now the ones that would have caught it.

fail=0
say () { printf '    %s\n' "$*"; }

echo
echo "==> the symbolic bit"
# Two spellings, because binutils changed its mind. The old one is a dynamic
# tag of its own, DT_SYMBOLIC, which readelf prints as `(SYMBOLIC)`; the
# current one is DF_SYMBOLIC inside DT_FLAGS. manylinux2014's ld writes the
# first, ubuntu's the second, and either answers the question.
readelf -d "$SO" | grep -E 'SYMBOLIC|FLAGS|NEEDED|SONAME'
readelf -d "$SO" | grep -qE '\(SYMBOLIC\)|FLAGS\).*SYMBOLIC' || {
  echo "neither DT_SYMBOLIC nor DF_SYMBOLIC is set: -Bsymbolic did not take, and this" >&2
  echo "binary will call Unity's HarfBuzz the moment the editor loads it" >&2; fail=1; }

echo
echo "==> NEEDED"
readelf -d "$SO" | sed -n 's/.*NEEDED.*\[\(.*\)\]/\1/p' | tee "$BUILD/needed.txt"
while read -r n; do
  case "$n" in
    libc.so.6|libm.so.6|libpthread.so.0|libdl.so.2|libgcc_s.so.1) ;;
    ld-linux-x86-64.so.2) ;;   # the loader naming itself; -static-libstdc++ adds it
    *) echo "unexpected NEEDED entry: $n" >&2; fail=1 ;;
  esac
done < "$BUILD/needed.txt"

echo
echo "==> glibc version requirements"
readelf -V "$SO" | grep -oE 'GLIBC_[0-9]+\.[0-9]+' | sort -u -V | tr '\n' ' '; echo
worst=$(readelf -V "$SO" | grep -oE 'GLIBC_[0-9]+\.[0-9]+' | sed 's/GLIBC_//' | sort -u -V | tail -1)
if [ "$(printf '%s\n%s\n' "$worst" "$GLIBC_CEILING" | sort -V | tail -1)" != "$GLIBC_CEILING" ]; then
  echo "needs glibc $worst, above the $GLIBC_CEILING floor this has to run on" >&2; fail=1
else
  say "ok: highest requirement is glibc $worst"
fi

echo
echo "==> symbols"
exported=$(nm -D --defined-only "$SO" | awk '$2 ~ /^[TDBRWiu]$/ {print $3}' | sort -u)
undef=$(nm -D --undefined-only "$SO" | awk '{print $NF}' | sort -u)

missing=()
for s in "${ENTRY[@]}"; do grep -qx "$s" <<<"$exported" || missing+=("$s"); done
if [ "${#missing[@]}" != 0 ]; then
  echo "not exported: ${missing[*]}" >&2; fail=1
else
  say "ok: all ${#ENTRY[@]} P/Invoke entry points exported"
fi

n_subset=$(grep -c '^hb_subset_' <<<"$exported" || true)
[ "$n_subset" -ge 31 ] || { echo "only $n_subset hb_subset_* exported, expected >= 31" >&2; fail=1; }
say "ok: $n_subset hb_subset_* exported"

# An undefined hb_* is precisely the shape of the bug: a symbol this library
# will go looking for in a process that has another HarfBuzz in it.
leftover=$(grep -E '^_?hb_' <<<"$undef" || true)
if [ -n "$leftover" ]; then
  echo "undefined HarfBuzz symbols; these resolve against whatever loaded first:" >&2
  head -20 <<<"$leftover" >&2; fail=1
else
  say "ok: no undefined hb_* symbols"
fi

say "ok: $(grep -cE '^hb_' <<<"$exported") hb_* exported, $(wc -l <<<"$exported") dynamic symbols in total"

# --------------------------------------------------------------- run it -----
#
# The checks above are all readable from the file. This one loads it the way
# Mono does and puts a second HarfBuzz in front of it, which is the only thing
# that actually answers the question the crash asked.

cat > "$BUILD/fakehb.c" <<'C'
/* Stands in for Unity's HarfBuzz: same names, earlier in the global scope,
   different everything else. Every hit is a call our library made to a symbol
   it defines itself and resolved against a stranger. */
#include <stddef.h>
int onetext_fake_hits = 0;
#define STUB(name) void name(void) { onetext_fake_hits++; }
STUB(hb_font_set_var_coords_normalized)
STUB(hb_font_set_funcs)
STUB(hb_font_set_scale)
STUB(hb_font_destroy)
STUB(hb_face_get_upem)
STUB(hb_face_reference_table)
STUB(hb_blob_destroy)
STUB(hb_blob_get_data)
STUB(hb_set_create)
STUB(hb_set_destroy)
STUB(hb_ot_var_get_axis_count)
STUB(hb_ot_var_has_data)
STUB(hb_buffer_reset)
STUB(hb_language_from_string)
C

cat > "$BUILD/smoke.c" <<'C'
/* argv[1] the library, argv[2] a TTF. Everything goes through dlsym on the
   library's own handle, so nothing here can accidentally test the stub. */
#include <dlfcn.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static void *lib;
static void *sym(const char *n) {
  void *p = dlsym(lib, n);
  if (!p) { fprintf(stderr, "no %s: %s\n", n, dlerror()); exit(1); }
  return p;
}

int main(int argc, char **argv) {
  void *fake = dlopen(argv[3], RTLD_NOW | RTLD_GLOBAL);
  if (!fake) { fprintf(stderr, "fake: %s\n", dlerror()); return 1; }
  int *hits = (int *)dlsym(fake, "onetext_fake_hits");

  lib = dlopen(argv[1], RTLD_NOW);
  if (!lib) { fprintf(stderr, "dlopen: %s\n", dlerror()); return 1; }

  const char *(*version)(void) = sym("hb_version_string");
  printf("    hb_version_string  %s\n", version());

  FILE *f = fopen(argv[2], "rb");
  if (!f) { perror(argv[2]); return 1; }
  fseek(f, 0, SEEK_END); long n = ftell(f); fseek(f, 0, SEEK_SET);
  char *bytes = malloc(n);
  if (fread(bytes, 1, n, f) != (size_t)n) return 1;
  fclose(f);

  void *(*blob_create)(const char *, unsigned, int, void *, void *) = sym("hb_blob_create");
  void *(*face_create)(void *, unsigned) = sym("hb_face_create");
  unsigned (*face_upem)(void *) = sym("hb_face_get_upem");
  void *(*font_create)(void *) = sym("hb_font_create");
  int (*nominal)(void *, unsigned, unsigned *) = sym("hb_font_get_nominal_glyph");
  void *(*buf_create)(void) = sym("hb_buffer_create");
  void (*buf_utf16)(void *, const unsigned short *, int, unsigned, int) = sym("hb_buffer_add_utf16");
  void (*buf_guess)(void *) = sym("hb_buffer_guess_segment_properties");
  void (*shape)(void *, void *, void *, unsigned) = sym("hb_shape");
  unsigned *(*infos)(void *, unsigned *) = sym("hb_buffer_get_glyph_infos");
  void (*buf_destroy)(void *) = sym("hb_buffer_destroy");
  void (*font_destroy)(void *) = sym("hb_font_destroy");
  void (*face_destroy)(void *) = sym("hb_face_destroy");
  void (*blob_destroy)(void *) = sym("hb_blob_destroy");

  void *blob = blob_create(bytes, (unsigned)n, 1 /* READONLY */, NULL, NULL);
  void *face = face_create(blob, 0);
  printf("    hb_face_get_upem   %u\n", face_upem(face));

  /* hb_font_create is the call that crashed the editor. */
  void *font = font_create(face);
  unsigned gid = 0;
  int found = nominal(font, 'A', &gid);   /* not inside printf: C does not say which */
  printf("    hb_font_get_nominal_glyph 'A' -> %d gid %u\n", found, gid);
  if (!found || !gid) { fprintf(stderr, "no glyph for 'A'\n"); return 1; }

  static const unsigned short text[] = { 'A','V','A','T','a','r',' ','W','a','v','e' };
  void *buf = buf_create();
  buf_utf16(buf, text, 11, 0, 11);
  buf_guess(buf);
  shape(font, buf, NULL, 0);
  unsigned count = 0;
  unsigned *first = infos(buf, &count);   /* hb_glyph_info_t opens with the glyph id */
  printf("    hb_shape           %u glyphs, first gid %u\n", count, count ? first[0] : 0);
  if (count != 11 || !first[0]) { fprintf(stderr, "that is not a shaped line\n"); return 1; }

  buf_destroy(buf);
  font_destroy(font);
  face_destroy(face);
  blob_destroy(blob);
  free(bytes);

  printf("    interposed calls   %d\n", *hits);
  return *hits ? 2 : 0;
}
C

${CC:-gcc} -shared -fPIC "$BUILD/fakehb.c" -o "$BUILD/libfakehb.so"
${CC:-gcc} "$BUILD/smoke.c" -o "$BUILD/smoke" -ldl

echo
echo "==> loading it with a second HarfBuzz already in the process"
if "$BUILD/smoke" "$SO" "$REPO/Tests/Fonts~/NotoSans.ttf" "$BUILD/libfakehb.so"; then
  say "ok: shaped, and bound to itself throughout"
else
  st=$?
  [ "$st" = 2 ] && echo "the library called a stranger's HarfBuzz; -Bsymbolic is not doing its job" >&2
  fail=1
fi

# The same harness, pointed at the binary being replaced, is what says whether
# any of this measures anything; it runs where a modern libstdc++ is, which is
# not in here. See the workflow.

[ "$fail" = 0 ] || exit 1

# ------------------------------------------------------------------ install --

mkdir -p "$OUT"
install -m 755 "$SO" "$OUT/libHarfBuzzSharp.so"

echo
echo "HarfBuzz $HB_VERSION, $(${CXX:-g++} --version | head -1)"
ls -l "$OUT/libHarfBuzzSharp.so" | awk '{printf "  %9d  %s\n", $5, $NF}'
