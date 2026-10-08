namespace DeezSpoTag.Services.Download.Soulseek;

/// <summary>
///     Thrown when an slskd configuration document cannot be rewritten with confidence.
/// </summary>
/// <remarks>
///     <para>
///         Rewriting the configuration is how shared directories are changed, because slskd exposes no
///         endpoint for setting them. The document also holds the Soulseek credentials, the transfer limits
///         and the relay configuration, so a rewrite that parses but has quietly dropped or reordered a
///         setting is a real harm.
///     </para>
///     <para>
///         This is raised rather than recovered from. A caller that catches it has changed nothing, and can
///         tell the user exactly why. The alternative - writing a best guess - would turn a visible refusal
///         into an invisible corruption.
///     </para>
/// </remarks>
public sealed class SoulseekShareConfigurationException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="SoulseekShareConfigurationException"/> class.</summary>
    public SoulseekShareConfigurationException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SoulseekShareConfigurationException"/> class.</summary>
    public SoulseekShareConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
