using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GitHub.DistributedTask.Pipelines;
using GitHub.DistributedTask.WebApi;
using GitHub.Runner.Worker;
using GitHub.Runner.Worker.Dap;
using Moq;
using Newtonsoft.Json;
using Xunit;

namespace GitHub.Runner.Common.Tests.Worker
{
    public sealed class JobExecutionViewLifecycleL0
    {
        private DapDebugger _debugger;

        private TestHostContext CreateTestContext([CallerMemberName] string testName = "")
        {
            var hc = new TestHostContext(this, testName);
            _debugger = new DapDebugger();
            _debugger.Initialize(hc);
            _debugger.SkipTunnelRelay = true;
            _debugger.SkipWebSocketBridge = true;
            return hc;
        }

        private static ushort GetFreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
        }

        private static Mock<IExecutionContext> CreateJobContextWithTunnel(CancellationToken cancellationToken, ushort port, string jobName = "ci-job")
        {
            var tunnel = new GitHub.DistributedTask.Pipelines.DebuggerTunnelInfo
            {
                TunnelId = "test-tunnel",
                ClusterId = "test-cluster",
                HostToken = "test-token",
                Port = port
            };
            var debuggerConfig = new DebuggerConfig(true, tunnel);
            var jobContext = new Mock<IExecutionContext>();
            jobContext.Setup(x => x.CancellationToken).Returns(cancellationToken);
            jobContext.Setup(x => x.Global).Returns(new GlobalContext { Debugger = debuggerConfig });
            jobContext
                .Setup(x => x.GetGitHubContext(It.IsAny<string>()))
                .Returns((string contextName) => string.Equals(contextName, "job", StringComparison.Ordinal) ? jobName : null);
            return jobContext;
        }

        private static async Task DriveToReadyAsync(DapDebugger debugger, int port)
        {
            var waitTask = debugger.WaitUntilReadyAsync();
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var stream = client.GetStream();
            var request = new Request { Seq = 1, Type = "request", Command = "configurationDone" };
            var json = JsonConvert.SerializeObject(request);
            var body = Encoding.UTF8.GetBytes(json);
            var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
            await stream.WriteAsync(header, 0, header.Length);
            await stream.WriteAsync(body, 0, body.Length);
            await stream.FlushAsync();
            await waitTask;
            // Keep client alive by holding a reference via GC root in caller scope.
            // We deliberately don't dispose here; tests dispose the context.
            _ = client;
        }

        private static Mock<IActionRunner> NewActionRunner(ActionRunStage stage, string displayName, string actionName = "actions/checkout", string actionRef = "v4", Guid actionId = default)
        {
            var mock = new Mock<IActionRunner>();
            mock.SetupGet(x => x.Stage).Returns(stage);
            mock.SetupGet(x => x.DisplayName).Returns(displayName);
            mock.SetupGet(x => x.Action).Returns(new ActionStep
            {
                Id = actionId,
                Reference = new RepositoryPathReference { Name = actionName, Ref = actionRef },
            });
            return mock;
        }

        private static Mock<IActionRunner> NewSelfActionRunner(ActionRunStage stage, string displayName, Guid actionId = default)
        {
            // RepositoryType = "self" — the predictor must skip these.
            var mock = new Mock<IActionRunner>();
            mock.SetupGet(x => x.Stage).Returns(stage);
            mock.SetupGet(x => x.DisplayName).Returns(displayName);
            mock.SetupGet(x => x.Action).Returns(new ActionStep
            {
                Id = actionId,
                Reference = new RepositoryPathReference
                {
                    RepositoryType = GitHub.DistributedTask.Pipelines.PipelineConstants.SelfAlias,
                    Path = "./.github/actions/local",
                },
            });
            return mock;
        }

        private static Mock<IActionRunner> NewScriptActionRunner(ActionRunStage stage, string displayName, Guid actionId = default)
        {
            // ScriptReference — a `run:` step. Not a RepositoryPathReference,
            // so the predictor's pattern match falls through.
            var mock = new Mock<IActionRunner>();
            mock.SetupGet(x => x.Stage).Returns(stage);
            mock.SetupGet(x => x.DisplayName).Returns(displayName);
            mock.SetupGet(x => x.Action).Returns(new ActionStep
            {
                Id = actionId,
                Reference = new ScriptReference(),
            });
            return mock;
        }

        // IActionManager mock that returns specific Definitions per action by
        // matching on the action's reference Name. Actions whose name is not
        // in the map get a Definition with HasPost = false.
        private static Mock<IActionManager> NewActionManagerWithPost(params string[] actionNamesWithPost)
        {
            var withPost = new HashSet<string>(actionNamesWithPost, StringComparer.Ordinal);
            var mock = new Mock<IActionManager>();
            mock.Setup(x => x.LoadAction(It.IsAny<IExecutionContext>(), It.IsAny<ActionStep>()))
                .Returns((IExecutionContext _, ActionStep step) =>
                {
                    var name = (step.Reference as RepositoryPathReference)?.Name ?? "";
                    return new Definition
                    {
                        Data = new ActionDefinitionData
                        {
                            Execution = withPost.Contains(name)
                                ? new NodeJSActionExecutionData { Post = "post.js" }
                                : new NodeJSActionExecutionData(),
                        },
                    };
                });
            return mock;
        }

        private static IStep NewJobExtensionRunner(string displayName)
        {
            return new JobExtensionRunner(
                runAsync: (_, __) => Task.CompletedTask,
                condition: null,
                displayName: displayName,
                data: null);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async Task OnJobStepsInitialized_NotActive_NoOps()
        {
            using (CreateTestContext())
            {
                var step = NewActionRunner(ActionRunStage.Main, "Run").Object;

                await _debugger.OnJobStepsInitializedAsync(new[] { step }, Array.Empty<IStep>());

                Assert.Null(_debugger.ExecutionView);
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async Task OnPostStepRegistered_NotActive_NoOps()
        {
            using (CreateTestContext())
            {
                var step = NewActionRunner(ActionRunStage.Post, "Post Run").Object;
                _debugger.OnPostStepRegistered(step); // must not throw
                Assert.Null(_debugger.ExecutionView);
                await Task.CompletedTask;
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async Task OnJobStepsInitialized_Active_BuildsView()
        {
            using (CreateTestContext())
            {
                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port);
                await _debugger.StartAsync(jobContext.Object);
                try
                {
                    await DriveToReadyAsync(_debugger, port);

                    var main1 = NewActionRunner(ActionRunStage.Main, "Run actions/checkout@v4").Object;
                    var main2 = NewActionRunner(ActionRunStage.Main, "Run actions/setup-node@v3", "actions/setup-node", "v3").Object;
                    var jobExt = NewJobExtensionRunner("Set up job");
                    var post1 = NewActionRunner(ActionRunStage.Post, "Post Run actions/checkout@v4").Object;

                    await _debugger.OnJobStepsInitializedAsync(
                        new IStep[] { main1, jobExt, main2 },
                        new IStep[] { post1 });

                    var view = _debugger.ExecutionView;
                    Assert.NotNull(view);
                    Assert.Equal(3, view.EntryCount); // jobExt filtered out
                    Assert.Contains("Run actions/checkout@v4", view.Yaml);
                    Assert.Contains("Run actions/setup-node@v3", view.Yaml);
                    Assert.Contains("Post Run actions/checkout@v4", view.Yaml);
                    Assert.NotNull(view.TryGetLineForStep(main1));
                    Assert.NotNull(view.TryGetLineForStep(main2));
                    Assert.NotNull(view.TryGetLineForStep(post1));
                }
                finally
                {
                    await _debugger.StopAsync();
                }
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async Task OnJobStepsInitialized_PreservesQueueOrder()
        {
            using (CreateTestContext())
            {
                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port);
                await _debugger.StartAsync(jobContext.Object);
                try
                {
                    await DriveToReadyAsync(_debugger, port);

                    var s1 = NewActionRunner(ActionRunStage.Main, "Step 1", "a/b", "v1").Object;
                    var s2 = NewActionRunner(ActionRunStage.Main, "Step 2", "c/d", "v2").Object;
                    var s3 = NewActionRunner(ActionRunStage.Main, "Step 3", "e/f", "v3").Object;

                    await _debugger.OnJobStepsInitializedAsync(new[] { s1, s2, s3 }, Array.Empty<IStep>());

                    var view = _debugger.ExecutionView;
                    Assert.Equal(3, view.EntryCount);
                    var l1 = view.TryGetLineForStep(s1);
                    var l2 = view.TryGetLineForStep(s2);
                    var l3 = view.TryGetLineForStep(s3);
                    Assert.NotNull(l1);
                    Assert.NotNull(l2);
                    Assert.NotNull(l3);
                    Assert.True(l1 < l2);
                    Assert.True(l2 < l3);
                    Assert.Equal(view.GetLine(0), l1);
                    Assert.Equal(view.GetLine(1), l2);
                    Assert.Equal(view.GetLine(2), l3);
                }
                finally
                {
                    await _debugger.StopAsync();
                }
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async Task OnPostStepRegistered_AppendsToView()
        {
            using (CreateTestContext())
            {
                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port);
                await _debugger.StartAsync(jobContext.Object);
                try
                {
                    await DriveToReadyAsync(_debugger, port);

                    var main1 = NewActionRunner(ActionRunStage.Main, "Run actions/checkout@v4").Object;
                    await _debugger.OnJobStepsInitializedAsync(new[] { main1 }, Array.Empty<IStep>());
                    Assert.Equal(1, _debugger.ExecutionView.EntryCount);

                    var post1 = NewActionRunner(ActionRunStage.Post, "Post Run actions/cache@v3", "actions/cache", "v3").Object;
                    _debugger.OnPostStepRegistered(post1);

                    var view = _debugger.ExecutionView;
                    Assert.Equal(2, view.EntryCount);
                    Assert.Contains("Post Run actions/cache@v3", view.Yaml);
                    Assert.NotNull(view.TryGetLineForStep(post1));
                }
                finally
                {
                    await _debugger.StopAsync();
                }
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async Task OnPostStepRegistered_BeforeViewBuilt_NoOps()
        {
            using (CreateTestContext())
            {
                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port);
                await _debugger.StartAsync(jobContext.Object);
                try
                {
                    await DriveToReadyAsync(_debugger, port);

                    var post = NewActionRunner(ActionRunStage.Post, "Post Run").Object;
                    _debugger.OnPostStepRegistered(post); // must not throw

                    Assert.Null(_debugger.ExecutionView);
                }
                finally
                {
                    await _debugger.StopAsync();
                }
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async Task OnPostStepRegistered_DuplicateStep_DoesNotThrow()
        {
            using (CreateTestContext())
            {
                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port);
                await _debugger.StartAsync(jobContext.Object);
                try
                {
                    await DriveToReadyAsync(_debugger, port);
                    await _debugger.OnJobStepsInitializedAsync(Array.Empty<IStep>(), Array.Empty<IStep>());

                    var post = NewActionRunner(ActionRunStage.Post, "Post Run").Object;
                    _debugger.OnPostStepRegistered(post);
                    _debugger.OnPostStepRegistered(post); // duplicate, must be silently ignored

                    Assert.Equal(1, _debugger.ExecutionView.EntryCount);
                }
                finally
                {
                    await _debugger.StopAsync();
                }
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async Task OnPostStepRegistered_FilteredStep_NoOps()
        {
            using (CreateTestContext())
            {
                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port);
                await _debugger.StartAsync(jobContext.Object);
                try
                {
                    await DriveToReadyAsync(_debugger, port);
                    await _debugger.OnJobStepsInitializedAsync(Array.Empty<IStep>(), Array.Empty<IStep>());

                    var before = _debugger.ExecutionView.EntryCount;
                    _debugger.OnPostStepRegistered(NewJobExtensionRunner("Cleanup"));
                    Assert.Equal(before, _debugger.ExecutionView.EntryCount);
                }
                finally
                {
                    await _debugger.StopAsync();
                }
            }
        }

        // ---- Predictive Post-step synthesis ----

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async Task OnJobStepsInitialized_PredictsPostForActionsWithHasPost()
        {
            using (var hc = CreateTestContext())
            {
                hc.SetSingleton<IActionManager>(NewActionManagerWithPost("actions/has-post").Object);

                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port);
                await _debugger.StartAsync(jobContext.Object);
                try
                {
                    await DriveToReadyAsync(_debugger, port);

                    var withPost = NewActionRunner(ActionRunStage.Main, "Run actions/has-post@v1", "actions/has-post", "v1", actionId: Guid.NewGuid()).Object;
                    var noPost = NewActionRunner(ActionRunStage.Main, "Run actions/no-post@v1", "actions/no-post", "v1", actionId: Guid.NewGuid()).Object;

                    await _debugger.OnJobStepsInitializedAsync(new[] { withPost, noPost }, Array.Empty<IStep>());

                    var view = _debugger.ExecutionView;
                    Assert.NotNull(view);
                    // 2 main entries + 1 predicted post placeholder.
                    Assert.Equal(3, view.EntryCount);
                    Assert.Contains("post:\n", view.Yaml);
                    Assert.Contains("Post Run actions/has-post@v1", view.Yaml);
                    Assert.DoesNotContain("Post Run actions/no-post@v1", view.Yaml);
                }
                finally
                {
                    await _debugger.StopAsync();
                }
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async Task OnJobStepsInitialized_PostPredictionsInReverseOrder()
        {
            using (var hc = CreateTestContext())
            {
                // Both actions have post — predictions must render in
                // reverse declaration order to mirror the runner's LIFO
                // post-execution order.
                hc.SetSingleton<IActionManager>(NewActionManagerWithPost("actions/a", "actions/b").Object);

                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port);
                await _debugger.StartAsync(jobContext.Object);
                try
                {
                    await DriveToReadyAsync(_debugger, port);

                    var aMain = NewActionRunner(ActionRunStage.Main, "Run actions/a@v1", "actions/a", "v1", actionId: Guid.NewGuid()).Object;
                    var bMain = NewActionRunner(ActionRunStage.Main, "Run actions/b@v1", "actions/b", "v1", actionId: Guid.NewGuid()).Object;

                    await _debugger.OnJobStepsInitializedAsync(new[] { aMain, bMain }, Array.Empty<IStep>());

                    string yaml = _debugger.ExecutionView.Yaml;
                    int idxPostB = yaml.IndexOf("Post Run actions/b@v1", StringComparison.Ordinal);
                    int idxPostA = yaml.IndexOf("Post Run actions/a@v1", StringComparison.Ordinal);
                    Assert.True(idxPostB > 0 && idxPostA > 0, "both post placeholders expected");
                    // Reverse declaration order: Post B appears BEFORE Post A.
                    Assert.True(idxPostB < idxPostA, $"expected Post B before Post A (b={idxPostB} a={idxPostA})");
                }
                finally
                {
                    await _debugger.StopAsync();
                }
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async Task OnJobStepsInitialized_SkipsScriptSteps()
        {
            using (var hc = CreateTestContext())
            {
                // Even if the action manager would say HasPost, the predictor
                // must skip script run-steps because their reference is not
                // a RepositoryPathReference.
                hc.SetSingleton<IActionManager>(NewActionManagerWithPost(/* nothing */).Object);

                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port);
                await _debugger.StartAsync(jobContext.Object);
                try
                {
                    await DriveToReadyAsync(_debugger, port);

                    var script = NewScriptActionRunner(ActionRunStage.Main, "Run script", Guid.NewGuid()).Object;
                    await _debugger.OnJobStepsInitializedAsync(new[] { script }, Array.Empty<IStep>());

                    var view = _debugger.ExecutionView;
                    Assert.NotNull(view);
                    Assert.DoesNotContain("post:\n", view.Yaml);
                    Assert.DoesNotContain("Post ", view.Yaml);
                }
                finally
                {
                    await _debugger.StopAsync();
                }
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async Task OnJobStepsInitialized_SkipsSelfActions()
        {
            using (var hc = CreateTestContext())
            {
                // Self-action: ActionRunner.cs:106 guards against creating a
                // Post for self-repository references. The predictor mirrors
                // that, regardless of what the manifest reports.
                hc.SetSingleton<IActionManager>(NewActionManagerWithPost("anything").Object);

                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port);
                await _debugger.StartAsync(jobContext.Object);
                try
                {
                    await DriveToReadyAsync(_debugger, port);

                    var selfRunner = NewSelfActionRunner(ActionRunStage.Main, "Run ./local-action", Guid.NewGuid()).Object;
                    await _debugger.OnJobStepsInitializedAsync(new[] { selfRunner }, Array.Empty<IStep>());

                    Assert.DoesNotContain("post:\n", _debugger.ExecutionView.Yaml);
                }
                finally
                {
                    await _debugger.StopAsync();
                }
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async Task OnPostStepRegistered_ClaimsExistingPlaceholder()
        {
            using (var hc = CreateTestContext())
            {
                hc.SetSingleton<IActionManager>(NewActionManagerWithPost("actions/has-post").Object);

                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port);
                await _debugger.StartAsync(jobContext.Object);
                try
                {
                    await DriveToReadyAsync(_debugger, port);

                    var actionId = Guid.NewGuid();
                    var mainRunner = NewActionRunner(ActionRunStage.Main, "Run actions/has-post@v1", "actions/has-post", "v1", actionId: actionId).Object;
                    await _debugger.OnJobStepsInitializedAsync(new[] { mainRunner }, Array.Empty<IStep>());

                    var view = _debugger.ExecutionView;
                    int before = view.EntryCount;
                    Assert.Equal(2, before); // main + predicted post placeholder

                    // The real Post IActionRunner shares the same Action.Id
                    // as the Main runner (ActionRunner.cs:131).
                    var postRunner = NewActionRunner(ActionRunStage.Post, "Post actions/has-post@v1", "actions/has-post", "v1", actionId: actionId).Object;
                    _debugger.OnPostStepRegistered(postRunner);

                    // No new entry: the placeholder was claimed.
                    Assert.Equal(before, view.EntryCount);
                    Assert.NotNull(view.TryGetLineForStep(postRunner));
                }
                finally
                {
                    await _debugger.StopAsync();
                }
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async Task OnPostStepRegistered_UnpredictedFallsBackToAppend()
        {
            using (var hc = CreateTestContext())
            {
                // Manager returns no HasPost — no predictions made.
                hc.SetSingleton<IActionManager>(NewActionManagerWithPost(/* nothing */).Object);

                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port);
                await _debugger.StartAsync(jobContext.Object);
                try
                {
                    await DriveToReadyAsync(_debugger, port);

                    var mainRunner = NewActionRunner(ActionRunStage.Main, "Run actions/a@v1", "actions/a", "v1", actionId: Guid.NewGuid()).Object;
                    await _debugger.OnJobStepsInitializedAsync(new[] { mainRunner }, Array.Empty<IStep>());

                    var view = _debugger.ExecutionView;
                    int before = view.EntryCount;
                    Assert.Equal(1, before); // just main, no predicted post

                    var unpredictedPost = NewActionRunner(ActionRunStage.Post, "Post Surprise", "actions/surprise", "v1", actionId: Guid.NewGuid()).Object;
                    _debugger.OnPostStepRegistered(unpredictedPost);

                    // Falls back to Append.
                    Assert.Equal(before + 1, view.EntryCount);
                    Assert.NotNull(view.TryGetLineForStep(unpredictedPost));
                    Assert.Contains("Post Surprise", view.Yaml);
                }
                finally
                {
                    await _debugger.StopAsync();
                }
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async Task OnPostStepRegistered_DuplicateClaim_NoDoubleEntry()
        {
            using (var hc = CreateTestContext())
            {
                hc.SetSingleton<IActionManager>(NewActionManagerWithPost("actions/has-post").Object);

                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port);
                await _debugger.StartAsync(jobContext.Object);
                try
                {
                    await DriveToReadyAsync(_debugger, port);

                    var actionId = Guid.NewGuid();
                    var mainRunner = NewActionRunner(ActionRunStage.Main, "Run actions/has-post@v1", "actions/has-post", "v1", actionId: actionId).Object;
                    await _debugger.OnJobStepsInitializedAsync(new[] { mainRunner }, Array.Empty<IStep>());
                    Assert.Equal(2, _debugger.ExecutionView.EntryCount);

                    // First registration claims the placeholder.
                    var post1 = NewActionRunner(ActionRunStage.Post, "Post actions/has-post@v1", "actions/has-post", "v1", actionId: actionId).Object;
                    _debugger.OnPostStepRegistered(post1);
                    Assert.Equal(2, _debugger.ExecutionView.EntryCount);

                    // Second registration with the same Action.Id but a
                    // different IStep: TryClaim returns null (already
                    // claimed). Falls through to Append. But the entry
                    // it builds matches no existing step, so a new entry
                    // would be added — UNLESS we constructed the second
                    // post as a duplicate IStep registration of the same
                    // step. Here we intentionally pass the same `post1`
                    // step a second time — Append will reject the
                    // already-registered step, the handler swallows it.
                    _debugger.OnPostStepRegistered(post1);

                    Assert.Equal(2, _debugger.ExecutionView.EntryCount);
                }
                finally
                {
                    await _debugger.StopAsync();
                }
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async Task OnStepCompleted_SkippedMainStep_MarksPostPlaceholder()
        {
            using (var hc = CreateTestContext())
            {
                hc.SetSingleton<IActionManager>(NewActionManagerWithPost("actions/has-post").Object);

                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port);
                await _debugger.StartAsync(jobContext.Object);
                try
                {
                    await DriveToReadyAsync(_debugger, port);

                    var actionId = Guid.NewGuid();
                    var mainMock = NewActionRunner(ActionRunStage.Main, "Run actions/has-post@v1", "actions/has-post", "v1", actionId: actionId);
                    var execCtx = new Mock<IExecutionContext>();
                    execCtx.SetupGet(x => x.Result).Returns(TaskResult.Skipped);
                    mainMock.SetupGet(x => x.ExecutionContext).Returns(execCtx.Object);

                    await _debugger.OnJobStepsInitializedAsync(new[] { mainMock.Object }, Array.Empty<IStep>());

                    var view = _debugger.ExecutionView;
                    Assert.Equal(2, view.EntryCount); // main + predicted post placeholder
                    Assert.DoesNotContain("(skipped", view.Yaml);

                    _debugger.OnStepCompleted(mainMock.Object);

                    Assert.Equal(2, _debugger.ExecutionView.EntryCount);
                    Assert.Contains("(skipped — main step did not execute)", _debugger.ExecutionView.Yaml);
                    // Inline annotation must not have introduced a new line.
                    Assert.Equal(view.Yaml.Split('\n').Length, _debugger.ExecutionView.Yaml.Split('\n').Length);
                }
                finally
                {
                    await _debugger.StopAsync();
                }
            }
        }
    }
}
