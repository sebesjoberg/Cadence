using Cadence.Api.Internal;
using Cadence.Execution;
using Cadence.Storage;
using Xunit;

namespace Cadence.Api.Tests;

/// <summary>§13.2's status table, as tests.</summary>
public sealed class ProblemMapperTests
{
    [Fact]
    public void AnUnknownJobIsNotFound()
    {
        var problem = ProblemMapper.Describe(new JobNotFoundException("nightly"));

        Assert.NotNull(problem);
        Assert.Equal(404, problem.Status);
        Assert.Contains("nightly", problem.Detail);
    }

    [Fact]
    public void ADisallowedTriggerIsABadRequest()
    {
        var problem = ProblemMapper.Describe(
            new TriggerNotAllowedException("nightly", TriggerKind.Api, "'nightly' allows Schedule."));

        Assert.NotNull(problem);
        Assert.Equal(400, problem.Status);
        Assert.Contains("Schedule", problem.Detail);
    }

    [Fact]
    public void APausedSchedulerIsAConflictThatSaysWhoAndWhy()
    {
        var state = new PauseState
        {
            Scope = PauseScope.Triggers,
            Reason = "incident 4021",
            SetBy = "token:0a1b2c3d",
            SetAtUtc = new DateTimeOffset(2026, 8, 26, 9, 0, 0, TimeSpan.Zero),
        };

        var problem = ProblemMapper.Describe(new SchedulerPausedException("nightly", state));

        Assert.NotNull(problem);
        Assert.Equal(409, problem.Status);
        Assert.Contains("incident 4021", problem.Detail);
        Assert.Contains("token:0a1b2c3d", problem.Detail);
    }

    [Fact]
    public void ASkippedDispatchIsAConflictCarryingTheReason()
    {
        var problem = ProblemMapper.Skipped("nightly", DispatchResult.Skipped("already running here"));

        Assert.Equal(409, problem.Status);
        Assert.Contains("already running here", problem.Detail);
    }

    [Fact]
    public void EveryProblemNamesAType()
    {
        var problem = ProblemMapper.Describe(new JobNotFoundException("nightly"));

        Assert.NotNull(problem);
        Assert.False(string.IsNullOrWhiteSpace(problem.Type));
    }
}
