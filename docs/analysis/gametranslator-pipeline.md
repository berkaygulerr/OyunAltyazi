# GameTranslator (sefflex) Windows pipeline vs OyunAltyazi

Source read: `windows/src/GameTranslator.Core/{SubtitlePipeline,SubtitleStabilizer,OcrNoise,ButtonPrompts(Reading.cs),TextMatch,ReadScheduler,FrameGate,StableCover,CoverFit,PoolFilter}`, `App/Views/DesktopSubtitleOverlay.cs`, Swift `SubtitleSegmenter`. Code is AGPL-3.0, ported with permission (NOTICE.md).

## How GameTranslator works
- **Reading / gating.** `FrameGate`: one frame in the pipeline, others dropped (stuck after 15 s => rebuild). `ReadScheduler`: a 64x12 grey signature of the region; unchanged (threshold 16, max 20 reuses) => reuse last text, no OCR. Empty regions get a "deep" (upscaled) read every 3rd time.
- **Block selection** (`Reading.SubtitleBlock`). Lines are grouped into blocks by position; menu-like blocks (>=3 stacked short, unpunctuated lines) are dropped; the lowest block centred on the region (+-12 %) is the subtitle. Same-row words with a gap > 4 line-heights and > 12 % width are separate texts (menu vs counter).
- **Noise before translation.** `ButtonPrompts.IsPrompt` drops control hints ("R2 Swing", "Press X to jump", bars with `(O)` labels, `:Read message`). `OcrNoise.Clean` per line: symbol chars `$@#%^*_=|\~<>{}...` make a word junk; junk glued to a real word is cut ("waters2aljd" -> "waters", "Enter>" -> "Enter"); usual mixes (PS5, R2, 2nd, 100m, $500) kept; lone `l`/`|` -> `I` or dropped; stray `?!` tokens; HUD counter number glued to the line start; junk after the final dot. Anything < 3 chars counts as an empty frame.
- **Stabilize** (`SubtitleStabilizer`, clock based). Candidate must repeat 2 frames (or end with .!?... = fast path). Garbled reads (`OcrNoise.LooksGarbled`: > 1/6 of letters in broken words) need 700 ms of identical reads. `TextMatch.IsSameSubtitle` (word-level, tolerant of 1-2 junk words, whole-row loss) stops re-translation of noisy re-reads; `IsWorseReread` ignores reads that are not better (OcrNoise.Score); typewriter growth is a new line. Clear after 800 ms empty, 4 s if the region signature is unchanged; a line back within 1.5 s is *restored*, not re-translated.
- **Segment.** `SubtitleSegmenter`: a row starting with a speaker label ("ATREUS:", <= 3 capitalised words, a stop-list of "Note:"/"Objective:") starts a new utterance; unlabelled rows continue the previous one; names are not translated.
- **Overlay.** `DesktopSubtitleOverlay` (a PC window) has no per-line cover: a fixed bar at the bottom (or a user-dragged spot). Only the TV path uses cover: `CoverFit` grows the English box to include the translation; `StableCover` unions boxes of lines within 4 s that are near each other (grow, never shrink), `CenteredOnScreen` widens a centred box symmetrically. No anchor/median: the backdrop still follows each line's own OCR box.

## Behaviour table
| Topic | GameTranslator | OyunAltyazi before | Decision / now |
|---|---|---|---|
| Frame gating | FrameGate + 64x12 signature scheduler | ChangeGate fine grid + forced re-read (catches more) | keep ours |
| Block choice | SubtitleBlock (centred, no menus) | union of all lines passing letter ratio | **ported** `SubtitleBlock` (+ same-row name merge) |
| Button/key prompts | ButtonPrompts | one regex `[F] word` | **ported** + PC keys (Enter/Esc/Space/Shift...), `[F] ..`, `<X> ..` |
| Symbol / junk words | OcrNoise.Clean | only 0/1/\| confusions | **ported** (confusions first, then Clean), `>`/`»`/bullets dropped |
| Garbled reads | LooksGarbled + 700 ms | `Garbled()` heuristics, +2 reads | **ported** as extra signal in Stabilizer |
| Same-line / worse re-read | TextMatch | Levenshtein 0.85 + fuzzy words | **ported** `TextMatch` (IsSameSubtitle, IsWorseReread, IsGrowth) |
| Stabilizer core | clock-based, restore-after-gap | read-count based, 187 tests | kept ours + ported matching; restore not needed (slots) |
| Speakers | segmenter: new utterance per label | one speaker per read (2nd line glued in) | **ported**: per-utterance slots (stabilizer, joiner, translation) |
| Cover position | per-line OCR box (+union) | per-line OCR box => jumps | **replaced**: `CoverAnchor` (stable edge median, region centre) |
| Several utterances | n/a (one text) | one | sink gets a list, stacked in one bubble group |

## Position fix (not in GameTranslator)
OCR boxes of one subtitle block vary line to line. `CoverAnchor` keeps the median of the last 7 samples of the steadier edge (bottom for bottom-anchored games, top when top varies < half as much, e.g. CoD where 1 and 2 rows share the top), centres on the region centre (when the English is within 12 % of it), and only changes size. The anchor moves only when the whole block is > 15 % of region height away for 2 agreeing lines. The box still covers the English (extends past the anchor if it sticks out > 4 % of region height; the overlay pad hides the rest).

## Measured (replay, CoD MW2022, 160 s crop, 28 stable segments)
| metric (28 stable GT lines, 15 fps) | before (864c9f6) | after |
|---|---|---|
| recall | 96.4 % (27/28) | 96.4 % (27/28) |
| garbage shows (<50 % dictionary words or stray symbols) | 7 / 59 | 0 / 66 |
| display changes per GT line (other speakers on screen excluded) | 1.78 (max 4) | 1.44 (max 3) |
| anchor jumps > 4 px between consecutive shows | 28 / 58 | 13 / 65 |
| horizontal centre std | 44.8 px | 0.0 px |
| pinned-edge std, main scene (<100 s) | 14.1 px | 15.5 px (see below) |

The pinned-edge std did not improve: this clip's English box really changes (garbled reads with taller boxes, a scene change at 104 s),
and the cover must still hide it, so the pinned edge moves whenever the English sticks out > 4 % of the region height. CoD is top-anchored
(1 and 2 rows share the top at y=54); before, the bottom-pinned bubble hopped 30 px on every 1-row/2-row change, now the top stays.
Tools: `tools/replay/score.py` (set `TMP_WORDS`, `FRAME_H/W`; `DBG=1` lists lines with > 2 display changes).

## Gibberish gate and replacement policy (added after "nonsense, text keeps changing")
- `EnglishWords` (SCOWL-60, 93k words, embedded gz from GameTranslator) + `TextQuality` (port of `Misspelling`): a read must have >= 70 % of
  tokens as words / contractions / abbreviations / properly capitalised names, else it counts as "nothing readable" (never pending, never shown).
- Any misspelled word makes a read "garbled": it must repeat 4 reads instead of 2 before it is shown.
- A shown line is replaced by a read of the SAME line only if it scores >= 4 points better (or is >= 2 words fuller and not worse), once per line.
  Worse/equal re-reads and similar (>= 0.6) lower-scoring variants are ignored, so the Turkish text is not re-translated.
- Slot identity follows the line, not its mis-read speaker label ("-Hadir:", "Far h:"): matching text reuses the slot; junk before a label is stripped.

