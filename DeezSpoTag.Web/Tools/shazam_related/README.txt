# Private Shazam related-song runtime

The ASP.NET host owns one Node 24 process over stdin/stdout. There is no listening port.
SAP, signatures and bearer tokens stay only in that process. The MIT code in sap/
comes from Lakr233/AssppWeb / ApplePackage; retain LICENSE when packaging.

Local prerequisites: Node 24 and npm. `dotnet build` builds the bundle when source
changes. To verify explicitly, run `npm ci --ignore-scripts --no-audit --no-fund`,
`npm run build`, and `npm test` in this directory. Docker builds and tests its own
bundle using the existing pinned Node stage; no host token or helper service is needed.

Each JSON-lines request contains requestId, shazamId (1–20 decimal digits), language
(en-US), and country (US). Replies contain the same requestId, ok, ordered normalized
tracks and omittedResourceCount, or a fixed errorCode. No raw upstream error is
returned. Authentication is not a protocol operation. EOF stops the helper.

Token acquisition is single-flight. Without known upstream expiry metadata, tokens
are renewed after ten minutes, and once after a downstream 401. No actual upstream
TTL is claimed. All signing is inside the one acquisition path. Results cache for
30 minutes (authentic empty: 60 seconds), maximum 2,000 entries and 32MiB of normalized result data. Per-request total
budget is 30 seconds, per HTTP operation 15 seconds, response bound 3MiB, and four
upstream related requests can run concurrently. 429/5xx retry at most twice within
the budget; Retry-After is respected. Five transient failures open a 30-second
circuit. The .NET boundary limits pending calls to 32 and response lines to 4MiB.

Source and related order are preserved; missing resources are omitted individually.
An upstream error is not cached as an empty successful list. Native source URLs are
retained; Deezer identity and playback are resolved by the existing application.
Private Apple/Shazam endpoint availability can change independently of this code.

Live checks use the authenticated local app's existing `/api/shazam/related/830640787`
and `/api/shazam/related/879079293` routes. Count raw/usable/mapped/playable results
separately; 20 results was an observed baseline, not a permanent contract. Never print
tokens, signatures, raw auth responses or session state when diagnosing failures.
