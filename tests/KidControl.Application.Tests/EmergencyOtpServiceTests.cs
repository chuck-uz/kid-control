using FluentAssertions;
using KidControl.Application.Services;
using KidControl.Application.Tests.Fakes;
using Xunit;

namespace KidControl.Application.Tests;

public sealed class EmergencyOtpServiceTests
{
    private static (EmergencyOtpService svc, FakeClock clock) Build()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        return (new EmergencyOtpService(clock), clock);
    }

    [Fact]
    public void TryIssue_Should_Return_SixDigitCode()
    {
        var (svc, _) = Build();

        var code = svc.TryIssue();

        code.Should().NotBeNull();
        code.Should().MatchRegex(@"^\d{6}$");
    }

    [Fact]
    public void TryIssue_Should_Return_Null_When_ReissuedWithinCooldown()
    {
        var (svc, clock) = Build();
        svc.TryIssue();

        clock.Advance(TimeSpan.FromSeconds(29));
        var second = svc.TryIssue();

        second.Should().BeNull();
    }

    [Fact]
    public void TryIssue_Should_Succeed_After_CooldownElapses()
    {
        var (svc, clock) = Build();
        svc.TryIssue();

        clock.Advance(TimeSpan.FromSeconds(31));
        var second = svc.TryIssue();

        second.Should().NotBeNull();
        second.Should().MatchRegex(@"^\d{6}$");
    }

    [Fact]
    public void Validate_Should_Return_Valid_And_BurnCode_When_CodeCorrect()
    {
        var (svc, _) = Build();
        var code = svc.TryIssue()!;

        svc.Validate(code).Should().Be(EmergencyOtpService.ValidationResult.Valid);
        // Single-use: the code must not validate again.
        svc.Validate(code).Should().Be(EmergencyOtpService.ValidationResult.NoActiveCode);
    }

    [Fact]
    public void Validate_Should_Return_NoActiveCode_When_NeverIssued()
    {
        var (svc, _) = Build();

        svc.Validate("123456").Should().Be(EmergencyOtpService.ValidationResult.NoActiveCode);
    }

    [Fact]
    public void Validate_Should_Return_Invalid_When_CodeWrong()
    {
        var (svc, _) = Build();
        var code = svc.TryIssue()!;
        var wrong = WrongCode(code);

        svc.Validate(wrong).Should().Be(EmergencyOtpService.ValidationResult.Invalid);
    }

    [Fact]
    public void Validate_Should_BurnCode_After_FiveWrongAttempts_AntiBruteForce()
    {
        var (svc, _) = Build();
        var code = svc.TryIssue()!;
        var wrong = WrongCode(code);

        // 5 attempts are allowed; each wrong guess is Invalid.
        for (var i = 0; i < 5; i++)
        {
            svc.Validate(wrong).Should().Be(EmergencyOtpService.ValidationResult.Invalid);
        }

        // The budget is exhausted and the code is burned: even the CORRECT code
        // is now rejected. This is the anti-brute-force guarantee.
        svc.Validate(code).Should().Be(EmergencyOtpService.ValidationResult.NoActiveCode);
    }

    [Fact]
    public void Validate_Should_Return_Expired_After_ValidityWindow()
    {
        var (svc, clock) = Build();
        var code = svc.TryIssue()!;

        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        svc.Validate(code).Should().Be(EmergencyOtpService.ValidationResult.Expired);
        // Expiry burns the code too.
        svc.Validate(code).Should().Be(EmergencyOtpService.ValidationResult.NoActiveCode);
    }

    [Fact]
    public void Validate_Should_Still_Accept_JustBefore_Expiry()
    {
        var (svc, clock) = Build();
        var code = svc.TryIssue()!;

        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));

        svc.Validate(code).Should().Be(EmergencyOtpService.ValidationResult.Valid);
    }

    private static string WrongCode(string code)
    {
        // Deterministically produce a 6-digit string different from the issued one.
        var value = int.Parse(code, System.Globalization.CultureInfo.InvariantCulture);
        var other = (value + 1) % 1_000_000;
        return other.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }
}
