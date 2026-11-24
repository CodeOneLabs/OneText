# Workload matrix — rebuild count against glyph novelty

Unity 6000.0.77f1, Null, Apple M4 Pro. Median of the frames in each run; p99 and max are what a player feels as a hitch. Draw groups are distinct material+texture pairs (the thing that decides whether uGUI can batch), counted structurally, since a hand-driven render does not tick the engine's own batch statistics.

Jobs: Burst, compiled synchronously before the run: what a player build gets.

Allocation measured by: managed heap delta, which resolves about a 4 KB page and rounds up: measured against known allocations it read 20.8 KB of real work as 28-32 KB, 5.2 KB as 4-8 KB, and 1 KB as 0 more often than not, while never reporting bytes for a frame that allocated none. Per-frame figures below a page mean nothing; the per-frame mean over hundreds of frames is the number to read, and it is an over-estimate. Frames that collected are excluded, not clamped. Each cell is the median of 3 repetitions, chosen by p99, so a stray GC pause in one repetition cannot decide the number.

Coverage is the share of the last frame's characters the system can actually draw, asked of its own fonts without letting them add anything while answering. It is in the table because a frame time is only comparable to another frame time when both frames drew the same text: a system that skips a fifth of the characters posts a better number for doing less work. **us/char is the crude correction and a floor, not a fair price** — the characters a system skips are not the average ones, they are the ones it has no glyph for, which are exactly the ones that would have cost it an atlas page. Where a row says "no system fonts", the same engine was run with its last-resort tier off so every system draws the same set; that row, not the arithmetic, is the like-for-like comparison.

| Scenario | System | Frames | Median ms | p99 ms | Max ms (frame) | Draw groups | Graphics | Alloc/frame | GC frames | Texture | Coverage | us/char |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| few rebuilds, warm atlas | OneText | 600 | 0.068 | 0.094 | 0.118 (0) | 1 | 200 | 4.2 KB | 0 | 4.0 MB | 100 % | 0.04 |
| few rebuilds, warm atlas | TMP dynamic | 600 | 0.099 | 0.138 | 0.200 (101) | 1 | 200 | 11.8 KB | 0 | 3.0 MB | 79 % | 0.07 |
| many rebuilds, warm atlas | OneText | 600 | 0.489 | 0.597 | 0.672 (0) | 1 | 200 | 19.3 KB | 0 | 4.0 MB | 100 % | 0.28 |
| many rebuilds, warm atlas | TMP dynamic | 600 | 0.924 | 1.053 | 1.130 (450) | 1 | 200 | 76.3 KB | 0 | 3.0 MB | 78 % | 0.67 |
| few rebuilds, new glyphs | OneText | 600 | 0.363 | 0.622 | 21.861 (373) | 1 | 200 | 22.1 KB | 0 | 4.0 MB | 100 % | 0.20 |
| few rebuilds, new glyphs | TMP dynamic | 600 | 11.367 | 33.402 | 41.015 (42) | 8 | 817 | 178.7 KB | 0 | 14.0 MB | 78 % | 7.85 |
| many rebuilds, new glyphs | OneText | 600 | 1.753 | 2.528 | 23.038 (37) | 1 | 200 | 120.7 KB | 0 | 4.0 MB | 100 % | 0.96 |
| many rebuilds, new glyphs | TMP dynamic | 600 | 2.698 | 173.701 | 204.980 (4) | 18 | 1582 | 596.1 KB | 3 | 20.0 MB | 79 % | 1.88 |

- **few rebuilds, warm atlas / OneText**: atlas 1024x1024x4 (4 MB), 516 tiles, 10 % full, 0 evictions, 0 compactions, 0 partial / 15 full uploads; drew 1747 of 1747 characters on the last frame (100 %); system font files probed: 141
- **few rebuilds, warm atlas / TMP dynamic**: NotoSans: 1 atlas texture(s) 1024x1024, 36 chars, Dynamic; NotoSansArabic: 1 atlas texture(s) 1024x1024, 36 chars, Dynamic; SystemCJK: 1 atlas texture(s) 1024x1024, 351 chars, Dynamic; drew 1388 of 1747 characters on the last frame (79 %)
- **many rebuilds, warm atlas / OneText**: atlas 1024x1024x4 (4 MB), 516 tiles, 10 % full, 0 evictions, 0 compactions, 0 partial / 15 full uploads; drew 1770 of 1770 characters on the last frame (100 %); system font files probed: 141
- **many rebuilds, warm atlas / TMP dynamic**: NotoSans: 1 atlas texture(s) 1024x1024, 36 chars, Dynamic; NotoSansArabic: 1 atlas texture(s) 1024x1024, 36 chars, Dynamic; SystemCJK: 1 atlas texture(s) 1024x1024, 351 chars, Dynamic; drew 1387 of 1770 characters on the last frame (78 %)
- **few rebuilds, new glyphs / OneText**: atlas 1024x1024x4 (4 MB), 4,462 tiles, 87 % full, 11,498 evictions, 1 compactions, 0 partial / 801 full uploads; drew 1851 of 1851 characters on the last frame (100 %); system font files probed: 3,483
- **few rebuilds, new glyphs / TMP dynamic**: NotoSans: 1 atlas texture(s) 1024x1024, 36 chars, Dynamic; NotoSansArabic: 1 atlas texture(s) 1024x1024, 36 chars, Dynamic; SystemCJK: 16 atlas texture(s) 1024x1024, 8470 chars, Dynamic; drew 1448 of 1851 characters on the last frame (78 %)
- **many rebuilds, new glyphs / OneText**: atlas 1024x1024x4 (4 MB), 4,472 tiles, 87 % full, 137,376 evictions, 1 compactions, 0 partial / 801 full uploads; drew 1828 of 1828 characters on the last frame (100 %); system font files probed: 4,463
- **many rebuilds, new glyphs / TMP dynamic**: NotoSans: 1 atlas texture(s) 1024x1024, 36 chars, Dynamic; NotoSansArabic: 1 atlas texture(s) 1024x1024, 36 chars, Dynamic; SystemCJK: 32 atlas texture(s) 1024x1024, 12748 chars, Dynamic; drew 1437 of 1828 characters on the last frame (79 %)
