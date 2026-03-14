# SonarQube Scan

Use the project-standard scan entry point from the repository root:

```bash
./scan.sh
```

Required environment:

```bash
export SONAR_TOKEN="your-token"
```

Optional environment:

```bash
export SONAR_HOST_URL="http://localhost:9000"
export SONAR_PROJECT_KEY="DeezSpoTag"
export BUILD_CONFIG="Debug"
export SONAR_SCAN_ALL="false"
export SONAR_LIGHTWEIGHT="false"
export SONAR_INCLUDE_COVERAGE="false"
export SONAR_COVERAGE_EXCLUSIONS="**/*"
```

Examples:

```bash
SONAR_TOKEN="your-token" ./scan.sh
SONAR_TOKEN="your-token" SONAR_HOST_URL="http://localhost:9000" ./scan.sh
SONAR_TOKEN="your-token" ./scan.sh --lightweight
SONAR_TOKEN="your-token" SONAR_INCLUDE_COVERAGE="true" ./scan.sh
./scan.sh --help
```

Do not use manual `dotnet sonarscanner begin ...`, `dotnet build`, `dotnet sonarscanner end ...` in this repository.

Reason:

- this machine has a global MSBuild Sonar import hook
- manual runs can leave `.sonarqube` state behind
- stale `.sonarqube` state causes later plain `dotnet build` runs to emit Sonar warnings

`./scan.sh` is the supported flow because it:

- validates SonarQube reachability first
- checks local memory/swap pressure before starting a heavy scan
- runs begin/build/test/end in one sequence
- applies the repo exclusions consistently
- cleans `.sonarqube` and `.sonar-coverage` before and after the scan

Use `./scan.sh --lightweight` on this machine when Sonar's JS/TS analyzer is unstable under memory pressure. That mode excludes web asset analyzers (`.js`, `.ts`, `.html`, `.css`, `.cshtml`) so the C# scan can still complete.
