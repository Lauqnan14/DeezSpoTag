# Third-Party Notices

This document summarizes third-party project references observed in:

- `References/`

It is intended as a maintainable attribution baseline for releases.

## Acknowledgements

Thank you to all original creators, maintainers, contributors, and users of these projects.

## Referenced projects

1. Apple Music Downloader (runv2) - MIT  
   Repo: https://github.com/zhaarey/apple-music-downloader
2. Apple Music API (`apple-music-api-master`) - MIT  
   Repo: https://github.com/Myp3a/apple-music-api
3. Deemixrr - AGPL-3.0  
   Repo: https://github.com/TheUltimateC0der/Deemixrr
4. MusicMover - GPL-3.0  
   Repo: https://github.com/MusicMoveArr/MusicMover
5. Quality Scanner (`whatsmybitrate-main`) - MIT (declared in README)  
   Repo: https://github.com/oren-cohen/whatsmybitrate
6. ShazamIO - MIT  
   Repo: https://github.com/dotX12/ShazamIO
7. SoulSync - MIT-style license text  
   Repo: https://github.com/Nezreka/SoulSync
8. SpotiFLAC Desktop (afkarxyz) - MIT  
   Repo: https://github.com/afkarxyz/SpotiFLAC
9. SPotiFLAC Mobile (zarzet) - MIT  
   Repo: https://github.com/zarzet/SpotiFLAC-Mobile
10. Wolframe Spotify Canvas - MIT  
    Repo: https://github.com/squeeeezy/Wolframe-spotify-canvas
11. Boomplay Music Downloader - MIT  
    Repo: https://github.com/OkoyaUsman/boomplay
12. deemix - GPL-3.0  
    Repo: https://github.com/bambanah/deemix
13. lidify - GPL-3.0  
    Repo: https://github.com/Chevron7Locked/lidify
14. OneTagger - GPL-3.0 (project), Apache-2.0 file in android assets  
    Repo: https://github.com/Marekkon5/onetagger
15. MusicBrainz Picard - GPL-2.0-or-later  
    Repo: https://github.com/metabrainz/picard
16. puddletag - GPL-3.0  
    Repo: https://github.com/puddletag/puddletag
17. Qobuz Artist Discography - MIT  
    Repo: https://github.com/pawllo01/qobuz-artist-discography
18. ReFreezer - GPL-3.0  
    Repo: https://github.com/DJDoubleD/ReFreezer
19. spotizerr-phoenix - GPL-3.0  
    Repo: https://lavaforge.org/spotizerrphoenix/spotizerr-phoenix (non-GitHub upstream)
20. syrics-web - MIT (`package.json`)  
    Repo: https://github.com/akashrchandran/syrics-web-react
21. ATL .NET - MIT  
    Repo: https://github.com/Zeugma440/atldotnet
22. tone - Apache-2.0  
    Repo: https://github.com/sandreas/tone

## Manual verification needed

The following directories did not expose a clear top-level license file in the scan depth used:

1. `References/Quality Scaanner/whatsmybitrate-main`  
   Note: README states MIT, but no dedicated LICENSE file found in the scanned levels.
2. `References/Wrapper/2`
3. `References/Apple Music/Wrapper`
4. `References/Apple Music/WorldObservationLog`
5. `References/meloday`
6. `References/Untitled Folder`

## License posture recommendation

Given GPL/AGPL lineage and direct ports in this codebase, a conservative default for this project is:

- `AGPL-3.0-or-later`

If AGPL-derived code is confirmed absent and only GPL-compatible sources are included, `GPL-3.0-or-later` may be sufficient. Review with legal counsel before final release.
