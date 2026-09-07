# Vibe Analysis Upgrade: Audiomack-primary semantics, Last.fm weights, Discogs519/MAEST

Scope: **Vibe Analysis only**. No Artist Library, AutoTag, enrichment/enhancement,
or file-tag writer changes.

Architecture:

- Audiomack = primary online semantic authority (Genre / Subgenre→Style / Mood).
- Last.fm = complementary community evidence, counts/weights preserved
  (track 0.75, artist 0.40 multipliers, RelativeWeight floor 0.20).
- Discogs519/MAEST = acoustic genre/style evidence (MAEST embedding →
  TensorflowPredict head; 519-label verification; top-8/0.15 preserved;
  `essentiaGenreEvidence` + `genreModel` provenance; hierarchy `A---B` split).
- DEAM (`deam-msd-musicnn-2.pb`) = real valence/arousal (1..9 → 0..1),
  heuristic kept only as labeled fallback.
- Fusion happens in the .NET Vibe layer; `vibe_analyzer.py` never does network I/O.
- Audiomack failure never fails Vibe; evidence resolution priority:
  Audiomack track → Last.fm track → acoustic → Last.fm artist fallback.
- Provenance persisted (`semanticEvidence`, resolved fields, model names).

Implementation order (each its own commit):
1. fixtures: audiomack vibe payload fixtures
2. feat: audiomack vibe metadata lookup + confidence-gated matching
3. feat: source-aware vibe semantic evidence model
4. refactor: lastfm tag weights preserved for vibe
5. feat: audiomack primary vibe semantic source
6. feat: source-aware vibe semantic resolver
7. feat: discogs519 acoustic genre (MAEST → 519 head)
8. fix: DEAM valence/arousal from the downloaded model
9. test: fusion + discogs519 regression coverage

Key files:
- `DeezSpoTag.Web/Tools/vibe_analyzer.py`
- `scripts/fetch-vibe-models.sh`
- `DeezSpoTag.Web/Services/Audiomack/*` (Vibe metadata service)
- `DeezSpoTag.Web/Services/LastFmTagService.cs`
- `DeezSpoTag.Web/Services/TrackAnalysisBackgroundService.cs` (existing orchestration path)

Confidence gate default: 0.85. Sources: `audiomack`, `lastfm`,
`essentia-discogs519`, `essentia-mood`.
