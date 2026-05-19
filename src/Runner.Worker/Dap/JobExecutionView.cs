using System;
using System.Collections.Generic;
using GitHub.Runner.Sdk;

namespace GitHub.Runner.Worker.Dap
{
    /// <summary>
    /// Stateful, append-only container that wraps <see cref="JobExecutionViewRenderer"/>
    /// for runtime use. Maintains a mutable list of entries, caches the rendered YAML,
    /// and provides O(1) lookup from <see cref="IStep"/> identity to the current line
    /// in the rendered YAML where that step's <c>- step:</c> key appears.
    ///
    /// Each <see cref="Append"/> can register the entry in one of three modes:
    ///   - With a non-null <c>stepIdentity</c>: registers the IStep→line mapping
    ///     immediately. Used for entries whose real <see cref="IStep"/> is already
    ///     known at append time.
    ///   - With a non-null <c>matchKey</c>: registers an unclaimed placeholder
    ///     that a later <see cref="TryClaim"/> binds to a real <see cref="IStep"/>.
    ///     Used for entries whose <see cref="IStep"/> is materialized later. A
    ///     placeholder that is never claimed simply stays in the view and is never
    ///     paused on — the IStep→line mapping is only populated on claim.
    ///   - With neither: a static entry that needs no line lookup.
    ///
    /// <see cref="Append"/> and <see cref="AppendRange"/> never remove or reorder
    /// existing entries. <see cref="TryClaim"/> does not re-render. The IStep→line
    /// mapping is rebuilt on every render, so lookups stay accurate even if a later
    /// Append happens to shift previously-emitted entries.
    /// </summary>
    internal sealed class JobExecutionView
    {
        private readonly object _lock = new();
        private readonly string _jobId;
        private readonly List<JobExecutionViewEntry> _entries = new();
        private readonly List<IStep> _stepIdentities = new();
        private readonly Dictionary<IStep, int> _lineByStep =
            new(ReferenceEqualityComparer.Instance);
        // Map matchKey -> entry index for placeholders awaiting a future
        // TryClaim. Removed when claimed.
        private readonly Dictionary<string, int> _unclaimedByKey =
            new(StringComparer.Ordinal);
        private string _yaml;
        private IReadOnlyList<int> _entryStartLines = Array.Empty<int>();

        public JobExecutionView(string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                throw new ArgumentException("jobId must not be null or whitespace.", nameof(jobId));
            }

            _jobId = jobId;
            Render();
        }

        public string JobId
        {
            get { return _jobId; }
        }

        /// <summary>
        /// Currently rendered YAML. Always reflects all entries appended so far,
        /// plus the synthetic Setup header and Cleanup footer emitted by the renderer.
        /// </summary>
        public string Yaml
        {
            get
            {
                lock (_lock)
                {
                    return _yaml;
                }
            }
        }

        /// <summary>Number of entries (excludes synthetic Setup/Cleanup boundaries).</summary>
        public int EntryCount
        {
            get
            {
                lock (_lock)
                {
                    return _entries.Count;
                }
            }
        }

        /// <summary>
        /// 1-based line where entry <paramref name="entryIndex"/>'s <c>- step:</c> key
        /// currently appears in <see cref="Yaml"/>.
        /// </summary>
        public int GetLine(int entryIndex)
        {
            lock (_lock)
            {
                if (entryIndex < 0 || entryIndex >= _entries.Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(entryIndex));
                }

                return _entryStartLines[entryIndex];
            }
        }

        /// <summary>
        /// 1-based line for the entry whose <see cref="IStep"/> reference identity
        /// matches <paramref name="step"/>. Returns null if <paramref name="step"/>
        /// is null or has not been registered.
        /// </summary>
        public int? TryGetLineForStep(IStep step)
        {
            if (step == null)
            {
                return null;
            }

            lock (_lock)
            {
                if (_lineByStep.TryGetValue(step, out var line))
                {
                    return line;
                }

                return null;
            }
        }

        /// <summary>
        /// Append a new entry. Exactly one of <paramref name="stepIdentity"/>
        /// or <paramref name="matchKey"/> may be non-null (or both may be
        /// null for a static entry that needs no line lookup):
        ///   - <paramref name="stepIdentity"/> non-null: registers the
        ///     IStep→line mapping immediately. Use when the real
        ///     <see cref="IStep"/> is known at append time.
        ///   - <paramref name="matchKey"/> non-null: registers an unclaimed
        ///     placeholder that a later <see cref="TryClaim"/> binds to a
        ///     real <see cref="IStep"/>.
        /// Re-renders the YAML and updates the start-line table.
        /// </summary>
        /// <returns>1-based line number of the newly-appended entry's <c>- step:</c> key.</returns>
        public int Append(JobExecutionViewEntry entry, IStep stepIdentity = null, string matchKey = null)
        {
            ArgUtil.NotNull(entry, nameof(entry));
            if (stepIdentity != null && matchKey != null)
            {
                throw new ArgumentException(
                    "Append cannot register both a step identity and a placeholder match key on the same entry; pass at most one.");
            }

            lock (_lock)
            {
                if (stepIdentity != null && _lineByStep.ContainsKey(stepIdentity))
                {
                    throw new InvalidOperationException("step already registered in execution view");
                }
                if (matchKey != null && _unclaimedByKey.ContainsKey(matchKey))
                {
                    throw new InvalidOperationException($"matchKey already registered: {matchKey}");
                }

                _entries.Add(entry);
                _stepIdentities.Add(stepIdentity);
                Render();

                int index = _entries.Count - 1;
                if (matchKey != null)
                {
                    _unclaimedByKey[matchKey] = index;
                }
                return _entryStartLines[index];
            }
        }

        /// <summary>
        /// Bind a previously-appended placeholder entry (registered via
        /// <see cref="Append(JobExecutionViewEntry, IStep, string)"/> with
        /// a non-null <c>matchKey</c>) to a real <see cref="IStep"/>.
        /// Returns the 1-based line of the now-claimed entry on success.
        /// Returns null when no unclaimed placeholder exists for
        /// <paramref name="matchKey"/>, OR when <paramref name="stepIdentity"/>
        /// is already registered for a different entry (defensive).
        /// Does not re-render: claim only updates the IStep -> line index.
        /// </summary>
        public int? TryClaim(string matchKey, IStep stepIdentity)
        {
            if (matchKey == null)
            {
                throw new ArgumentNullException(nameof(matchKey));
            }
            if (stepIdentity == null)
            {
                throw new ArgumentNullException(nameof(stepIdentity));
            }

            lock (_lock)
            {
                if (!_unclaimedByKey.TryGetValue(matchKey, out int index))
                {
                    return null;
                }
                if (_lineByStep.ContainsKey(stepIdentity))
                {
                    // Bail rather than double-register the step.
                    return null;
                }

                _unclaimedByKey.Remove(matchKey);
                _stepIdentities[index] = stepIdentity;
                _lineByStep[stepIdentity] = _entryStartLines[index];
                return _entryStartLines[index];
            }
        }

        /// <summary>
        /// Bulk-append for the initial population. Equivalent to calling
        /// <see cref="Append"/> once per pair, but renders only once at the end.
        /// State is left unchanged if any input is invalid.
        /// </summary>
        public void AppendRange(IEnumerable<(JobExecutionViewEntry entry, IStep stepIdentity)> items)
        {
            ArgUtil.NotNull(items, nameof(items));

            // Materialize first so we don't enumerate twice.
            var materialized = new List<(JobExecutionViewEntry entry, IStep stepIdentity)>(items);
            for (int i = 0; i < materialized.Count; i++)
            {
                if (materialized[i].entry == null)
                {
                    throw new ArgumentException($"items[{i}].entry is null.", nameof(items));
                }
            }

            lock (_lock)
            {
                // Validate no duplicates within the input or with existing identities,
                // before mutating state.
                var seen = new HashSet<IStep>(ReferenceEqualityComparer.Instance);
                foreach (var (_, stepIdentity) in materialized)
                {
                    if (stepIdentity == null)
                    {
                        continue;
                    }
                    if (_lineByStep.ContainsKey(stepIdentity) || !seen.Add(stepIdentity))
                    {
                        throw new InvalidOperationException("step already registered in execution view");
                    }
                }

                foreach (var (entry, stepIdentity) in materialized)
                {
                    _entries.Add(entry);
                    _stepIdentities.Add(stepIdentity);
                }
                Render();
            }
        }

        // Caller MUST hold _lock (constructor's call is safe — no concurrent access yet).
        private void Render()
        {
            var result = JobExecutionViewRenderer.Render(_jobId, _entries.AsReadOnly());
            _yaml = result.Yaml;
            _entryStartLines = result.EntryStartLines;

            _lineByStep.Clear();
            for (int i = 0; i < _stepIdentities.Count; i++)
            {
                var step = _stepIdentities[i];
                if (step != null)
                {
                    _lineByStep[step] = _entryStartLines[i];
                }
            }
        }
    }
}
