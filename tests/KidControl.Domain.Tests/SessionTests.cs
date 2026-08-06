using FluentAssertions;
using KidControl.Domain.Entities;
using KidControl.Domain.Enums;
using KidControl.Domain.ValueObjects;
using Xunit;

namespace KidControl.Domain.Tests;

public sealed class SessionTests
{
    private static readonly ScheduleRule Rule40x20 = ScheduleRule.Default; // 40 play / 20 rest

    // ─── Construction ───────────────────────────────────────────────────────

    [Fact]
    public void New_Should_StartPlaying_With_FullPlayDuration()
    {
        var session = new Session();

        session.Status.Should().Be(SessionStatus.Playing);
        session.TimeRemaining.Should().Be(Rule40x20.PlayDuration);
        session.Rule.Should().Be(ScheduleRule.Default);
    }

    // ─── Tick: normal countdown ─────────────────────────────────────────────

    [Fact]
    public void Tick_Should_DecrementRemaining_When_Playing()
    {
        var session = new Session();

        session.Tick(TimeSpan.FromMinutes(10));

        session.Status.Should().Be(SessionStatus.Playing);
        session.TimeRemaining.Should().Be(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void Tick_Should_TransitionToResting_And_ResetToRestDuration_When_PlayTimeExpires()
    {
        var session = new Session();

        session.Tick(Rule40x20.PlayDuration);

        session.Status.Should().Be(SessionStatus.Resting);
        session.TimeRemaining.Should().Be(Rule40x20.RestDuration);
    }

    [Fact]
    public void Tick_Should_TransitionRestingToPlaying_When_RestTimeExpires()
    {
        var session = new Session();
        session.StartRest(); // Resting, 20 min

        session.Tick(Rule40x20.RestDuration);

        session.Status.Should().Be(SessionStatus.Playing);
        session.TimeRemaining.Should().Be(Rule40x20.PlayDuration);
    }

    [Fact]
    public void Tick_Should_LandOnCorrectPhase_When_ElapsedSpansMultiplePhases()
    {
        var session = new Session(); // Playing, 40 min

        // 40 (finish play) + 20 (finish rest) + 10 (into next play) = 70 min.
        session.Tick(TimeSpan.FromMinutes(70));

        session.Status.Should().Be(SessionStatus.Playing);
        session.TimeRemaining.Should().Be(TimeSpan.FromMinutes(30));
    }

    // ─── Tick: frozen states are no-ops ─────────────────────────────────────

    [Fact]
    public void Tick_Should_BeNoOp_When_ForceBlocked()
    {
        var session = new Session();
        session.ForceBlock();

        session.Tick(TimeSpan.FromMinutes(15));

        session.Status.Should().Be(SessionStatus.ForceBlocked);
        session.TimeRemaining.Should().Be(Rule40x20.PlayDuration);
    }

    [Fact]
    public void Tick_Should_BeNoOp_When_Paused()
    {
        var session = new Session();
        session.Pause();

        session.Tick(TimeSpan.FromMinutes(15));

        session.Status.Should().Be(SessionStatus.Paused);
        session.TimeRemaining.Should().Be(Rule40x20.PlayDuration);
    }

    [Fact]
    public void Tick_Should_BeNoOp_When_NightBlocked()
    {
        var session = new Session();
        session.EnterNight();

        session.Tick(TimeSpan.FromMinutes(15));

        session.Status.Should().Be(SessionStatus.NightBlocked);
        session.TimeRemaining.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Tick_Should_BeNoOp_When_ElapsedIsZero()
    {
        var session = new Session();

        session.Tick(TimeSpan.Zero);

        session.Status.Should().Be(SessionStatus.Playing);
        session.TimeRemaining.Should().Be(Rule40x20.PlayDuration);
    }

    [Fact]
    public void Tick_Should_Throw_When_ElapsedIsNegative()
    {
        var session = new Session();

        var act = () => session.Tick(TimeSpan.FromSeconds(-1));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ─── AddTime ────────────────────────────────────────────────────────────

    [Fact]
    public void AddTime_Should_ExtendRemaining_When_Playing()
    {
        var session = new Session();

        session.AddTime(TimeSpan.FromMinutes(10));

        session.Status.Should().Be(SessionStatus.Playing);
        session.TimeRemaining.Should().Be(TimeSpan.FromMinutes(50));
    }

    [Fact]
    public void AddTime_Should_FlipRestingToPlaying_And_ExtendRemaining()
    {
        var session = new Session();
        session.StartRest(); // Resting, 20 min

        session.AddTime(TimeSpan.FromMinutes(10));

        session.Status.Should().Be(SessionStatus.Playing);
        session.TimeRemaining.Should().Be(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void AddTime_Should_Throw_When_Negative()
    {
        var session = new Session();

        var act = () => session.AddTime(TimeSpan.FromSeconds(-1));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ─── StartRest / ForceBlock / ReleaseBlock ──────────────────────────────

    [Fact]
    public void StartRest_Should_EnterResting_With_RestDuration()
    {
        var session = new Session();

        session.StartRest();

        session.Status.Should().Be(SessionStatus.Resting);
        session.TimeRemaining.Should().Be(Rule40x20.RestDuration);
    }

    [Fact]
    public void ReleaseBlock_Should_ReturnToPlaying_And_KeepPositiveRemaining()
    {
        var session = new Session(); // Playing, 40 min
        session.Tick(TimeSpan.FromMinutes(5)); // 35 min left
        session.ForceBlock();

        session.ReleaseBlock();

        session.Status.Should().Be(SessionStatus.Playing);
        session.TimeRemaining.Should().Be(TimeSpan.FromMinutes(35));
    }

    [Fact]
    public void ReleaseBlock_Should_RefillPlayDuration_When_RemainingExhausted()
    {
        var session = Session.Restore(SessionStatus.ForceBlocked, TimeSpan.Zero, ScheduleRule.Default);

        session.ReleaseBlock();

        session.Status.Should().Be(SessionStatus.Playing);
        session.TimeRemaining.Should().Be(Rule40x20.PlayDuration);
    }

    // ─── Pause / Resume ─────────────────────────────────────────────────────

    [Fact]
    public void Resume_Should_ReturnToPlaying_When_Paused()
    {
        var session = new Session();
        session.Tick(TimeSpan.FromMinutes(5)); // 35 min
        session.Pause();

        session.Resume();

        session.Status.Should().Be(SessionStatus.Playing);
        session.TimeRemaining.Should().Be(TimeSpan.FromMinutes(35));
    }

    [Fact]
    public void Resume_Should_BeNoOp_When_NotPaused()
    {
        var session = new Session();
        session.ForceBlock();

        session.Resume();

        session.Status.Should().Be(SessionStatus.ForceBlocked);
    }

    // ─── Night snapshot / restore ───────────────────────────────────────────

    [Fact]
    public void EnterNight_Should_SnapshotPriorState_And_FreezeTimer()
    {
        var session = new Session();
        session.Tick(TimeSpan.FromMinutes(5)); // Playing, 35 min

        session.EnterNight();

        session.Status.Should().Be(SessionStatus.NightBlocked);
        session.TimeRemaining.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void ExitNight_Should_RestorePriorStatus_And_Remaining()
    {
        var session = new Session();
        session.Tick(TimeSpan.FromMinutes(5)); // Playing, 35 min
        session.EnterNight();

        session.ExitNight();

        session.Status.Should().Be(SessionStatus.Playing);
        session.TimeRemaining.Should().Be(TimeSpan.FromMinutes(35));
    }

    [Fact]
    public void ExitNight_Should_RestoreRestingState_When_SnapshottedWhileResting()
    {
        var session = new Session();
        session.StartRest(); // Resting, 20 min
        session.Tick(TimeSpan.FromMinutes(5)); // Resting, 15 min
        session.EnterNight();

        session.ExitNight();

        session.Status.Should().Be(SessionStatus.Resting);
        session.TimeRemaining.Should().Be(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void ExitNight_Should_ReleaseToPlaying_When_NoSnapshotExists()
    {
        // A rehydrated night-blocked session has no in-memory pre-night snapshot.
        var session = Session.Restore(SessionStatus.NightBlocked, TimeSpan.Zero, ScheduleRule.Default);

        session.ExitNight();

        session.Status.Should().Be(SessionStatus.Playing);
        session.TimeRemaining.Should().Be(Rule40x20.PlayDuration);
    }

    [Fact]
    public void ExitNight_Should_BeNoOp_When_NotNightBlocked()
    {
        var session = new Session();

        session.ExitNight();

        session.Status.Should().Be(SessionStatus.Playing);
        session.TimeRemaining.Should().Be(Rule40x20.PlayDuration);
    }

    [Fact]
    public void EnterNight_Should_NotOverwriteSnapshot_When_AlreadyNightBlocked()
    {
        var session = new Session();
        session.Tick(TimeSpan.FromMinutes(5)); // Playing, 35 min
        session.EnterNight();

        session.EnterNight(); // second call must not snapshot NightBlocked/Zero
        session.ExitNight();

        session.Status.Should().Be(SessionStatus.Playing);
        session.TimeRemaining.Should().Be(TimeSpan.FromMinutes(35));
    }

    // ─── ResetToPlayStart ───────────────────────────────────────────────────

    [Fact]
    public void ResetToPlayStart_Should_RestartPlayPhase_When_Resting()
    {
        var session = new Session();
        session.StartRest();

        session.ResetToPlayStart();

        session.Status.Should().Be(SessionStatus.Playing);
        session.TimeRemaining.Should().Be(Rule40x20.PlayDuration);
    }

    [Fact]
    public void ResetToPlayStart_Should_BeIgnored_When_ForceBlocked()
    {
        var session = new Session();
        session.ForceBlock();

        session.ResetToPlayStart();

        session.Status.Should().Be(SessionStatus.ForceBlocked);
    }

    [Fact]
    public void ResetToPlayStart_Should_BeIgnored_When_NightBlocked()
    {
        var session = new Session();
        session.EnterNight();

        session.ResetToPlayStart();

        session.Status.Should().Be(SessionStatus.NightBlocked);
        session.TimeRemaining.Should().Be(TimeSpan.Zero);
    }

    // ─── ApplyRule / ApplyRuleResettingPhase ────────────────────────────────

    [Fact]
    public void ApplyRule_Should_ChangeRule_Without_ResizingRemaining()
    {
        var session = new Session();
        session.Tick(TimeSpan.FromMinutes(5)); // Playing, 35 min
        var newRule = new ScheduleRule(50, 10);

        session.ApplyRule(newRule);

        session.Rule.Should().Be(newRule);
        session.TimeRemaining.Should().Be(TimeSpan.FromMinutes(35));
    }

    [Fact]
    public void ApplyRule_Should_Throw_When_Null()
    {
        var session = new Session();

        var act = () => session.ApplyRule(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ApplyRuleResettingPhase_Should_ResizePlayPhase_When_Playing()
    {
        var session = new Session();
        session.Tick(TimeSpan.FromMinutes(5));

        session.ApplyRuleResettingPhase(new ScheduleRule(50, 10));

        session.Status.Should().Be(SessionStatus.Playing);
        session.TimeRemaining.Should().Be(TimeSpan.FromMinutes(50));
    }

    [Fact]
    public void ApplyRuleResettingPhase_Should_ResizeRestPhase_When_Resting()
    {
        var session = new Session();
        session.StartRest();

        session.ApplyRuleResettingPhase(new ScheduleRule(50, 10));

        session.Status.Should().Be(SessionStatus.Resting);
        session.TimeRemaining.Should().Be(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void ApplyRuleResettingPhase_Should_LeaveRemaining_When_Blocked()
    {
        var session = Session.Restore(SessionStatus.ForceBlocked, TimeSpan.FromMinutes(7), ScheduleRule.Default);

        session.ApplyRuleResettingPhase(new ScheduleRule(50, 10));

        session.Status.Should().Be(SessionStatus.ForceBlocked);
        session.TimeRemaining.Should().Be(TimeSpan.FromMinutes(7));
    }

    // ─── Restore ────────────────────────────────────────────────────────────

    [Fact]
    public void Restore_Should_RehydrateValues_Without_RunningTransitions()
    {
        var rule = new ScheduleRule(50, 10);

        var session = Session.Restore(SessionStatus.Resting, TimeSpan.FromMinutes(3), rule);

        session.Status.Should().Be(SessionStatus.Resting);
        session.TimeRemaining.Should().Be(TimeSpan.FromMinutes(3));
        session.Rule.Should().Be(rule);
    }

    [Fact]
    public void Restore_Should_ClampNegativeRemaining_ToZero()
    {
        var session = Session.Restore(SessionStatus.Playing, TimeSpan.FromMinutes(-5), ScheduleRule.Default);

        session.TimeRemaining.Should().Be(TimeSpan.Zero);
    }
}
