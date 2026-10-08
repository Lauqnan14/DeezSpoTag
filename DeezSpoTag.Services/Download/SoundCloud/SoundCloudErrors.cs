namespace DeezSpoTag.Services.Download.SoundCloud;

/// <summary>
///     Base type for every expected SoundCloud failure.
/// </summary>
/// <remarks>
///     <para>
///         A typed failure is what lets the engine report a recoverable provider problem and hand the item to
///         the fallback coordinator. Anything that is not one of these is a bug in this code and must not be
///         silently filed as "SoundCloud could not deliver".
///     </para>
///     <para>
///         Messages are built from already-redacted values only. Callers pass URLs through
///         <see cref="SoundCloudUrlRedactor"/> before they reach a message.
///     </para>
/// </remarks>
public abstract class SoundCloudException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="SoundCloudException" /> class.</summary>
    protected SoundCloudException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    /// <summary>
    ///     A short stable code the activity log and fallback history record in place of the message.
    /// </summary>
    public abstract string Reason { get; }
}

/// <summary>The supplied text was not a usable SoundCloud track or set URL.</summary>
public sealed class SoundCloudInvalidUrlException : SoundCloudException
{
    /// <summary>Initializes a new instance of the <see cref="SoundCloudInvalidUrlException" /> class.</summary>
    public SoundCloudInvalidUrlException(string message)
        : base(message)
    {
    }

    /// <inheritdoc />
    public override string Reason => "invalid_url";
}

/// <summary>An <c>on.soundcloud.com</c> short link did not resolve to a SoundCloud URL.</summary>
public sealed class SoundCloudShortLinkException : SoundCloudException
{
    /// <summary>Initializes a new instance of the <see cref="SoundCloudShortLinkException" /> class.</summary>
    public SoundCloudShortLinkException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    /// <inheritdoc />
    public override string Reason => "short_link_failed";
}

/// <summary>The page was fetched but carried no usable <c>window.__sc_hydration</c> entry.</summary>
public sealed class SoundCloudHydrationException : SoundCloudException
{
    /// <summary>Initializes a new instance of the <see cref="SoundCloudHydrationException" /> class.</summary>
    public SoundCloudHydrationException(string message, string hydratable, Exception? innerException = null)
        : base(message, innerException)
    {
        Hydratable = hydratable;
    }

    /// <summary>Gets the hydration entry type that was being looked for.</summary>
    public string Hydratable { get; }

    /// <inheritdoc />
    public override string Reason => "hydration_missing";
}

/// <summary>The track or set exists but SoundCloud will not serve it to this request.</summary>
public sealed class SoundCloudUnavailableException : SoundCloudException
{
    /// <summary>Initializes a new instance of the <see cref="SoundCloudUnavailableException" /> class.</summary>
    public SoundCloudUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    /// <inheritdoc />
    public override string Reason => "unavailable";
}

/// <summary>A public <c>client_id</c> could not be discovered from SoundCloud's asset bundles.</summary>
public sealed class SoundCloudClientIdException : SoundCloudException
{
    /// <summary>Initializes a new instance of the <see cref="SoundCloudClientIdException" /> class.</summary>
    public SoundCloudClientIdException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    /// <inheritdoc />
    public override string Reason => "client_id_unavailable";
}

/// <summary>The saved OAuth token was rejected, or was absent where the track needed one.</summary>
public sealed class SoundCloudAuthenticationException : SoundCloudException
{
    /// <summary>Initializes a new instance of the <see cref="SoundCloudAuthenticationException" /> class.</summary>
    public SoundCloudAuthenticationException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    /// <inheritdoc />
    public override string Reason => "authentication_failed";
}

/// <summary>No search result satisfied the shared candidate validator.</summary>
public sealed class SoundCloudNoCandidateException : SoundCloudException
{
    /// <summary>Initializes a new instance of the <see cref="SoundCloudNoCandidateException" /> class.</summary>
    public SoundCloudNoCandidateException(string message)
        : base(message)
    {
    }

    /// <inheritdoc />
    public override string Reason => "no_candidate";
}

/// <summary>The track advertises no plain-HLS MP3 stream this engine can transfer.</summary>
public sealed class SoundCloudNoStreamException : SoundCloudException
{
    /// <summary>Initializes a new instance of the <see cref="SoundCloudNoStreamException" /> class.</summary>
    public SoundCloudNoStreamException(string message, bool drmOnly = false)
        : base(message)
    {
        DrmOnly = drmOnly;
    }

    /// <summary>
    ///     Gets a value indicating whether the track only advertised DRM-protected transcodings.
    /// </summary>
    /// <remarks>
    ///     Reported separately because it is a materially different situation from "no audio at all": the
    ///     track is fine, the engine simply cannot decrypt it, and no fallback step elsewhere will do better.
    /// </remarks>
    public bool DrmOnly { get; }

    /// <inheritdoc />
    public override string Reason => DrmOnly ? "drm_only" : "no_stream";
}

/// <summary>An HTTP call to SoundCloud failed or returned an unusable status.</summary>
public sealed class SoundCloudTransportException : SoundCloudException
{
    /// <summary>Initializes a new instance of the <see cref="SoundCloudTransportException" /> class.</summary>
    public SoundCloudTransportException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    /// <inheritdoc />
    public override string Reason => "transport_failed";
}