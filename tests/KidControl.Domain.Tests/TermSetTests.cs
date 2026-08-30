using FluentAssertions;
using KidControl.Domain.Monitoring;
using Xunit;

namespace KidControl.Domain.Tests;

public sealed class TermSetTests
{
    private static string N(string s) => TextNormalizer.Normalize(s);

    [Fact]
    public void Matches_normalized_substring_and_returns_original_term()
    {
        var set = new TermSet(new[] { "сука" });

        set.TryMatch(N("ты ж сукадура"), out var term).Should().BeTrue();
        term.Should().Be("сука");
    }

    [Fact]
    public void Matches_obfuscated_input_via_normalization()
    {
        var set = new TermSet(new[] { "сука" });

        set.TryMatch(N("с у к а !!!"), out _).Should().BeTrue();
    }

    [Fact]
    public void Latin_typed_input_matches_cyrillic_term()
    {
        var set = new TermSet(new[] { "сука" });

        set.TryMatch(N("cyka blyat"), out _).Should().BeTrue();
    }

    [Fact]
    public void Prefers_the_longest_matching_term()
    {
        var set = new TermSet(new[] { "ху", "хуйня" });

        set.TryMatch(N("это хуйня"), out var term).Should().BeTrue();
        term.Should().Be("хуйня");
    }

    [Fact]
    public void Drops_blank_and_duplicate_terms()
    {
        var set = new TermSet(new[] { "бля", "  ", "", "бля", "!!!" });

        set.Count.Should().Be(1);
    }

    [Fact]
    public void No_match_returns_false()
    {
        var set = new TermSet(new[] { "сука" });

        set.TryMatch(N("совершенно нормальный текст"), out var term).Should().BeFalse();
        term.Should().BeEmpty();
    }
}
