using System;
using System.Globalization;
using System.IO;
using GitHub.DistributedTask.ObjectTemplating;
using GitHub.DistributedTask.ObjectTemplating.Tokens;
using GitHub.Runner.Sdk;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace GitHub.Runner.Worker.Dap
{
    /// <summary>
    /// Adapts a YamlDotNet <see cref="IEmitter"/> as a DT
    /// <see cref="IObjectWriter"/> so a <see cref="TemplateToken"/> DOM
    /// can be serialized back to YAML preserving its pre-evaluation form
    /// (basic <c>${{ }}</c> expressions are written through verbatim).
    ///
    /// Used by the DAP execution view to surface user-authored step
    /// parameters (<c>env:</c>, <c>with:</c>, <c>run:</c>, ...) without
    /// any expression substitution.
    /// </summary>
    internal sealed class TemplateTokenYamlAdapter : IObjectWriter
    {
        private readonly IEmitter _emitter;

        public TemplateTokenYamlAdapter(IEmitter emitter)
        {
            ArgUtil.NotNull(emitter, nameof(emitter));
            _emitter = emitter;
        }

        public void WriteStart()
        {
            _emitter.Emit(new StreamStart());
            _emitter.Emit(new DocumentStart(null, null, true));
        }

        public void WriteEnd()
        {
            _emitter.Emit(new DocumentEnd(true));
            _emitter.Emit(new StreamEnd());
        }

        public void WriteNull() =>
            _emitter.Emit(new Scalar(null, null, "null", ScalarStyle.Plain, true, false));

        public void WriteBoolean(bool value) =>
            _emitter.Emit(new Scalar(null, null, value ? "true" : "false", ScalarStyle.Plain, true, false));

        public void WriteNumber(double value) =>
            _emitter.Emit(new Scalar(null, null, value.ToString("R", CultureInfo.InvariantCulture), ScalarStyle.Plain, true, false));

        public void WriteString(string value)
        {
            if (value == null)
            {
                WriteNull();
                return;
            }
            // Multi-line strings render as block literal so embedded
            // newlines survive the YAML round trip.
            var style = value.IndexOf('\n') >= 0 ? ScalarStyle.Literal : ScalarStyle.Any;
            _emitter.Emit(new Scalar(null, null, value, style, true, true));
        }

        public void WriteSequenceStart() =>
            _emitter.Emit(new SequenceStart(null, null, true, SequenceStyle.Any));

        public void WriteSequenceEnd() =>
            _emitter.Emit(new SequenceEnd());

        public void WriteMappingStart() =>
            _emitter.Emit(new MappingStart(null, null, true, MappingStyle.Any));

        public void WriteMappingEnd() =>
            _emitter.Emit(new MappingEnd());

        /// <summary>
        /// Serialize a TemplateToken to a YAML fragment ready to embed
        /// under a parent key. Each non-empty line is prefixed by
        /// <paramref name="indentSpaces"/> spaces. Trailing newlines and
        /// the YAML stream start/document markers are stripped, so the
        /// caller controls line breaks.
        /// </summary>
        /// <remarks>
        /// Empty mappings render as <c>{}</c> and empty sequences as
        /// <c>[]</c> via YamlDotNet's flow style fallback for empty
        /// collections.
        /// </remarks>
        internal static string Serialize(TemplateToken token, int indentSpaces)
        {
            if (indentSpaces < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(indentSpaces));
            }

            using var sw = new StringWriter(CultureInfo.InvariantCulture);
            // Force LF line breaks; YamlDotNet's Emitter calls WriteLine,
            // which would otherwise produce CRLF on Windows and corrupt
            // both the document-end stripping below and the per-line
            // indentation pass that follows.
            sw.NewLine = "\n";
            var emitter = new Emitter(sw);
            var adapter = new TemplateTokenYamlAdapter(emitter);
            adapter.WriteStart();
            WriteToken(adapter, token);
            adapter.WriteEnd();

            string raw = sw.ToString();
            // Strip YAML document markers. The Emitter most commonly elides
            // these for our use (DocumentStart isImplicit=true), but emits
            // them for some scalar edge cases (e.g. empty strings) and may
            // emit them on their own line for collection roots under some
            // settings. Strip both shapes defensively so callers never see
            // a leaked marker leak into the embedded fragment.
            if (raw.StartsWith("--- ", StringComparison.Ordinal))
            {
                raw = raw.Substring(4);
            }
            else if (raw.StartsWith("---\n", StringComparison.Ordinal))
            {
                raw = raw.Substring(4);
            }
            const string DocEndMarker = "\n...";
            if (raw.EndsWith(DocEndMarker + "\n", StringComparison.Ordinal))
            {
                raw = raw.Substring(0, raw.Length - DocEndMarker.Length - 1);
            }
            else if (raw.EndsWith(DocEndMarker, StringComparison.Ordinal))
            {
                raw = raw.Substring(0, raw.Length - DocEndMarker.Length);
            }
            raw = raw.TrimEnd('\n');

            if (indentSpaces == 0)
            {
                return raw;
            }

            // Re-indent every non-empty line. Empty lines remain empty
            // so YAML block-literal blank lines stay valid.
            var pad = new string(' ', indentSpaces);
            var sb = new System.Text.StringBuilder(raw.Length + indentSpaces * 4);
            int i = 0;
            while (i < raw.Length)
            {
                int end = raw.IndexOf('\n', i);
                int lineEnd = end < 0 ? raw.Length : end;
                if (lineEnd > i)
                {
                    sb.Append(pad);
                    sb.Append(raw, i, lineEnd - i);
                }
                if (end < 0)
                {
                    break;
                }
                sb.Append('\n');
                i = end + 1;
            }
            return sb.ToString();
        }

        /// <summary>
        /// Mirrors <see cref="TemplateWriter"/>'s recursive walk, with one
        /// behavioural change: <see cref="BasicExpressionToken"/> is emitted
        /// via <c>ToDisplayString()</c> instead of <c>ToString()</c>.
        /// </summary>
        /// <remarks>
        /// The workflow parser tokenizes a mixed scalar like
        /// <c>${{ runner.os }}-primes</c> as a single
        /// <see cref="BasicExpressionToken"/> whose internal expression is
        /// <c>format('{0}-primes', runner.os)</c>. <c>ToString()</c> emits
        /// the normalized form verbatim; <c>ToDisplayString()</c> reverses
        /// the <c>format(...)</c> rewrite so the user sees the original
        /// authored form. Other token kinds delegate to the same writer
        /// calls <see cref="TemplateWriter"/> would make.
        /// </remarks>
        private static void WriteToken(IObjectWriter writer, TemplateToken token)
        {
            switch (token?.Type ?? TokenType.Null)
            {
                case TokenType.Null:
                    writer.WriteNull();
                    break;
                case TokenType.Boolean:
                    writer.WriteBoolean(((BooleanToken)token).Value);
                    break;
                case TokenType.Number:
                    writer.WriteNumber(((NumberToken)token).Value);
                    break;
                case TokenType.String:
                    writer.WriteString(token.ToString());
                    break;
                case TokenType.BasicExpression:
                    writer.WriteString(((BasicExpressionToken)token).ToDisplayString());
                    break;
                case TokenType.InsertExpression:
                    writer.WriteString(token.ToString());
                    break;
                case TokenType.Mapping:
                    writer.WriteMappingStart();
                    foreach (var pair in (MappingToken)token)
                    {
                        WriteToken(writer, pair.Key);
                        WriteToken(writer, pair.Value);
                    }
                    writer.WriteMappingEnd();
                    break;
                case TokenType.Sequence:
                    writer.WriteSequenceStart();
                    foreach (var item in (SequenceToken)token)
                    {
                        WriteToken(writer, item);
                    }
                    writer.WriteSequenceEnd();
                    break;
                default:
                    throw new NotSupportedException($"Unexpected token type '{token.GetType()}'.");
            }
        }
    }
}
