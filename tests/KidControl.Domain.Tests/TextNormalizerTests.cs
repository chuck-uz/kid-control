using FluentAssertions;
using KidControl.Domain.Monitoring;
using Xunit;

namespace KidControl.Domain.Tests;

public sealed class TextNormalizerTests
{
    [Theory]
    [InlineData("ХУЙ", "хуй")]              // lowercase
    [InlineData("жёстко", "жестко")]        // ё → е
    [InlineData("с.у.к.а", "сука")]         // separators stripped
    [InlineData("с у к а", "сука")]         // spaces stripped
    [InlineData("б-л-я", "бля")]            // dashes stripped
    [InlineData("бляяя", "бля")]            // repeats collapsed
    [InlineData("сссука", "сука")]          // leading repeats collapsed
    public void Normalizes_case_yo_separators_and_repeats(string raw, string expected)
        => TextNormalizer.Normalize(raw).Should().Be(expected);

    [Theory]
    [InlineData("cyka", "сука")]            // latin look-alikes → cyrillic
    [InlineData("xer", "хер")]              // x→х, e→е, r→р
    [InlineData("с0су", "сосу")]            // 0 → о
    [InlineData("3бал", "ебал")]            // 3 → е
    public void Folds_latin_and_leet_to_cyrillic(string raw, string expected)
        => TextNormalizer.Normalize(raw).Should().Be(expected);

    [Fact]
    public void Latin_word_folds_consistently_with_its_cyrillic_form()
    {
        // The point of folding: a latin-typed variant and the cyrillic list term collapse
        // onto the SAME canonical string, so either can match the other.
        TextNormalizer.Normalize("cyka").Should().Be(TextNormalizer.Normalize("сука"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!! ??? …")]
    [InlineData("🙂👍")]
    public void Empty_or_symbol_only_becomes_empty(string? raw)
        => TextNormalizer.Normalize(raw).Should().BeEmpty();
}
