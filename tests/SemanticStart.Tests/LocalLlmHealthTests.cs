using SemanticStart.Core.Synthesis;
using Xunit;

namespace SemanticStart.Tests;

/// <summary>
/// Guards the check that decides whether a local model is fit to build an index with. The failure
/// this exists for was silent: a model reported itself ready, answered every request, and wrote
/// nonsense into all 553 profiles before anyone noticed.
/// </summary>
public class LocalLlmHealthTests
{
    [Theory]
    [InlineData("OK")]
    [InlineData("ok")]
    [InlineData("OK.")]
    [InlineData("\"OK\"")]
    [InlineData(" OK \n")]
    [InlineData("Okay")]
    public void CompliantAnswers_AreAccepted(string completion)
    {
        Assert.True(LocalLlmProfileSynthesizer.IsCoherentProbeResponse(completion));
    }

    [Fact]
    public void DegenerateOutput_IsRejected()
    {
        // Verbatim from a Foundry Local CUDA build of qwen2.5-7b that passed the old non-empty check.
        const string salad = "เข้ามาyệnyệnyện a a a laptop laptop's's power power settings setting setting "
                             + "can control control control display brightness brightness brightness";

        Assert.False(LocalLlmProfileSynthesizer.IsCoherentProbeResponse(salad));
    }

    [Fact]
    public void PlausibleButNonCompliantProse_IsRejected()
    {
        // A model that ignores an instruction this explicit cannot be trusted to return the strict
        // JSON that synthesis depends on.
        Assert.False(LocalLlmProfileSynthesizer.IsCoherentProbeResponse(
            "I am a large language model and I am happy to help you with your request today."));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyAnswers_AreRejected(string? completion)
    {
        Assert.False(LocalLlmProfileSynthesizer.IsCoherentProbeResponse(completion));
    }
}
