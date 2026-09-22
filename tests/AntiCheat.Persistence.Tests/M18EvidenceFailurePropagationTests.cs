using NUnit.Framework;

namespace AntiCheat.Persistence.Tests;

[TestFixture]
public sealed class M18EvidenceFailurePropagationTests
{
    [Test]
    public void RequiredFinallyEvidenceFailureEscapesWhenNoExperimentErrorIsActive()
    {
        var error = Assert.Throws<IOException>(() =>
            M18EvidenceFailurePropagation.ThrowIfRequiredEvidenceFailed(null, ["qa-event-copy: denied"]));

        Assert.That(error!.Message, Does.Contain("qa-event-copy: denied"));
    }

    [Test]
    public void RequiredFinallyEvidenceFailureDoesNotReplaceThePrimaryExperimentError()
    {
        var primary = new InvalidOperationException("native receiver assertion");

        Assert.DoesNotThrow(() =>
            M18EvidenceFailurePropagation.ThrowIfRequiredEvidenceFailed(primary, ["run-summary-write: denied"]));
    }
}
