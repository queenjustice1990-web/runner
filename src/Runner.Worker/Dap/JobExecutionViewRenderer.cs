using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using GitHub.Runner.Sdk;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace GitHub.Runner.Worker.Dap
{
    /// <summary>
    /// Phase a step occupies in the runner's flat execution sequence.
    /// Setup and Cleanup are NOT modeled here — they are synthetic
    /// boundaries hard-coded by <see cref="JobExecutionViewRenderer"/>
    /// and cannot be constructed by callers.
    /// </summary>
    internal enum JobExecutionPhase
    {
        Pre,
        Main,
        Post,
    }

    /// <summary>
    /// One step in the rendered execution view. Pure data; no link to
    /// any worker type. Phase 2 will translate runner step objects
    /// into instances of this record.
    /// </summary>
    internal sealed class JobExecutionViewEntry
    {
        public JobExecutionViewEntry(
            JobExecutionPhase phase,
            string displayName,
            string uses = null,
            string run = null,
            string sourcePath = null,
            int sourceLine = 0,
            string id = null,
            string @if = null,
            string continueOnError = null,
            string timeoutMinutes = null,
            string envYaml = null,
            string withYaml = null,
            string shell = null,
            string workingDirectory = null)
        {
            if (string.IsNullOrWhiteSpace(displayName))
            {
                throw new ArgumentException("displayName must not be null or whitespace.", nameof(displayName));
            }
            if (sourcePath != null && sourceLine < 1)
            {
                throw new ArgumentException(
                    "sourceLine must be >= 1 when sourcePath is provided.",
                    nameof(sourceLine));
            }

            Phase = phase;
            DisplayName = displayName;
            Uses = uses;
            Run = run;
            SourcePath = sourcePath;
            SourceLine = sourceLine;
            Id = id;
            If = @if;
            ContinueOnError = continueOnError;
            TimeoutMinutes = timeoutMinutes;
            EnvYaml = envYaml;
            WithYaml = withYaml;
            Shell = shell;
            WorkingDirectory = workingDirectory;
        }

        public JobExecutionPhase Phase { get; }
        public string DisplayName { get; }
        public string Uses { get; }
        public string Run { get; }
        public string SourcePath { get; }
        public int SourceLine { get; }
        public string Id { get; }
        public string If { get; }
        public string ContinueOnError { get; }
        public string TimeoutMinutes { get; }
        // Pre-serialized YAML fragment, already indented for embedding
        // under the entry's `env:` key (6-space child indent).
        public string EnvYaml { get; }
        public string WithYaml { get; }
        public string Shell { get; }
        public string WorkingDirectory { get; }

        /// <summary>
        /// Set when the corresponding step was skipped (e.g. predicted Post
        /// placeholder for a Main step that never executed because its
        /// <c>if:</c> evaluated false). Rendered as an inline YAML comment
        /// on the entry's <c>- step:</c> line so subsequent entry line
        /// numbers stay stable.
        /// </summary>
        public bool IsSkipped { get; internal set; }
    }

    /// <summary>
    /// Output of <see cref="JobExecutionViewRenderer.Render"/>: the YAML
    /// document plus a parallel array of 1-based line numbers, one per
    /// input entry, where each entry's <c>- step:</c> key appears.
    /// Synthetic Setup/Cleanup boundaries are not tracked here.
    /// </summary>
    internal readonly struct RenderResult
    {
        public RenderResult(string yaml, IReadOnlyList<int> entryStartLines)
        {
            Yaml = yaml;
            EntryStartLines = entryStartLines;
        }

        public string Yaml { get; }
        public IReadOnlyList<int> EntryStartLines { get; }
    }

    /// <summary>
    /// Renders a job's execution-view YAML. Pure function; no I/O,
    /// no logging, no static state. Output format and Setup/Cleanup
    /// boundaries are fixed; callers cannot influence them.
    ///
    /// Output is structured as phase-keyed top-level sections:
    /// <c>setup:</c>, <c>pre:</c>, <c>main:</c>, <c>post:</c>, <c>cleanup:</c>.
    /// <c>setup:</c> and <c>cleanup:</c> always render; <c>pre:</c>,
    /// <c>main:</c>, <c>post:</c> only render when they contain at least
    /// one entry.
    /// </summary>
    internal static class JobExecutionViewRenderer
    {
        public static RenderResult Render(string jobId, IReadOnlyList<JobExecutionViewEntry> entries)
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                throw new ArgumentException("jobId must not be null or whitespace.", nameof(jobId));
            }
            ArgUtil.NotNull(entries, nameof(entries));

            // Pre-validate non-null entries before any output, so partial
            // state is never observed by callers.
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i] == null)
                {
                    throw new ArgumentException($"entries[{i}] is null.", nameof(entries));
                }
            }

            var sb = new StringBuilder();
            var startLines = new int[entries.Count];
            int newlinesEmitted = 0;

            // Header (3 lines).
            sb.Append("# Job: ").Append(FormatScalar(jobId)).Append('\n');
            sb.Append("# Runner execution plan — read-only.\n");
            sb.Append('\n');
            newlinesEmitted += 3;

            // setup: section — always present.
            sb.Append("setup:\n");
            sb.Append("  - step: Setup job\n");
            newlinesEmitted += 2;

            // Render phase sections in fixed order. Each emits a leading
            // blank line separator before its header.
            EmitPhaseSection(sb, "pre", JobExecutionPhase.Pre, entries, startLines, ref newlinesEmitted);
            EmitPhaseSection(sb, "main", JobExecutionPhase.Main, entries, startLines, ref newlinesEmitted);
            EmitPhaseSection(sb, "post", JobExecutionPhase.Post, entries, startLines, ref newlinesEmitted);

            // cleanup: section — always present, preceded by a blank line.
            sb.Append('\n');
            sb.Append("cleanup:\n");
            sb.Append("  - step: Complete job\n");

            return new RenderResult(sb.ToString(), Array.AsReadOnly(startLines));
        }

        private static void EmitPhaseSection(
            StringBuilder sb,
            string sectionName,
            JobExecutionPhase phase,
            IReadOnlyList<JobExecutionViewEntry> entries,
            int[] startLines,
            ref int newlinesEmitted)
        {
            // Skip the section entirely if no entries belong to this phase.
            bool any = false;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].Phase == phase) { any = true; break; }
            }
            if (!any)
            {
                return;
            }

            // Blank line separator + section header.
            sb.Append('\n');
            sb.Append(sectionName).Append(":\n");
            newlinesEmitted += 2;

            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry.Phase != phase)
                {
                    continue;
                }

                // 1-based line of the `- step:` key for this entry.
                startLines[i] = newlinesEmitted + 1;

                sb.Append("  - step: ").Append(FormatScalar(entry.DisplayName));
                if (entry.IsSkipped)
                {
                    // Inline comment — keeps following entry line numbers stable.
                    sb.Append("  # (skipped — main step did not execute)");
                }
                sb.Append('\n');
                newlinesEmitted++;

                switch (phase)
                {
                    case JobExecutionPhase.Pre:
                    case JobExecutionPhase.Post:
                        if (!string.IsNullOrEmpty(entry.Uses))
                        {
                            sb.Append("    action: ").Append(FormatScalar(entry.Uses)).Append('\n');
                            newlinesEmitted++;
                        }
                        // No source: annotation for pre/post.
                        break;

                    case JobExecutionPhase.Main:
                        if (!string.IsNullOrEmpty(entry.Id))
                        {
                            sb.Append("    id: ").Append(FormatScalar(entry.Id)).Append('\n');
                            newlinesEmitted++;
                        }
                        if (!string.IsNullOrEmpty(entry.Uses))
                        {
                            sb.Append("    uses: ").Append(FormatScalar(entry.Uses)).Append('\n');
                            newlinesEmitted++;
                        }
                        if (!string.IsNullOrEmpty(entry.Run))
                        {
                            if (entry.Run.IndexOf('\n') < 0)
                            {
                                sb.Append("    run: ").Append(FormatScalar(entry.Run)).Append('\n');
                                newlinesEmitted++;
                            }
                            else
                            {
                                sb.Append("    run: |\n");
                                newlinesEmitted++;
                                newlinesEmitted += AppendIndentedBlock(sb, entry.Run, "      ");
                            }
                        }
                        if (!string.IsNullOrEmpty(entry.If))
                        {
                            sb.Append("    if: ").Append(FormatScalar(entry.If)).Append('\n');
                            newlinesEmitted++;
                        }
                        if (!string.IsNullOrEmpty(entry.ContinueOnError))
                        {
                            sb.Append("    continue-on-error: ").Append(entry.ContinueOnError).Append('\n');
                            newlinesEmitted++;
                        }
                        if (!string.IsNullOrEmpty(entry.TimeoutMinutes))
                        {
                            sb.Append("    timeout-minutes: ").Append(entry.TimeoutMinutes).Append('\n');
                            newlinesEmitted++;
                        }
                        if (!string.IsNullOrEmpty(entry.EnvYaml))
                        {
                            sb.Append("    env:\n");
                            newlinesEmitted++;
                            sb.Append(entry.EnvYaml).Append('\n');
                            newlinesEmitted += CountChar(entry.EnvYaml, '\n') + 1;
                        }
                        if (!string.IsNullOrEmpty(entry.WithYaml))
                        {
                            sb.Append("    with:\n");
                            newlinesEmitted++;
                            sb.Append(entry.WithYaml).Append('\n');
                            newlinesEmitted += CountChar(entry.WithYaml, '\n') + 1;
                        }
                        if (!string.IsNullOrEmpty(entry.Shell))
                        {
                            sb.Append("    shell: ").Append(FormatScalar(entry.Shell)).Append('\n');
                            newlinesEmitted++;
                        }
                        if (!string.IsNullOrEmpty(entry.WorkingDirectory))
                        {
                            sb.Append("    working-directory: ").Append(FormatScalar(entry.WorkingDirectory)).Append('\n');
                            newlinesEmitted++;
                        }
                        if (entry.SourcePath != null)
                        {
                            sb.Append("    source: ")
                              .Append(entry.SourcePath)
                              .Append(':')
                              .Append(entry.SourceLine.ToString(CultureInfo.InvariantCulture))
                              .Append('\n');
                            newlinesEmitted++;
                        }
                        break;
                }
            }
        }

        private static int AppendIndentedBlock(StringBuilder sb, string text, string indent)
        {
            int newlines = 0;
            int i = 0;
            while (i < text.Length)
            {
                int end = text.IndexOf('\n', i);
                int lineEnd = end < 0 ? text.Length : end;
                int trimEnd = lineEnd;
                if (trimEnd > i && text[trimEnd - 1] == '\r')
                {
                    trimEnd--;
                }
                if (trimEnd > i)
                {
                    sb.Append(indent);
                    sb.Append(text, i, trimEnd - i);
                }
                sb.Append('\n');
                newlines++;
                if (end < 0)
                {
                    break;
                }
                i = end + 1;
            }
            return newlines;
        }

        private static int CountChar(string s, char c)
        {
            int n = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == c) n++;
            }
            return n;
        }

        /// <summary>
        /// Formats a single string as a YAML 1.x flow scalar, delegating
        /// quoting/escaping decisions to YamlDotNet. This avoids maintaining
        /// our own escape table for every YAML-significant character: we
        /// just emit the value through the YAML library and use whichever
        /// scalar style (plain, single-quoted, double-quoted) it picks.
        /// A new <see cref="Emitter"/> is created per call, so the helper
        /// is safe to invoke concurrently.
        /// </summary>
        internal static string FormatScalar(string value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            using var sw = new StringWriter(CultureInfo.InvariantCulture);
            var emitter = new Emitter(sw);
            emitter.Emit(new StreamStart());
            emitter.Emit(new DocumentStart(null, null, true));
            emitter.Emit(new Scalar(null, null, value, ScalarStyle.Any, true, true));
            emitter.Emit(new DocumentEnd(true));
            emitter.Emit(new StreamEnd());

            string raw = sw.ToString();
            if (raw.StartsWith("--- ", StringComparison.Ordinal))
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
