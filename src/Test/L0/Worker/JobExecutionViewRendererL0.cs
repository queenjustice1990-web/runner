using System;
using System.Collections.Generic;
using System.Linq;
using GitHub.Runner.Worker.Dap;
using Xunit;

namespace GitHub.Runner.Common.Tests.Worker
{
    public sealed class JobExecutionViewRendererL0
    {
        // Verbatim expected YAML for the design doc's "Worked example".
        // The render output is structured as phase-keyed top-level sections;
        // there is no per-entry `phase:` field. The setup: and cleanup:
        // sections always render; pre:/main:/post: render only when
        // they contain at least one entry. The Main entries surface
        // user-authored step parameters pre-evaluation (no expression
        // substitution); Pre/Post entries stay minimal.
        private const string ExpectedWorkedExampleYaml =
            "# Job: build\n" +
            "# Runner execution plan — read-only.\n" +
            "\n" +
            "setup:\n" +
            "  - step: Setup job\n" +
            "\n" +
            "pre:\n" +
            "  - step: Pre actions/checkout@v4\n" +
            "    action: actions/checkout@v4\n" +
            "  - step: Pre actions/cache@v5\n" +
            "    action: actions/cache@v5\n" +
            "\n" +
            "main:\n" +
            "  - step: actions/checkout@v4\n" +
            "    uses: actions/checkout@v4\n" +
            "    source: .github/workflows/ci.yml:10\n" +
            "  - step: Cache Primes\n" +
            "    id: cache-primes\n" +
            "    uses: actions/cache@v5\n" +
            "    with:\n" +
            "      path: prime-numbers\n" +
            "      key: ${{ runner.os }}-primes\n" +
            "    source: .github/workflows/ci.yml:12\n" +
            "  - step: Run tests\n" +
            "    id: test\n" +
            "    run: |\n" +
            "      echo starting\n" +
            "      npm test\n" +
            "    if: ${{ github.event_name == 'push' }}\n" +
            "    env:\n" +
            "      NODE_ENV: production\n" +
            "    shell: bash\n" +
            "    working-directory: ./api\n" +
            "    source: .github/workflows/ci.yml:18\n" +
            "  - step: npm ci\n" +
            "    run: npm ci\n" +
            "    source: .github/workflows/ci.yml:28\n" +
            "\n" +
            "post:\n" +
            "  - step: Post actions/cache@v5\n" +
            "    action: actions/cache@v5\n" +
            "  - step: Post actions/checkout@v4\n" +
            "    action: actions/checkout@v4\n" +
            "\n" +
            "cleanup:\n" +
            "  - step: Complete job\n";

        private static List<JobExecutionViewEntry> WorkedExampleEntries()
        {
            return new List<JobExecutionViewEntry>
            {
                new JobExecutionViewEntry(JobExecutionPhase.Pre, "Pre actions/checkout@v4", uses: "actions/checkout@v4"),
                new JobExecutionViewEntry(JobExecutionPhase.Pre, "Pre actions/cache@v5", uses: "actions/cache@v5"),
                new JobExecutionViewEntry(JobExecutionPhase.Main, "actions/checkout@v4", uses: "actions/checkout@v4", sourcePath: ".github/workflows/ci.yml", sourceLine: 10),
                new JobExecutionViewEntry(
                    JobExecutionPhase.Main,
                    "Cache Primes",
                    uses: "actions/cache@v5",
                    id: "cache-primes",
                    withYaml: "      path: prime-numbers\n      key: ${{ runner.os }}-primes",
                    sourcePath: ".github/workflows/ci.yml",
                    sourceLine: 12),
                new JobExecutionViewEntry(
                    JobExecutionPhase.Main,
                    "Run tests",
                    run: "echo starting\nnpm test",
                    id: "test",
                    @if: "${{ github.event_name == 'push' }}",
                    envYaml: "      NODE_ENV: production",
                    shell: "bash",
                    workingDirectory: "./api",
                    sourcePath: ".github/workflows/ci.yml",
                    sourceLine: 18),
                new JobExecutionViewEntry(JobExecutionPhase.Main, "npm ci", run: "npm ci", sourcePath: ".github/workflows/ci.yml", sourceLine: 28),
                new JobExecutionViewEntry(JobExecutionPhase.Post, "Post actions/cache@v5", uses: "actions/cache@v5"),
                new JobExecutionViewEntry(JobExecutionPhase.Post, "Post actions/checkout@v4", uses: "actions/checkout@v4"),
            };
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Render_MatchesDesignDocWorkedExample()
        {
            var entries = WorkedExampleEntries();

            var result = JobExecutionViewRenderer.Render("build", entries);

            Assert.Equal(ExpectedWorkedExampleYaml, result.Yaml);
            Assert.Equal(8, result.EntryStartLines.Count);
            var lines = result.Yaml.Split('\n');
            for (int i = 0; i < entries.Count; i++)
            {
                Assert.StartsWith("  - step: ", lines[result.EntryStartLines[i] - 1]);
                Assert.Contains(entries[i].DisplayName, lines[result.EntryStartLines[i] - 1]);
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Render_AlwaysEmitsSetupAndCleanup()
        {
            var result = JobExecutionViewRenderer.Render("job-1", new List<JobExecutionViewEntry>());

            const string expected =
                "# Job: job-1\n" +
                "# Runner execution plan — read-only.\n" +
                "\n" +
                "setup:\n" +
                "  - step: Setup job\n" +
                "\n" +
                "cleanup:\n" +
                "  - step: Complete job\n";
            Assert.Equal(expected, result.Yaml);
            Assert.Empty(result.EntryStartLines);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Render_OmitsEmptyOptionalSections()
        {
            // Only a Main entry — pre:/post: must not appear.
            var result = JobExecutionViewRenderer.Render("j", new[]
            {
                new JobExecutionViewEntry(JobExecutionPhase.Main, "echo", run: "echo hello"),
            });

            Assert.Contains("setup:\n", result.Yaml);
            Assert.Contains("main:\n", result.Yaml);
            Assert.Contains("cleanup:\n", result.Yaml);
            Assert.DoesNotContain("\npre:\n", result.Yaml);
            Assert.DoesNotContain("\npost:\n", result.Yaml);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Render_EmitsPhaseSectionsInFixedOrder()
        {
            // Input order [Post, Pre, Main] should still render as setup → pre → main → post → cleanup.
            var entries = new[]
            {
                new JobExecutionViewEntry(JobExecutionPhase.Post, "post-a", uses: "a/b@v1"),
                new JobExecutionViewEntry(JobExecutionPhase.Pre, "pre-a", uses: "a/b@v1"),
                new JobExecutionViewEntry(JobExecutionPhase.Main, "main-a", uses: "a/b@v1"),
            };

            var result = JobExecutionViewRenderer.Render("j", entries);
            string yaml = result.Yaml;

            int setupIdx = yaml.IndexOf("setup:\n", StringComparison.Ordinal);
            int preIdx = yaml.IndexOf("\npre:\n", StringComparison.Ordinal);
            int mainIdx = yaml.IndexOf("\nmain:\n", StringComparison.Ordinal);
            int postIdx = yaml.IndexOf("\npost:\n", StringComparison.Ordinal);
            int cleanupIdx = yaml.IndexOf("\ncleanup:\n", StringComparison.Ordinal);
            Assert.True(setupIdx >= 0 && preIdx > setupIdx && mainIdx > preIdx && postIdx > mainIdx && cleanupIdx > postIdx,
                $"section ordering wrong: setup={setupIdx} pre={preIdx} main={mainIdx} post={postIdx} cleanup={cleanupIdx}");
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Render_StartLinesAlignWithInputOrder()
        {
            // Input order is [Pre, Main, Post]; output order is also pre/main/post,
            // but startLines must be indexed by INPUT position, not by section.
            var entries = new[]
            {
                new JobExecutionViewEntry(JobExecutionPhase.Pre, "pre-x", uses: "x/y@v1"),     // index 0
                new JobExecutionViewEntry(JobExecutionPhase.Main, "main-x", uses: "x/y@v1"),    // index 1
                new JobExecutionViewEntry(JobExecutionPhase.Post, "post-x", uses: "x/y@v1"),    // index 2
            };

            var result = JobExecutionViewRenderer.Render("j", entries);
            var lines = result.Yaml.Split('\n');

            Assert.StartsWith("  - step: pre-x", lines[result.EntryStartLines[0] - 1]);
            Assert.StartsWith("  - step: main-x", lines[result.EntryStartLines[1] - 1]);
            Assert.StartsWith("  - step: post-x", lines[result.EntryStartLines[2] - 1]);
            // And input-order ordering of start lines is strictly increasing
            // when phases are in declaration order matching the section order.
            Assert.True(result.EntryStartLines[0] < result.EntryStartLines[1]);
            Assert.True(result.EntryStartLines[1] < result.EntryStartLines[2]);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Render_StartLinesFollowInputOrderEvenWhenPhasesAreInterleaved()
        {
            // Input order is [Main A, Pre B, Main C]: pre section will render
            // first (Pre B) and main second (Main A then Main C). startLines
            // must still be indexed by input order.
            var entries = new[]
            {
                new JobExecutionViewEntry(JobExecutionPhase.Main, "main-a", uses: "a@v1"),  // index 0 — renders in main section
                new JobExecutionViewEntry(JobExecutionPhase.Pre, "pre-b", uses: "b@v1"),    // index 1 — renders in pre section
                new JobExecutionViewEntry(JobExecutionPhase.Main, "main-c", uses: "c@v1"),  // index 2 — renders in main section
            };

            var result = JobExecutionViewRenderer.Render("j", entries);
            var lines = result.Yaml.Split('\n');

            Assert.StartsWith("  - step: main-a", lines[result.EntryStartLines[0] - 1]);
            Assert.StartsWith("  - step: pre-b", lines[result.EntryStartLines[1] - 1]);
            Assert.StartsWith("  - step: main-c", lines[result.EntryStartLines[2] - 1]);
            // The pre section comes before main: input-index-1 entry's line is
            // before input-index-0 entry's line.
            Assert.True(result.EntryStartLines[1] < result.EntryStartLines[0]);
            Assert.True(result.EntryStartLines[0] < result.EntryStartLines[2]);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Render_EntryStartLinesPointAtStepKeys()
        {
            var entries = WorkedExampleEntries();
            var result = JobExecutionViewRenderer.Render("build", entries);
            var lines = result.Yaml.Split('\n');

            for (int i = 0; i < result.EntryStartLines.Count; i++)
            {
                int oneBased = result.EntryStartLines[i];
                Assert.True(oneBased >= 1 && oneBased <= lines.Length, $"start line {oneBased} out of range");
                Assert.StartsWith("  - step: ", lines[oneBased - 1]);
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Render_EntryStartLinesExcludeSetupAndCleanup()
        {
            var entries = WorkedExampleEntries();
            var result = JobExecutionViewRenderer.Render("build", entries);
            var lines = result.Yaml.Split('\n');

            int setupLine = -1, cleanupLine = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i] == "  - step: Setup job") setupLine = i + 1;
                if (lines[i] == "  - step: Complete job") cleanupLine = i + 1;
            }
            Assert.True(setupLine > 0 && cleanupLine > 0, "Setup/Cleanup lines must exist");
            Assert.DoesNotContain(setupLine, result.EntryStartLines);
            Assert.DoesNotContain(cleanupLine, result.EntryStartLines);
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
        public void Render_QuotesSpecialChars(string displayName)
        {
            // Round-trip the rendered YAML through YamlDotNet's deserializer
            // and assert the parsed step's display name matches the input.
            // This decouples the test from any specific quoting style.
            var entry = new JobExecutionViewEntry(JobExecutionPhase.Main, displayName);
            var result = JobExecutionViewRenderer.Render("j", new[] { entry });

            var deserializer = new YamlDotNet.Serialization.DeserializerBuilder().Build();
            var doc = deserializer.Deserialize<Dictionary<string, List<Dictionary<string, object>>>>(result.Yaml);
            Assert.NotNull(doc);
            Assert.True(doc.ContainsKey("main"), "rendered YAML missing top-level 'main' key");
            var mainSteps = doc["main"];
            Assert.Single(mainSteps);
            Assert.Equal(displayName, mainSteps[0]["step"] as string);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Render_EmitsSourceAnnotationForMainStep()
        {
            var entry = new JobExecutionViewEntry(
                JobExecutionPhase.Main,
                "npm ci",
                run: "npm ci",
                sourcePath: ".github/workflows/ci.yml",
                sourceLine: 42);

            var result = JobExecutionViewRenderer.Render("j", new[] { entry });

            Assert.Contains("    source: .github/workflows/ci.yml:42\n", result.Yaml);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Render_OmitsSourceAnnotationForPreAndPost()
        {
            var pre = new JobExecutionViewEntry(
                JobExecutionPhase.Pre,
                "Pre actions/checkout@v4",
                uses: "actions/checkout@v4",
                sourcePath: ".github/workflows/ci.yml",
                sourceLine: 9);
            var post = new JobExecutionViewEntry(
                JobExecutionPhase.Post,
                "Post actions/checkout@v4",
                uses: "actions/checkout@v4",
                sourcePath: ".github/workflows/ci.yml",
                sourceLine: 9);

            var result = JobExecutionViewRenderer.Render("j", new[] { pre, post });

            Assert.DoesNotContain("source:", result.Yaml);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Render_EmitsMultilineRunAsBlockScalar()
        {
            var entry = new JobExecutionViewEntry(
                JobExecutionPhase.Main,
                "multi",
                run: "echo a\necho b\necho c");

            var result = JobExecutionViewRenderer.Render("j", new[] { entry });

            Assert.Contains("    run: |\n", result.Yaml);
            Assert.Contains("      echo a\n", result.Yaml);
            Assert.Contains("      echo b\n", result.Yaml);
            Assert.Contains("      echo c\n", result.Yaml);
            Assert.DoesNotContain("truncated", result.Yaml);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Render_EmitsAllUserAuthoredParamsForActionStep()
        {
            var entry = new JobExecutionViewEntry(
                JobExecutionPhase.Main,
                "Run action",
                uses: "actions/cache@v5",
                id: "cache-primes",
                @if: "${{ github.event_name == 'push' }}",
                continueOnError: "true",
                timeoutMinutes: "10",
                envYaml: "      NODE_ENV: production",
                withYaml: "      path: prime-numbers\n      key: ${{ runner.os }}-primes",
                sourcePath: "ci.yml",
                sourceLine: 5);

            var result = JobExecutionViewRenderer.Render("j", new[] { entry });

            Assert.Contains("    id: cache-primes\n", result.Yaml);
            Assert.Contains("    uses: actions/cache@v5\n", result.Yaml);
            Assert.Contains("    continue-on-error: true\n", result.Yaml);
            Assert.Contains("    timeout-minutes: 10\n", result.Yaml);
            Assert.Contains("    env:\n      NODE_ENV: production\n", result.Yaml);
            Assert.Contains("    with:\n      path: prime-numbers\n      key: ${{ runner.os }}-primes\n", result.Yaml);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Render_EmitsRunStepWithShellAndWorkingDirectory()
        {
            var entry = new JobExecutionViewEntry(
                JobExecutionPhase.Main,
                "Run tests",
                run: "echo starting\nnpm test",
                id: "test",
                shell: "bash",
                workingDirectory: "./api");

            var result = JobExecutionViewRenderer.Render("j", new[] { entry });

            Assert.Contains("    run: |\n      echo starting\n      npm test\n", result.Yaml);
            Assert.Contains("    shell: bash\n", result.Yaml);
            Assert.Contains("    working-directory: ./api\n", result.Yaml);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Render_PreservesExpressionsInRenderedYaml()
        {
            var entry = new JobExecutionViewEntry(
                JobExecutionPhase.Main,
                "Cache",
                uses: "actions/cache@v5",
                withYaml: "      key: ${{ runner.os }}-primes");

            var result = JobExecutionViewRenderer.Render("j", new[] { entry });

            // Expressions render exactly as authored — no evaluation.
            Assert.Contains("${{ runner.os }}-primes", result.Yaml);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Render_PrePostStepsRemainMinimal()
        {
            // Even if a pre/post entry carries user-param fields (it shouldn't
            // in production, but the renderer must defensively drop them),
            // only step: + action: render for these phases.
            var pre = new JobExecutionViewEntry(
                JobExecutionPhase.Pre,
                "Pre actions/cache@v5",
                uses: "actions/cache@v5",
                id: "should-not-appear",
                envYaml: "      X: y",
                withYaml: "      key: nope");
            var post = new JobExecutionViewEntry(
                JobExecutionPhase.Post,
                "Post actions/cache@v5",
                uses: "actions/cache@v5",
                id: "should-not-appear",
                envYaml: "      X: y");

            var result = JobExecutionViewRenderer.Render("j", new[] { pre, post });

            Assert.DoesNotContain("id:", result.Yaml);
            Assert.DoesNotContain("env:", result.Yaml);
            Assert.DoesNotContain("with:", result.Yaml);
            Assert.DoesNotContain("should-not-appear", result.Yaml);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Render_FieldOrderIsStable()
        {
            var entry = new JobExecutionViewEntry(
                JobExecutionPhase.Main,
                "Everything",
                uses: "actions/cache@v5",
                id: "x",
                @if: "always()",
                continueOnError: "false",
                timeoutMinutes: "5",
                envYaml: "      A: 1",
                withYaml: "      key: k",
                sourcePath: "ci.yml",
                sourceLine: 1);

            var result = JobExecutionViewRenderer.Render("j", new[] { entry });
            var y = result.Yaml;
            int iStep = y.IndexOf("    - step: ", StringComparison.Ordinal) >= 0
                ? y.IndexOf("- step:", StringComparison.Ordinal) : y.IndexOf("- step:", StringComparison.Ordinal);
            int iId = y.IndexOf("    id:", StringComparison.Ordinal);
            int iUses = y.IndexOf("    uses:", StringComparison.Ordinal);
            int iIf = y.IndexOf("    if:", StringComparison.Ordinal);
            int iCoe = y.IndexOf("    continue-on-error:", StringComparison.Ordinal);
            int iTm = y.IndexOf("    timeout-minutes:", StringComparison.Ordinal);
            int iEnv = y.IndexOf("    env:", StringComparison.Ordinal);
            int iWith = y.IndexOf("    with:", StringComparison.Ordinal);
            int iSrc = y.IndexOf("    source:", StringComparison.Ordinal);
            Assert.True(iId < iUses && iUses < iIf && iIf < iCoe && iCoe < iTm && iTm < iEnv && iEnv < iWith && iWith < iSrc,
                $"order wrong: id={iId} uses={iUses} if={iIf} coe={iCoe} tm={iTm} env={iEnv} with={iWith} src={iSrc}");
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Render_OmitsEmptyOptionalFields()
        {
            var entry = new JobExecutionViewEntry(
                JobExecutionPhase.Main,
                "bare",
                uses: "a/b@v1");

            var result = JobExecutionViewRenderer.Render("j", new[] { entry });
            Assert.DoesNotContain("    id:", result.Yaml);
            Assert.DoesNotContain("    if:", result.Yaml);
            Assert.DoesNotContain("    continue-on-error:", result.Yaml);
            Assert.DoesNotContain("    timeout-minutes:", result.Yaml);
            Assert.DoesNotContain("    env:", result.Yaml);
            Assert.DoesNotContain("    with:", result.Yaml);
            Assert.DoesNotContain("    shell:", result.Yaml);
            Assert.DoesNotContain("    working-directory:", result.Yaml);
            Assert.DoesNotContain("    source:", result.Yaml);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Render_HandlesEmptyEntries()
        {
            var result = JobExecutionViewRenderer.Render("j", new List<JobExecutionViewEntry>());

            Assert.Empty(result.EntryStartLines);
            Assert.Contains("  - step: Setup job\n", result.Yaml);
            Assert.Contains("  - step: Complete job\n", result.Yaml);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Render_NoPerEntryPhaseField()
        {
            // The phase: <value> per-entry field is gone — the section
            // header is the phase indicator. Guard against accidental
            // regressions.
            var result = JobExecutionViewRenderer.Render("build", WorkedExampleEntries());
            Assert.DoesNotContain("phase:", result.Yaml);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Render_ThrowsOnNullJobId()
        {
            Assert.Throws<ArgumentException>(
                () => JobExecutionViewRenderer.Render(null, new List<JobExecutionViewEntry>()));
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Render_ThrowsOnWhitespaceJobId()
        {
            Assert.Throws<ArgumentException>(
                () => JobExecutionViewRenderer.Render("   ", new List<JobExecutionViewEntry>()));
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Render_ThrowsOnNullEntries()
        {
            Assert.Throws<ArgumentNullException>(
                () => JobExecutionViewRenderer.Render("j", null));
        }

        [Theory]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        [InlineData(null, 1)]
        [InlineData("", 1)]
        [InlineData("   ", 1)]
        public void Entry_Constructor_RejectsBadDisplayName(string displayName, int sourceLine)
        {
            Assert.Throws<ArgumentException>(
                () => new JobExecutionViewEntry(JobExecutionPhase.Main, displayName, sourceLine: sourceLine));
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Entry_Constructor_RejectsZeroLineWhenSourcePathSet()
        {
            Assert.Throws<ArgumentException>(
                () => new JobExecutionViewEntry(
                    JobExecutionPhase.Main,
                    "ok",
                    sourcePath: "ci.yml",
                    sourceLine: 0));
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Render_EmitsSkippedAnnotationForMarkedEntry()
        {
            var entry = new JobExecutionViewEntry(JobExecutionPhase.Post, "Post X", uses: "actions/x@v1");
            entry.IsSkipped = true;

            var result = JobExecutionViewRenderer.Render("j", new[] { entry });

            // Annotation is inline on the `- step:` line so subsequent
            // entry line numbers stay stable.
            Assert.Contains("- step: Post X  # (skipped — main step did not execute)\n", result.Yaml);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void Render_SkippedAnnotation_DoesNotShiftSubsequentLines()
        {
            var skipped = new JobExecutionViewEntry(JobExecutionPhase.Post, "Post A", uses: "actions/a@v1");
            var following = new JobExecutionViewEntry(JobExecutionPhase.Post, "Post B", uses: "actions/b@v1");

            var unmarked = JobExecutionViewRenderer.Render("j", new[] { skipped, following });
            skipped.IsSkipped = true;
            var marked = JobExecutionViewRenderer.Render("j", new[] { skipped, following });

            // Following entry's start line must not move when the prior
            // entry gets an inline skipped annotation.
            Assert.Equal(unmarked.EntryStartLines[1], marked.EntryStartLines[1]);
        }
    }
}
