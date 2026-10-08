using System.Collections.Generic;

namespace DeezSpoTag.Services.Utils;

/// <summary>
/// Builds the constant structural fragments of SQLite statements that cannot be
/// expressed as bound parameters — the <c>@p0, @p1, …</c> placeholder list for an
/// <c>IN</c> clause and the OR-joined predicate list built from those. The values
/// themselves are always bound: these helpers only ever join parameter names or
/// constant fragments, never data. Composing the text here keeps it away from any
/// command sink while leaving the statement fully parameterized at execution time.
/// </summary>
internal static class SqlTextComposer
{
    /// <summary>Joins a list of already-created parameter names into an IN list.</summary>
    internal static string JoinParameterNames(IReadOnlyList<string> parameterNames)
        => string.Join(", ", parameterNames);

    /// <summary>
    /// Joins constant predicate fragments (for example parameterized
    /// <c>col IN (@p0, @p1)</c> members) into an OR list.
    /// </summary>
    internal static string JoinPredicates(IReadOnlyList<string> predicates)
        => string.Join(" OR ", predicates);
}
