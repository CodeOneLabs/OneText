# Before and after, in one process

`Scoreboard.Run` (kept beside this file as `Scoreboard.cs.txt`), 2026-11-24,
Unity 6000.0.77f1, Apple M4 Pro, null graphics device, Release code
optimization, three repetitions. Each cell is `median / p99 / max us`, then the
share of the last frame's characters drawn. `OFF` is every hot-path switch off
(the code 0.4.2 ran), `ON` is the package as it ships.

```
[score] rep 0 C1 global UI    | OFF    308/  3050/  40941 us 100% | ON    135/   490/   3615 us 100% | TMPdyn    283/ 13180/ 328755 us 100% | TMPstatic    330/   495/   1739 us 100%
[score] rep 0 C2 chat stream  | OFF    404/  1100/   1781 us 100% | ON    113/   239/    763 us 100% | TMPdyn    689/ 11041/  12052 us 82% | TMPstatic     59/    81/     91 us 61%
[score] rep 0 C3 world labels | OFF    467/   493/    696 us 100% | ON    180/   205/    269 us 100% | TMPdyn    380/   394/    443 us 100% | TMPstatic    385/   401/    421 us 100%
[score] rep 0 matrix 5 warm   | OFF    118/   139/    184 us 100% | ON     67/    81/    103 us 100% | TMPdyn     94/   112/    140 us 79% | TMPstatic    120/   144/    179 us 79%
[score] rep 0 matrix 50 warm  | OFF   1016/  1074/   1143 us 100% | ON    478/   553/    571 us 100% | TMPdyn    892/   973/   1063 us 78% | TMPstatic   1237/  1586/   5959 us 78%
[score] rep 0 matrix 5 new    | OFF   2055/  3163/  21957 us 100% | ON    332/   568/  20070 us 100% | TMPdyn  10861/ 32374/  38964 us 78% | TMPstatic    176/   215/    221 us 32%
[score] rep 0 matrix 50 new   | OFF  21298/ 25055/  42356 us 100% | ON   1695/  2440/  21233 us 100% | TMPdyn   2450/169149/ 208118 us 79% | TMPstatic   1678/  2157/   7451 us 31%
[score] rep 1 C1 global UI    | OFF    308/  2511/  12408 us 100% | ON    135/   395/   1491 us 100% | TMPdyn    284/ 13900/ 322737 us 100% | TMPstatic    331/   482/   1571 us 100%
[score] rep 1 C2 chat stream  | OFF    393/  1042/   1302 us 100% | ON    107/   242/    388 us 100% | TMPdyn    683/  9809/  10472 us 82% | TMPstatic     58/    82/    100 us 61%
[score] rep 1 C3 world labels | OFF    467/   531/    615 us 100% | ON    180/   206/    236 us 100% | TMPdyn    382/   406/    543 us 100% | TMPstatic    384/   399/    444 us 100%
[score] rep 1 matrix 5 warm   | OFF    118/   136/    184 us 100% | ON     68/    82/    109 us 100% | TMPdyn     96/   113/    118 us 79% | TMPstatic    121/   145/    184 us 79%
[score] rep 1 matrix 50 warm  | OFF   1018/  1102/   1152 us 100% | ON    479/   576/    676 us 100% | TMPdyn    892/   958/   1012 us 78% | TMPstatic   1152/  1508/   6288 us 78%
[score] rep 1 matrix 5 new    | OFF   2036/  3120/  21702 us 100% | ON    372/   676/  33362 us 100% | TMPdyn  10429/ 30758/  36798 us 78% | TMPstatic    175/   218/    246 us 32%
[score] rep 1 matrix 50 new   | OFF  21213/ 24998/  38194 us 100% | ON   1761/  3236/  35226 us 100% | TMPdyn   2441/170742/ 201726 us 79% | TMPstatic   1685/  2125/   7301 us 31%
[score] rep 2 C1 global UI    | OFF    311/  2488/  11842 us 100% | ON    135/   385/   1644 us 100% | TMPdyn    282/ 13360/ 336244 us 100% | TMPstatic    332/   482/   1576 us 100%
[score] rep 2 C2 chat stream  | OFF    392/  1041/   1355 us 100% | ON    106/   229/    358 us 100% | TMPdyn    683/  9834/  10480 us 82% | TMPstatic     58/    78/    100 us 61%
[score] rep 2 C3 world labels | OFF    467/   486/    663 us 100% | ON    180/   213/    371 us 100% | TMPdyn    380/   400/    432 us 100% | TMPstatic    385/   407/    447 us 100%
[score] rep 2 matrix 5 warm   | OFF    119/   141/    145 us 100% | ON     67/    80/     92 us 100% | TMPdyn     96/   113/    119 us 79% | TMPstatic    121/   145/    161 us 79%
[score] rep 2 matrix 50 warm  | OFF   1023/  1100/   1163 us 100% | ON    479/   557/    582 us 100% | TMPdyn    886/   959/   1047 us 78% | TMPstatic   1151/  1544/   6372 us 78%
[score] rep 2 matrix 5 new    | OFF   2032/  3109/  20945 us 100% | ON    327/   518/  19068 us 100% | TMPdyn  10418/ 31026/  36545 us 78% | TMPstatic    177/   222/    239 us 32%
[score] rep 2 matrix 50 new   | OFF  21316/ 25830/  37692 us 100% | ON   1689/  2521/  20590 us 100% | TMPdyn   2429/170708/ 202070 us 79% | TMPstatic   1684/  2175/   7502 us 31%
```
