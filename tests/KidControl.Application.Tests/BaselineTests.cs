using FluentAssertions;
using KidControl.Application.Interfaces;
using KidControl.Application.Services;
using Moq;
using Xunit;

namespace KidControl.Application.Tests;

public sealed class SessionOrchestratorTests
{
    [Fact]
    public async Task ProcessTickAsync_ShouldNotifyUiState()
    {
        var uiNotifier = new Mock<IUiNotifier>();
        var telegramNotifier = new Mock<ITelegramNotifier>();
        var orchestrator = new SessionOrchestrator(uiNotifier.Object, telegramNotifier.Object);

        await orchestrator.ProcessTickAsync();

        uiNotifier.Verify(
            x => x.NotifyStateChangedAsync(It.IsAny<KidControl.Contracts.SessionStateDto>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleTelegramCommandAsync_Block_ShouldForceBlockAndSendTelegramReply()
    {
        var uiNotifier = new Mock<IUiNotifier>();
        var telegramNotifier = new Mock<ITelegramNotifier>();
        var orchestrator = new SessionOrchestrator(uiNotifier.Object, telegramNotifier.Object);
        const long chatId = 1001;

        await orchestrator.HandleTelegramCommandAsync("/block", chatId);

        var state = orchestrator.GetCurrentState();
        state.Status.Should().Be("Blocked");
        state.TimeRemaining.Should().Be(TimeSpan.FromMinutes(15));
        telegramNotifier.Verify(
            x => x.SendReplyAsync(
                chatId,
                It.Is<string>(s => s.Contains("заблок", StringComparison.OrdinalIgnoreCase))),
            Times.Once);
    }

    [Fact]
    public async Task HandleTelegramCommandAsync_AddTime_ShouldIncreaseRemainingTime()
    {
        var uiNotifier = new Mock<IUiNotifier>();
        var telegramNotifier = new Mock<ITelegramNotifier>();
        var orchestrator = new SessionOrchestrator(uiNotifier.Object, telegramNotifier.Object);
        const long chatId = 1002;

        var before = orchestrator.GetCurrentState().TimeRemaining;
        await orchestrator.HandleTelegramCommandAsync("/addtime 30", chatId);
        var after = orchestrator.GetCurrentState().TimeRemaining;

        after.Should().Be(before + TimeSpan.FromMinutes(30));
        telegramNotifier.Verify(
            x => x.SendReplyAsync(
                chatId,
                It.Is<string>(s => s.Contains("Добавлено 30 минут", StringComparison.OrdinalIgnoreCase))),
            Times.Once);
    }

    [Fact]
    public async Task UpdateRules_ShouldRefreshStateAndNotifyUi()
    {
        var uiNotifier = new Mock<IUiNotifier>();
        var telegramNotifier = new Mock<ITelegramNotifier>();
        var orchestrator = new SessionOrchestrator(uiNotifier.Object, telegramNotifier.Object);

        var confirmation = await orchestrator.UpdateRules(TimeSpan.FromMinutes(45), TimeSpan.FromMinutes(15));
        var state = orchestrator.GetCurrentState();

        confirmation.Should().Contain("45 мин работы");
        state.TimeRemaining.Should().Be(TimeSpan.FromMinutes(45));
        uiNotifier.Verify(x => x.NotifyStateChangedAsync(It.IsAny<KidControl.Contracts.SessionStateDto>()), Times.Once);
    }

    [Fact]
    public async Task TryHandleCustomRuleInputAsync_ShouldApplyRule_WhenFormatIsValid()
    {
        var uiNotifier = new Mock<IUiNotifier>();
        var telegramNotifier = new Mock<ITelegramNotifier>();
        var orchestrator = new SessionOrchestrator(uiNotifier.Object, telegramNotifier.Object);
        const long chatId = 2001;

        orchestrator.BeginCustomRuleInput(chatId);
        var handled = await orchestrator.TryHandleCustomRuleInputAsync(chatId, "50/10");
        var state = orchestrator.GetCurrentState();

        handled.Should().BeTrue();
        state.TimeRemaining.Should().Be(TimeSpan.FromMinutes(50));
        telegramNotifier.Verify(
            x => x.SendReplyAsync(chatId, It.Is<string>(s => s.Contains("50 мин работы", StringComparison.OrdinalIgnoreCase))),
            Times.Once);
    }
}
