using DeezSpoTag.Services.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;

namespace DeezSpoTag.Web.Services;

public sealed class LibrarySchemaHostedService : IHostedService
{
    private readonly LibraryDbService _libraryDbService;
    private readonly ILogger<LibrarySchemaHostedService> _logger;

    public LibrarySchemaHostedService(LibraryDbService libraryDbService, ILogger<LibrarySchemaHostedService> logger)
    {
        _libraryDbService = libraryDbService;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _libraryDbService.EnsureSchemaAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to initialize library schema.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
