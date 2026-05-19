using System;
using System.Collections.Generic;
using GitHub.Runner.Worker.Dap;
using Xunit;
using YamlDotNet.Serialization;

namespace GitHub.Runner.Common.Tests.Worker
{
    public sealed class YamlScalarFormatterL0
    {
        private static readonly IDeserializer Deserializer = new DeserializerBuilder().Build();

        // Embed the formatter output inside a minimal YAML mapping and
        // round-trip through YamlDotNet, asserting the parsed value equals
        // the original input. Decouples assertions from the emitter's
        // quoting choices (plain vs single- vs double-quoted).
        private static void AssertRoundTrips(string value)
        {
            string scalar = YamlScalarFormatter.Format(value);
            string yaml = $"k: {scalar}\n";

            Dictionary<string, object> doc;
            try
            {
                doc = Deserializer.Deserialize<Dictionary<string, object>>(yaml);
            }
            catch (Exception ex)
            {
                throw new Xunit.Sdk.XunitException(
                    $"Formatted scalar did not round-trip as valid YAML.\nInput: '{value}'\nFormatted: '{scalar}'\nFull YAML:\n{yaml}\nError: {ex}");
            }
            Assert.NotNull(doc);
            Assert.True(doc.ContainsKey("k"), $"missing key in parsed doc. Formatted: '{scalar}'");
            Assert.Equal(value, doc["k"] as string);
        }

        [Theory]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        [InlineData("hello")]
        [InlineData("with: colon")]
        [InlineData("with#hash")]
        [InlineData(" leading")]
        [InlineData("trailing ")]
        [InlineData("a\"b")]
        [InlineData("a\\b")]
        [InlineData("@at")]
        [InlineData("*star")]
        [InlineData("&amp")]
        [InlineData("?question")]
        [InlineData("!exclaim")]
        [InlineData("- dash")]
        [InlineData("{brace}")]
        [InlineData("[bracket]")]
        public void Format_RoundTripsThroughYamlDeserializer(string value)
        {
            // The formatter must produce output that, embedded under a key,
            // parses back to exactly the input. The emitter is free to
            // pick plain, single-quoted, or double-quoted style.
            AssertRoundTrips(value);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Format_PlainAscii_NoQuotingNeeded()
        {
            // Sanity check that the simple case stays plain.
            Assert.Equal("hello", YamlScalarFormatter.Format("hello"));
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Format_NoTrailingNewline()
        {
            Assert.False(YamlScalarFormatter.Format("hello").EndsWith("\n"));
            Assert.False(YamlScalarFormatter.Format("with: colon").EndsWith("\n"));
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Format_NoDocumentMarkers()
        {
            // The emitter wraps the scalar in a document; the formatter
            // must strip both `--- ` (with space) and `---\n` (on its
            // own line) prefixes plus the `\n...` suffix.
            Assert.DoesNotContain("---", YamlScalarFormatter.Format("hello"));
            Assert.DoesNotContain("...", YamlScalarFormatter.Format("hello"));
            // Empty string is one of the cases where the emitter does
            // produce a document marker by default.
            Assert.DoesNotContain("---", YamlScalarFormatter.Format(""));
            Assert.DoesNotContain("...", YamlScalarFormatter.Format(""));
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Format_AlwaysUsesLfLineBreaks()
        {
            // Regression: YamlDotNet's Emitter calls WriteLine, which on
            // Windows produces CRLF (the host's Environment.NewLine).
            // Format must force LF so the output round-trips regardless
            // of platform.
            Assert.DoesNotContain('\r', YamlScalarFormatter.Format("hello"));
            Assert.DoesNotContain('\r', YamlScalarFormatter.Format("with: colon"));
            Assert.DoesNotContain('\r', YamlScalarFormatter.Format(""));
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Format_NullValue_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => YamlScalarFormatter.Format(null));
        }
    }
}
