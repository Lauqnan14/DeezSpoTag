# CodeQL Remediation Tracker

Source: SECURITY_CODEQL_EXPORT.md
Total alerts: 430

## Severity Summary
- medium: 277
- high: 149
- critical: 4

## Rule Summary
- cs/log-forging: 276
- cs/web/missing-token-validation: 135
- cs/user-controlled-bypass: 7
- cs/command-line-injection: 4
- py/incomplete-url-substring-sanitization: 3
- js/incomplete-url-substring-sanitization: 2
- py/weak-sensitive-data-hashing: 1
- js/xss-through-exception: 1
- cpp/cleartext-transmission: 1

## Critical Backlog (Do First)
- [x] Alert #12: cs/command-line-injection | cs/command-line-injection | DeezSpoTag.Web/Services/SpotifyBlobService.cs:1286 | https://github.com/Lauqnan14/DeezSpoTag/security/code-scanning/12
- [x] Alert #11: cs/command-line-injection | cs/command-line-injection | DeezSpoTag.Services/Download/Conversion/FfmpegConversionService.cs:72 | https://github.com/Lauqnan14/DeezSpoTag/security/code-scanning/11
- [x] Alert #10: cs/command-line-injection | cs/command-line-injection | DeezSpoTag.Services/Download/Apple/AppleExternalToolRunner.cs:103 | https://github.com/Lauqnan14/DeezSpoTag/security/code-scanning/10
- [x] Alert #9: cs/command-line-injection | cs/command-line-injection | DeezSpoTag.Services/Download/Apple/AppleExternalToolRunner.cs:49 | https://github.com/Lauqnan14/DeezSpoTag/security/code-scanning/9

## Rule Workstreams
- [x] cs/command-line-injection (4 critical): eliminate string arguments, enforce executable-path validation, add process-launch tests.
- [ ] cs/web/missing-token-validation (135 high): enforce endpoint auth policy and add unauthorized-access tests. (Implementation applied; pending CodeQL + tests verification)
- [ ] cs/user-controlled-bypass (7 high): move sensitive guards to trusted server state and add bypass tests.
- [ ] cs/log-forging (276 medium): sanitize CR/LF/control chars at log boundaries and use structured logging.
- [ ] js/py URL sanitization (5): parse URLs and enforce scheme+host allowlists.
- [ ] py/weak-sensitive-data-hashing (1): migrate to Argon2/bcrypt/PBKDF2 with salt+work factor.
- [ ] js/xss-through-exception (1): encode exception content before rendering.
- [ ] cpp/cleartext-transmission (1): enforce TLS and certificate validation.

## Verification Gate (Per Batch)
- [x] Build touched projects
- [ ] Add/update targeted tests
- [ ] Run CodeQL scan for touched languages
- [ ] Mark each alert closed with commit/PR reference
