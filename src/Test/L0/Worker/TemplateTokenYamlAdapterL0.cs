using GitHub.DistributedTask.ObjectTemplating.Tokens;
using GitHub.Runner.Worker.Dap;
using Xunit;

namespace GitHub.Runner.Common.Tests.Worker
{
    public sealed class TemplateTokenYamlAdapterL0
    {
        private static StringToken Str(string s) => new(null, null, null, s);
        private static BooleanToken Bool(bool b) => new(null, null, null, b);
        private static NumberToken Num(double n) => new(null, null, null, n);
        private static BasicExpressionToken Expr(string s) => new(null, null, null, s);

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Serialize_StringScalar()
        {
            Assert.Equal("hello", TemplateTokenYamlAdapter.Serialize(Str("hello"), 0));
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Serialize_BooleanScalar()
        {
            Assert.Equal("true", TemplateTokenYamlAdapter.Serialize(Bool(true), 0));
            Assert.Equal("false", TemplateTokenYamlAdapter.Serialize(Bool(false), 0));
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Serialize_NumberScalar()
        {
            Assert.Equal("10", TemplateTokenYamlAdapter.Serialize(Num(10), 0));
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Serialize_NullToken_RendersAsNull()
        {
            Assert.Equal("null", TemplateTokenYamlAdapter.Serialize(null, 0));
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Serialize_PreservesBasicExpression()
        {
            var token = Expr("runner.os");
            string yaml = TemplateTokenYamlAdapter.Serialize(token, 0);
            Assert.Contains("${{ runner.os }}", yaml);
            Assert.DoesNotContain("Linux", yaml);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Serialize_PreservesCompositeExpressionInStringToken()
        {
            // A StringToken constructed directly with the literal text
            // round-trips unchanged. (The workflow parser does NOT produce
            // a StringToken for this input — see
            // Serialize_ReversesFormatRewriteForCompositeExpression — but
            // direct StringToken construction must still preserve the
            // literal verbatim.)
            var token = Str("${{ runner.os }}-primes");
            string yaml = TemplateTokenYamlAdapter.Serialize(token, 0);
            Assert.Contains("${{ runner.os }}-primes", yaml);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Serialize_ReversesFormatRewriteForCompositeExpression()
        {
            // The workflow parser tokenizes a mixed scalar like
            // `${{ runner.os }}-primes` as a single BasicExpressionToken
            // whose internal expression is `format('{0}-primes', runner.os)`.
            // The adapter must surface the author-facing form, not the
            // parser's normalized rewrite.
            var token = Expr("format('{0}-primes', runner.os)");
            string yaml = TemplateTokenYamlAdapter.Serialize(token, 0);
            Assert.Contains("${{ runner.os }}-primes", yaml);
            Assert.DoesNotContain("format(", yaml);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Serialize_NestedMapping()
        {
            var inner = new MappingToken(null, null, null);
            inner.Add(Str("b"), Num(1));
            inner.Add(Str("c"), Expr("x"));
            var outer = new MappingToken(null, null, null);
            outer.Add(Str("a"), inner);

            string yaml = TemplateTokenYamlAdapter.Serialize(outer, 0);

            Assert.Contains("a:", yaml);
            Assert.Contains("b: 1", yaml);
            Assert.Contains("c: ${{ x }}", yaml);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Serialize_EmptyMapping()
        {
            var token = new MappingToken(null, null, null);
            string yaml = TemplateTokenYamlAdapter.Serialize(token, 0);
            Assert.Equal("{}", yaml);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Serialize_EmptySequence()
        {
            var token = new SequenceToken(null, null, null);
            string yaml = TemplateTokenYamlAdapter.Serialize(token, 0);
            Assert.Equal("[]", yaml);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Serialize_MultilineString_UsesBlockScalar()
        {
            var token = Str("line1\nline2\nline3");
            string yaml = TemplateTokenYamlAdapter.Serialize(token, 0);
            // Block-literal indicator `|` appears for multi-line scalars.
            Assert.Contains("|", yaml);
            Assert.Contains("line1", yaml);
            Assert.Contains("line2", yaml);
            Assert.Contains("line3", yaml);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Serialize_IndentLevel_PrefixesNonEmptyLines()
        {
            var map = new MappingToken(null, null, null);
            map.Add(Str("k1"), Str("v1"));
            map.Add(Str("k2"), Str("v2"));

            string yaml = TemplateTokenYamlAdapter.Serialize(map, indentSpaces: 4);

            foreach (var line in yaml.Split('\n'))
            {
                if (line.Length > 0)
                {
                    Assert.StartsWith("    ", line);
                }
            }
            Assert.Contains("k1: v1", yaml);
            Assert.Contains("k2: v2", yaml);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Serialize_NoTrailingNewline()
        {
            var token = Str("hello");
            string yaml = TemplateTokenYamlAdapter.Serialize(token, 0);
            Assert.False(yaml.EndsWith("\n"));
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Serialize_AlwaysUsesLfLineBreaks()
        {
            // Regression: YamlDotNet's Emitter calls WriteLine, which on
            // Windows produces CRLF (the host's Environment.NewLine).
            // Serialize must force LF so the rendered view round-trips
            // regardless of platform.
            var map = new MappingToken(null, null, null);
            map.Add(Str("k1"), Str("v1"));
            map.Add(Str("k2"), Num(2));
            map.Add(Str("k3"), Bool(true));
            string yaml = TemplateTokenYamlAdapter.Serialize(map, indentSpaces: 2);
            Assert.DoesNotContain("\r", yaml);
        }
    }
}
