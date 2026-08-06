# OneText vs TextMeshPro vs UniText: compound scenarios

Unity 6000.0.77f1, Null, Apple M4 Pro. Median of the frames in each run; p99 and max are what a player feels as a hitch. Draw groups are distinct material+texture pairs, the thing that decides whether uGUI can batch, counted structurally, since a hand-driven render does not tick the engine's own batch statistics.

Allocation measured by: managed heap delta (a floor, since collections inside a frame hide allocations). Each cell is the median of 3 repetitions, chosen by p99, so a stray GC pause in one repetition cannot decide the number.

| Scenario | System | Frames | Median ms | p99 ms | Max ms (frame) | Draw groups | Graphics | Alloc/frame | Texture |
|---|---|---|---|---|---|---|---|---|---|
| C2 chat stream (CJK churn) | OneText 4MB +prewarm | 2000 | 0.224 | 0.500 | 3.041 (273) | 1 | 30 | 10.6 KB | 4.0 MB |
| C1 global UI | OneText 4MB +prewarm | 600 | 0.435 | 1.432 | 6.713 (300) | 1 | 60 | 20.2 KB | 4.0 MB |
| C3 world-space labels | OneText 4MB +prewarm | 600 | 0.712 | 0.964 | 3.895 (260) | 1 | 200 | 38.1 KB | 4.0 MB |
| C2 chat stream (CJK churn) | TMP dynamic 1024² @32pt +prewarm | 2000 | 0.587 | 10.101 | 11.093 (411) | 6 | 119 | 40.0 KB | 11.0 MB |
| C1 global UI | TMP dynamic 1024² @32pt +prewarm | 600 | 0.455 | 14.892 | 347.312 (450) | 5 | 100 | 18.0 KB | 4.0 MB |
| C3 world-space labels | TMP dynamic 1024² @32pt +prewarm | 600 | 0.627 | 0.729 | 0.792 (582) | 3 | 300 | 1.6 KB | 3.0 MB |
| C2 chat stream (CJK churn) | TMP static 1024² @32pt | 2000 | 0.094 | 0.137 | 0.184 (1412) | 2 | 60 | 6.7 KB | 3.0 MB |
| C1 global UI | TMP static 1024² @32pt | 600 | 0.516 | 0.784 | 2.513 (300) | 8 | 160 | 12.6 KB | 3.0 MB |
| C3 world-space labels | TMP static 1024² @32pt | 600 | 0.632 | 0.730 | 0.827 (444) | 5 | 500 | 1.6 KB | 3.0 MB |
| C2 chat stream (CJK churn) | UniText 1024² | 2000 | 0.256 | 0.684 | 1.154 (26) | 1 | 60 | 1.3 KB | 9.0 MB |
| C1 global UI | UniText 1024² | 600 | 0.506 | 1.237 | 3.045 (450) | 1 | 120 | 1.7 KB | 4.0 MB |
| C3 world-space labels | UniText 1024² | 600 | 0.997 | 1.080 | 1.111 (181) | 1 | 400 | 1.6 KB | 2.0 MB |
| C2 chat stream (CJK churn) | UniText 1024² +prewarm | 2000 | 0.252 | 0.685 | 0.907 (1370) | 1 | 60 | 1.4 KB | 11.0 MB |
| C1 global UI | UniText 1024² +prewarm | 600 | 0.549 | 1.156 | 3.191 (450) | 1 | 120 | 1.0 KB | 4.0 MB |
| C3 world-space labels | UniText 1024² +prewarm | 600 | 1.036 | 1.259 | 1.336 (117) | 1 | 400 | 102 B | 3.0 MB |

- **C2 chat stream (CJK churn) / OneText 4MB +prewarm**: atlas 1024x1024x4 (4 MB), 4,721 tiles, 88 % full, 1,752 evictions, 0 compactions, 0 partial / 1,839 full uploads (median of 3 runs by p99)
- **C1 global UI / OneText 4MB +prewarm**: atlas 1024x1024x4 (4 MB), 1,797 tiles, 39 % full, 0 evictions, 0 compactions, 0 partial / 117 full uploads (median of 3 runs by p99)
- **C3 world-space labels / OneText 4MB +prewarm**: atlas 1024x1024x4 (4 MB), 1,302 tiles, 27 % full, 0 evictions, 0 compactions, 0 partial / 19 full uploads (median of 3 runs by p99)
- **C2 chat stream (CJK churn) / TMP dynamic 1024² @32pt +prewarm**: NotoSans: 1 atlas texture(s) 1024x1024, 36 chars, Dynamic; NotoSansArabic: 1 atlas texture(s) 1024x1024, 36 chars, Dynamic; SystemCJK: 16 atlas texture(s) 1024x1024, 6239 chars, Dynamic; drew 517 of 659 characters on the last frame (78 %) (median of 3 runs by p99)
- **C1 global UI / TMP dynamic 1024² @32pt +prewarm**: NotoSans: 1 atlas texture(s) 1024x1024, 39 chars, Dynamic; NotoSansArabic: 1 atlas texture(s) 1024x1024, 39 chars, Dynamic; SystemCJK: 2 atlas texture(s) 1024x1024, 855 chars, Dynamic; drew 357 of 357 characters on the last frame (100 %) (median of 3 runs by p99)
- **C3 world-space labels / TMP dynamic 1024² @32pt +prewarm**: NotoSans: 1 atlas texture(s) 1024x1024, 13 chars, Dynamic; NotoSansArabic: 1 atlas texture(s) 1024x1024, 10 chars, Dynamic; SystemCJK: 1 atlas texture(s) 1024x1024, 640 chars, Dynamic; drew 975 of 975 characters on the last frame (100 %) (median of 3 runs by p99)
- **C2 chat stream (CJK churn) / TMP static 1024² @32pt**: NotoSans: 1 atlas texture(s) 1024x1024, 36 chars, Static; NotoSansArabic: 1 atlas texture(s) 1024x1024, 36 chars, Static; SystemCJK: 1 atlas texture(s) 1024x1024, 349 chars, Static; drew 398 of 659 characters on the last frame (60 %) (median of 3 runs by p99)
- **C1 global UI / TMP static 1024² @32pt**: NotoSans: 1 atlas texture(s) 1024x1024, 10 chars, Static; NotoSansArabic: 1 atlas texture(s) 1024x1024, 10 chars, Static; SystemCJK: 1 atlas texture(s) 1024x1024, 638 chars, Static; drew 357 of 357 characters on the last frame (100 %) (median of 3 runs by p99)
- **C3 world-space labels / TMP static 1024² @32pt**: NotoSans: 1 atlas texture(s) 1024x1024, 10 chars, Static; NotoSansArabic: 1 atlas texture(s) 1024x1024, 10 chars, Static; SystemCJK: 1 atlas texture(s) 1024x1024, 637 chars, Static; drew 975 of 975 characters on the last frame (100 %) (median of 3 runs by p99)
- **C2 chat stream (CJK churn) / UniText 1024²**: NotoSans: 0 atlas texture(s) 1024x1024, 1 glyphs, SDF; NotoSansArabic: 0 atlas texture(s) 1024x1024, 1 glyphs, SDF; SystemCJK: 9 atlas texture(s) 1024x1024, 6227 glyphs, SDF; 168 canvas renderers and 839 laid-out glyphs across 30 labels; drew 517 of 659 characters on the last frame (78 %) (median of 3 runs by p99)
- **C1 global UI / UniText 1024²**: NotoSans: 1 atlas texture(s) 1024x1024, 39 glyphs, SDF; NotoSansArabic: 1 atlas texture(s) 1024x1024, 38 glyphs, SDF; SystemCJK: 2 atlas texture(s) 1024x1024, 854 glyphs, SDF; 133 canvas renderers and 417 laid-out glyphs across 60 labels; drew 357 of 357 characters on the last frame (100 %) (median of 3 runs by p99)
- **C3 world-space labels / UniText 1024²**: NotoSans: 1 atlas texture(s) 1024x1024, 11 glyphs, SDF; NotoSansArabic: 0 atlas texture(s) 1024x1024, 1 glyphs, SDF; SystemCJK: 1 atlas texture(s) 1024x1024, 407 glyphs, SDF; 300 canvas renderers and 1125 laid-out glyphs across 200 labels; drew 975 of 975 characters on the last frame (100 %) (median of 3 runs by p99)
- **C2 chat stream (CJK churn) / UniText 1024² +prewarm**: NotoSans: 1 atlas texture(s) 1024x1024, 37 glyphs, SDF; NotoSansArabic: 1 atlas texture(s) 1024x1024, 37 glyphs, SDF; SystemCJK: 9 atlas texture(s) 1024x1024, 6237 glyphs, SDF; 158 canvas renderers and 839 laid-out glyphs across 30 labels; drew 517 of 659 characters on the last frame (78 %) (median of 3 runs by p99)
- **C1 global UI / UniText 1024² +prewarm**: NotoSans: 1 atlas texture(s) 1024x1024, 39 glyphs, SDF; NotoSansArabic: 1 atlas texture(s) 1024x1024, 38 glyphs, SDF; SystemCJK: 2 atlas texture(s) 1024x1024, 854 glyphs, SDF; 133 canvas renderers and 417 laid-out glyphs across 60 labels; drew 357 of 357 characters on the last frame (100 %) (median of 3 runs by p99)
- **C3 world-space labels / UniText 1024² +prewarm**: NotoSans: 1 atlas texture(s) 1024x1024, 11 glyphs, SDF; NotoSansArabic: 1 atlas texture(s) 1024x1024, 11 glyphs, SDF; SystemCJK: 1 atlas texture(s) 1024x1024, 638 glyphs, SDF; 300 canvas renderers and 1125 laid-out glyphs across 200 labels; drew 975 of 975 characters on the last frame (100 %) (median of 3 runs by p99)
