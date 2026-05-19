using System;
using System.Collections.Generic;
using GitHub.DistributedTask.ObjectTemplating.Tokens;
using GitHub.DistributedTask.Pipelines;
using GitHub.Runner.Sdk;

namespace GitHub.Runner.Worker.Dap
{
    /// <summary>
    /// Translates runner <see cref="IStep"/> instances into pure-data
    /// <see cref="JobExecutionViewEntry"/> records used by the DAP debugger
    /// execution view. Filters out runner-internal steps (e.g.
    /// <see cref="JobExtensionRunner"/>) so the rendered view only shows
    /// user-visible workflow steps.
    /// </summary>
    internal static class StepEntryTranslator
    {
        // Run-step internals carried on ActionStep.Inputs that are NOT
        // user-authored `with:` entries. The runner stores these under
        // the keys defined in PipelineConstants.ScriptStepInputs, NOT
        // their kebab-case workflow-YAML spellings.
        private static readonly HashSet<string> RunStepInternalKeys = new(StringComparer.Ordinal)
        {
            PipelineConstants.ScriptStepInputs.Script,
            PipelineConstants.ScriptStepInputs.Shell,
            PipelineConstants.ScriptStepInputs.WorkingDirectory,
        };

        /// <summary>
        /// Translate an IStep into a JobExecutionViewEntry.
        /// </summary>
        /// <param name="step">The IStep to translate. Must not be null.</param>
        /// <returns>
        /// A JobExecutionViewEntry, or null if the step is not user-visible
        /// (JobExtensionRunner and any other non-IActionRunner IStep impls).
        /// </returns>
        public static JobExecutionViewEntry TryTranslate(IStep step)
        {
            ArgUtil.NotNull(step, nameof(step));

            if (step is JobExtensionRunner)
            {
                return null;
            }

            if (step is not IActionRunner actionRunner)
            {
                return null;
            }

            var phase = actionRunner.Stage switch
            {
                ActionRunStage.Pre => JobExecutionPhase.Pre,
                ActionRunStage.Post => JobExecutionPhase.Post,
                _ => JobExecutionPhase.Main,
            };

            string displayName = actionRunner.DisplayName;
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = "run";
            }

            string uses = null;
            string run = null;
            string id = null;
            string ifCond = null;
            string continueOnError = null;
            string timeoutMinutes = null;
            string envYaml = null;
            string withYaml = null;
            string shell = null;
            string workingDirectory = null;

            var action = actionRunner.Action;
            var reference = action?.Reference;
            bool isScript = reference?.Type == ActionSourceType.Script;

            if (reference != null && !isScript)
            {
                uses = FormatActionReference(reference);
            }

            // Only the user-visible Main entry surfaces authored params.
            // Pre/Post stay minimal (step + action) — they reference the
            // same Action as the Main entry, and duplicating params adds
            // noise without information.
            if (phase == JobExecutionPhase.Main && action != null)
            {
                id = FilterAuthoredId(action.ContextName);

                if (!string.IsNullOrEmpty(action.Condition))
                {
                    ifCond = action.Condition;
                }

                if (action.ContinueOnError != null)
                {
                    continueOnError = TemplateTokenYamlAdapter.Serialize(action.ContinueOnError, indentSpaces: 0);
                }
                if (action.TimeoutInMinutes != null)
                {
                    timeoutMinutes = TemplateTokenYamlAdapter.Serialize(action.TimeoutInMinutes, indentSpaces: 0);
                }

                if (action.Environment is MappingToken envMap && envMap.Count > 0)
                {
                    envYaml = TemplateTokenYamlAdapter.Serialize(envMap, indentSpaces: 6);
                }
                else if (action.Environment != null && !(action.Environment is MappingToken))
                {
                    // Unusual but possible: env: ${{ ... }} expression form.
                    envYaml = TemplateTokenYamlAdapter.Serialize(action.Environment, indentSpaces: 6);
                }

                if (isScript)
                {
                    var inputs = action.Inputs as MappingToken;
                    if (inputs != null)
                    {
                        if (TryGetMapValue(inputs, PipelineConstants.ScriptStepInputs.Script, out var scriptTok) && scriptTok != null)
                        {
                            run = scriptTok.ToString();
                        }
                        if (TryGetMapValue(inputs, PipelineConstants.ScriptStepInputs.Shell, out var shellTok) && shellTok != null)
                        {
                            string shellText = shellTok.ToString();
                            if (!string.IsNullOrEmpty(shellText))
                            {
                                shell = shellText;
                            }
                        }
                        if (TryGetMapValue(inputs, PipelineConstants.ScriptStepInputs.WorkingDirectory, out var wdTok) && wdTok != null)
                        {
                            string wdText = wdTok.ToString();
                            if (!string.IsNullOrEmpty(wdText))
                            {
                                workingDirectory = wdText;
                            }
                        }
                    }
                }
                else
                {
                    // Action step: surface `with:` entries, filtering any
                    // run-step internal keys defensively.
                    if (action.Inputs is MappingToken withMap && withMap.Count > 0)
                    {
                        var filtered = FilterMapping(withMap, RunStepInternalKeys);
                        if (filtered != null && filtered.Count > 0)
                        {
                            withYaml = TemplateTokenYamlAdapter.Serialize(filtered, indentSpaces: 6);
                        }
                    }
                }
            }

            // Source annotation (SourcePath/SourceLine) requires a public
            // seam onto TemplateToken position info — not wired yet.
            return new JobExecutionViewEntry(
                phase: phase,
                displayName: displayName,
                uses: uses,
                run: run,
                sourcePath: null,
                sourceLine: 0,
                id: id,
                @if: ifCond,
                continueOnError: continueOnError,
                timeoutMinutes: timeoutMinutes,
                envYaml: envYaml,
                withYaml: withYaml,
                shell: shell,
                workingDirectory: workingDirectory);
        }

        /// <summary>
        /// Auto-generated step IDs are noise in the view: filter them out.
        /// The runner's convention (see ExecutionContext) is that auto-
        /// generated context names start with <c>__</c>. Only user-authored
        /// IDs survive the filter.
        /// </summary>
        internal static string FilterAuthoredId(string contextName)
        {
            if (string.IsNullOrWhiteSpace(contextName))
            {
                return null;
            }
            if (contextName.StartsWith("__", StringComparison.Ordinal))
            {
                return null;
            }
            return contextName;
        }

        private static bool TryGetMapValue(MappingToken map, string key, out TemplateToken value)
        {
            foreach (var pair in map)
            {
                if (pair.Key is StringToken s && string.Equals(s.Value, key, StringComparison.Ordinal))
                {
                    value = pair.Value;
                    return true;
                }
            }
            value = null;
            return false;
        }

        private static MappingToken FilterMapping(MappingToken source, HashSet<string> excludeKeys)
        {
            var copy = new MappingToken(source.FileId, source.Line, source.Column);
            foreach (var pair in source)
            {
                if (pair.Key is StringToken sk && excludeKeys.Contains(sk.Value))
                {
                    continue;
                }
                copy.Add(pair);
            }
            return copy;
        }

        internal static string FormatActionReference(ActionStepDefinitionReference reference)
        {
            switch (reference)
            {
                case RepositoryPathReference repo:
                    var path = string.IsNullOrEmpty(repo.Path) ? string.Empty : $"/{repo.Path}";
                    return string.IsNullOrEmpty(repo.Ref)
                        ? $"{repo.Name}{path}"
                        : $"{repo.Name}{path}@{repo.Ref}";
                case ContainerRegistryReference container:
                    return container.Image;
                default:
                    return reference.ToString();
            }
        }
    }
}
