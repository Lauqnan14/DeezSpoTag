using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeezSpoTag.Web.Services;

public sealed class ShazamRecognitionService
{
    public enum RecognitionMode
    {
        SearchAssisted = 0,
        AudioOnly = 1
    }

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RuntimeProbeSuccessCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RuntimeProbeFailureRetryTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RuntimeBootstrapCooldown = TimeSpan.FromMinutes(3);
    private static readonly int[] AudioOnlySignatureRetryWindowsSeconds = [10, 14, 18];
    private const bool UseSearchAssistedFallbackAfterAudioOnlyMiss = true;
    private const string ToolsDirectory = "Tools";
    private const string ShazamPortDirectory = "shazam_port";
    private static readonly string[] BashExecutableCandidates =
    {
        "/usr/bin/bash",
        "/bin/bash"
    };
    private const int ProbeProcessTimeoutMs = 10000;
    private const int BootstrapTimeoutMs = 300000;
    private static readonly Regex TrackIdRegex = new(@"/track/(?<id>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
    private static string ReplaceWithTimeout(string input, string pattern, string replacement, RegexOptions options = RegexOptions.None)
        => Regex.Replace(input, pattern, replacement, options, RegexTimeout);
    private readonly IWebHostEnvironment _environment;
    private readonly ShazamDiscoveryService _discoveryService;
    private readonly ILogger<ShazamRecognitionService> _logger;
    private readonly object _runtimeProbeGate = new();
    private RecognizerRuntimeProbe? _runtimeProbe;
    private DateTimeOffset _runtimeProbeCheckedAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastBootstrapAttemptUtc = DateTimeOffset.MinValue;
    private string? _lastProbeErrorLogged;
    private bool _hasLoggedProbeRecovery;

    public ShazamRecognitionService(
        IWebHostEnvironment environment,
        ShazamDiscoveryService discoveryService,
        ILogger<ShazamRecognitionService> logger)
    {
        _environment = environment;
        _discoveryService = discoveryService;
        _logger = logger;
    }

    public bool IsAvailable => GetRuntimeProbe().IsAvailable;

    public string? AvailabilityError => GetRuntimeProbe().Error;

    private RecognizerRuntimeProbe GetRuntimeProbe(bool forceRefresh = false)
    {
        lock (_runtimeProbeGate)
        {
            var now = DateTimeOffset.UtcNow;
            if (!forceRefresh && _runtimeProbe != null)
            {
                var ttl = _runtimeProbe.IsAvailable
                    ? RuntimeProbeSuccessCacheTtl
                    : RuntimeProbeFailureRetryTtl;
                if (now - _runtimeProbeCheckedAtUtc < ttl)
                {
                    return _runtimeProbe;
                }
            }

            var previous = _runtimeProbe;
            var refreshed = ProbeRecognizerRuntime(now);
            _runtimeProbe = refreshed;
            _runtimeProbeCheckedAtUtc = now;
            LogRuntimeProbeTransition(previous, refreshed);
            return refreshed;
        }
    }

    private void LogRuntimeProbeTransition(RecognizerRuntimeProbe? previous, RecognizerRuntimeProbe current)
    {
        if (!current.IsAvailable)
        {
            if (!string.Equals(_lastProbeErrorLogged, current.Error, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "Shazam recognizer unavailable: {Reason}",
                    string.IsNullOrWhiteSpace(current.Error) ? "unknown reason" : current.Error);
                _lastProbeErrorLogged = current.Error;
            }
            _hasLoggedProbeRecovery = false;
            return;
        }

        if (!_hasLoggedProbeRecovery && previous is { IsAvailable: false })
        {
            _logger.LogInformation("Shazam recognizer runtime recovered and is now available.");
        }

        _hasLoggedProbeRecovery = true;
        _lastProbeErrorLogged = null;
    }

    public ShazamRecognitionInfo? Recognize(string filePath, CancellationToken cancellationToken = default)
    {
        var attempt = RecognizeWithResilience(filePath, cancellationToken);
        return attempt.Matched ? attempt.Recognition : null;
    }

    private ShazamRecognitionAttempt RecognizeWithResilience(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var invalidInputAttempt = BuildInvalidInputAttempt(filePath);
        if (invalidInputAttempt != null)
        {
            return invalidInputAttempt;
        }

        ShazamRecognitionAttempt? firstRecognizerFailure = null;
        foreach (var signatureWindowSeconds in AudioOnlySignatureRetryWindowsSeconds)
        {
            var attempt = RecognizeWithDetails(
                filePath,
                signatureWindowSeconds: signatureWindowSeconds,
                mode: RecognitionMode.AudioOnly,
                cancellationToken: cancellationToken);
            if (attempt.Matched)
            {
                return attempt;
            }

            if (attempt.Outcome == ShazamRecognitionOutcome.RecognizerUnavailable)
            {
                return attempt;
            }

            if (attempt.Outcome == ShazamRecognitionOutcome.RecognizerError
                && firstRecognizerFailure == null)
            {
                firstRecognizerFailure = attempt;
            }
        }

        if (UseSearchAssistedFallbackAfterAudioOnlyMiss)
        {
            var assistedAttempt = RecognizeWithSearchAssistedFallback(filePath, cancellationToken);
            if (assistedAttempt != null)
            {
                return assistedAttempt;
            }
        }

        return firstRecognizerFailure ?? new ShazamRecognitionAttempt
        {
            Outcome = ShazamRecognitionOutcome.NoMatch
        };
    }

    private ShazamRecognitionAttempt? RecognizeWithSearchAssistedFallback(string filePath, CancellationToken cancellationToken)
    {
        var assistedAttempt = RecognizeWithDetails(
            filePath,
            mode: RecognitionMode.SearchAssisted,
            cancellationToken: cancellationToken);
        if (assistedAttempt.Matched)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Shazam search-assisted fallback matched after audio-only retries for {Path}.",
                    filePath);
            }
            return assistedAttempt;
        }

        return assistedAttempt.Outcome == ShazamRecognitionOutcome.NoMatch
            ? null
            : assistedAttempt;
    }

    public ShazamRecognitionAttempt RecognizeWithDetails(
        string filePath,
        int? signatureWindowSeconds = null,
        RecognitionMode mode = RecognitionMode.AudioOnly,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var invalidInputAttempt = BuildInvalidInputAttempt(filePath);
        if (invalidInputAttempt != null)
        {
            return invalidInputAttempt;
        }

        var portedStatus = EvaluatePortedRecognizer(filePath, signatureWindowSeconds, out var matchedFromPorted, cancellationToken);
        if (matchedFromPorted != null)
        {
            return matchedFromPorted;
        }

        if (mode == RecognitionMode.AudioOnly)
        {
            return BuildFailureAttempt(portedStatus);
        }

        var context = BuildLookupContext(filePath);
        var queries = BuildSearchQueries(context, filePath);
        if (queries.Count == 0)
        {
            return BuildFailureAttempt(portedStatus);
        }

        var candidates = CollectCandidates(queries, context, filePath, cancellationToken);
        if (candidates.Count == 0)
        {
            return BuildFailureAttempt(portedStatus);
        }

        var matchedFromCandidates = TryBuildMatchedAttempt(candidates, context, cancellationToken);
        return matchedFromCandidates ?? BuildFailureAttempt(portedStatus);
    }

    private static ShazamRecognitionAttempt? BuildInvalidInputAttempt(string filePath)
    {
        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
        {
            return null;
        }

        return new ShazamRecognitionAttempt
        {
            Outcome = ShazamRecognitionOutcome.InvalidInput,
            Error = "Audio file is missing or unreadable."
        };
    }

    private PortedFailureState EvaluatePortedRecognizer(
        string filePath,
        int? signatureWindowSeconds,
        out ShazamRecognitionAttempt? matchedAttempt,
        CancellationToken cancellationToken)
    {
        matchedAttempt = null;
        var portedExecution = RunPortedRecognizer(filePath, signatureWindowSeconds, cancellationToken);
        if (portedExecution.State == PortedRecognizerState.Recognized && portedExecution.Result != null)
        {
            var fromPorted = ResolveFromPorted(portedExecution.Result, cancellationToken);
            if (fromPorted?.HasCoreMetadata == true)
            {
                matchedAttempt = new ShazamRecognitionAttempt
                {
                    Outcome = ShazamRecognitionOutcome.Matched,
                    Recognition = fromPorted
                };
            }

            return PortedFailureState.None;
        }

        if (portedExecution.State == PortedRecognizerState.Unavailable)
        {
            return new PortedFailureState(true, false, portedExecution.Error);
        }

        if (portedExecution.State == PortedRecognizerState.Error)
        {
            return new PortedFailureState(false, true, portedExecution.Error);
        }

        return PortedFailureState.None;
    }

    private Dictionary<string, ScoredCard> CollectCandidates(
        IEnumerable<string> queries,
        LookupContext context,
        string filePath,
        CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<string, ScoredCard>(StringComparer.OrdinalIgnoreCase);
        foreach (var query in queries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var results = SearchCandidates(query, filePath, cancellationToken);
            foreach (var card in results)
            {
                AddOrReplaceCandidate(candidates, card, context);
            }
        }

        return candidates;
    }

    private IReadOnlyList<ShazamTrackCard> SearchCandidates(string query, string filePath, CancellationToken cancellationToken)
    {
        try
        {
            return _discoveryService.SearchTracksAsync(query, limit: 12, cancellationToken: cancellationToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Shazam search failed for query '{Query}' and file {Path}.", query, filePath);
            }
            return Array.Empty<ShazamTrackCard>();
        }
    }

    private static void AddOrReplaceCandidate(
        Dictionary<string, ScoredCard> candidates,
        ShazamTrackCard card,
        LookupContext context)
    {
        var scored = ScoreCandidate(card, context);
        var key = !string.IsNullOrWhiteSpace(card.Id)
            ? card.Id.Trim()
            : $"{card.Title}|{card.Artist}";
        if (!candidates.TryGetValue(key, out var current) || scored.Score > current.Score)
        {
            candidates[key] = scored;
        }
    }

    private ShazamRecognitionAttempt? TryBuildMatchedAttempt(
        Dictionary<string, ScoredCard> candidates,
        LookupContext context,
        CancellationToken cancellationToken)
    {
        var best = candidates.Values
            .OrderByDescending(value => value.IsIsrcExact)
            .ThenByDescending(value => value.Score)
            .First();
        if (!PassesThreshold(best, context))
        {
            return null;
        }

        var bestCard = EnrichCard(best.Card, cancellationToken);
        var info = BuildInfo(bestCard);
        if (!info.HasCoreMetadata)
        {
            return null;
        }

        return new ShazamRecognitionAttempt
        {
            Outcome = ShazamRecognitionOutcome.Matched,
            Recognition = info
        };
    }

    private static ShazamRecognitionAttempt BuildFailureAttempt(PortedFailureState state)
        => BuildFailureAttempt(state.RecognizerUnavailable, state.RecognizerFailed, state.RecognizerError);

    private static ShazamRecognitionAttempt BuildFailureAttempt(bool recognizerUnavailable, bool recognizerFailed, string? recognizerError)
    {
        if (recognizerUnavailable)
        {
            return new ShazamRecognitionAttempt
            {
                Outcome = ShazamRecognitionOutcome.RecognizerUnavailable,
                Error = FirstNonEmpty(recognizerError, "Shazam recognizer script is unavailable.")
            };
        }

        if (recognizerFailed)
        {
            return new ShazamRecognitionAttempt
            {
                Outcome = ShazamRecognitionOutcome.RecognizerError,
                Error = FirstNonEmpty(recognizerError, "Shazam recognizer execution failed.")
            };
        }

        return new ShazamRecognitionAttempt
        {
            Outcome = ShazamRecognitionOutcome.NoMatch
        };
    }

    private readonly record struct PortedFailureState(bool RecognizerUnavailable, bool RecognizerFailed, string? RecognizerError)
    {
        public static PortedFailureState None => new(false, false, null);
    }

    private string GetRecognizerScriptPath()
    {
        return Path.Combine(_environment.ContentRootPath, ToolsDirectory, ShazamPortDirectory, "recognize.py");
    }

    private string ResolvePythonExecutable()
    {
        var explicitPython = Environment.GetEnvironmentVariable("SHAZAM_PYTHON");
        if (!string.IsNullOrWhiteSpace(explicitPython) && File.Exists(explicitPython))
        {
            return explicitPython;
        }

        var shazamRuntimeRoot = Path.Combine(_environment.ContentRootPath, ToolsDirectory, ShazamPortDirectory, ".venv");
        var runtimeCandidate = EnumeratePythonCandidates(shazamRuntimeRoot)
            .FirstOrDefault(File.Exists);
        if (!string.IsNullOrWhiteSpace(runtimeCandidate))
        {
            return runtimeCandidate;
        }

        var vibePython = Environment.GetEnvironmentVariable("VIBE_ANALYZER_PYTHON");
        if (!string.IsNullOrWhiteSpace(vibePython) && File.Exists(vibePython))
        {
            return vibePython;
        }

        var localVenvRoot = Path.Combine(_environment.ContentRootPath, ToolsDirectory, "venv");
        var localCandidate = EnumeratePythonCandidates(localVenvRoot)
            .FirstOrDefault(File.Exists);
        if (!string.IsNullOrWhiteSpace(localCandidate))
        {
            return localCandidate;
        }

        return "python3";
    }

    private static IEnumerable<string> EnumeratePythonCandidates(string venvRoot)
    {
        yield return Path.Combine(venvRoot, "bin", "python3");
        yield return Path.Combine(venvRoot, "bin", "python");
        yield return Path.Combine(venvRoot, "Scripts", "python.exe");
    }

    private PortedRecognizerExecution RunPortedRecognizer(
        string filePath,
        int? signatureWindowSeconds,
        CancellationToken cancellationToken)
    {
        var scriptPath = GetRecognizerScriptPath();
        var runtimeProbe = GetRuntimeProbe();
        if (!runtimeProbe.IsAvailable)
        {
            return new PortedRecognizerExecution
            {
                State = PortedRecognizerState.Unavailable,
                Error = runtimeProbe.Error
            };
        }

        var pythonExecutable = ResolvePythonExecutable();
        var startInfo = CreateRecognizerProcessStartInfo(scriptPath, filePath, pythonExecutable, signatureWindowSeconds);
        using var process = new Process { StartInfo = startInfo };
        if (!TryStartRecognizerProcess(process, pythonExecutable, out var startError))
        {
            return new PortedRecognizerExecution
            {
                State = PortedRecognizerState.Error,
                Error = startError
            };
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        process.WaitForExitAsync(cancellationToken).GetAwaiter().GetResult();

        var stdout = stdoutTask.GetAwaiter().GetResult().Trim();
        var stderr = stderrTask.GetAwaiter().GetResult().Trim();
        if (process.ExitCode != 0)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Shazam ported recognizer exited with code {Code}. stderr={Stderr}", process.ExitCode, stderr);
            }
            return new PortedRecognizerExecution
            {
                State = PortedRecognizerState.Error,
                Error = BuildRecognizerExitError(process.ExitCode, stderr)
            };
        }

        if (string.IsNullOrWhiteSpace(stdout))
        {
            _logger.LogDebug("Shazam ported recognizer returned empty output.");
            return new PortedRecognizerExecution
            {
                State = PortedRecognizerState.Error,
                Error = "Shazam recognizer returned empty output."
            };
        }

        return ParsePortedRecognizerOutput(stdout);
    }

    private ProcessStartInfo CreateRecognizerProcessStartInfo(
        string scriptPath,
        string filePath,
        string pythonExecutable,
        int? signatureWindowSeconds)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = pythonExecutable,
            WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? _environment.ContentRootPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add(filePath);
        if (signatureWindowSeconds.HasValue)
        {
            startInfo.ArgumentList.Add("--signature-seconds");
            startInfo.ArgumentList.Add(Math.Clamp(signatureWindowSeconds.Value, 3, 20).ToString());
        }
        startInfo.Environment["PYTHONUNBUFFERED"] = "1";
        return startInfo;
    }

    private bool TryStartRecognizerProcess(Process process, string pythonExecutable, out string? error)
    {
        try
        {
            process.Start();
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to start Shazam ported recognizer using {Python}.", pythonExecutable);
            }
            error = $"Failed to start Shazam recognizer with '{pythonExecutable}'. {ex.Message}";
            return false;
        }
    }

    private RecognizerRuntimeProbe ProbeRecognizerRuntime(DateTimeOffset nowUtc)
    {
        var scriptPath = GetRecognizerScriptPath();
        if (!File.Exists(scriptPath))
        {
            return new RecognizerRuntimeProbe(false, $"Shazam recognizer script not found at '{scriptPath}'.");
        }

        if (TryProbeRecognizerRuntime(out var probe))
        {
            return probe;
        }

        if (!ShouldAttemptRuntimeBootstrap(nowUtc))
        {
            return probe;
        }

        if (!TryBootstrapShazamRuntime(out var bootstrapError))
        {
            return new RecognizerRuntimeProbe(
                false,
                FirstNonEmpty(
                    probe.Error,
                    bootstrapError,
                    "Modern Shazam runtime dependencies are unavailable."));
        }

        if (TryProbeRecognizerRuntime(out var postBootstrapProbe))
        {
            return postBootstrapProbe;
        }

        return new RecognizerRuntimeProbe(
            false,
            FirstNonEmpty(
                postBootstrapProbe.Error,
                probe.Error,
                "Modern Shazam runtime dependencies are unavailable."));
    }

    private bool TryProbeRecognizerRuntime(out RecognizerRuntimeProbe probe)
    {
        var pythonExecutable = ResolvePythonExecutable();
        if (!CanRunProcess(pythonExecutable, "--version", out var pythonError))
        {
            probe = new RecognizerRuntimeProbe(
                false,
                FirstNonEmpty(pythonError, $"Failed to start Shazam recognizer with '{pythonExecutable}'."));
            return false;
        }

        var runtimeImportCheckArgs = "-c \"import shazamio; import shazamio_core; from aiohttp_retry import ExponentialRetry; from shazamio import HTTPClient, SearchParams, Shazam\"";
        if (!CanRunProcess(pythonExecutable, runtimeImportCheckArgs, out var runtimeImportError, useShellArgumentParsing: true))
        {
            probe = new RecognizerRuntimeProbe(
                false,
                FirstNonEmpty(runtimeImportError, "Modern Shazam runtime dependencies are unavailable."));
            return false;
        }

        probe = new RecognizerRuntimeProbe(true, null);
        return true;
    }

    private bool ShouldAttemptRuntimeBootstrap(DateTimeOffset nowUtc)
    {
        if (nowUtc - _lastBootstrapAttemptUtc < RuntimeBootstrapCooldown)
        {
            return false;
        }

        var setupScriptPath = GetRuntimeSetupScriptPath();
        return File.Exists(setupScriptPath);
    }

    private bool TryBootstrapShazamRuntime(out string? error)
    {
        _lastBootstrapAttemptUtc = DateTimeOffset.UtcNow;
        var setupScriptPath = GetRuntimeSetupScriptPath();
        if (!File.Exists(setupScriptPath))
        {
            error = $"Shazam runtime bootstrap script not found at '{setupScriptPath}'.";
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            error = "Automatic Shazam runtime bootstrap is only supported on Unix-like hosts.";
            return false;
        }

        var bootstrapPython = ResolveBootstrapPythonExecutable();
        var bashExecutable = ResolveBashExecutable();
        if (string.IsNullOrWhiteSpace(bashExecutable))
        {
            error = "Unable to locate bash executable in expected system paths.";
            return false;
        }
        var startInfo = new ProcessStartInfo
        {
            FileName = bashExecutable,
            WorkingDirectory = Path.GetDirectoryName(setupScriptPath) ?? _environment.ContentRootPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(setupScriptPath);
        startInfo.Environment["PYTHONUNBUFFERED"] = "1";
        if (!string.IsNullOrWhiteSpace(bootstrapPython))
        {
            startInfo.Environment["SHAZAM_BOOTSTRAP_PYTHON"] = bootstrapPython;
        }

        try
        {
            using var process = new Process { StartInfo = startInfo };
            process.Start();
            if (!process.WaitForExit(BootstrapTimeoutMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // best effort
                }

                error = "Timed out while bootstrapping Shazam runtime dependencies.";
                return false;
            }

            var stdout = process.StandardOutput.ReadToEnd().Trim();
            var stderr = process.StandardError.ReadToEnd().Trim();
            if (process.ExitCode != 0)
            {
                error = FirstNonEmpty(
                    stderr,
                    stdout,
                    $"Shazam runtime bootstrap exited with code {process.ExitCode}.");
                return false;
            }

            _logger.LogInformation("Shazam runtime bootstrap completed successfully.");
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to execute Shazam runtime bootstrap script.");
            }

            error = $"Failed to execute Shazam runtime bootstrap script. {ex.Message}";
            return false;
        }
    }

    private string GetRuntimeSetupScriptPath()
    {
        return Path.Combine(_environment.ContentRootPath, ToolsDirectory, ShazamPortDirectory, "setup_runtime.sh");
    }

    private static string? ResolveBashExecutable()
    {
        return BashExecutableCandidates
            .FirstOrDefault(File.Exists);
    }

    private string ResolveBootstrapPythonExecutable()
    {
        var explicitBootstrap = Environment.GetEnvironmentVariable("SHAZAM_BOOTSTRAP_PYTHON");
        if (!string.IsNullOrWhiteSpace(explicitBootstrap))
        {
            return explicitBootstrap.Trim();
        }

        var runtimePython = ResolvePythonExecutable();
        if (!string.IsNullOrWhiteSpace(runtimePython))
        {
            return runtimePython;
        }

        return "python3";
    }

    private bool CanRunProcess(string fileName, string arguments, out string? error, bool useShellArgumentParsing = false)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            if (useShellArgumentParsing)
            {
                process.StartInfo.Arguments = arguments;
            }
            else
            {
                process.StartInfo.ArgumentList.Add(arguments);
            }

            process.Start();
            if (!process.WaitForExit(ProbeProcessTimeoutMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // best effort
                }

                error = $"Command '{fileName} {arguments}' timed out.";
                return false;
            }
            if (process.ExitCode == 0)
            {
                error = null;
                return true;
            }

            var stderr = process.StandardError.ReadToEnd().Trim();
            error = string.IsNullOrWhiteSpace(stderr)
                ? $"Command '{fileName} {arguments}' exited with code {process.ExitCode}."
                : stderr;
            return false;
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed probing Shazam recognizer dependency {FileName} {Arguments}.", fileName, arguments);
            }
            error = ex.Message;
            return false;
        }
    }

    private static string BuildRecognizerExitError(int exitCode, string stderr)
    {
        var detail = string.IsNullOrWhiteSpace(stderr) ? null : stderr;
        return detail == null
            ? $"Shazam recognizer exited with code {exitCode}."
            : $"Shazam recognizer exited with code {exitCode}. {detail}";
    }

    private PortedRecognizerExecution ParsePortedRecognizerOutput(string stdout)
    {
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            if (!root.TryGetProperty("ok", out var okElement) || okElement.ValueKind != JsonValueKind.True)
            {
                var error = root.TryGetProperty("error", out var errElement) && errElement.ValueKind == JsonValueKind.String
                    ? errElement.GetString()
                    : null;
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Shazam ported recognizer did not return ok=true. error={Error}", error ?? "unknown");
                }
                return new PortedRecognizerExecution
                {
                    State = PortedRecognizerState.Error,
                    Error = FirstNonEmpty(error, "Shazam recognizer returned a non-ok response.")
                };
            }

            var result = new PortedRecognitionResult();

            if (root.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.Object)
            {
                result.TrackId = ReadString(summary, "trackId");
                result.Title = ReadString(summary, "title");
                result.Artist = ReadString(summary, AutoTagLiterals.ArtistTag);
                result.Isrc = ReadString(summary, "isrc");
                result.Url = ReadString(summary, "url");
            }

            if ((string.IsNullOrWhiteSpace(result.TrackId) || string.IsNullOrWhiteSpace(result.Title))
                && root.TryGetProperty("response", out var response)
                && response.ValueKind == JsonValueKind.Object
                && response.TryGetProperty("track", out var track)
                && track.ValueKind == JsonValueKind.Object)
            {
                result.TrackId ??= FirstNonEmpty(
                    ReadString(track, "key"),
                    ReadString(track, "id"),
                    ReadString(track, "track_id"),
                    ReadString(track, "trackId"));
                result.Title ??= FirstNonEmpty(ReadString(track, "title"), ReadString(track, "name"));
                result.Artist ??= FirstNonEmpty(ReadString(track, "subtitle"), ReadString(track, AutoTagLiterals.ArtistTag));
                result.Isrc ??= ReadString(track, "isrc");
                result.Url ??= FirstNonEmpty(ReadString(track, "url"), ReadNestedString(track, "share", "href"));
            }

            if (string.IsNullOrWhiteSpace(result.TrackId)
                && string.IsNullOrWhiteSpace(result.Title)
                && string.IsNullOrWhiteSpace(result.Artist))
            {
                return new PortedRecognizerExecution
                {
                    State = PortedRecognizerState.NoMatch
                };
            }

            return new PortedRecognizerExecution
            {
                State = PortedRecognizerState.Recognized,
                Result = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse Shazam ported recognizer output.");
            return new PortedRecognizerExecution
            {
                State = PortedRecognizerState.Error,
                Error = $"Failed to parse Shazam recognizer output. {ex.Message}"
            };
        }
    }

    private static string? ReadString(JsonElement source, string propertyName)
    {
        if (!source.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static string? ReadNestedString(JsonElement source, string objectProperty, string propertyName)
    {
        if (!source.TryGetProperty(objectProperty, out var nested) || nested.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return ReadString(nested, propertyName);
    }

    private ShazamRecognitionInfo? ResolveFromPorted(PortedRecognitionResult ported, CancellationToken cancellationToken)
    {
        // Prefer recognizer output directly to keep recognition latency aligned with upstream flow.
        var fallbackInfo = BuildFallbackPortedInfo(ported);
        if (fallbackInfo != null)
        {
            return fallbackInfo;
        }

        return TryResolveDetailedPortedInfo(ported, cancellationToken);
    }

    private ShazamRecognitionInfo? TryResolveDetailedPortedInfo(PortedRecognitionResult ported, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ported.TrackId))
        {
            return null;
        }

        try
        {
            var card = _discoveryService.GetTrackAsync(ported.TrackId, cancellationToken).GetAwaiter().GetResult();
            if (card == null)
            {
                return null;
            }

            var info = BuildInfo(card);
            if (string.IsNullOrWhiteSpace(info.Isrc))
            {
                info.Isrc = ported.Isrc;
            }

            if (string.IsNullOrWhiteSpace(info.Url))
            {
                info.Url = ported.Url;
            }

            return info.HasCoreMetadata ? info : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Shazam track enrichment lookup failed for trackId {TrackId}.", ported.TrackId);
            }
            return null;
        }
    }

    private static ShazamRecognitionInfo? BuildFallbackPortedInfo(PortedRecognitionResult ported)
    {
        var title = FirstNonEmpty(ported.Title);
        var artist = FirstNonEmpty(ported.Artist);
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
        {
            return null;
        }

        var infoFallback = new ShazamRecognitionInfo
        {
            Title = title,
            Artist = artist,
            Artists = SplitArtists(artist),
            Isrc = FirstNonEmpty(ported.Isrc),
            TrackId = FirstNonEmpty(ported.TrackId),
            Url = FirstNonEmpty(ported.Url)
        };

        if (infoFallback.Artists.Count == 0)
        {
            infoFallback.Artists.Add(artist);
        }

        if (!string.IsNullOrWhiteSpace(infoFallback.TrackId))
        {
            infoFallback.Tags["SHAZAM_TRACK_ID"] = new List<string> { infoFallback.TrackId! };
        }

        if (!string.IsNullOrWhiteSpace(infoFallback.Url))
        {
            infoFallback.Tags["SHAZAM_URL"] = new List<string> { infoFallback.Url! };
        }

        if (!string.IsNullOrWhiteSpace(infoFallback.Isrc))
        {
            infoFallback.Tags["SHAZAM_ISRC"] = new List<string> { infoFallback.Isrc! };
        }

        return infoFallback;
    }

    private ShazamTrackCard EnrichCard(ShazamTrackCard card, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(card.Id) || card.Id.StartsWith("am:", StringComparison.OrdinalIgnoreCase))
        {
            return card;
        }

        try
        {
            var detailed = _discoveryService.GetTrackAsync(card.Id, cancellationToken).GetAwaiter().GetResult();
            return detailed ?? card;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Shazam detail lookup failed for track {TrackId}.", card.Id);
            }
            return card;
        }
    }

    private static ShazamRecognitionInfo BuildInfo(ShazamTrackCard card)
    {
        var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (card.Tags != null)
        {
            foreach (var (tagKey, values) in card.Tags)
            {
                if (string.IsNullOrWhiteSpace(tagKey))
                {
                    continue;
                }

                var normalizedValues = values?
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                    ?? new List<string>();
                if (normalizedValues.Count == 0)
                {
                    continue;
                }

                tags[tagKey.Trim()] = normalizedValues;
            }
        }

        var artists = SplitArtists(card.Artist);
        var tagArtist = GetTag(tags, "SHAZAM_ARTIST", "ARTIST");
        if (!string.IsNullOrWhiteSpace(tagArtist))
        {
            artists = artists
                .Concat(SplitArtists(tagArtist))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var url = FirstNonEmpty(
            card.Url,
            GetTag(tags, "SHAZAM_URL", "URL", "SHAZAM_TRACK_URL"));
        var trackId = FirstNonEmpty(
            string.IsNullOrWhiteSpace(card.Id) ? null : card.Id,
            ParseTrackId(url),
            GetTag(tags, "SHAZAM_TRACK_ID", "SHAZAM_TRACKID", "SHAZAM_TRACK_KEY"));
        var title = FirstNonEmpty(card.Title, GetTag(tags, "TITLE", "SHAZAM_TITLE"));
        var album = FirstNonEmpty(card.Album, GetTag(tags, "SHAZAM_ALBUM", "ALBUM"));
        var genre = FirstNonEmpty(card.Genre, GetTag(tags, "SHAZAM_GENRE", "GENRE"));
        var label = FirstNonEmpty(card.Label, GetTag(tags, "SHAZAM_LABEL", "LABEL"));
        var releaseDate = FirstNonEmpty(card.ReleaseDate, GetTag(tags, "SHAZAM_RELEASE_DATE", "DATE", "YEAR"));
        var isrc = FirstNonEmpty(card.Isrc, GetTag(tags, "ISRC", "SHAZAM_ISRC"));
        var artworkUrl = FirstNonEmpty(
            card.ArtworkUrl,
            GetTag(tags, "SHAZAM_ARTWORK", "SHAZAM_ART_URL", "ARTWORK", "COVERART"));
        var artworkHqUrl = FirstNonEmpty(
            GetTag(tags, "SHAZAM_ARTWORK_HQ", "SHAZAM_ART_HQ_URL", "COVERARTHQ"),
            artworkUrl);
        var key = FirstNonEmpty(card.Key, GetTag(tags, "SHAZAM_MUSICAL_KEY", "SHAZAM_META_KEY"), GetTag(tags, "SHAZAM_KEY"));
        var durationMs = card.DurationMs ?? ParseDurationMs(GetTag(tags, "SHAZAM_DURATION_MS", "SHAZAM_META_DURATION", "SHAZAM_META_TIME", "SHAZAM_META_LENGTH"));
        var artistIds = card.ArtistIds?.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            ?? GetTagValues(tags, "SHAZAM_ARTIST_IDS");
        var artistAdamIds = card.ArtistAdamIds?.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            ?? GetTagValues(tags, "SHAZAM_ARTIST_ADAM_IDS");
        var trackNumber = card.TrackNumber ?? ParseInt(GetTag(tags, "SHAZAM_TRACK_NUMBER", "SHAZAM_META_TRACK_NUMBER", "SHAZAM_META_TRACK"));
        var discNumber = card.DiscNumber ?? ParseInt(GetTag(tags, "SHAZAM_DISC_NUMBER", "SHAZAM_META_DISC_NUMBER", "SHAZAM_META_DISC"));
        var explicitValue = card.Explicit ?? ParseBool(GetTag(tags, "SHAZAM_EXPLICIT", "SHAZAM_META_EXPLICIT", "SHAZAM_META_CONTENT_RATING"));
        var language = FirstNonEmpty(card.Language, GetTag(tags, "SHAZAM_LANGUAGE", "LANGUAGE", "SHAZAM_META_LANGUAGE"));
        var composer = FirstNonEmpty(card.Composer, GetTag(tags, "SHAZAM_COMPOSER", "COMPOSER", "SHAZAM_META_COMPOSER", "SHAZAM_META_SONGWRITER", "SHAZAM_META_SONGWRITER_S", "SHAZAM_META_WRITTEN_BY"));
        var lyricist = FirstNonEmpty(card.Lyricist, GetTag(tags, "SHAZAM_LYRICIST", "LYRICIST", "SHAZAM_META_LYRICIST"));
        var publisher = FirstNonEmpty(card.Publisher, GetTag(tags, "SHAZAM_PUBLISHER", "PUBLISHER", "SHAZAM_META_PUBLISHER"));
        var albumAdamId = FirstNonEmpty(card.AlbumAdamId, GetTag(tags, "SHAZAM_ALBUM_ADAM_ID"));
        var appleMusicUrl = FirstNonEmpty(card.AppleMusicUrl, GetTag(tags, "SHAZAM_APPLE_MUSIC_URL"));
        var spotifyUrl = FirstNonEmpty(card.SpotifyUrl, GetTag(tags, "SHAZAM_SPOTIFY_URL"));
        var youtubeUrl = FirstNonEmpty(GetTag(tags, "SHAZAM_YOUTUBE_URL"));

        return new ShazamRecognitionInfo
        {
            Title = title,
            Artist = artists.FirstOrDefault() ?? card.Artist,
            Artists = artists,
            Isrc = isrc,
            DurationMs = durationMs,
            TrackId = trackId,
            Url = url,
            Genre = genre,
            Album = album,
            Label = label,
            ReleaseDate = releaseDate,
            ArtworkUrl = artworkUrl,
            ArtworkHqUrl = artworkHqUrl,
            Key = key,
            AlbumAdamId = albumAdamId,
            ArtistIds = artistIds,
            ArtistAdamIds = artistAdamIds,
            TrackNumber = trackNumber,
            DiscNumber = discNumber,
            Explicit = explicitValue,
            Language = language,
            Composer = composer,
            Lyricist = lyricist,
            Publisher = publisher,
            AppleMusicUrl = appleMusicUrl,
            SpotifyUrl = spotifyUrl,
            YoutubeUrl = youtubeUrl,
            Tags = tags
        };
    }

    private static LookupContext BuildLookupContext(string filePath)
    {
        var context = new LookupContext
        {
            FileStem = Path.GetFileNameWithoutExtension(filePath)?.Trim()
        };

        try
        {
            using var tagFile = TagLib.File.Create(filePath);
            context.Title = FirstNonEmpty(tagFile.Tag.Title);
            context.Artist = FirstNonEmpty(tagFile.Tag.Performers.FirstOrDefault());
            context.Album = FirstNonEmpty(tagFile.Tag.Album);
            context.Isrc = FirstNonEmpty(tagFile.Tag.ISRC);
            if (tagFile.Properties.Duration.TotalMilliseconds > 0)
            {
                context.DurationMs = (long)Math.Round(tagFile.Properties.Duration.TotalMilliseconds);
            }
        }
        catch
        {
            // Best-effort only.
        }

        if (string.IsNullOrWhiteSpace(context.Title))
        {
            context.Title = CleanupFileStem(context.FileStem);
        }

        return context;
    }

    private static List<string> BuildSearchQueries(LookupContext context, string filePath)
    {
        var queries = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static string NormalizeQuery(string raw)
        {
            return ReplaceWithTimeout(raw, @"\s+", " ").Trim();
        }

        void Add(string? query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return;
            }

            var normalized = NormalizeQuery(query);
            if (normalized.Length < 2)
            {
                return;
            }

            if (seen.Add(normalized))
            {
                queries.Add(normalized);
            }
        }

        Add(context.Isrc);
        Add($"{context.Artist} {context.Title}");
        Add($"{context.Artist} {context.Title} {context.Album}");
        Add(context.Title);
        Add(CleanupFileStem(Path.GetFileNameWithoutExtension(filePath)));
        Add(context.FileStem);

        return queries;
    }

    private static ScoredCard ScoreCandidate(ShazamTrackCard card, LookupContext context)
    {
        var score = 0.0;

        var isIsrcExact = !string.IsNullOrWhiteSpace(context.Isrc)
            && !string.IsNullOrWhiteSpace(card.Isrc)
            && string.Equals(context.Isrc.Trim(), card.Isrc.Trim(), StringComparison.OrdinalIgnoreCase);
        if (isIsrcExact)
        {
            score += 120;
        }

        if (!string.IsNullOrWhiteSpace(context.Title))
        {
            score += Similarity(context.Title, card.Title) * 60.0;
        }

        if (!string.IsNullOrWhiteSpace(context.Artist))
        {
            score += Similarity(context.Artist, card.Artist) * 40.0;
        }

        if (!string.IsNullOrWhiteSpace(context.Album) && !string.IsNullOrWhiteSpace(card.Album))
        {
            score += Similarity(context.Album, card.Album) * 15.0;
        }

        if (context.DurationMs.HasValue && card.DurationMs.HasValue)
        {
            var diffSeconds = Math.Abs(context.DurationMs.Value - card.DurationMs.Value) / 1000.0;
            score += diffSeconds switch
            {
                <= 2 => 20,
                <= 5 => 12,
                <= 8 => 6,
                <= 15 => 0,
                _ => -10
            };
        }

        return new ScoredCard(card, score, isIsrcExact);
    }

    private static bool PassesThreshold(ScoredCard candidate, LookupContext context)
    {
        if (candidate.IsIsrcExact)
        {
            return true;
        }

        var hasStrongInput = !string.IsNullOrWhiteSpace(context.Title) || !string.IsNullOrWhiteSpace(context.Artist);
        return hasStrongInput
            ? candidate.Score >= 45
            : candidate.Score >= 65;
    }

    private static string CleanupFileStem(string? fileStem)
    {
        if (string.IsNullOrWhiteSpace(fileStem))
        {
            return string.Empty;
        }

        var cleaned = fileStem.Trim();
        cleaned = ReplaceWithTimeout(cleaned, @"^\s*\d+\s*[-._)\]]\s*", string.Empty);
        cleaned = cleaned.Replace('_', ' ');
        cleaned = ReplaceWithTimeout(cleaned, @"\s+", " ").Trim();
        return cleaned;
    }

    private static List<string> SplitArtists(string? artists)
    {
        if (string.IsNullOrWhiteSpace(artists))
        {
            return new List<string>();
        }

        return artists
            .Split([",", "&", ";", " feat. ", " ft. ", " featuring "], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static double Similarity(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return 0;
        }

        var a = NormalizeForSimilarity(left);
        var b = NormalizeForSimilarity(right);
        if (a == b)
        {
            return 1;
        }

        var distance = ShazamSharedParsing.LevenshteinDistance(a, b);
        var maxLen = Math.Max(a.Length, b.Length);
        return maxLen == 0 ? 0 : 1.0 - ((double)distance / maxLen);
    }

    private static string NormalizeForSimilarity(string value)
    {
        return ReplaceWithTimeout(value.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();
    }

    private static string? GetTag(Dictionary<string, List<string>> tags, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!tags.TryGetValue(key, out var values) || values == null)
            {
                continue;
            }

            var value = values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static List<string> GetTagValues(Dictionary<string, List<string>> tags, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!tags.TryGetValue(key, out var values) || values == null || values.Count == 0)
            {
                continue;
            }

            var normalized = values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (normalized.Count > 0)
            {
                return normalized;
            }
        }

        return new List<string>();
    }

    private static int? ParseInt(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return int.TryParse(raw, out var parsed) && parsed > 0 ? parsed : null;
    }

    private static long? ParseDurationMs(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (trimmed.Contains(':'))
        {
            var parts = trimmed.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
            {
                return null;
            }

            long totalSeconds = 0;
            foreach (var part in parts)
            {
                if (!long.TryParse(part, out var value) || value < 0)
                {
                    return null;
                }

                totalSeconds = checked((totalSeconds * 60) + value);
            }

            return totalSeconds > 0 ? totalSeconds * 1000 : null;
        }

        if (!long.TryParse(trimmed, out var numeric) || numeric <= 0)
        {
            return null;
        }

        return numeric <= 1000 ? numeric * 1000 : numeric;
    }

    private static bool? ParseBool(string? raw) => ShazamSharedParsing.ParseExplicitFlag(raw);

    private static string? ParseTrackId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var match = TrackIdRegex.Match(url);
        if (!match.Success)
        {
            return null;
        }

        return match.Groups["id"].Value.Trim();
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .FirstOrDefault();
    }

    private sealed class LookupContext
    {
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? Album { get; set; }
        public string? Isrc { get; set; }
        public long? DurationMs { get; set; }
        public string? FileStem { get; set; }
    }

    private sealed class PortedRecognitionResult
    {
        public string? TrackId { get; set; }
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? Isrc { get; set; }
        public string? Url { get; set; }
    }

    private sealed class PortedRecognizerExecution
    {
        public PortedRecognizerState State { get; init; }
        public PortedRecognitionResult? Result { get; init; }
        public string? Error { get; init; }
    }

    private enum PortedRecognizerState
    {
        Recognized,
        NoMatch,
        Error,
        Unavailable
    }

    private sealed record ScoredCard(ShazamTrackCard Card, double Score, bool IsIsrcExact);
    private sealed record RecognizerRuntimeProbe(bool IsAvailable, string? Error);
}

public sealed class ShazamRecognitionAttempt
{
    public ShazamRecognitionOutcome Outcome { get; init; }
    public ShazamRecognitionInfo? Recognition { get; init; }
    public string? Error { get; init; }

    public bool Matched =>
        Outcome == ShazamRecognitionOutcome.Matched &&
        Recognition != null;
}

public enum ShazamRecognitionOutcome
{
    Matched,
    NoMatch,
    RecognizerError,
    RecognizerUnavailable,
    InvalidInput
}

public sealed class ShazamRecognitionInfo
{
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public List<string> Artists { get; set; } = new();
    public string? Isrc { get; set; }
    public long? DurationMs { get; set; }
    public string? TrackId { get; set; }
    public string? Url { get; set; }
    public string? Genre { get; set; }
    public string? Album { get; set; }
    public string? Label { get; set; }
    public string? ReleaseDate { get; set; }
    public string? ArtworkUrl { get; set; }
    public string? ArtworkHqUrl { get; set; }
    public string? Key { get; set; }
    public string? AlbumAdamId { get; set; }
    public List<string> ArtistIds { get; set; } = new();
    public List<string> ArtistAdamIds { get; set; } = new();
    public int? TrackNumber { get; set; }
    public int? DiscNumber { get; set; }
    public bool? Explicit { get; set; }
    public string? Language { get; set; }
    public string? Composer { get; set; }
    public string? Lyricist { get; set; }
    public string? Publisher { get; set; }
    public string? AppleMusicUrl { get; set; }
    public string? SpotifyUrl { get; set; }
    public string? YoutubeUrl { get; set; }
    public Dictionary<string, List<string>> Tags { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool HasCoreMetadata =>
        !string.IsNullOrWhiteSpace(Title) &&
        (!string.IsNullOrWhiteSpace(Artist) || Artists.Any(value => !string.IsNullOrWhiteSpace(value)));
}
