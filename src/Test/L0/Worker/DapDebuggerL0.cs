using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using GitHub.Runner.Worker;
using GitHub.Runner.Worker.Dap;
using Newtonsoft.Json;
using Xunit;

namespace GitHub.Runner.Common.Tests.Worker
{
    public sealed class DapDebuggerL0
    {
        private const string TimeoutEnvironmentVariable = "ACTIONS_RUNNER_DAP_CONNECTION_TIMEOUT";
        private const string TunnelConnectTimeoutVariable = "ACTIONS_RUNNER_DAP_TUNNEL_CONNECT_TIMEOUT_SECONDS";
        private DapDebugger _debugger;
        private TestWebSocketDapBridge _testWebSocketBridge;

        private sealed class TestWebSocketDapBridge : RunnerService, IWebSocketDapBridge
        {
            private readonly WebSocketDapBridge _inner = new WebSocketDapBridge();

            public int ListenPort => _inner.ListenPort;

            public override void Initialize(IHostContext hostContext)
            {
                base.Initialize(hostContext);
                _inner.Initialize(hostContext);
            }

            public void Start(int listenPort, int targetPort)
            {
                _inner.Start(0, targetPort);
            }

            public Task ShutdownAsync()
            {
                return _inner.ShutdownAsync();
            }
        }

        private TestHostContext CreateTestContext(bool enableWebSocketBridge = false, [CallerMemberName] string testName = "")
        {
            var hc = new TestHostContext(this, testName);
            _debugger = new DapDebugger();
            _testWebSocketBridge = null;
            _debugger.Initialize(hc);
            _debugger.SkipTunnelRelay = true;
            _debugger.SkipWebSocketBridge = !enableWebSocketBridge;
            if (enableWebSocketBridge)
            {
                _testWebSocketBridge = new TestWebSocketDapBridge();
                hc.EnqueueInstance<IWebSocketDapBridge>(_testWebSocketBridge);
            }

            return hc;
        }

        private static async Task WithEnvironmentVariableAsync(string name, string value, Func<Task> action)
        {
            var originalValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
            try
            {
                await action();
            }
            finally
            {
                Environment.SetEnvironmentVariable(name, originalValue);
            }
        }

        private static void WithEnvironmentVariable(string name, string value, Action action)
        {
            var originalValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
            try
            {
                action();
            }
            finally
            {
                Environment.SetEnvironmentVariable(name, originalValue);
            }
        }

        private static ushort GetFreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
        }

        private static async Task<TcpClient> ConnectClientAsync(int port)
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            return client;
        }

        private static async Task<ClientWebSocket> ConnectWebSocketClientAsync(int port)
        {
            var client = new ClientWebSocket();
            client.Options.Proxy = null;
            await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), CancellationToken.None);
            return client;
        }

        private static async Task SendRequestAsync(NetworkStream stream, Request request)
        {
            var json = JsonConvert.SerializeObject(request);
            var body = Encoding.UTF8.GetBytes(json);
            var header = $"Content-Length: {body.Length}\r\n\r\n";
            var headerBytes = Encoding.ASCII.GetBytes(header);

            await stream.WriteAsync(headerBytes, 0, headerBytes.Length);
            await stream.WriteAsync(body, 0, body.Length);
            await stream.FlushAsync();
        }

        private static async Task SendRequestAsync(WebSocket client, Request request)
        {
            var json = JsonConvert.SerializeObject(request);
            var body = Encoding.UTF8.GetBytes(json);

            await client.SendAsync(new ArraySegment<byte>(body), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        }

        /// <summary>
        /// Reads a single DAP-framed message from a stream with a timeout.
        /// Parses the Content-Length header, reads exactly that many bytes,
        /// and returns the JSON body. Fails with a clear error on timeout.
        /// </summary>
        private static async Task<string> ReadDapMessageAsync(NetworkStream stream, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            var token = cts.Token;

            var headerBuilder = new StringBuilder();
            var buffer = new byte[1];
            var contentLength = -1;

            while (true)
            {
                var readTask = stream.ReadAsync(buffer, 0, 1, token);
                var bytesRead = await readTask;
                if (bytesRead == 0)
                {
                    throw new EndOfStreamException("Connection closed while reading DAP headers");
                }

                headerBuilder.Append((char)buffer[0]);
                var headers = headerBuilder.ToString();
                if (headers.EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    foreach (var line in headers.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (line.StartsWith("Content-Length: ", StringComparison.OrdinalIgnoreCase))
                        {
                            contentLength = int.Parse(line.Substring("Content-Length: ".Length).Trim());
                        }
                    }
                    break;
                }
            }

            if (contentLength < 0)
            {
                throw new InvalidOperationException("No Content-Length header found in DAP message");
            }

            var body = new byte[contentLength];
            var totalRead = 0;
            while (totalRead < contentLength)
            {
                var bytesRead = await stream.ReadAsync(body, totalRead, contentLength - totalRead, token);
                if (bytesRead == 0)
                {
                    throw new EndOfStreamException("Connection closed while reading DAP body");
                }
                totalRead += bytesRead;
            }

            return Encoding.UTF8.GetString(body);
        }

        private static async Task<string> ReadWebSocketDataUntilAsync(WebSocket client, TimeSpan timeout, params string[] expectedFragments)
        {
            using var cts = new CancellationTokenSource(timeout);
            var buffer = new byte[4096];
            var allMessages = new StringBuilder();

            while (true)
            {
                using var messageStream = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        throw new EndOfStreamException("WebSocket closed before expected DAP messages were received.");
                    }

                    if (result.Count > 0)
                    {
                        messageStream.Write(buffer, 0, result.Count);
                    }
                }
                while (!result.EndOfMessage);

                var messageText = Encoding.UTF8.GetString(messageStream.ToArray());
                allMessages.Append(messageText);

                var text = allMessages.ToString();
                var containsAllFragments = true;
                foreach (var fragment in expectedFragments)
                {
                    if (!text.Contains(fragment, StringComparison.Ordinal))
                    {
                        containsAllFragments = false;
                        break;
                    }
                }

                if (containsAllFragments)
                {
                    return text;
                }
            }
        }

        private static Mock<IExecutionContext> CreateJobContextWithTunnel(CancellationToken cancellationToken, ushort port, string jobName = null, bool overrideWelcomeMessage = false, string welcomeMessage = null)
        {
            var tunnel = new GitHub.DistributedTask.Pipelines.DebuggerTunnelInfo
            {
                TunnelId = "test-tunnel",
                ClusterId = "test-cluster",
                HostToken = "test-token",
                Port = port
            };
            var debuggerConfig = new DebuggerConfig(true, tunnel, overrideWelcomeMessage, welcomeMessage);
            var jobContext = new Mock<IExecutionContext>();
            jobContext.Setup(x => x.CancellationToken).Returns(cancellationToken);
            jobContext.Setup(x => x.Global).Returns(new GlobalContext { Debugger = debuggerConfig });
            jobContext
                .Setup(x => x.GetGitHubContext(It.IsAny<string>()))
                .Returns((string contextName) => string.Equals(contextName, "job", StringComparison.Ordinal) ? jobName : null);
            return jobContext;
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void InitializeSucceeds()
        {
            using (CreateTestContext())
            {
                Assert.NotNull(_debugger);
                Assert.False(_debugger.IsActive);
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async Task StartAsyncFailsWithoutValidTunnelConfig()
        {
            using (CreateTestContext())
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = new Mock<IExecutionContext>();
                jobContext.Setup(x => x.CancellationToken).Returns(cts.Token);
                jobContext.Setup(x => x.Global).Returns(new GlobalContext
                {
                    Debugger = new DebuggerConfig(true, null)
                });

                await Assert.ThrowsAsync<ArgumentException>(() => _debugger.StartAsync(jobContext.Object));
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async Task StartAsyncUsesPortFromTunnelConfig()
        {
            using (CreateTestContext())
            {
                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port);
                await _debugger.StartAsync(jobContext.Object);
                using var client = await ConnectClientAsync(port);
                Assert.True(client.Connected);
                await _debugger.StopAsync();
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async Task StartAsyncWithWebSocketBridgeAcceptsInitializeOverWebSocket()
        {
            using (CreateTestContext(enableWebSocketBridge: true))
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, GetFreePort());
                await _debugger.StartAsync(jobContext.Object);

                var bridgePort = _testWebSocketBridge.ListenPort;
                Assert.NotEqual(0, _debugger.InternalDapPort);
                Assert.NotEqual(0, bridgePort);
                Assert.NotEqual(bridgePort, _debugger.InternalDapPort);

                using var client = await ConnectWebSocketClientAsync(bridgePort);
                await SendRequestAsync(client, new Request
                {
                    Seq = 1,
                    Type = "request",
                    Command = "initialize"
                });

                var response = await ReadWebSocketDataUntilAsync(
                    client,
                    TimeSpan.FromSeconds(5),
                    "\"type\":\"response\"",
                    "\"command\":\"initialize\"",
                    "\"event\":\"initialized\"");

                Assert.Contains("\"success\":true", response);
                await _debugger.StopAsync();
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async Task StartAsyncWithWebSocketBridgeAcceptsPreUpgradedWebSocketStream()
        {
            using (CreateTestContext(enableWebSocketBridge: true))
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, GetFreePort());
                await _debugger.StartAsync(jobContext.Object);

                var bridgePort = _testWebSocketBridge.ListenPort;
                Assert.NotEqual(0, _debugger.InternalDapPort);
                Assert.NotEqual(0, bridgePort);
                Assert.NotEqual(bridgePort, _debugger.InternalDapPort);

                using var tcpClient = await ConnectClientAsync(bridgePort);
                using var webSocket = WebSocket.CreateFromStream(
                    tcpClient.GetStream(),
                    isServer: false,
                    subProtocol: null,
                    keepAliveInterval: TimeSpan.FromSeconds(30));

                await SendRequestAsync(webSocket, new Request
                {
                    Seq = 1,
                    Type = "request",
                    Command = "initialize"
                });

                var response = await ReadWebSocketDataUntilAsync(
                    webSocket,
                    TimeSpan.FromSeconds(5),
                    "\"type\":\"response\"",
                    "\"command\":\"initialize\"",
                    "\"event\":\"initialized\"");

                Assert.Contains("\"success\":true", response);
                await _debugger.StopAsync();
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void ResolveTimeoutUsesCustomTimeoutFromEnvironment()
        {
            using (CreateTestContext())
            {
                WithEnvironmentVariable(TimeoutEnvironmentVariable, "30", () =>
                {
                    Assert.Equal(30, _debugger.ResolveTimeout());
                });
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void ResolveTimeoutIgnoresInvalidTimeoutFromEnvironment()
        {
            using (CreateTestContext())
            {
                WithEnvironmentVariable(TimeoutEnvironmentVariable, "not-a-number", () =>
                {
                    Assert.Equal(15, _debugger.ResolveTimeout());
                });
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void ResolveTimeoutIgnoresZeroTimeoutFromEnvironment()
        {
            using (CreateTestContext())
            {
                WithEnvironmentVariable(TimeoutEnvironmentVariable, "0", () =>
                {
                    Assert.Equal(15, _debugger.ResolveTimeout());
                });
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async Task StartAndStopLifecycle()
        {
            using (CreateTestContext())
            {
                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port);
                await _debugger.StartAsync(jobContext.Object);
                using var client = await ConnectClientAsync(port);
                Assert.True(client.Connected);
                await _debugger.StopAsync();
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async Task StartAndStopMultipleTimesDoesNotThrow()
        {
            using (CreateTestContext())
            {
                foreach (var port in new[] { GetFreePort(), GetFreePort() })
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var jobContext = CreateJobContextWithTunnel(cts.Token, port);
                    await _debugger.StartAsync(jobContext.Object);
                    await _debugger.StopAsync();
                }
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async Task WaitUntilReadyCompletesAfterClientConnectionAndConfigurationDone()
        {
            using (CreateTestContext())
            {
                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port);
                await _debugger.StartAsync(jobContext.Object);

                var waitTask = _debugger.WaitUntilReadyAsync();
                using var client = await ConnectClientAsync(port);
                await SendRequestAsync(client.GetStream(), new Request
                {
                    Seq = 1,
                    Type = "request",
                    Command = "configurationDone"
                });

                await waitTask;
                Assert.Equal(DapSessionState.Ready, _debugger.State);
                await _debugger.StopAsync();
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async Task StartStoresJobContextForThreadsRequest()
        {
            using (CreateTestContext())
            {
                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port, "ci-job");
                await _debugger.StartAsync(jobContext.Object);
                using var client = await ConnectClientAsync(port);
                var stream = client.GetStream();
                await SendRequestAsync(client.GetStream(), new Request
                {
                    Seq = 1,
                    Type = "request",
                    Command = "threads"
                });

                var response = await ReadDapMessageAsync(stream, TimeSpan.FromSeconds(5));
                Assert.Contains("\"command\":\"threads\"", response);
                Assert.Contains("\"name\":\"Job: ci-job\"", response);
                await _debugger.StopAsync();
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async Task CancellationUnblocksAndOnJobCompletedTerminates()
        {
            using (CreateTestContext())
            {
                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port);
                await _debugger.StartAsync(jobContext.Object);

                var waitTask = _debugger.WaitUntilReadyAsync();
                using var client = await ConnectClientAsync(port);
                await SendRequestAsync(client.GetStream(), new Request
                {
                    Seq = 1,
                    Type = "request",
                    Command = "configurationDone"
                });

                await waitTask;
                cts.Cancel();

                // In the real runner, JobRunner always calls OnJobCompletedAsync
                // from a finally block. The cancellation callback only unblocks
                // pending waits; OnJobCompletedAsync handles state + cleanup.
                await _debugger.OnJobCompletedAsync();
                Assert.Equal(DapSessionState.Terminated, _debugger.State);
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async Task StopWithoutStartDoesNotThrow()
        {
            using (CreateTestContext())
            {
                await _debugger.StopAsync();
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async Task OnJobCompletedTerminatesSession()
        {
            using (CreateTestContext())
            {
                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port);
                await _debugger.StartAsync(jobContext.Object);

                var waitTask = _debugger.WaitUntilReadyAsync();
                using var client = await ConnectClientAsync(port);
                await SendRequestAsync(client.GetStream(), new Request
                {
                    Seq = 1,
                    Type = "request",
                    Command = "configurationDone"
                });

                await waitTask;
                await _debugger.OnJobCompletedAsync();
                Assert.Equal(DapSessionState.Terminated, _debugger.State);
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async Task WaitUntilReadyBeforeStartIsNoOp()
        {
            using (CreateTestContext())
            {
                await _debugger.WaitUntilReadyAsync();
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async Task WaitUntilReadyJobCancellationPropagatesAsOperationCancelledException()
        {
            using (CreateTestContext())
            {
                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port);
                await _debugger.StartAsync(jobContext.Object);

                var waitTask = _debugger.WaitUntilReadyAsync();
                await Task.Delay(50);
                cts.Cancel();

                var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitTask);
                Assert.IsNotType<TimeoutException>(ex);
                await _debugger.StopAsync();
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async Task InitializeRequestOverSocketPreservesProtocolMetadataWhenSecretsCollide()
        {
            using (var hc = CreateTestContext())
            {
                hc.SecretMasker.AddValue("response");
                hc.SecretMasker.AddValue("initialize");
                hc.SecretMasker.AddValue("event");
                hc.SecretMasker.AddValue("initialized");

                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port);
                await _debugger.StartAsync(jobContext.Object);
                using var client = await ConnectClientAsync(port);
                var stream = client.GetStream();

                await SendRequestAsync(stream, new Request
                {
                    Seq = 1,
                    Type = "request",
                    Command = "initialize"
                });

                var response = await ReadDapMessageAsync(stream, TimeSpan.FromSeconds(5));
                Assert.Contains("\"type\":\"response\"", response);
                Assert.Contains("\"command\":\"initialize\"", response);
                Assert.Contains("\"success\":true", response);

                var initializedEvent = await ReadDapMessageAsync(stream, TimeSpan.FromSeconds(5));
                Assert.Contains("\"type\":\"event\"", initializedEvent);
                Assert.Contains("\"event\":\"initialized\"", initializedEvent);

                await _debugger.StopAsync();
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async Task CancellationDuringStepPauseReleasesWait()
        {
            using (CreateTestContext())
            {
                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port);
                await _debugger.StartAsync(jobContext.Object);

                // Complete handshake so session is ready
                var waitTask = _debugger.WaitUntilReadyAsync();
                using var client = await ConnectClientAsync(port);
                var stream = client.GetStream();
                await SendRequestAsync(stream, new Request
                {
                    Seq = 1,
                    Type = "request",
                    Command = "configurationDone"
                });
                await waitTask;

                // Simulate a step starting (which pauses)
                var step = new Mock<IStep>();
                step.Setup(s => s.DisplayName).Returns("Test Step");
                step.Setup(s => s.ExecutionContext).Returns((IExecutionContext)null);
                var stepTask = _debugger.OnStepStartingAsync(step.Object);

                // Give the step time to pause
                await Task.Delay(50);

                // Cancel the job — should release the step pause
                cts.Cancel();
                await stepTask;

                // In the real runner, OnJobCompletedAsync always follows.
                await _debugger.OnJobCompletedAsync();
                Assert.Equal(DapSessionState.Terminated, _debugger.State);
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async Task StopAsyncSafeAtAnyLifecyclePoint()
        {
            using (CreateTestContext())
            {
                // StopAsync before start
                await _debugger.StopAsync();

                // Start then immediate stop (no connection, no WaitUntilReady)
                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port);
                await _debugger.StartAsync(jobContext.Object);
                await _debugger.StopAsync();

                // StopAsync after already stopped
                await _debugger.StopAsync();
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async Task OnJobCompletedSendsTerminatedAndExitedEvents()
        {
            using (CreateTestContext())
            {
                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port);
                await _debugger.StartAsync(jobContext.Object);

                var waitTask = _debugger.WaitUntilReadyAsync();
                using var client = await ConnectClientAsync(port);
                var stream = client.GetStream();
                await SendRequestAsync(stream, new Request
                {
                    Seq = 1,
                    Type = "request",
                    Command = "configurationDone"
                });

                // Read the configurationDone response
                await ReadDapMessageAsync(stream, TimeSpan.FromSeconds(5));
                // Read the welcome message output event
                await ReadDapMessageAsync(stream, TimeSpan.FromSeconds(5));
                await waitTask;

                // Complete the job — OnJobCompletedAsync pauses when stepping,
                // so run it in the background and send continue to unblock.
                var completedTask = _debugger.OnJobCompletedAsync();

                // Read the stopped event from the pause
                var stoppedMsg = await ReadDapMessageAsync(stream, TimeSpan.FromSeconds(5));
                Assert.Contains("\"event\":\"stopped\"", stoppedMsg);

                // Send continue to unblock the pause
                await SendRequestAsync(stream, new Request
                {
                    Seq = 2,
                    Type = "request",
                    Command = "continue"
                });

                await completedTask;

                // Read remaining messages — continue response + continued event + terminated + exited
                var allMessages = new System.Text.StringBuilder();
                for (int i = 0; i < 4; i++)
                {
                    allMessages.Append(await ReadDapMessageAsync(stream, TimeSpan.FromSeconds(5)));
                }

                var combined = allMessages.ToString();
                Assert.Contains("\"event\":\"terminated\"", combined);
                Assert.Contains("\"event\":\"exited\"", combined);
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void ResolveTunnelConnectTimeoutReturnsDefaultWhenNoVariable()
        {
            using (CreateTestContext())
            {
                Assert.Equal(30, _debugger.ResolveTunnelConnectTimeout());
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void ResolveTunnelConnectTimeoutUsesCustomValue()
        {
            using (CreateTestContext())
            {
                WithEnvironmentVariable(TunnelConnectTimeoutVariable, "60", () =>
                {
                    Assert.Equal(60, _debugger.ResolveTunnelConnectTimeout());
                });
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void ResolveTunnelConnectTimeoutIgnoresInvalidValue()
        {
            using (CreateTestContext())
            {
                WithEnvironmentVariable(TunnelConnectTimeoutVariable, "not-a-number", () =>
                {
                    Assert.Equal(30, _debugger.ResolveTunnelConnectTimeout());
                });
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void ResolveTunnelConnectTimeoutIgnoresZeroValue()
        {
            using (CreateTestContext())
            {
                WithEnvironmentVariable(TunnelConnectTimeoutVariable, "0", () =>
                {
                    Assert.Equal(30, _debugger.ResolveTunnelConnectTimeout());
                });
            }
        }
        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async Task WaitForCommandAsyncUnblocksOnCancellationDuringWait()
        {
            using (CreateTestContext())
            {
                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port);
                await _debugger.StartAsync(jobContext.Object);

                var waitTask = _debugger.WaitUntilReadyAsync();
                using var client = await ConnectClientAsync(port);
                var stream = client.GetStream();
                await SendRequestAsync(stream, new Request
                {
                    Seq = 1,
                    Type = "request",
                    Command = "configurationDone"
                });

                await ReadDapMessageAsync(stream, TimeSpan.FromSeconds(5));
                // Read the welcome message output event
                await ReadDapMessageAsync(stream, TimeSpan.FromSeconds(5));
                await waitTask;

                // Start OnJobCompletedAsync — it will pause because _pauseOnNextStep is true
                var completedTask = _debugger.OnJobCompletedAsync();

                // Read the stopped event
                var stoppedMsg = await ReadDapMessageAsync(stream, TimeSpan.FromSeconds(5));
                Assert.Contains("\"event\":\"stopped\"", stoppedMsg);

                // Cancel the job while waiting — should unblock the pause
                cts.Cancel();

                // OnJobCompletedAsync should complete without hanging
                var finished = await Task.WhenAny(completedTask, Task.Delay(TimeSpan.FromSeconds(5)));
                Assert.Equal(completedTask, finished);
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async Task WelcomeMessageSendsDefaultHelpWhenOverrideDisabled()
        {
            using (CreateTestContext())
            {
                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port);
                await _debugger.StartAsync(jobContext.Object);

                using var client = await ConnectClientAsync(port);
                var stream = client.GetStream();

                await SendRequestAsync(stream, new Request
                {
                    Seq = 1,
                    Type = "request",
                    Command = "configurationDone"
                });

                // First message: configurationDone response
                var configDoneResponse = await ReadDapMessageAsync(stream, TimeSpan.FromSeconds(5));
                Assert.Contains("\"command\":\"configurationDone\"", configDoneResponse);

                // Second message: welcome output event with default help text
                var welcomeMsg = await ReadDapMessageAsync(stream, TimeSpan.FromSeconds(5));
                Assert.Contains("\"event\":\"output\"", welcomeMsg);
                Assert.Contains("\"category\":\"console\"", welcomeMsg);
                Assert.Contains("Actions Debug Console", welcomeMsg);
                Assert.Contains("help", welcomeMsg);

                await _debugger.StopAsync();
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async Task WelcomeMessageShowsCustomMessageWhenOverrideEnabled()
        {
            using (CreateTestContext())
            {
                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port,
                    overrideWelcomeMessage: true,
                    welcomeMessage: "Welcome to debugging!");
                await _debugger.StartAsync(jobContext.Object);

                using var client = await ConnectClientAsync(port);
                var stream = client.GetStream();

                await SendRequestAsync(stream, new Request
                {
                    Seq = 1,
                    Type = "request",
                    Command = "configurationDone"
                });

                // First: configurationDone response
                var configDoneResponse = await ReadDapMessageAsync(stream, TimeSpan.FromSeconds(5));
                Assert.Contains("\"command\":\"configurationDone\"", configDoneResponse);

                // Second: custom welcome message
                var welcomeMsg = await ReadDapMessageAsync(stream, TimeSpan.FromSeconds(5));
                Assert.Contains("\"event\":\"output\"", welcomeMsg);
                Assert.Contains("Welcome to debugging!", welcomeMsg);

                await _debugger.StopAsync();
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async Task WelcomeMessageSuppressedWhenOverrideEnabledWithEmptyMessage()
        {
            using (CreateTestContext())
            {
                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port,
                    overrideWelcomeMessage: true,
                    welcomeMessage: "");
                await _debugger.StartAsync(jobContext.Object);

                using var client = await ConnectClientAsync(port);
                var stream = client.GetStream();

                await SendRequestAsync(stream, new Request
                {
                    Seq = 1,
                    Type = "request",
                    Command = "configurationDone"
                });

                // Read configurationDone response
                var configDoneResponse = await ReadDapMessageAsync(stream, TimeSpan.FromSeconds(5));
                Assert.Contains("\"command\":\"configurationDone\"", configDoneResponse);

                // Send threads request — if welcome message was suppressed, this
                // should be the next response (no output event in between)
                await SendRequestAsync(stream, new Request
                {
                    Seq = 2,
                    Type = "request",
                    Command = "threads"
                });

                var threadsResponse = await ReadDapMessageAsync(stream, TimeSpan.FromSeconds(5));
                Assert.Contains("\"command\":\"threads\"", threadsResponse);

                await _debugger.StopAsync();
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async Task WelcomeMessageSuppressedWhenOverrideEnabledWithNullMessage()
        {
            using (CreateTestContext())
            {
                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port,
                    overrideWelcomeMessage: true,
                    welcomeMessage: null);
                await _debugger.StartAsync(jobContext.Object);

                using var client = await ConnectClientAsync(port);
                var stream = client.GetStream();

                await SendRequestAsync(stream, new Request
                {
                    Seq = 1,
                    Type = "request",
                    Command = "configurationDone"
                });

                // Read configurationDone response
                var configDoneResponse = await ReadDapMessageAsync(stream, TimeSpan.FromSeconds(5));
                Assert.Contains("\"command\":\"configurationDone\"", configDoneResponse);

                // Send threads request — if welcome message was suppressed, this
                // should be the next response (no output event in between)
                await SendRequestAsync(stream, new Request
                {
                    Seq = 2,
                    Type = "request",
                    Command = "threads"
                });

                var threadsResponse = await ReadDapMessageAsync(stream, TimeSpan.FromSeconds(5));
                Assert.Contains("\"command\":\"threads\"", threadsResponse);

                await _debugger.StopAsync();
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async Task WelcomeMessageSentOnlyOnce()
        {
            using (CreateTestContext())
            {
                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port);
                await _debugger.StartAsync(jobContext.Object);

                using var client = await ConnectClientAsync(port);
                var stream = client.GetStream();

                // First configurationDone
                await SendRequestAsync(stream, new Request
                {
                    Seq = 1,
                    Type = "request",
                    Command = "configurationDone"
                });

                var configDoneResponse = await ReadDapMessageAsync(stream, TimeSpan.FromSeconds(5));
                Assert.Contains("\"command\":\"configurationDone\"", configDoneResponse);

                // Welcome message should appear
                var welcomeMsg = await ReadDapMessageAsync(stream, TimeSpan.FromSeconds(5));
                Assert.Contains("\"event\":\"output\"", welcomeMsg);
                Assert.Contains("Actions Debug Console", welcomeMsg);

                // Second configurationDone — should NOT produce another welcome message
                await SendRequestAsync(stream, new Request
                {
                    Seq = 2,
                    Type = "request",
                    Command = "configurationDone"
                });

                var secondResponse = await ReadDapMessageAsync(stream, TimeSpan.FromSeconds(5));
                Assert.Contains("\"command\":\"configurationDone\"", secondResponse);

                // Next message should be threads response, not another welcome output
                await SendRequestAsync(stream, new Request
                {
                    Seq = 3,
                    Type = "request",
                    Command = "threads"
                });

                var threadsResponse = await ReadDapMessageAsync(stream, TimeSpan.FromSeconds(5));
                Assert.Contains("\"command\":\"threads\"", threadsResponse);

                await _debugger.StopAsync();
            }
        }

        // ---------------------------------------------------------------------
        // Phase 2c: synthesized execution view as DAP source.
        // ---------------------------------------------------------------------

        private static Mock<GitHub.Runner.Worker.IActionRunner> NewActionRunner(
            GitHub.Runner.Worker.ActionRunStage stage,
            string displayName,
            string actionName = "actions/checkout",
            string actionRef = "v4")
        {
            var mock = new Mock<GitHub.Runner.Worker.IActionRunner>();
            mock.SetupGet(x => x.Stage).Returns(stage);
            mock.SetupGet(x => x.DisplayName).Returns(displayName);
            mock.SetupGet(x => x.Action).Returns(new GitHub.DistributedTask.Pipelines.ActionStep
            {
                Reference = new GitHub.DistributedTask.Pipelines.RepositoryPathReference
                {
                    Name = actionName,
                    Ref = actionRef,
                },
            });
            return mock;
        }

        private async Task DriveDebuggerToReadyAsync(int port)
        {
            var waitTask = _debugger.WaitUntilReadyAsync();
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var stream = client.GetStream();
            await SendRequestAsync(stream, new Request
            {
                Seq = 1,
                Type = "request",
                Command = "configurationDone",
            });
            await waitTask;
            // Hold the client alive via GC root in caller scope through field.
            _liveDriveClient = client;
        }

        private TcpClient _liveDriveClient;

        private static Request MakeRequest(string command, object args = null)
        {
            return new Request
            {
                Seq = 1,
                Type = "request",
                Command = command,
                Arguments = args == null ? null : Newtonsoft.Json.Linq.JObject.FromObject(args),
            };
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async Task HandleSource_ReturnsExecutionViewYaml()
        {
            using (CreateTestContext())
            {
                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port, "ci-job");
                await _debugger.StartAsync(jobContext.Object);
                try
                {
                    await DriveDebuggerToReadyAsync(port);
                    var step = NewActionRunner(GitHub.Runner.Worker.ActionRunStage.Main, "Run").Object;
                    await _debugger.OnJobStepsInitializedAsync(new[] { step }, Array.Empty<IStep>());

                    var response = _debugger.HandleSource(MakeRequest("source", new SourceArguments { SourceReference = 1 }));
                    Assert.True(response.Success);
                    var body = Assert.IsType<SourceResponseBody>(response.Body);
                    Assert.Equal(_debugger.ExecutionView.Yaml, body.Content);
                    Assert.Null(body.MimeType);
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
        public async Task HandleSource_UnknownReference_Fails()
        {
            using (CreateTestContext())
            {
                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port, "ci-job");
                await _debugger.StartAsync(jobContext.Object);
                try
                {
                    await DriveDebuggerToReadyAsync(port);
                    var step = NewActionRunner(GitHub.Runner.Worker.ActionRunStage.Main, "Run").Object;
                    await _debugger.OnJobStepsInitializedAsync(new[] { step }, Array.Empty<IStep>());

                    var response = _debugger.HandleSource(MakeRequest("source", new SourceArguments { SourceReference = 999 }));
                    Assert.False(response.Success);
                    Assert.Contains("Unknown source reference", response.Message);
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
        public async Task HandleSource_NoView_Fails()
        {
            using (CreateTestContext())
            {
                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port, "ci-job");
                await _debugger.StartAsync(jobContext.Object);
                try
                {
                    await DriveDebuggerToReadyAsync(port);
                    // No OnJobStepsInitializedAsync call — view is null.
                    var response = _debugger.HandleSource(MakeRequest("source", new SourceArguments { SourceReference = 1 }));
                    Assert.False(response.Success);
                    Assert.Contains("Execution view not yet available", response.Message);
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
        public async Task HandleSource_OmitsMimeType()
        {
            using (CreateTestContext())
            {
                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port, "ci-job");
                await _debugger.StartAsync(jobContext.Object);
                try
                {
                    await DriveDebuggerToReadyAsync(port);
                    var step = NewActionRunner(GitHub.Runner.Worker.ActionRunStage.Main, "Run").Object;
                    await _debugger.OnJobStepsInitializedAsync(new[] { step }, Array.Empty<IStep>());

                    var response = _debugger.HandleSource(MakeRequest("source", new SourceArguments { SourceReference = 1 }));
                    Assert.True(response.Success);
                    var body = Assert.IsType<SourceResponseBody>(response.Body);
                    // MimeType is intentionally omitted so DAP clients fall through
                    // to path-extension detection (`.yml`) — the most consistently
                    // honored mechanism across VS Code, nvim-dap, and JetBrains.
                    Assert.Null(body.MimeType);
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
        public async Task HandleSource_MasksSecrets()
        {
            const string Secret = "supersecret-shhh-value";
            using (var hc = CreateTestContext())
            {
                hc.SecretMasker.AddValue(Secret);

                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port, "ci-job");
                await _debugger.StartAsync(jobContext.Object);
                try
                {
                    await DriveDebuggerToReadyAsync(port);
                    // Embed the secret in a step display name so it ends up in the rendered YAML.
                    var step = NewActionRunner(GitHub.Runner.Worker.ActionRunStage.Main, $"Run {Secret} step").Object;
                    await _debugger.OnJobStepsInitializedAsync(new[] { step }, Array.Empty<IStep>());

                    // Sanity-check the raw view actually contains the secret;
                    // otherwise the masking assertion below would pass vacuously.
                    Assert.Contains(Secret, _debugger.ExecutionView.Yaml);

                    var response = _debugger.HandleSource(MakeRequest("source", new SourceArguments { SourceReference = 1 }));
                    Assert.True(response.Success);
                    var body = Assert.IsType<SourceResponseBody>(response.Body);
                    Assert.DoesNotContain(Secret, body.Content);
                    Assert.Contains("***", body.Content);
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
        public async Task HandleStackTrace_MasksSecretsInSourcePath()
        {
            const string Secret = "secret-job-name-xyz";
            using (var hc = CreateTestContext())
            {
                hc.SecretMasker.AddValue(Secret);

                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                // Job name (== GitHub context "job") embeds the secret.
                var jobContext = CreateJobContextWithTunnel(cts.Token, port, Secret);
                await _debugger.StartAsync(jobContext.Object);
                try
                {
                    await DriveDebuggerToReadyAsync(port);
                    var step = NewActionRunner(GitHub.Runner.Worker.ActionRunStage.Main, "Run").Object;
                    await _debugger.OnJobStepsInitializedAsync(new[] { step }, Array.Empty<IStep>());

                    _ = _debugger.OnStepStartingAsync(step, isFirstStep: false);
                    await Task.Delay(50);

                    var response = _debugger.HandleStackTrace(MakeRequest("stackTrace"));
                    var body = Assert.IsType<StackTraceResponseBody>(response.Body);
                    Assert.NotEmpty(body.StackFrames);
                    foreach (var frame in body.StackFrames)
                    {
                        if (frame.Source != null)
                        {
                            Assert.DoesNotContain(Secret, frame.Source.Path ?? string.Empty);
                            Assert.DoesNotContain(Secret, frame.Source.Name ?? string.Empty);
                            Assert.Contains("***", frame.Source.Path ?? string.Empty);
                        }
                        Assert.DoesNotContain(Secret, frame.Name ?? string.Empty);
                    }
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
        public async Task HandleStackTrace_TwoFramesWhenStepping()
        {
            using (CreateTestContext())
            {
                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port, "ci-job");
                await _debugger.StartAsync(jobContext.Object);
                try
                {
                    await DriveDebuggerToReadyAsync(port);
                    var step1 = NewActionRunner(GitHub.Runner.Worker.ActionRunStage.Main, "Step One").Object;
                    var step2 = NewActionRunner(GitHub.Runner.Worker.ActionRunStage.Main, "Step Two", "actions/setup-node", "v3").Object;
                    await _debugger.OnJobStepsInitializedAsync(new[] { step1, step2 }, Array.Empty<IStep>());

                    // Fire-and-forget: first step pauses, but we just want _currentStep set.
                    _ = _debugger.OnStepStartingAsync(step1, isFirstStep: false);
                    await Task.Delay(50);

                    var response = _debugger.HandleStackTrace(MakeRequest("stackTrace"));
                    var body = Assert.IsType<StackTraceResponseBody>(response.Body);
                    Assert.Equal(2, body.StackFrames.Count);

                    var frame0 = body.StackFrames[0];
                    Assert.Equal(1, frame0.Source.SourceReference);
                    Assert.Equal(_debugger.ExecutionView.TryGetLineForStep(step1), frame0.Line);
                    Assert.Equal("normal", frame0.PresentationHint);

                    var frame1 = body.StackFrames[1];
                    Assert.Equal(1, frame1.Line);
                    Assert.Equal("subtle", frame1.PresentationHint);
                    Assert.Equal(1, frame1.Source.SourceReference);
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
        public async Task HandleStackTrace_OneFrameWhenViewMissing()
        {
            using (CreateTestContext())
            {
                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port, "ci-job");
                await _debugger.StartAsync(jobContext.Object);
                try
                {
                    await DriveDebuggerToReadyAsync(port);
                    var step = NewActionRunner(GitHub.Runner.Worker.ActionRunStage.Main, "Lonely").Object;
                    // No view built — but pause the step so _currentStep is set.
                    _ = _debugger.OnStepStartingAsync(step, isFirstStep: false);
                    await Task.Delay(50);

                    var response = _debugger.HandleStackTrace(MakeRequest("stackTrace"));
                    var body = Assert.IsType<StackTraceResponseBody>(response.Body);
                    Assert.Single(body.StackFrames);
                    Assert.Null(body.StackFrames[0].Source);
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
        public async Task HandleStackTrace_NoFramesWhenIdle()
        {
            using (CreateTestContext())
            {
                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port, "ci-job");
                await _debugger.StartAsync(jobContext.Object);
                try
                {
                    await DriveDebuggerToReadyAsync(port);
                    var response = _debugger.HandleStackTrace(MakeRequest("stackTrace"));
                    var body = Assert.IsType<StackTraceResponseBody>(response.Body);
                    Assert.Empty(body.StackFrames);
                    Assert.Equal(0, body.TotalFrames);
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
        public async Task HandleLoadedSources_ReturnsExecutionView()
        {
            using (CreateTestContext())
            {
                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port, "ci-job");
                await _debugger.StartAsync(jobContext.Object);
                try
                {
                    await DriveDebuggerToReadyAsync(port);
                    var step = NewActionRunner(GitHub.Runner.Worker.ActionRunStage.Main, "Run").Object;
                    await _debugger.OnJobStepsInitializedAsync(new[] { step }, Array.Empty<IStep>());

                    var response = _debugger.HandleLoadedSources(MakeRequest("loadedSources"));
                    Assert.True(response.Success);
                    var body = Assert.IsType<LoadedSourcesResponseBody>(response.Body);
                    Assert.Single(body.Sources);
                    Assert.Equal("execution.yml", body.Sources[0].Name);
                    Assert.Equal("ci-job/execution.yml", body.Sources[0].Path);
                    Assert.Equal(1, body.Sources[0].SourceReference);
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
        public async Task HandleLoadedSources_NoView_ReturnsEmpty()
        {
            using (CreateTestContext())
            {
                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port, "ci-job");
                await _debugger.StartAsync(jobContext.Object);
                try
                {
                    await DriveDebuggerToReadyAsync(port);
                    var response = _debugger.HandleLoadedSources(MakeRequest("loadedSources"));
                    Assert.True(response.Success);
                    var body = Assert.IsType<LoadedSourcesResponseBody>(response.Body);
                    Assert.Empty(body.Sources);
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
        public async Task HandleSetBreakpoints_ReturnsUnverifiedPlaceholders()
        {
            using (CreateTestContext())
            {
                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port, "ci-job");
                await _debugger.StartAsync(jobContext.Object);
                try
                {
                    await DriveDebuggerToReadyAsync(port);
                    var step = NewActionRunner(GitHub.Runner.Worker.ActionRunStage.Main, "Run").Object;
                    await _debugger.OnJobStepsInitializedAsync(new[] { step }, Array.Empty<IStep>());

                    var args = new SetBreakpointsArguments
                    {
                        Source = new Source { SourceReference = 1 },
                        Breakpoints = new System.Collections.Generic.List<SourceBreakpoint>
                        {
                            new SourceBreakpoint { Line = 5 },
                            new SourceBreakpoint { Line = 10 },
                            new SourceBreakpoint { Line = 15 },
                        },
                    };

                    var response = _debugger.HandleSetBreakpoints(MakeRequest("setBreakpoints", args));
                    Assert.True(response.Success);
                    var body = Assert.IsType<SetBreakpointsResponseBody>(response.Body);
                    Assert.Equal(3, body.Breakpoints.Count);
                    Assert.All(body.Breakpoints, bp => Assert.False(bp.Verified));
                    Assert.Equal(new[] { 5, 10, 15 }, body.Breakpoints.ConvertAll(b => b.Line ?? -1));
                    Assert.All(body.Breakpoints, bp => Assert.False(string.IsNullOrEmpty(bp.Message)));
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
        public async Task OnJobStepsInitialized_EmitsLoadedSourceNewEvent()
        {
            using (CreateTestContext())
            {
                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port, "ci-job");
                await _debugger.StartAsync(jobContext.Object);
                using var client = await ConnectClientAsync(port);
                try
                {
                    var stream = client.GetStream();
                    await SendRequestAsync(stream, new Request { Seq = 1, Type = "request", Command = "configurationDone" });
                    await ReadDapMessageAsync(stream, TimeSpan.FromSeconds(5)); // configurationDone response
                    await ReadDapMessageAsync(stream, TimeSpan.FromSeconds(5)); // welcome output event
                    await _debugger.WaitUntilReadyAsync();

                    var step = NewActionRunner(GitHub.Runner.Worker.ActionRunStage.Main, "Run").Object;
                    await _debugger.OnJobStepsInitializedAsync(new[] { step }, Array.Empty<IStep>());

                    var msg = await ReadDapMessageAsync(stream, TimeSpan.FromSeconds(5));
                    Assert.Contains("\"event\":\"loadedSource\"", msg);
                    Assert.Contains("\"reason\":\"new\"", msg);
                    Assert.Contains("\"sourceReference\":1", msg);
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
        public async Task OnPostStepRegistered_EmitsLoadedSourceChangedEvent()
        {
            using (CreateTestContext())
            {
                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port, "ci-job");
                await _debugger.StartAsync(jobContext.Object);
                using var client = await ConnectClientAsync(port);
                try
                {
                    var stream = client.GetStream();
                    await SendRequestAsync(stream, new Request { Seq = 1, Type = "request", Command = "configurationDone" });
                    await ReadDapMessageAsync(stream, TimeSpan.FromSeconds(5)); // configurationDone response
                    await ReadDapMessageAsync(stream, TimeSpan.FromSeconds(5)); // welcome output event
                    await _debugger.WaitUntilReadyAsync();

                    var main = NewActionRunner(GitHub.Runner.Worker.ActionRunStage.Main, "Run").Object;
                    await _debugger.OnJobStepsInitializedAsync(new[] { main }, Array.Empty<IStep>());
                    // Drain the "new" loadedSource event.
                    await ReadDapMessageAsync(stream, TimeSpan.FromSeconds(5));

                    var post = NewActionRunner(GitHub.Runner.Worker.ActionRunStage.Post, "Post Run", "actions/cache", "v3").Object;
                    _debugger.OnPostStepRegistered(post);

                    var msg = await ReadDapMessageAsync(stream, TimeSpan.FromSeconds(5));
                    Assert.Contains("\"event\":\"loadedSource\"", msg);
                    Assert.Contains("\"reason\":\"changed\"", msg);
                    Assert.Contains("\"sourceReference\":1", msg);
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
        public async Task StackTrace_LineUpdatesAsStepsAdvance()
        {
            using (CreateTestContext())
            {
                var port = GetFreePort();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var jobContext = CreateJobContextWithTunnel(cts.Token, port, "ci-job");
                await _debugger.StartAsync(jobContext.Object);
                try
                {
                    await DriveDebuggerToReadyAsync(port);
                    var s1 = NewActionRunner(GitHub.Runner.Worker.ActionRunStage.Main, "Step 1", "a/b", "v1").Object;
                    var s2 = NewActionRunner(GitHub.Runner.Worker.ActionRunStage.Main, "Step 2", "c/d", "v2").Object;
                    await _debugger.OnJobStepsInitializedAsync(new[] { s1, s2 }, Array.Empty<IStep>());

                    _ = _debugger.OnStepStartingAsync(s1, isFirstStep: true);
                    await Task.Delay(50);

                    var first = _debugger.HandleStackTrace(MakeRequest("stackTrace"));
                    var firstBody = Assert.IsType<StackTraceResponseBody>(first.Body);
                    int firstLine = firstBody.StackFrames[0].Line;
                    Assert.Equal(_debugger.ExecutionView.TryGetLineForStep(s1), firstLine);

                    _ = _debugger.OnStepStartingAsync(s2, isFirstStep: false);
                    await Task.Delay(50);

                    var second = _debugger.HandleStackTrace(MakeRequest("stackTrace"));
                    var secondBody = Assert.IsType<StackTraceResponseBody>(second.Body);
                    int secondLine = secondBody.StackFrames[0].Line;
                    Assert.Equal(_debugger.ExecutionView.TryGetLineForStep(s2), secondLine);
                    Assert.NotEqual(firstLine, secondLine);
                }
                finally
                {
                    await _debugger.StopAsync();
                }
            }
        }
    }
}
