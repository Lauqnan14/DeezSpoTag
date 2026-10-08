namespace DeezSpoTag.Services.Genre;

/// <summary>What cleanup did to one value that was found in a file.</summary>
public enum GenreCleanupAction
{
    /// <summary>The value is unchanged and stays exactly where it was.</summary>
    Keep,

    /// <summary>The value was recognised and rewritten to its canonical spelling.</summary>
    Canonicalize,

    /// <summary>The value was recognised as a different kind than the field it was in, and moved.</summary>
    Move,

    /// <summary>The value was removed. Only a block rule, a researched exclusion or an explicit user action may do this.</summary>
    Remove,

    /// <summary>The value is not in the researched vocabulary and was left in place.</summary>
    PreserveUnknown,

    /// <summary>The value was recognised as something that is deliberately not a Genre or a Style.</summary>
    Excluded
}

/// <summary>
/// One proposed change, in the form a run has to be able to show before anything is
/// written.
/// </summary>
/// <param name="OriginalValue">The value exactly as the file held it.</param>
/// <param name="NormalizedValue">
/// The spelling the shared Genre Normalization preferences produced, when they
/// changed it. Null when they did not.
/// </param>
/// <param name="CanonicalValue">The canonical term name, when the value was recognised.</param>
/// <param name="OriginalField">The file field the value was read from.</param>
/// <param name="FinalField">
/// The field the value will live in afterwards, or null when it will be removed.
/// </param>
/// <param name="Action">What cleanup did to the value.</param>
/// <param name="Reason">Why, in terms the user can act on.</param>
public sealed record GenreCleanupDecision(
    string OriginalValue,
    string? NormalizedValue,
    string? CanonicalValue,
    PersonalGenreTaxonKind OriginalField,
    PersonalGenreTaxonKind? FinalField,
    GenreCleanupAction Action,
    string Reason);

/// <summary>
/// The complete, inspectable result of analysing a file's genre tags.
/// </summary>
/// <remarks>
/// <para>
/// Producing this must not touch the audio file. It is the answer to "what would
/// change", and it is built from the same pure plan the writer consumes, so what is
/// shown is what would be written rather than a second opinion about it.
/// </para>
/// <para>
/// An empty value list for a dimension means that dimension is not being written.
/// It never means "clear this field", because cleanup never empties a field.
/// </para>
/// </remarks>
public sealed record GenreCleanupPreview(
    IReadOnlyList<string> BeforeGenres,
    IReadOnlyList<string> AfterGenres,
    IReadOnlyList<string> BeforeStyles,
    IReadOnlyList<string> AfterStyles,
    IReadOnlyList<GenreCleanupDecision> Decisions,
    IReadOnlyList<string> Moved,
    IReadOnlyList<string> Canonicalized,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> PreservedUnknown,
    string CatalogVersion,
    bool WouldWrite)
{
    /// <summary>Whether the run would change anything at all.</summary>
    public bool HasChanges => Moved.Count > 0
                              || Canonicalized.Count > 0
                              || Removed.Count > 0
                              || BeforeGenres.Where(value => !AfterGenres.Contains(value, StringComparer.OrdinalIgnoreCase)).Any()
                              || BeforeStyles.Where(value => !AfterStyles.Contains(value, StringComparer.OrdinalIgnoreCase)).Any();
}
