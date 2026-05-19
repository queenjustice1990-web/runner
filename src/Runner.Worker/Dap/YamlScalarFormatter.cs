using System;
using System.Globalization;
using System.IO;
using GitHub.Runner.Sdk;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace GitHub.Runner.Worker.Dap
{
    /// <summary>
    /// Formats a single string as a quote-safe YAML scalar by routing it
    /// through YamlDotNet's <see cref="Emitter"/>. The returned text is
    /// safe to splice into a hand-emitted YAML document fragment.
    ///
    /// Caller responsibility: this only handles the scalar value; it does
    /// not emit a key, indent, or trailing newline.
    /// </summary>
    internal static class YamlScalarFormatter
    {
        /// <summary>
        /// Return <paramref name="value"/> formatted as a YAML scalar:
        /// plain, single-quoted, or double-quoted as the emitter chooses,
        /// with no surrounding document markers or trailing newline.
        /// </summary>
        public static string Format(string value)
        {
            ArgUtil.NotNull(value, nameof(value));

            using var sw = new StringWriter(CultureInfo.InvariantCulture);
            // Force LF line breaks; YamlDotNet's Emitter calls WriteLine,
            // which would otherwise produce CRLF on Windows and break
            // both our document-end stripping below and downstream
            // consumers that assume a single line-break convention.
            sw.NewLine = "\n";
            var emitter = new Emitter(sw);
            emitter.Emit(new StreamStart());
            emitter.Emit(new DocumentStart(null, null, true));
            emitter.Emit(new Scalar(null, null, value, ScalarStyle.Any, true, true));
            emitter.Emit(new DocumentEnd(true));
            emitter.Emit(new StreamEnd());

            string raw = sw.ToString();
            // Strip YAML document markers. Emitter elides these for most
            // scalars but emits "--- " (with space) for some edge cases
            // (e.g. empty strings). Defensively handle "---\n" too.
            if (raw.StartsWith("--- ", StringComparison.Ordinal))
            {
                raw = raw.Substring(4);
            }
            else if (raw.StartsWith("---\n", StringComparison.Ordinal))
            {
                raw = raw.Substring(4);
            }
            raw = raw.TrimEnd('\n');
            const string DocEndMarker = "\n...";
            if (raw.EndsWith(DocEndMarker, StringComparison.Ordinal))
            {
                raw = raw.Substring(0, raw.Length - DocEndMarker.Length);
            }
            return raw.TrimEnd('\n');
        }
    }
}
