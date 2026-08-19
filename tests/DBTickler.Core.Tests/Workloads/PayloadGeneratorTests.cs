using System.Text.RegularExpressions;
using DBTickler.Core.Workloads;

namespace DBTickler.Core.Tests.Workloads;

public partial class PayloadGeneratorTests
{
    [GeneratedRegex("^[0-9a-f]{16}$")]
    private static partial Regex LowercaseHex16();

    public class Sizing
    {
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(200)]
        [InlineData(5000)]
        public void NextText_returns_exactly_the_requested_length(int requestedLength)
        {
            var generator = new PayloadGenerator(maxTextChars: 10_000, maxBinaryBytes: 10_000, seed: 1);
            var random = new Random(1);

            var text = generator.NextText(random, requestedLength);

            Assert.Equal(requestedLength, text.Length);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(200)]
        [InlineData(5000)]
        public void NextBytes_returns_exactly_the_requested_length(int requestedLength)
        {
            var generator = new PayloadGenerator(maxTextChars: 10_000, maxBinaryBytes: 10_000, seed: 1);
            var random = new Random(1);

            var bytes = generator.NextBytes(random, requestedLength);

            Assert.Equal(requestedLength, bytes.Length);
        }

        [Fact]
        public void NextText_with_no_explicit_length_uses_MaxTextChars()
        {
            var generator = new PayloadGenerator(maxTextChars: 321, maxBinaryBytes: 0, seed: 1);
            Assert.Equal(321, generator.NextText(new Random(1)).Length);
        }

        [Fact]
        public void NextBytes_with_no_explicit_length_uses_MaxBinaryBytes()
        {
            var generator = new PayloadGenerator(maxTextChars: 0, maxBinaryBytes: 654, seed: 1);
            Assert.Equal(654, generator.NextBytes(new Random(1)).Length);
        }

        [Fact]
        public void Negative_constructor_arguments_are_clamped_to_zero()
        {
            var generator = new PayloadGenerator(maxTextChars: -5, maxBinaryBytes: -10, seed: 1);
            Assert.Equal(0, generator.MaxTextChars);
            Assert.Equal(0, generator.MaxBinaryBytes);
        }

        [Fact]
        public void ApproximateRowBytes_combines_doubled_text_length_and_binary_length()
        {
            var generator = new PayloadGenerator(maxTextChars: 100, maxBinaryBytes: 50, seed: 1);
            // NVARCHAR is 2 bytes per character.
            Assert.Equal(100 * 2 + 50, generator.ApproximateRowBytes);
        }

        [Fact]
        public void Zero_budget_is_handled_without_throwing()
        {
            var generator = PayloadGenerator.ForRowBudget(0, seed: 1);
            var random = new Random(1);

            Assert.Equal("", generator.NextText(random));
            Assert.Empty(generator.NextBytes(random));
        }
    }

    public class RowBudgetSplitting
    {
        [Theory]
        [InlineData(2048, 512, 1024)]  // budget/4 text chars, budget/2 binary bytes
        [InlineData(4000, 1000, 2000)]
        [InlineData(0, 0, 0)]
        [InlineData(3, 0, 1)] // integer division rounds down
        public void ForRowBudget_splits_the_budget_as_documented(int payloadBytes, int expectedTextChars, int expectedBinaryBytes)
        {
            var generator = PayloadGenerator.ForRowBudget(payloadBytes, seed: 1);

            Assert.Equal(expectedTextChars, generator.MaxTextChars);
            Assert.Equal(expectedBinaryBytes, generator.MaxBinaryBytes);
        }

        [Fact]
        public void Negative_budget_is_treated_as_zero()
        {
            var generator = PayloadGenerator.ForRowBudget(-100, seed: 1);
            Assert.Equal(0, generator.MaxTextChars);
            Assert.Equal(0, generator.MaxBinaryBytes);
        }
    }

    public class Determinism
    {
        [Fact]
        public void Same_generator_and_same_random_seed_produce_identical_text()
        {
            var generator = new PayloadGenerator(maxTextChars: 500, maxBinaryBytes: 500, seed: 7);

            var first = generator.NextText(new Random(123), 50);
            var second = generator.NextText(new Random(123), 50);

            Assert.Equal(first, second);
        }

        [Fact]
        public void Same_generator_and_same_random_seed_produce_identical_bytes()
        {
            var generator = new PayloadGenerator(maxTextChars: 500, maxBinaryBytes: 500, seed: 7);

            var first = generator.NextBytes(new Random(99), 40);
            var second = generator.NextBytes(new Random(99), 40);

            Assert.Equal(first, second);
        }

        [Fact]
        public void Two_generators_built_with_the_same_seed_produce_the_same_pool_contents()
        {
            var first = new PayloadGenerator(maxTextChars: 200, maxBinaryBytes: 200, seed: 55);
            var second = new PayloadGenerator(maxTextChars: 200, maxBinaryBytes: 200, seed: 55);

            Assert.Equal(first.NextText(new Random(1), 200), second.NextText(new Random(1), 200));
        }

        [Fact]
        public void Successive_draws_from_the_same_random_produce_variety_not_a_constant()
        {
            var generator = new PayloadGenerator(maxTextChars: 2000, maxBinaryBytes: 2000, seed: 1);
            var random = new Random(42);

            var draws = Enumerable.Range(0, 5).Select(_ => generator.NextText(random, 30)).ToList();

            Assert.True(draws.Distinct().Count() > 1, "Expected different offsets to produce different text.");
        }
    }

    public class Tags
    {
        [Fact]
        public void NextTag_produces_a_16_character_lowercase_hex_string()
        {
            var tag = PayloadGenerator.NextTag(new Random(1));

            Assert.Equal(16, tag.Length);
            Assert.Matches(LowercaseHex16(), tag);
        }

        [Fact]
        public void NextTag_varies_between_calls()
        {
            var random = new Random(1);
            var tags = Enumerable.Range(0, 10).Select(_ => PayloadGenerator.NextTag(random)).ToList();

            Assert.True(tags.Distinct().Count() > 1);
        }
    }
}
