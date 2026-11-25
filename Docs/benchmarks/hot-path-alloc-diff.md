# Frame allocation, one variable at a time

Unity 6000.0.77f1, Apple M4 Pro, 200 labels, 600 frames, managed heap delta, which resolves about a 4 KB page and rounds up: measured against known allocations it read 20.8 KB of real work as 28-32 KB, 5.2 KB as 4-8 KB, and 1 KB as 0 more often than not, while never reporting bytes for a frame that allocated none. Per-frame figures below a page mean nothing; the per-frame mean over hundreds of frames is the number to read, and it is an over-estimate. Frames that collected are excluded, not clamped.

| Case | System | KB/frame | median µs | p99 µs | draw groups | GC frames |
|---|---|---|---|---|---|---|
| idle: nothing changes | OneText | 0.00 | 14 | 23 | 1 | 0 |
| 50 labels retexted, ToString() each (= C3) | OneText | 1.36 | 179 | 210 | 1 | 0 |
| 50 labels retexted, strings pre-built | OneText | 0.00 | 167 | 214 | 1 | 0 |
| 200 labels retexted, strings pre-built | OneText | 0.00 | 634 | 791 | 1 | 0 |
| 50 labels retexted, screen-space canvas | OneText | 0.00 | 170 | 223 | 1 | 0 |
| 50 labels retexted, rich-text markup | OneText | 0.00 | 364 | 446 | 1 | 0 |
| idle: nothing changes | TMP | 0.00 | 6 | 14 | 3 | 0 |
| 50 labels retexted, ToString() each (= C3) | TMP | 1.56 | 407 | 478 | 3 | 0 |
| 50 labels retexted, strings pre-built | TMP | 0.00 | 362 | 394 | 3 | 0 |
| 200 labels retexted, strings pre-built | TMP | 0.00 | 1424 | 1498 | 3 | 0 |
| 50 labels retexted, screen-space canvas | TMP | 0.00 | 362 | 373 | 3 | 0 |
| 50 labels retexted, rich-text markup | TMP | 0.00 | 617 | 652 | 3 | 0 |
| raw: setter only, nothing rebuilt | OneText | 0.00 | 0 | 0 | 0 | 0 |
| raw: setter + ForceMeshUpdate, no canvas | OneText | 0.00 | 0 | 0 | 0 | 0 |
| raw: setter + canvas rebuild | OneText | 0.00 | 0 | 0 | 0 | 0 |
| raw: setter only, nothing rebuilt | TMP | 0.00 | 0 | 0 | 0 | 0 |
| raw: setter + ForceMeshUpdate, no canvas | TMP | 0.00 | 0 | 0 | 0 | 0 |
| raw: setter + canvas rebuild | TMP | 0.00 | 0 | 0 | 0 | 0 |
| raw: setter + EnsureLayout (no mesh) | OneText | 0.00 | 0 | 0 | 0 | 0 |
| raw: ForceMeshUpdate only, text unchanged | OneText | 0.00 | 0 | 0 | 0 | 0 |
| raw: re-assign the SAME string + ForceMeshUpdate | OneText | 0.00 | 0 | 0 | 0 | 0 |
| raw: re-assign the SAME string + ForceMeshUpdate | TMP | 0.00 | 0 | 0 | 0 | 0 |
| raw: SetText(int), no string anywhere | OneText | 0.00 | 0 | 0 | 0 | 0 |
| raw: SetText("{0}", n), no string anywhere | TMP | 2.75 | 0 | 0 | 0 | 0 |
| raw: markup, parse | OneText | 0.00 | 0 | 0 | 0 | 0 |
| raw: markup, parse+layout | OneText | 0.00 | 0 | 0 | 0 | 0 |
| raw: markup, everything | OneText | 0.00 | 0 | 0 | 0 | 0 |
| raw: markup, color only | OneText | 0.00 | 0 | 0 | 0 | 0 |
| raw: markup, bold only | OneText | 0.00 | 0 | 0 | 0 | 0 |
| raw: markup, both | OneText | 0.00 | 0 | 0 | 0 | 0 |
| raw: markup, no tags | OneText | 0.00 | 0 | 0 | 0 | 0 |
| raw: korean, no tags | OneText | 0.00 | 0 | 0 | 0 | 0 |
| raw: latin wrapped, no tags | OneText | 0.00 | 0 | 0 | 0 | 0 |
| 50 labels retexted, RichText = false | OneText | 0.00 | 167 | 193 | 1 | 0 |
