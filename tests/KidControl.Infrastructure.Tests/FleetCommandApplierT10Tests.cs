using FluentAssertions;
using KidControl.Application.Abstractions;
using KidControl.Application.Commands;
using KidControl.Application.Services;
using KidControl.Contracts;
using KidControl.Fleet.Contracts;
using KidControl.Infrastructure.Fleet;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace KidControl.Infrastructure.Tests;

public class FleetCommandApplierT10Tests
{
    private sealed class StubClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset LocalNow { get; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    }

    private static CommandDto Cmd(string type, Dictionary<string, string>? payload = null) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Type = type,
        Payload = payload,
        TtlAt = DateTimeOffset.UtcNow.AddMinutes(5)
    };

    private sealed class FakeUi : KidControl.Infrastructure.Ipc.IUiCommandClient
    {
        public string? ScreenshotPath { get; set; } // null = capture failed (UI not running)
        public bool PlayResult { get; set; } = true;
        public string? PlayedPath { get; private set; }

        public Task<string?> CaptureScreenshotAsync(CancellationToken ct = default) => Task.FromResult(ScreenshotPath);
        public Task<bool> PlayAudioAsync(string audioPath, CancellationToken ct = default)
        {
            PlayedPath = audioPath;
            return Task.FromResult(PlayResult);
        }
    }

    private sealed class FakeFleet : IFleetClient
    {
        public string? UploadedId { get; private set; }
        public byte[]? UploadedImage { get; private set; }
        public bool UploadResult { get; set; } = true;

        public string? DownloadedMediaId { get; private set; }
        public byte[]? AudioToReturn { get; set; } = new byte[] { 4, 4, 4 };

        public Task<bool> UploadMediaAsync(string uploadId, byte[] image, CancellationToken ct = default)
        {
            UploadedId = uploadId;
            UploadedImage = image;
            return Task.FromResult(UploadResult);
        }

        public Task<byte[]?> DownloadMediaAsync(string mediaId, CancellationToken ct = default)
        {
            DownloadedMediaId = mediaId;
            return Task.FromResult(AudioToReturn);
        }

        public Task<EnrollOutcome> EnrollAsync(EnrollRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<HeartbeatResponse?> HeartbeatAsync(HeartbeatRequest request, CancellationToken ct = default)
            => Task.FromResult<HeartbeatResponse?>(null);
        public Task<IReadOnlyList<CommandDto>> PollCommandsAsync(int waitSeconds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CommandDto>>([]);
        public Task AckCommandsAsync(CommandAckBatch batch, CancellationToken ct = default) => Task.CompletedTask;
        public void UseToken(string token) { }
    }

    private static (FleetCommandApplier applier, Mock<ISystemController> system, FakeUpdateService update,
        FleetUpdateTarget target, FakeUi uiCmd, FakeFleet fleet) Build()
    {
        var ui = new Mock<IUiNotifier>();
        ui.Setup(x => x.NotifyStateChangedAsync(It.IsAny<SessionStateDto>(), It.IsAny<CancellationToken>()))
          .Returns(Task.CompletedTask);
        var tg = new Mock<ITelegramGateway>();
        tg.Setup(x => x.BroadcastAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var system = new Mock<ISystemController>();
        system.Setup(x => x.ShutdownAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        system.Setup(x => x.RestartAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var session = new SessionService(new StubClock(), new Mock<ISessionStore>().Object, ui.Object, tg.Object,
            system.Object, NullLogger<SessionService>.Instance);
        var update = new FakeUpdateService();
        var target = new FleetUpdateTarget();
        var uiCmd = new FakeUi();
        var fleet = new FakeFleet();
        var applier = new FleetCommandApplier(session, update, target, uiCmd, NullLogger<FleetCommandApplier>.Instance);
        return (applier, system, update, target, uiCmd, fleet);
    }

    [Fact]
    public async Task Shutdown_executes_and_acks_ok()
    {
        var (applier, system, _, _, _, fleet) = Build();
        var (ok, _) = await applier.ApplyAsync(Cmd(CommandTypes.Shutdown), fleet);
        ok.Should().BeTrue();
        system.Verify(s => s.ShutdownAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Restart_executes_and_acks_ok()
    {
        var (applier, system, _, _, _, fleet) = Build();
        var (ok, _) = await applier.ApplyAsync(Cmd(CommandTypes.Restart), fleet);
        ok.Should().BeTrue();
        system.Verify(s => s.RestartAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResetTimer_executes_and_acks_ok()
    {
        var (applier, _, _, _, _, fleet) = Build();
        var (ok, _) = await applier.ApplyAsync(Cmd(CommandTypes.ResetTimer), fleet);
        ok.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateNow_with_explicit_tag_installs_that_tag()
    {
        var (applier, _, update, _, _, fleet) = Build();
        var (ok, _) = await applier.ApplyAsync(Cmd(CommandTypes.UpdateNow, new() { ["tag"] = "v2.0.9" }), fleet);
        ok.Should().BeTrue();
        update.InstalledTag.Should().Be("v2.0.9");
    }

    [Fact]
    public async Task UpdateNow_without_tag_uses_pinned_target()
    {
        var (applier, _, update, target, _, fleet) = Build();
        target.Set("v2.0.8");
        await applier.ApplyAsync(Cmd(CommandTypes.UpdateNow), fleet);
        update.InstalledTag.Should().Be("v2.0.8");
    }

    [Fact]
    public async Task UpdateNow_without_tag_or_pin_resolves_latest()
    {
        var (applier, _, update, _, _, fleet) = Build();
        update.LatestTag = "v2.0.12"; // a newer release is available
        await applier.ApplyAsync(Cmd(CommandTypes.UpdateNow), fleet);
        update.InstalledTag.Should().Be("v2.0.12");
    }

    [Fact]
    public async Task UpdateNow_when_already_latest_is_a_noop_ok()
    {
        var (applier, _, update, _, _, fleet) = Build(); // LatestTag null → up to date
        var (ok, msg) = await applier.ApplyAsync(Cmd(CommandTypes.UpdateNow), fleet);
        ok.Should().BeTrue();
        msg.Should().Contain("up to date");
        update.InstallCalls.Should().Be(0);
    }

    [Fact]
    public async Task PlayAudio_downloads_by_mediaId_and_plays()
    {
        var (applier, _, _, _, uiCmd, fleet) = Build();
        fleet.AudioToReturn = new byte[] { 1, 2, 3, 4 };

        var (ok, _) = await applier.ApplyAsync(Cmd(CommandTypes.PlayAudio, new() { ["mediaId"] = "m-1" }), fleet);

        ok.Should().BeTrue();
        fleet.DownloadedMediaId.Should().Be("m-1");
        uiCmd.PlayedPath.Should().NotBeNull();
        uiCmd.PlayedPath.Should().EndWith(".ogg");
        File.Exists(uiCmd.PlayedPath!).Should().BeFalse(); // temp cleaned up
    }

    [Fact]
    public async Task PlayAudio_without_mediaId_fails_without_downloading()
    {
        var (applier, _, _, _, uiCmd, fleet) = Build();
        var (ok, error) = await applier.ApplyAsync(Cmd(CommandTypes.PlayAudio), fleet);
        ok.Should().BeFalse();
        error.Should().Contain("mediaId");
        fleet.DownloadedMediaId.Should().BeNull();
        uiCmd.PlayedPath.Should().BeNull();
    }

    [Fact]
    public async Task Screenshot_captures_and_uploads_under_the_upload_id()
    {
        var (applier, _, _, _, uiCmd, fleet) = Build();
        var png = Path.Combine(Path.GetTempPath(), $"kc-shot-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(png, new byte[] { 9, 8, 7, 6 });
        uiCmd.ScreenshotPath = png;

        var (ok, _) = await applier.ApplyAsync(Cmd(CommandTypes.Screenshot, new() { ["uploadId"] = "up-1" }), fleet);

        ok.Should().BeTrue();
        fleet.UploadedId.Should().Be("up-1");
        fleet.UploadedImage.Should().Equal(new byte[] { 9, 8, 7, 6 });
        File.Exists(png).Should().BeFalse(); // temp file cleaned up
    }

    [Fact]
    public async Task Screenshot_without_uploadId_fails_without_capturing()
    {
        var (applier, _, _, _, _, fleet) = Build();
        var (ok, error) = await applier.ApplyAsync(Cmd(CommandTypes.Screenshot), fleet);
        ok.Should().BeFalse();
        error.Should().Contain("uploadId");
        fleet.UploadedImage.Should().BeNull();
    }

    [Fact]
    public async Task Screenshot_fails_gracefully_when_capture_fails()
    {
        var (applier, _, _, _, uiCmd, fleet) = Build();
        uiCmd.ScreenshotPath = null; // UI not running

        var (ok, error) = await applier.ApplyAsync(Cmd(CommandTypes.Screenshot, new() { ["uploadId"] = "up-2" }), fleet);

        ok.Should().BeFalse();
        error.Should().Contain("capture failed");
        fleet.UploadedImage.Should().BeNull();
    }
}
