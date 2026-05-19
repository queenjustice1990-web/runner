using System;
using GitHub.DistributedTask.ObjectTemplating.Tokens;
using GitHub.DistributedTask.Pipelines;
using GitHub.Runner.Worker;
using GitHub.Runner.Worker.Dap;
using Moq;
using Xunit;

namespace GitHub.Runner.Common.Tests.Worker
{
    public sealed class StepEntryTranslatorL0
    {
        private static StringToken Str(string s) => new(null, null, null, s);

        private static MappingToken Map(params (string Key, TemplateToken Value)[] pairs)
        {
            var m = new MappingToken(null, null, null);
            foreach (var (k, v) in pairs)
            {
                m.Add(Str(k), v);
            }
            return m;
        }

        private static Mock<IActionRunner> NewActionRunnerMock(
            ActionRunStage stage,
            string displayName,
            ActionStepDefinitionReference reference,
            ActionStep actionOverride = null)
        {
            var mock = new Mock<IActionRunner>();
            mock.SetupGet(x => x.Stage).Returns(stage);
            mock.SetupGet(x => x.DisplayName).Returns(displayName);
            mock.SetupGet(x => x.Action).Returns(actionOverride ?? new ActionStep
            {
                Reference = reference,
            });
            return mock;
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Translate_NullStep_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                StepEntryTranslator.TryTranslate(null));
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Translate_JobExtensionRunner_ReturnsNull()
        {
            var step = new JobExtensionRunner(
                runAsync: (_, __) => System.Threading.Tasks.Task.CompletedTask,
                condition: null,
                displayName: "Set up job",
                data: null);

            Assert.Null(StepEntryTranslator.TryTranslate(step));
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Translate_OtherIStepType_ReturnsNull()
        {
            var mock = new Mock<IStep>();
            mock.SetupGet(x => x.DisplayName).Returns("custom");

            Assert.Null(StepEntryTranslator.TryTranslate(mock.Object));
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Translate_ActionRunnerPre_ReturnsPreEntry()
        {
            var reference = new RepositoryPathReference
            {
                Name = "actions/checkout",
                Ref = "v4",
            };
            var mock = NewActionRunnerMock(ActionRunStage.Pre, "Pre Run actions/checkout@v4", reference);

            var entry = StepEntryTranslator.TryTranslate(mock.Object);

            Assert.NotNull(entry);
            Assert.Equal(JobExecutionPhase.Pre, entry.Phase);
            Assert.Equal("Pre Run actions/checkout@v4", entry.DisplayName);
            Assert.Equal("actions/checkout@v4", entry.Uses);
            Assert.Null(entry.Run);
            Assert.Null(entry.SourcePath);
            Assert.Equal(0, entry.SourceLine);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Translate_ActionRunnerMain_ReturnsMainEntryWithUses()
        {
            var reference = new RepositoryPathReference
            {
                Name = "actions/setup-node",
                Path = "subdir",
                Ref = "v3",
            };
            var mock = NewActionRunnerMock(ActionRunStage.Main, "Run actions/setup-node@v3", reference);

            var entry = StepEntryTranslator.TryTranslate(mock.Object);

            Assert.NotNull(entry);
            Assert.Equal(JobExecutionPhase.Main, entry.Phase);
            Assert.Equal("actions/setup-node/subdir@v3", entry.Uses);
            Assert.Null(entry.Run);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Translate_ActionRunnerMain_ScriptReference_LeavesUsesNull()
        {
            var mock = NewActionRunnerMock(ActionRunStage.Main, "Run echo hi", new ScriptReference());

            var entry = StepEntryTranslator.TryTranslate(mock.Object);

            Assert.NotNull(entry);
            Assert.Equal(JobExecutionPhase.Main, entry.Phase);
            Assert.Null(entry.Uses);
            Assert.Null(entry.Run);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Translate_ActionRunnerMain_ContainerReference_UsesImage()
        {
            var reference = new ContainerRegistryReference { Image = "alpine:3.18" };
            var mock = NewActionRunnerMock(ActionRunStage.Main, "Run alpine", reference);

            var entry = StepEntryTranslator.TryTranslate(mock.Object);

            Assert.NotNull(entry);
            Assert.Equal("alpine:3.18", entry.Uses);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Translate_ActionRunnerPost_ReturnsPostEntry()
        {
            var reference = new RepositoryPathReference { Name = "actions/cache", Ref = "v3" };
            var mock = NewActionRunnerMock(ActionRunStage.Post, "Post Run actions/cache@v3", reference);

            var entry = StepEntryTranslator.TryTranslate(mock.Object);

            Assert.NotNull(entry);
            Assert.Equal(JobExecutionPhase.Post, entry.Phase);
            Assert.Equal("Post Run actions/cache@v3", entry.DisplayName);
            Assert.Equal("actions/cache@v3", entry.Uses);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Translate_ActionRunner_NullAction_LeavesUsesNull()
        {
            var mock = new Mock<IActionRunner>();
            mock.SetupGet(x => x.Stage).Returns(ActionRunStage.Main);
            mock.SetupGet(x => x.DisplayName).Returns("anonymous");
            mock.SetupGet(x => x.Action).Returns((ActionStep)null);

            var entry = StepEntryTranslator.TryTranslate(mock.Object);

            Assert.NotNull(entry);
            Assert.Equal("anonymous", entry.DisplayName);
            Assert.Null(entry.Uses);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Translate_ActionStep_ExtractsWith()
        {
            var reference = new RepositoryPathReference { Name = "actions/cache", Ref = "v5" };
            var action = new ActionStep
            {
                Reference = reference,
                Inputs = Map(("path", Str("prime-numbers")), ("key", Str("k"))),
            };
            var mock = NewActionRunnerMock(ActionRunStage.Main, "Cache", reference, action);

            var entry = StepEntryTranslator.TryTranslate(mock.Object);

            Assert.NotNull(entry);
            Assert.NotNull(entry.WithYaml);
            Assert.Contains("path: prime-numbers", entry.WithYaml);
            Assert.Contains("key: k", entry.WithYaml);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Translate_ActionStep_PreservesExpressionInWith()
        {
            var reference = new RepositoryPathReference { Name = "actions/cache", Ref = "v5" };
            var action = new ActionStep
            {
                Reference = reference,
                Inputs = Map(("key", Str("${{ runner.os }}-primes"))),
            };
            var mock = NewActionRunnerMock(ActionRunStage.Main, "Cache", reference, action);

            var entry = StepEntryTranslator.TryTranslate(mock.Object);

            Assert.NotNull(entry);
            Assert.Contains("${{ runner.os }}-primes", entry.WithYaml);
            Assert.DoesNotContain("Linux", entry.WithYaml);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Translate_RunStep_ExtractsScript()
        {
            var action = new ActionStep
            {
                Reference = new ScriptReference(),
                Inputs = Map(("script", Str("echo hi"))),
            };
            var mock = NewActionRunnerMock(ActionRunStage.Main, "Run echo", new ScriptReference(), action);

            var entry = StepEntryTranslator.TryTranslate(mock.Object);

            Assert.NotNull(entry);
            Assert.Null(entry.Uses);
            Assert.Equal("echo hi", entry.Run);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Translate_RunStep_ExtractsShellAndWorkingDirectory()
        {
            // The runner stores run-step inputs under the keys defined in
            // PipelineConstants.ScriptStepInputs (camelCase), NOT their
            // kebab-case workflow-YAML spellings — see
            // ActionManifestManagerWrapper:244.
            var action = new ActionStep
            {
                Reference = new ScriptReference(),
                Inputs = Map(
                    (PipelineConstants.ScriptStepInputs.Script, Str("npm test")),
                    (PipelineConstants.ScriptStepInputs.Shell, Str("bash")),
                    (PipelineConstants.ScriptStepInputs.WorkingDirectory, Str("./api"))),
            };
            var mock = NewActionRunnerMock(ActionRunStage.Main, "Run", new ScriptReference(), action);

            var entry = StepEntryTranslator.TryTranslate(mock.Object);

            Assert.NotNull(entry);
            Assert.Equal("npm test", entry.Run);
            Assert.Equal("bash", entry.Shell);
            Assert.Equal("./api", entry.WorkingDirectory);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Translate_ActionStep_FiltersRunStepKeysFromWith()
        {
            // Defensive: an action step's Inputs should not contain
            // run-step internal keys, but if it did, they must not
            // surface in the with: rendering.
            var reference = new RepositoryPathReference { Name = "a/b", Ref = "v1" };
            var action = new ActionStep
            {
                Reference = reference,
                Inputs = Map(
                    ("mode", Str("ci")),
                    (PipelineConstants.ScriptStepInputs.Script, Str("leak")),
                    (PipelineConstants.ScriptStepInputs.Shell, Str("leak")),
                    (PipelineConstants.ScriptStepInputs.WorkingDirectory, Str("leak"))),
            };
            var mock = NewActionRunnerMock(ActionRunStage.Main, "Run", reference, action);

            var entry = StepEntryTranslator.TryTranslate(mock.Object);

            Assert.NotNull(entry);
            Assert.NotNull(entry.WithYaml);
            Assert.Contains("mode: ci", entry.WithYaml);
            Assert.DoesNotContain("leak", entry.WithYaml);
            Assert.DoesNotContain(PipelineConstants.ScriptStepInputs.Script, entry.WithYaml);
            Assert.DoesNotContain(PipelineConstants.ScriptStepInputs.Shell, entry.WithYaml);
            Assert.DoesNotContain(PipelineConstants.ScriptStepInputs.WorkingDirectory, entry.WithYaml);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Translate_ActionStep_OmitsEmptyEnv()
        {
            var reference = new RepositoryPathReference { Name = "a/b", Ref = "v1" };
            var action = new ActionStep
            {
                Reference = reference,
                Environment = new MappingToken(null, null, null),
            };
            var mock = NewActionRunnerMock(ActionRunStage.Main, "Run", reference, action);

            var entry = StepEntryTranslator.TryTranslate(mock.Object);

            Assert.NotNull(entry);
            Assert.Null(entry.EnvYaml);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Translate_ActionStep_ExtractsEnv()
        {
            var reference = new RepositoryPathReference { Name = "a/b", Ref = "v1" };
            var action = new ActionStep
            {
                Reference = reference,
                Environment = Map(("NODE_ENV", Str("production"))),
            };
            var mock = NewActionRunnerMock(ActionRunStage.Main, "Run", reference, action);

            var entry = StepEntryTranslator.TryTranslate(mock.Object);

            Assert.NotNull(entry);
            Assert.NotNull(entry.EnvYaml);
            Assert.Contains("NODE_ENV: production", entry.EnvYaml);
        }

        [Theory]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("__1")]
        [InlineData("__123")]
        public void Translate_FiltersAutoGeneratedId(string contextName)
        {
            var reference = new RepositoryPathReference { Name = "a/b", Ref = "v1" };
            var action = new ActionStep
            {
                Reference = reference,
                ContextName = contextName,
            };
            var mock = NewActionRunnerMock(ActionRunStage.Main, "Run", reference, action);

            var entry = StepEntryTranslator.TryTranslate(mock.Object);

            Assert.NotNull(entry);
            Assert.Null(entry.Id);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Translate_PreservesUserId()
        {
            var reference = new RepositoryPathReference { Name = "a/b", Ref = "v1" };
            var action = new ActionStep
            {
                Reference = reference,
                ContextName = "cache-primes",
            };
            var mock = NewActionRunnerMock(ActionRunStage.Main, "Cache", reference, action);

            var entry = StepEntryTranslator.TryTranslate(mock.Object);

            Assert.NotNull(entry);
            Assert.Equal("cache-primes", entry.Id);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Translate_ActionStep_ExtractsCondition()
        {
            var reference = new RepositoryPathReference { Name = "a/b", Ref = "v1" };
            var action = new ActionStep
            {
                Reference = reference,
                Condition = "always()",
            };
            var mock = NewActionRunnerMock(ActionRunStage.Main, "Run", reference, action);

            var entry = StepEntryTranslator.TryTranslate(mock.Object);

            Assert.NotNull(entry);
            Assert.Equal("always()", entry.If);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Translate_PreEntry_OmitsUserParams()
        {
            // Pre entries stay minimal — they reference the same Action as
            // Main, and duplicating params adds noise.
            var reference = new RepositoryPathReference { Name = "a/b", Ref = "v1" };
            var action = new ActionStep
            {
                Reference = reference,
                ContextName = "user-id",
                Condition = "always()",
                Environment = Map(("X", Str("y"))),
                Inputs = Map(("k", Str("v"))),
            };
            var mock = NewActionRunnerMock(ActionRunStage.Pre, "Pre a/b@v1", reference, action);

            var entry = StepEntryTranslator.TryTranslate(mock.Object);

            Assert.NotNull(entry);
            Assert.Equal(JobExecutionPhase.Pre, entry.Phase);
            Assert.Null(entry.Id);
            Assert.Null(entry.If);
            Assert.Null(entry.EnvYaml);
            Assert.Null(entry.WithYaml);
        }
    }
}
