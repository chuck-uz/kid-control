using FluentAssertions;
using KidControl.Backend.Fleet;
using Telegram.Bot;
using Xunit;

namespace KidControl.Backend.Tests;

/// <summary>
/// The relay's decision logic (G1): only deliver to the chat that requested it, only from the
/// device that was asked, within TTL, and only a sane-sized payload. Delivery is captured via a
/// test subclass so nothing touches Telegram.
/// </summary>
public sealed class ScreenshotRelayTests
{
    private sealed class SpyRelay() : ScreenshotRelay(new TelegramBotClient("0:DISABLED"))
    {
        public int Sends { get; private set; }
        public long LastChatId { get; private set; }
        public int LastBytes { get; private set; }

        protected override Task SendPhotoAsync(long chatId, byte[] image, CancellationToken ct)
        {
            Sends++;
            LastChatId = chatId;
            LastBytes = image.Length;
            return Task.CompletedTask;
        }
    }

    private static readonly Guid Device = Guid.NewGuid();

    [Fact]
    public async Task Delivers_to_the_requesting_chat_once()
    {
        var relay = new SpyRelay();
        relay.Register("u1", chatId: 42, Device);

        (await relay.DeliverAsync("u1", Device, new byte[] { 1, 2, 3 })).Should().BeTrue();
        relay.Sends.Should().Be(1);
        relay.LastChatId.Should().Be(42);
        relay.LastBytes.Should().Be(3);

        // The pending entry is consumed — a second upload for the same id is rejected.
        (await relay.DeliverAsync("u1", Device, new byte[] { 1 })).Should().BeFalse();
    }

    [Fact]
    public async Task Rejects_wrong_device_unknown_id_and_bad_sizes()
    {
        var relay = new SpyRelay();
        relay.Register("u1", chatId: 42, Device);

        (await relay.DeliverAsync("u1", Guid.NewGuid(), new byte[] { 1 })).Should().BeFalse(); // wrong device
        (await relay.DeliverAsync("nope", Device, new byte[] { 1 })).Should().BeFalse();       // unknown id
        (await relay.DeliverAsync("u1", Device, Array.Empty<byte>())).Should().BeFalse();      // empty
        (await relay.DeliverAsync("u1", Device, new byte[ScreenshotRelay.MaxBytes + 1])).Should().BeFalse(); // too big

        relay.Sends.Should().Be(0); // nothing was ever sent
    }
}
