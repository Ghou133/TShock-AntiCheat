using AntiCheat.Core;

namespace AntiCheat.Rules;

/// <summary>
/// Explicitly separates observation availability from candidate controls.
/// Auto is allowed to keep bounded observations in ordinary scopes, while
/// pre-forward/service controls remain a TestLab or explicit candidate choice.
/// </summary>
public static class M18ExecutionModePolicy
{
    public static bool RecordEnabled(ExecutionScope scope, M18CandidateMode mode)
        => mode switch
        {
            M18CandidateMode.Auto => scope is ExecutionScope.ObserveOnly or
                ExecutionScope.TestLab or ExecutionScope.Production,
            M18CandidateMode.TestLabCandidate => scope == ExecutionScope.TestLab,
            M18CandidateMode.ProductionCandidate => scope == ExecutionScope.Production,
            _ => false,
        };

    public static bool CandidateControlsEnabled(ExecutionScope scope, M18CandidateMode mode)
        => mode switch
        {
            M18CandidateMode.Auto => scope == ExecutionScope.TestLab,
            M18CandidateMode.TestLabCandidate => scope == ExecutionScope.TestLab,
            M18CandidateMode.ProductionCandidate => scope == ExecutionScope.Production,
            _ => false,
        };
}
