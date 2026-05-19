using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GitHub.Runner.Worker;
using GitHub.Runner.Worker.Dap;
using Moq;
using Xunit;

namespace GitHub.Runner.Common.Tests.Worker
{
    public sealed class JobExecutionViewL0
    {
        private static JobExecutionViewEntry MainEntry(string name)
        {
            return new JobExecutionViewEntry(JobExecutionPhase.Main, name, run: name);
        }

        private static IStep NewStep(string displayName = "step")
        {
            var mock = new Mock<IStep>();
            mock.Setup(s => s.DisplayName).Returns(displayName);
            return mock.Object;
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Constructor_RendersEmptyView()
        {
            var view = new JobExecutionView("my-job");

            Assert.Equal(0, view.EntryCount);
            Assert.Contains("# Job: my-job", view.Yaml);
            Assert.Contains("- step: Setup job", view.Yaml);
            Assert.Contains("- step: Complete job", view.Yaml);

            // Only the two synthetic boundaries appear.
            int stepCount = view.Yaml.Split("- step: ").Length - 1;
            Assert.Equal(2, stepCount);
        }

        [Theory]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Constructor_ThrowsOnInvalidJobId(string jobId)
        {
            Assert.Throws<ArgumentException>(() => new JobExecutionView(jobId));
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Append_IncrementsEntryCount()
        {
            var view = new JobExecutionView("j");

            int line0 = view.Append(MainEntry("a"));
            int line1 = view.Append(MainEntry("b"));
            int line2 = view.Append(MainEntry("c"));

            Assert.Equal(3, view.EntryCount);
            Assert.True(line0 < line1);
            Assert.True(line1 < line2);
            Assert.Equal(line0, view.GetLine(0));
            Assert.Equal(line1, view.GetLine(1));
            Assert.Equal(line2, view.GetLine(2));
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Append_PreservesPriorEntryLines()
        {
            var view = new JobExecutionView("j");

            int l0 = view.Append(MainEntry("a"));
            int l1 = view.Append(MainEntry("b"));
            int l2 = view.Append(MainEntry("c"));

            view.Append(MainEntry("d"));
            Assert.Equal(l0, view.GetLine(0));
            Assert.Equal(l1, view.GetLine(1));
            Assert.Equal(l2, view.GetLine(2));

            view.Append(MainEntry("e"));
            Assert.Equal(l0, view.GetLine(0));
            Assert.Equal(l1, view.GetLine(1));
            Assert.Equal(l2, view.GetLine(2));
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Append_RegistersStepIdentity()
        {
            var view = new JobExecutionView("j");
            var step = NewStep();

            int line = view.Append(MainEntry("a"), step);

            Assert.Equal(line, view.GetLine(0));
            Assert.Equal(line, view.TryGetLineForStep(step));
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Append_NullStepIdentity_StillAppends()
        {
            var view = new JobExecutionView("j");

            view.Append(MainEntry("a"), stepIdentity: null);

            Assert.Equal(1, view.EntryCount);
            Assert.Null(view.TryGetLineForStep(null));
            Assert.Contains("- step: a", view.Yaml);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Append_DuplicateStepIdentity_Throws()
        {
            var view = new JobExecutionView("j");
            var step = NewStep();

            view.Append(MainEntry("a"), step);
            Assert.Throws<InvalidOperationException>(() => view.Append(MainEntry("b"), step));

            // State preserved: only the first entry is present.
            Assert.Equal(1, view.EntryCount);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Append_NullEntry_Throws()
        {
            var view = new JobExecutionView("j");
            Assert.Throws<ArgumentNullException>(() => view.Append(null));
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void AppendRange_AppendsAllAndRendersOnce()
        {
            var view = new JobExecutionView("j");
            var steps = Enumerable.Range(0, 5).Select(i => NewStep("s" + i)).ToList();
            var items = steps
                .Select((s, i) => (entry: MainEntry("e" + i), stepIdentity: s))
                .ToList();

            view.AppendRange(items);

            Assert.Equal(5, view.EntryCount);
            for (int i = 0; i < 5; i++)
            {
                int line = view.GetLine(i);
                Assert.Equal(line, view.TryGetLineForStep(steps[i]));
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void AppendRange_RejectsDuplicateInInput()
        {
            var view = new JobExecutionView("j");
            var dup = NewStep();
            var items = new List<(JobExecutionViewEntry, IStep)>
            {
                (MainEntry("a"), dup),
                (MainEntry("b"), dup),
            };

            Assert.Throws<InvalidOperationException>(() => view.AppendRange(items));
            Assert.Equal(0, view.EntryCount);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void AppendRange_RejectsOverlapWithExisting()
        {
            var view = new JobExecutionView("j");
            var step = NewStep();
            view.Append(MainEntry("a"), step);

            var items = new List<(JobExecutionViewEntry, IStep)>
            {
                (MainEntry("b"), step),
            };

            Assert.Throws<InvalidOperationException>(() => view.AppendRange(items));
            Assert.Equal(1, view.EntryCount);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void AppendRange_NullItems_Throws()
        {
            var view = new JobExecutionView("j");
            Assert.Throws<ArgumentNullException>(() => view.AppendRange(null));
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TryGetLineForStep_NullStep_ReturnsNull()
        {
            var view = new JobExecutionView("j");
            Assert.Null(view.TryGetLineForStep(null));
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TryGetLineForStep_UnknownStep_ReturnsNull()
        {
            var view = new JobExecutionView("j");
            var step = NewStep();
            Assert.Null(view.TryGetLineForStep(step));
        }

        [Theory]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        [InlineData(-1)]
        [InlineData(2)]
        public void GetLine_OutOfRange_Throws(int index)
        {
            var view = new JobExecutionView("j");
            view.Append(MainEntry("a"));
            view.Append(MainEntry("b"));

            Assert.Throws<ArgumentOutOfRangeException>(() => view.GetLine(index));
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Yaml_UpdatesAfterAppend()
        {
            var view = new JobExecutionView("j");
            view.Append(MainEntry("first"));
            string before = view.Yaml;
            Assert.Contains("- step: first", before);

            view.Append(MainEntry("second"));
            string after = view.Yaml;

            Assert.Contains("- step: first", after);
            Assert.Contains("- step: second", after);
            Assert.NotEqual(before, after);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Yaml_AlwaysEndsWithCleanupBoundary()
        {
            var view = new JobExecutionView("j");
            Assert.EndsWith("cleanup:\n  - step: Complete job\n", view.Yaml);

            view.Append(MainEntry("a"));
            Assert.EndsWith("cleanup:\n  - step: Complete job\n", view.Yaml);

            view.Append(MainEntry("b"));
            view.Append(MainEntry("c"));
            Assert.EndsWith("cleanup:\n  - step: Complete job\n", view.Yaml);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Append_WithMatchKey_TracksUnclaimed()
        {
            var view = new JobExecutionView("j");

            int line = view.Append(MainEntry("placeholder"), stepIdentity: null, matchKey: "k1");

            var step = NewStep("real");
            int? claimed = view.TryClaim("k1", step);
            Assert.Equal(line, claimed);
            Assert.Equal(line, view.TryGetLineForStep(step));
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TryClaim_UnknownKey_ReturnsNull()
        {
            var view = new JobExecutionView("j");
            view.Append(MainEntry("a"), stepIdentity: null, matchKey: "k1");

            Assert.Null(view.TryClaim("nope", NewStep()));
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TryClaim_AlreadyClaimed_ReturnsNull()
        {
            var view = new JobExecutionView("j");
            view.Append(MainEntry("a"), stepIdentity: null, matchKey: "k1");

            var first = NewStep("first");
            Assert.NotNull(view.TryClaim("k1", first));

            var second = NewStep("second");
            Assert.Null(view.TryClaim("k1", second));
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TryClaim_StepAlreadyRegistered_ReturnsNull()
        {
            var view = new JobExecutionView("j");
            var step = NewStep();
            // Step is registered for the first entry.
            view.Append(MainEntry("a"), step);
            // A placeholder is registered for the second entry.
            view.Append(MainEntry("b"), stepIdentity: null, matchKey: "k1");

            // Trying to claim the placeholder with the already-registered
            // step must return null (defensive — would otherwise double-bind).
            Assert.Null(view.TryClaim("k1", step));
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Append_DuplicateMatchKey_Throws()
        {
            var view = new JobExecutionView("j");
            view.Append(MainEntry("a"), stepIdentity: null, matchKey: "k1");

            Assert.Throws<InvalidOperationException>(
                () => view.Append(MainEntry("b"), stepIdentity: null, matchKey: "k1"));
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Append_MatchKeyNull_BehavesLikeOldOverload()
        {
            var view = new JobExecutionView("j");
            var step = NewStep();

            int line = view.Append(MainEntry("a"), step);

            Assert.Equal(line, view.GetLine(0));
            Assert.Equal(line, view.TryGetLineForStep(step));
            // TryClaim with any key must return null since no matchKey was registered.
            Assert.Null(view.TryClaim("anything", NewStep()));
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TryClaim_AfterClaim_TryGetLineForStepResolves()
        {
            var view = new JobExecutionView("j");
            int line = view.Append(MainEntry("placeholder"), stepIdentity: null, matchKey: "k1");

            var step = NewStep();
            Assert.Equal(line, view.TryClaim("k1", step));
            Assert.Equal(line, view.TryGetLineForStep(step));

            // And a later Append doesn't lose the claim (Render rebuilds
            // the IStep -> line map from the persisted identities).
            view.Append(MainEntry("b"));
            Assert.Equal(line, view.TryGetLineForStep(step));
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TryClaim_NullArgs_Throws()
        {
            var view = new JobExecutionView("j");
            Assert.Throws<ArgumentNullException>(() => view.TryClaim(null, NewStep()));
            Assert.Throws<ArgumentNullException>(() => view.TryClaim("k", null));
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async Task ConcurrentAppends_DontCorruptState()
        {
            var view = new JobExecutionView("j");
            const int N = 50;
            var steps = Enumerable.Range(0, N).Select(i => NewStep("s" + i)).ToList();
            var returnedLines = new ConcurrentBag<int>();

            var tasks = Enumerable.Range(0, N).Select(i => Task.Run(() =>
            {
                int line = view.Append(MainEntry("e" + i), steps[i]);
                returnedLines.Add(line);
            })).ToArray();

            await Task.WhenAll(tasks);

            Assert.Equal(N, view.EntryCount);
            Assert.Equal(N, returnedLines.Distinct().Count());

            // Every step identity resolves to some line in [0, N).
            var entryLines = Enumerable.Range(0, N).Select(view.GetLine).ToHashSet();
            Assert.Equal(N, entryLines.Count);
            foreach (var step in steps)
            {
                int? line = view.TryGetLineForStep(step);
                Assert.NotNull(line);
                Assert.Contains(line.Value, entryLines);
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Append_RejectsBothStepIdentityAndMatchKey()
        {
            // Allowing both would orphan the IStep→line mapping the moment
            // TryClaim overwrites _stepIdentities[index] for a different
            // step, so the API rejects the combination at append time.
            var view = new JobExecutionView("j");
            var entry = new JobExecutionViewEntry(JobExecutionPhase.Post, "Post X", uses: "actions/x@v1");
            Assert.Throws<ArgumentException>(() =>
                view.Append(entry, stepIdentity: NewStep("real"), matchKey: "k1"));
            // State unchanged.
            Assert.Equal(0, view.EntryCount);
        }
    }
}
