using AiDevControlPanel.Core.Domain;

namespace AiDevControlPanel.Core.Tests;

public class PriorityRulesTests
{
    [Theory]
    [InlineData(IssueState.Blocked, false, 1)]
    [InlineData(IssueState.Blocked, true, 2)]
    [InlineData(IssueState.Waiting, false, 3)]
    [InlineData(IssueState.Waiting, true, 4)]
    [InlineData(IssueState.Planning, false, 5)]
    [InlineData(IssueState.Review, true, 6)]
    [InlineData(IssueState.Rejected, false, 5)]
    public void Compute_DerivesPriorityFromStateAndFlag(IssueState state, bool prioritized, int expected)
    {
        var issue = new Issue { State = state, IsPrioritized = prioritized };

        Assert.Equal(expected, PriorityRules.Compute(issue));
    }

    [Theory]
    [InlineData(false, 7)]
    [InlineData(true, 8)]
    public void Compute_InterruptedByUsageLimit(bool prioritized, int expected)
    {
        var issue = new Issue { State = IssueState.Executing, IsPrioritized = prioritized, IsInterrupted = true };

        Assert.Equal(expected, PriorityRules.Compute(issue));
    }

    [Fact]
    public void Compute_OnHoldIsZero()
    {
        Assert.Equal(0, PriorityRules.Compute(new Issue { State = IssueState.Waiting, OnHold = true, IsPrioritized = true }));
    }

    [Fact]
    public void Compute_DoneKeepsLastPriority()
    {
        Assert.Equal(6, PriorityRules.Compute(new Issue { State = IssueState.Done, Priority = 6 }));
    }

    [Theory]
    [InlineData(0, true, false)]
    [InlineData(1, false, false)]
    [InlineData(2, false, true)]
    [InlineData(3, false, false)]
    [InlineData(4, false, true)]
    public void FromRefinerPriority_MapsToFlags(int priority, bool onHold, bool prioritized)
    {
        Assert.Equal((onHold, prioritized), PriorityRules.FromRefinerPriority(priority));
    }
}

public class StateAggregationTests
{
    private static readonly Project ReadyProject = new() { OnboardingState = OnboardingState.Ready };

    [Theory]
    [InlineData(IssueState.Waiting, StateColor.White)]
    [InlineData(IssueState.Blocked, StateColor.Gray)]
    [InlineData(IssueState.Planning, StateColor.LightYellow)]
    [InlineData(IssueState.AwaitingAnswer, StateColor.LightBlue)]
    [InlineData(IssueState.Executing, StateColor.Yellow)]
    [InlineData(IssueState.Review, StateColor.DarkBlue)]
    [InlineData(IssueState.Merging, StateColor.Orange)]
    [InlineData(IssueState.AdjustRequested, StateColor.Purple)]
    [InlineData(IssueState.Rejected, StateColor.Red)]
    [InlineData(IssueState.Done, StateColor.Green)]
    public void IssueColor(IssueState state, StateColor expected)
    {
        Assert.Equal(expected, StateAggregation.IssueColor(new Issue { State = state }));
    }

    [Fact]
    public void TsaColor_FollowsRefinerThenIssues()
    {
        Assert.Equal(StateColor.White, StateAggregation.TsaColor(new Tsa { State = TsaState.Waiting }, []));
        Assert.Equal(StateColor.Yellow, StateAggregation.TsaColor(new Tsa { State = TsaState.Refining }, []));
        Assert.Equal(StateColor.LightBlue, StateAggregation.TsaColor(new Tsa { State = TsaState.AwaitingAnswer }, []));

        var refined = new Tsa { State = TsaState.Refined };
        Assert.Equal(StateColor.Brown, StateAggregation.TsaColor(refined, [new Issue { State = IssueState.Waiting }]));
        Assert.Equal(StateColor.Orange, StateAggregation.TsaColor(refined, [new Issue { State = IssueState.Waiting }, new Issue { State = IssueState.Executing }]));
        Assert.Equal(StateColor.Green, StateAggregation.TsaColor(refined, [new Issue { State = IssueState.Done }, new Issue { State = IssueState.Waiting, OnHold = true }]));
    }

    [Fact]
    public void ProjectColor_HumanInterventionWins()
    {
        var tsas = new[] { new Tsa { State = TsaState.Refined } };
        var issues = new[] { new Issue { State = IssueState.Review }, new Issue { State = IssueState.Executing } };

        Assert.Equal(StateColor.LightBlue, StateAggregation.ProjectColor(ReadyProject, tsas, issues, jobRunning: true));
    }

    [Fact]
    public void ProjectColor_YellowWhileWorking_GreenWhenDone_WhiteOtherwise()
    {
        var tsas = new[] { new Tsa { State = TsaState.Refined } };

        Assert.Equal(StateColor.Yellow, StateAggregation.ProjectColor(ReadyProject, tsas, [new Issue { State = IssueState.Waiting }], jobRunning: true));
        Assert.Equal(StateColor.White, StateAggregation.ProjectColor(ReadyProject, tsas, [new Issue { State = IssueState.Waiting }], jobRunning: false));
        Assert.Equal(StateColor.Green, StateAggregation.ProjectColor(ReadyProject, tsas,
            [new Issue { State = IssueState.Done }, new Issue { State = IssueState.Waiting, OnHold = true }], jobRunning: false));
        Assert.Equal(StateColor.White, StateAggregation.ProjectColor(ReadyProject, [new Tsa { State = TsaState.Waiting }], [], jobRunning: false));
    }

    [Fact]
    public void ProjectColor_Onboarding()
    {
        Assert.Equal(StateColor.Yellow, StateAggregation.ProjectColor(new Project { OnboardingState = OnboardingState.Initializing }, [], [], false));
        Assert.Equal(StateColor.LightBlue, StateAggregation.ProjectColor(new Project { OnboardingState = OnboardingState.Failed }, [], [], false));
    }
}
