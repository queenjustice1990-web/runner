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
            var emitter = new Emitter(sw);
            var adapter = new TemplateTokenYamlAdapter(emitter);
            TemplateWriter.Write(adapter, token);

            string raw = sw.ToString();
            // Strip YAML document markers ("--- " prefix and "\n..." suffix).
            if (raw.StartsWith("--- ", StringComparison.Ordinal))
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
    }
}
