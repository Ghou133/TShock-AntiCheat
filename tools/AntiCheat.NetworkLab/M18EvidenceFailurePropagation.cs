using System.IO;

// Shared by the M18 P0-A scenario and its controlled failure regression. A
// finally-stage evidence failure must escape only when no experiment error is
// already in flight; the active experiment exception remains the primary
// result and is rethrown by the scenario's catch block.
internal static class M18EvidenceFailurePropagation
{
    public static void ThrowIfRequiredEvidenceFailed(Exception? primaryError,
        IReadOnlyList<string> evidenceFailures)
    {
        ArgumentNullException.ThrowIfNull(evidenceFailures);
        if (primaryError is null && evidenceFailures.Count > 0)
            throw new IOException("M18 required evidence output failed: " +
                string.Join(" | ", evidenceFailures));
    }
}
