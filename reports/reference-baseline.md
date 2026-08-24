# MDPlayer reference baseline

- mdPlayer commit: `4bf5d7c5`
- songs analyzed: 10
- top divergences: 75

## md-arctic-wind — arctic-wind

| reference attacks | candidate attacks | matched | missing | extra |
|---|---|---|---|---|
| 0 | 833 | 0 | 0 | 833 |

## md-first-attack — first-attack

| reference attacks | candidate attacks | matched | missing | extra |
|---|---|---|---|---|
| 0 | 2211 | 0 | 0 | 2211 |

## md-master-ninja — master-ninja

| reference attacks | candidate attacks | matched | missing | extra |
|---|---|---|---|---|
| 16386 | 5824 | 3300 | 13086 | 2524 |

## md-ninja-yashiki — ninja-yashiki

| reference attacks | candidate attacks | matched | missing | extra |
|---|---|---|---|---|
| 10378 | 6152 | 3038 | 7340 | 3114 |

## md-robotnik — robotnik

| reference attacks | candidate attacks | matched | missing | extra |
|---|---|---|---|---|
| 24236 | 994 | 953 | 23283 | 41 |

## md-smoking-head — smoking-head

| reference attacks | candidate attacks | matched | missing | extra |
|---|---|---|---|---|
| 16662 | 940 | 883 | 15779 | 57 |

## md-stranger — stranger

| reference attacks | candidate attacks | matched | missing | extra |
|---|---|---|---|---|
| 15832 | 6495 | 1697 | 14135 | 4798 |

## md-triumphal-arch — triumphal-arch

| reference attacks | candidate attacks | matched | missing | extra |
|---|---|---|---|---|
| 4000 | 4199 | 2367 | 1633 | 1832 |

## md-twilight-express — twilight-express

| reference attacks | candidate attacks | matched | missing | extra |
|---|---|---|---|---|
| 13311 | 1140 | 1013 | 12298 | 127 |

## md-usa-ken-i — usa-ken-i

| reference attacks | candidate attacks | matched | missing | extra |
|---|---|---|---|---|
| 2595 | 3591 | 2595 | 0 | 996 |

## Top divergences

| song | category | severity | metric | message |
|---|---|---|---|---|
| md-master-ninja | D1 | 99 | 16386.000 | missing 13086/16386 reference attacks |
| md-ninja-yashiki | D1 | 99 | 10378.000 | missing 7340/10378 reference attacks |
| md-robotnik | D1 | 99 | 24236.000 | missing 23283/24236 reference attacks |
| md-smoking-head | D1 | 99 | 16662.000 | missing 15779/16662 reference attacks |
| md-stranger | D1 | 99 | 15832.000 | missing 14135/15832 reference attacks |
| md-triumphal-arch | D1 | 99 | 4000.000 | missing 1633/4000 reference attacks |
| md-twilight-express | D1 | 99 | 13311.000 | missing 12298/13311 reference attacks |
| md-arctic-wind | D2 | 94 | 833.000 | extra 833/833 candidate attacks |
| md-first-attack | D2 | 94 | 2211.000 | extra 2211/2211 candidate attacks |
| md-master-ninja | D2 | 94 | 5824.000 | extra 2524/5824 candidate attacks |
| md-ninja-yashiki | D2 | 94 | 6152.000 | extra 3114/6152 candidate attacks |
| md-stranger | D2 | 94 | 6495.000 | extra 4798/6495 candidate attacks |
| md-triumphal-arch | D2 | 94 | 4199.000 | extra 1832/4199 candidate attacks |
| md-twilight-express | D2 | 94 | 1140.000 | extra 127/1140 candidate attacks |
| md-usa-ken-i | D2 | 94 | 3591.000 | extra 996/3591 candidate attacks |
| md-smoking-head | D2 | 91 | 940.000 | extra 57/940 candidate attacks |
| md-master-ninja | D3 | 80 | 33.049 | gross pitch error p99=33.05 semitones |
| md-ninja-yashiki | D3 | 80 | 51.982 | gross pitch error p99=51.98 semitones |
| md-robotnik | D3 | 80 | 30.525 | gross pitch error p99=30.53 semitones |
| md-smoking-head | D3 | 80 | 88.170 | gross pitch error p99=88.17 semitones |
| md-stranger | D3 | 80 | 33.797 | gross pitch error p99=33.80 semitones |
| md-triumphal-arch | D3 | 80 | 94.457 | gross pitch error p99=94.46 semitones |
| md-twilight-express | D3 | 80 | 88.173 | gross pitch error p99=88.17 semitones |
| md-master-ninja | D10 | 75 | 0.201 | recall=0.201 |
| md-ninja-yashiki | D10 | 75 | 0.293 | recall=0.293 |
| md-robotnik | D10 | 75 | 0.039 | recall=0.039 |
| md-smoking-head | D10 | 75 | 0.053 | recall=0.053 |
| md-stranger | D10 | 75 | 0.107 | recall=0.107 |
| md-triumphal-arch | D10 | 75 | 0.592 | recall=0.592 |
| md-twilight-express | D10 | 75 | 0.076 | recall=0.076 |
| md-master-ninja | D9 | 62 | 0.567 | precision=0.567 |
| md-ninja-yashiki | D9 | 62 | 0.494 | precision=0.494 |
| md-stranger | D9 | 62 | 0.261 | precision=0.261 |
| md-triumphal-arch | D9 | 62 | 0.564 | precision=0.564 |
| md-usa-ken-i | D9 | 62 | 0.723 | precision=0.723 |
| md-master-ninja | D4 | 60 | 4229899.627 | duration mismatch p95=4229899.6 ms |
| md-master-ninja | D11 | 60 | 0.297 | F1=0.297 |
| md-ninja-yashiki | D4 | 60 | 3745610.404 | duration mismatch p95=3745610.4 ms |
| md-ninja-yashiki | D11 | 60 | 0.368 | F1=0.368 |
| md-robotnik | D4 | 60 | 200486.610 | duration mismatch p95=200486.6 ms |
| md-robotnik | D11 | 60 | 0.075 | F1=0.076 |
| md-smoking-head | D4 | 60 | 5167013.972 | duration mismatch p95=5167014.0 ms |
| md-smoking-head | D11 | 60 | 0.100 | F1=0.100 |
| md-stranger | D4 | 60 | 384456.096 | duration mismatch p95=384456.1 ms |
| md-stranger | D11 | 60 | 0.152 | F1=0.152 |
| md-triumphal-arch | D4 | 60 | 1342382.333 | duration mismatch p95=1342382.3 ms |
| md-triumphal-arch | D11 | 60 | 0.577 | F1=0.577 |
| md-twilight-express | D4 | 60 | 5401041.667 | duration mismatch p95=5401041.7 ms |
| md-twilight-express | D11 | 60 | 0.140 | F1=0.140 |
| md-usa-ken-i | D4 | 60 | 997.154 | duration mismatch p95=997.2 ms |
| md-arctic-wind | D5 | 55 | 26.000 | retrigger mismatch ref=0 cand=26 |
| md-master-ninja | D5 | 55 | 10854.000 | retrigger mismatch ref=10854 cand=660 |
| md-ninja-yashiki | D5 | 55 | 6005.000 | retrigger mismatch ref=6005 cand=218 |
| md-robotnik | D5 | 55 | 20048.000 | retrigger mismatch ref=20048 cand=7 |
| md-smoking-head | D5 | 55 | 6606.000 | retrigger mismatch ref=6606 cand=5 |
| md-stranger | D5 | 55 | 7246.000 | retrigger mismatch ref=7246 cand=544 |
| md-triumphal-arch | D5 | 55 | 1794.000 | retrigger mismatch ref=1794 cand=8 |
| md-twilight-express | D5 | 55 | 10799.000 | retrigger mismatch ref=10799 cand=8 |
| md-usa-ken-i | D5 | 55 | 500.000 | retrigger mismatch ref=0 cand=500 |
| md-first-attack | D0 | 50 | 0.892 | tempo mismatch ratio=0.892 |
| md-master-ninja | D0 | 50 | 0.675 | tempo mismatch ratio=0.675 |
| md-robotnik | D0 | 50 | 1.875 | tempo mismatch ratio=1.875 |
| md-smoking-head | D0 | 50 | 1.500 | tempo mismatch ratio=1.500 |
| md-stranger | D0 | 50 | 1.574 | tempo mismatch ratio=1.574 |
| md-twilight-express | D0 | 50 | 0.208 | tempo mismatch ratio=0.208 |
| md-usa-ken-i | D0 | 50 | 0.779 | tempo mismatch ratio=0.779 |
| md-arctic-wind | D6 | 40 | 6.000 | bend spam cand/ref=6.00x cand=6.0/note |
| md-master-ninja | D7 | 30 | 0.000 | instrument mismatch across non-percussion channels |
| md-ninja-yashiki | D7 | 30 | 0.000 | instrument mismatch across non-percussion channels |
| md-robotnik | D7 | 30 | 0.000 | instrument mismatch across non-percussion channels |
| md-smoking-head | D7 | 30 | 0.000 | instrument mismatch across non-percussion channels |
| md-stranger | D7 | 30 | 0.000 | instrument mismatch across non-percussion channels |
| md-triumphal-arch | D7 | 30 | 0.000 | instrument mismatch across non-percussion channels |
| md-twilight-express | D7 | 30 | 0.000 | instrument mismatch across non-percussion channels |
| md-usa-ken-i | D7 | 30 | 0.000 | instrument mismatch across non-percussion channels |

## Listening notes (placeholder)

Human listening pass pending — results below are machine-ruled only.

