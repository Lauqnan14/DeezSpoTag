using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Linq;
using Microsoft.AspNetCore.Hosting;
using DeezSpoTag.Integrations.Plex;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Security;
using DeezSpoTag.Services.Utils;
using DeezSpoTag.Web.Services.CoverPort;
using DeezSpoTag.Web.Services.AutoTag;

namespace DeezSpoTag.Web.Services;

public partial class AutoTagService
{

    private void NotifyCompleted(AutoTagJob job)
    {
        _activeJobStages.TryRemove(job.Id, out _);
        _activeJobIds.TryRemove(job.Id, out _);
        _jobCancellationSources.TryRemove(job.Id, out _);

        try
        {
            JobCompleted?.Invoke(job);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "AutoTag job {JobId}: completion handler failed.", job.Id);
            }
        }
    }

    private void NotifyDownloadToast(string message, string type)
    {
        try
        {
            _downloadEvents.Send("toastNotification", new
            {
                message,
                type,
                action = new
                {
                    label = "Settings",
                    href = "/Settings#download-path-settings"
                }
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to publish download toast notification.");
        }
    }
}
