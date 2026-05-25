using DeezSpoTag.Integrations.Deezer;

namespace DeezSpoTag.Web.Services;

public sealed class DeezerLoginCoordinator
{
    private readonly DeezerClient _deezerClient;
    private readonly ILogger<DeezerLoginCoordinator> _logger;
    private readonly SemaphoreSlim _loginGate = new(1, 1);

    public DeezerLoginCoordinator(
        DeezerClient deezerClient,
        ILogger<DeezerLoginCoordinator> logger)
    {
        _deezerClient = deezerClient;
        _logger = logger;
    }

    public async Task<DeezerLoginCoordinatorResult> LoginViaArlAsync(
        string? arl,
        int child = 0,
        CancellationToken cancellationToken = default)
    {
        if (_deezerClient.LoggedIn && _deezerClient.CurrentUser != null)
        {
            return DeezerLoginCoordinatorResult.Ok(alreadyLive: true);
        }

        if (string.IsNullOrWhiteSpace(arl))
        {
            return DeezerLoginCoordinatorResult.Failed("missing_arl");
        }

        await _loginGate.WaitAsync(cancellationToken);
        try
        {
            if (_deezerClient.LoggedIn && _deezerClient.CurrentUser != null)
            {
                return DeezerLoginCoordinatorResult.Ok(alreadyLive: true);
            }

            var success = await _deezerClient.LoginViaArlAsync(arl, child);
            return success && _deezerClient.CurrentUser != null
                ? DeezerLoginCoordinatorResult.Ok(alreadyLive: false)
                : DeezerLoginCoordinatorResult.Failed(_deezerClient.LastLoginFailureReason ?? "login_failed");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(ex, "Deezer login coordination failed.");
            return DeezerLoginCoordinatorResult.Failed("exception");
        }
        finally
        {
            _loginGate.Release();
        }
    }
}

public sealed record DeezerLoginCoordinatorResult(
    bool Success,
    bool AlreadyLive,
    string? FailureReason)
{
    public static DeezerLoginCoordinatorResult Ok(bool alreadyLive)
        => new(true, alreadyLive, null);

    public static DeezerLoginCoordinatorResult Failed(string failureReason)
        => new(false, false, failureReason);
}
