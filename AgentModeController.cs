using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using DigitRaver.Bridge.LoopbackWS;
using DigitRaver.Env;
using Firebase.Auth;
using Newtonsoft.Json.Linq;
using PerSpec;
using UnityEngine;
#if FIREBASE_MULTI_INSTANCE
using DigitRaver.Bridge.FirebaseIsolation;
#endif

namespace DigitRaver.Bridge.Agent
{
    /// <summary>
    /// Agent mode controller - runs LLM reasoning loop with direct tool execution.
    /// Bypasses WebSocket/MCP entirely, calling domain handlers directly.
    /// </summary>
    public class AgentModeController : MonoBehaviour
    {
        [SerializeField] [DigitRaver.ReadOnly] 
        private AgentModeConfig _config;
        private BridgeServer _bridgeServer;
        private CancellationTokenSource _cts;
        private ConversationManager _conversation;
        private DirectToolExecutor _toolExecutor;
        private ILLMProvider _provider;
        private List<ToolDefinition> _tools;

        // Agentic build dependencies
        [SerializeField] [DigitRaver.ReadOnly] private DigitRaver.MiscRequests _miscRequests;
        [SerializeField] [DigitRaver.ReadOnly] private DigitRaver.AccountEvents _accountEvents;
        [SerializeField] [DigitRaver.ReadOnly] private DigitRaver.UserInfo _userInfo;
        [SerializeField] [DigitRaver.ReadOnly] private DigitRaver.ChatBridgeEvents _chatBridgeEvents;
        [SerializeField] [DigitRaver.ReadOnly] private DigitRaver.FXEvents _fxEvents;

        private readonly ConcurrentQueue<string> _eventQueue = new ConcurrentQueue<string>();
        private int _cycleCount;
        private DateTime _lastCycleTime;
        private int _toolCallsThisCycle;
        private bool _isRunning;
        private bool _hasApiKey;

        public bool IsRunning => _isRunning;
        public int CycleCount => _cycleCount;
        public AgentModeConfig Config => _config;
        public ConversationManager Conversation => _conversation;

        private void OnEnable()
        {
            PerSpecDebug.Log("[AgentMode] OnEnable called");
            // Load agentic build dependencies
            FindVarsSO();

            // Ensure API key is loaded from prefs (ClearOnPlayModeExit may have cleared the SO field)
            if (_config != null)
            {
                _config.LoadFromPlayerPrefs();
                PerSpecDebug.Log($"[AgentMode] Prefs loaded: provider={_config.provider}, model={_config.model}, hasApiKey={!string.IsNullOrEmpty(_config.apiKey)}");
            }

            PerSpecDebug.Log($"[AgentMode] Config loaded: enabled={_config.enabled}, provider={_config.provider}, model={_config.model}, hasApiKey={!string.IsNullOrEmpty(_config.apiKey)}");

            _bridgeServer = GetComponent<BridgeServer>();
            if (_bridgeServer == null)
            {
                PerSpecDebug.LogWarning("[AgentMode] BridgeServer not found. Agent disabled.");
                enabled = false;
                return;
            }
            PerSpecDebug.Log("[AgentMode] BridgeServer found");



            _config.OnAgentModeEnabled += OnConfigEnabled;
            _config.OnAgentModeDisabled += OnConfigDisabled;
            _config.OnConfigChanged += OnConfigChanged;
            _config.OnSttTranscript += HandleSttTranscript;

            if (_userInfo != null)
            {
                _userInfo.OnSignInStatusChanged += HandleSignInStatusChanged;
            }

            if (_chatBridgeEvents != null)
            {
                _chatBridgeEvents.OnChatMessageReceived += HandleChatMessage;
            }

            if (_fxEvents != null)
            {
                _fxEvents.OnBlurbDispatch += HandleBlurbDispatch;
            }

            // Handle agentic build initialization BEFORE enabling agent mode
            if (_config.isAgenticBuild)
            {
                HandleAgenticBuildStartup().Forget();
                return; // Don't check _config.enabled yet - HandleAgenticBuildStartup will call SetEnabled
            }

            if (_config.enabled)
            {
                PerSpecDebug.Log("[AgentMode] Config.enabled=true, calling StartAgent()");
                StartAgent().Forget();
            }
            else
            {
                PerSpecDebug.Log("[AgentMode] Config.enabled=false, waiting for enable");
            }
        }

        [ContextMenu("FindVars SO")]
        private void FindVarsSO()
        {
            _config = Resources.Load<AgentModeConfig>("AgentModeConfig");
            if (_config == null)
            {
                PerSpecDebug.LogWarning("[AgentMode] AgentModeConfig not found in Resources. Agent disabled.");
                enabled = false;
                return;
            }
            _miscRequests = Resources.Load<DigitRaver.MiscRequests>("MiscRequests");
            _accountEvents = Resources.Load<DigitRaver.AccountEvents>("AccountEvents");
            _userInfo = Resources.Load<DigitRaver.UserInfo>("UserInfo");
            _chatBridgeEvents = Resources.Load<DigitRaver.ChatBridgeEvents>("ChatBridgeEvents");
            _fxEvents = Resources.Load<DigitRaver.FXEvents>("FXEvents");
            EditorExtensions.MarkEditorDirty();
        }

        private void OnDisable()
        {
            StopAgent();
            if (_config != null)
            {
                _config.OnAgentModeEnabled -= OnConfigEnabled;
                _config.OnAgentModeDisabled -= OnConfigDisabled;
                _config.OnConfigChanged -= OnConfigChanged;
                _config.OnSttTranscript -= HandleSttTranscript;
            }
            if (_userInfo != null)
            {
                _userInfo.OnSignInStatusChanged -= HandleSignInStatusChanged;
            }

            if (_chatBridgeEvents != null)
            {
                _chatBridgeEvents.OnChatMessageReceived -= HandleChatMessage;
            }

            if (_fxEvents != null)
            {
                _fxEvents.OnBlurbDispatch -= HandleBlurbDispatch;
            }
        }

        private void OnConfigEnabled()
        {
            PerSpecDebug.Log("[AgentMode] OnConfigEnabled event fired");
            // Disable LoopbackWS if active (mutual exclusivity)
            DisableLoopbackWS();
            // Disable BridgeServer transport (Agent Mode uses DirectToolExecutor, not WebSocket)
            DisableBridgeServer();
            StartAgent().Forget();
        }

        private void OnConfigDisabled()
        {
            PerSpecDebug.Log("[AgentMode] OnConfigDisabled event fired");
            StopAgent();
        }

        private void OnConfigChanged()
        {
            if (!_config.enabled) return;
            PerSpecDebug.Log("[AgentMode] OnConfigChanged: restarting agent with updated config");
            StopAgent();
            StartAgent().Forget();
        }

        private void HandleSignInStatusChanged(bool isSignedIn)
        {
            if (!isSignedIn && _isRunning)
            {
                PerSpecDebug.Log("[AgentMode] User signed out, stopping agent");
                StopAgent();
            }
        }

        public void SetEnabled(bool value)
        {
            _config.SetEnabled(value);
        }

        /// <summary>
        /// Handles agentic build startup sequence:
        /// 1. Hide canvas (after CanvasManager subscribes)
        /// 2. Auto-login with random agent credentials
        /// 3. Enable agent mode AFTER login completes
        /// </summary>
        private async UniTaskVoid HandleAgenticBuildStartup()
        {
            PerSpecDebug.Log("[AgentMode] Agentic build detected, starting auto-login sequence");

            // 1. Wait a frame for CanvasManager.Awake() to subscribe to OnShowCanvas
            await UniTask.Yield();

            // 2. Hide canvas
            if (_miscRequests != null)
            {
                _miscRequests.ShowCanvas(false);
                PerSpecDebug.Log("[AgentMode] Canvas hidden");
            }
            else
            {
                PerSpecDebug.LogWarning("[AgentMode] MiscRequests not found, cannot hide canvas");
            }

            // 3. Await auto-login completion (blocking wait for result)
            await AutoLoginAsync();

            // 4. ONLY NOW enable agent mode (after login result received)
            // Ensure we're on the main thread before SetEnabled - OnConfigEnabled
            // triggers Resources.Load (DisableLoopbackWS) which requires main thread
            await UniTask.SwitchToMainThread();

            PerSpecDebug.Log("[AgentMode] Auto-login complete, now enabling agent mode");
            SetEnabled(true);
        }

        /// <summary>
        /// Performs auto-login with a random agent credential.
        /// Awaits UserInfo.OnSignInStatusChanged to ensure UserInfo is fully populated before returning.
        /// </summary>
        private async UniTask AutoLoginAsync()
        {
            // Wait for Firebase auth to be ready (polls every 1 second, 30s timeout)
            const int maxWaitSeconds = 30;
            const int pollIntervalMs = 1000;

            for (int i = 0; i < maxWaitSeconds; i++)
            {
                try
                {
                    // Check if Firebase auth is initialized and ready
#if FIREBASE_MULTI_INSTANCE
                    var auth = FirebaseIsolationService.Auth;
#else
                    var auth = FirebaseAuth.DefaultInstance;
#endif
                    if (auth != null)
                    {
                        PerSpecDebug.Log($"[AgentMode] Firebase auth ready after {i} seconds");
                        break;
                    }
                }
                catch (Exception)
                {
                    // Firebase not ready yet, continue waiting
                }

                if (i == maxWaitSeconds - 1)
                {
                    PerSpecDebug.LogWarning("[AgentMode] Firebase auth not ready after 30 seconds, aborting auto-login");
                    return;
                }

                await UniTask.Delay(pollIntervalMs);
            }

            // Sign out any persisted user from shared auth persistence before auto-login
            // This ensures OnSignInStatusChanged fires for the new user (not skipped as "re-auth")
#if FIREBASE_MULTI_INSTANCE
            {
                var auth = FirebaseIsolationService.Auth;
                if (auth.CurrentUser != null)
                {
                    PerSpecDebug.Log($"[AgentMode] Signing out persisted user: {auth.CurrentUser.UserId}");
                    auth.SignOut();
                    await UniTask.Yield(); // Allow state to settle
                }
            }
#endif

            var credential = _config.GetRandomCredential();
            if (credential == null)
            {
                PerSpecDebug.LogWarning("[AgentMode] No agent credentials configured");
                return;
            }

            if (_accountEvents == null)
            {
                PerSpecDebug.LogWarning("[AgentMode] AccountEvents not found, cannot auto-login");
                return;
            }

            if (_userInfo == null)
            {
                PerSpecDebug.LogWarning("[AgentMode] UserInfo not found, cannot auto-login");
                return;
            }

            PerSpecDebug.Log($"[AgentMode] Auto-login with: {(string)credential.email}");

            // Wait for UserInfo.OnSignInStatusChanged (fires AFTER UserInfo is populated)
            var tcs = new UniTaskCompletionSource<bool>();

            void OnSignInStatusChanged(bool isSignedIn)
            {
                if (isSignedIn)
                {
                    tcs.TrySetResult(true);
                }
            }

            // Also listen for sign-in failure via OnSignInWithEmailResult
            bool signInFailed = false;
            string failureMessage = "";

            void OnSignInResult(bool success, string msg)
            {
                if (!success)
                {
                    signInFailed = true;
                    failureMessage = msg;
                    tcs.TrySetResult(false);
                }
                // On success, wait for OnSignInStatusChanged - don't complete here
            }

            _userInfo.OnSignInStatusChanged += OnSignInStatusChanged;
            _accountEvents.OnSignInWithEmailResult += OnSignInResult;

            try
            {
                // Trigger sign-in through the standard flow
                _accountEvents.OnSignInWithEmail?.Invoke(credential.email, credential.password);

                // Wait for UserInfo to be populated (or failure)
                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    bool success = await tcs.Task.AttachExternalCancellation(cts.Token);

                    if (success && _userInfo.isSignedIn)
                    {
                        PerSpecDebug.Log($"[AgentMode] Auto-login successful: {(string)_userInfo.username} ({(string)_userInfo.userID})");
                    }
                    else if (signInFailed)
                    {
                        PerSpecDebug.LogWarning($"[AgentMode] Auto-login failed: {failureMessage}");
                    }
                }
                catch (OperationCanceledException)
                {
                    PerSpecDebug.LogWarning("[AgentMode] Auto-login timed out after 30 seconds");
                    return; // Abort auto-login instead of continuing
                }
            }
            finally
            {
                _userInfo.OnSignInStatusChanged -= OnSignInStatusChanged;
                _accountEvents.OnSignInWithEmailResult -= OnSignInResult;
            }
        }

        public JObject GetStatus()
        {
            return new JObject
            {
                ["enabled"] = _config != null && _config.enabled,
                ["running"] = _isRunning,
                ["hasApiKey"] = _hasApiKey,
                ["provider"] = _config?.provider.ToString() ?? "none",
                ["model"] = _config?.model ?? "",
                ["cycleCount"] = _cycleCount,
                ["lastCycleTime"] = _lastCycleTime.ToString("o"),
                ["pendingNudges"] = _bridgeServer?.NudgeManager?.QueueDepth ?? 0,
                ["contextTokens"] = _conversation?.EstimatedTokens ?? 0,
                ["toolCallsThisCycle"] = _toolCallsThisCycle,
                ["userSignedIn"] = _userInfo != null && _userInfo.isSignedIn,
                ["username"] = _userInfo != null ? (string)_userInfo.username : ""
            };
        }

        /// <summary>
        /// Queue an event to be processed in the next reasoning cycle.
        /// </summary>
        public void QueueEvent(string eventJson)
        {
            _eventQueue.Enqueue(eventJson);
        }

        /// <summary>
        /// Queue a nudge message (user input) to be processed in the next reasoning cycle.
        /// </summary>
        public void QueueNudge(string message)
        {
            PerSpecDebug.Log($"[AgentMode] QueueNudge called: \"{message}\"");
            if (_bridgeServer?.NudgeManager == null)
            {
                PerSpecDebug.LogWarning("[AgentMode] NudgeManager is null, cannot queue nudge");
                return;
            }
            _bridgeServer.NudgeManager.Enqueue(message);
            PerSpecDebug.Log($"[AgentMode] Nudge queued, queue depth={_bridgeServer.NudgeManager.QueueDepth}");
        }

        #region Event Handlers

        private void HandleSttTranscript(string transcript)
        {
            if (!_isRunning || string.IsNullOrEmpty(transcript))
                return;

            var nudge = $"[Voice command]: {transcript}";
            QueueNudge(nudge);
            PerSpecDebug.Log($"[AgentMode] STT transcript queued: {nudge}");
        }

        private void HandleChatMessage(string rawMessage)
        {
            if (!_isRunning || string.IsNullOrEmpty(rawMessage))
                return;

            // Parse message: format is "username,content,colorIndex,isLocation,ownerID,targetUsername[,x,y,z]"
            var parts = rawMessage.Split(',');
            if (parts.Length < 2)
                return;

            var username = parts[0].Trim();
            var content = parts[1].Trim();

            // Skip system messages (presence, invite, etc.)
            if (username.StartsWith("__") || content.StartsWith("__"))
                return;

            // Skip own messages (check against UserInfo)
            if (_userInfo != null)
            {
                var localUsername = (string)_userInfo.username;
                if (!string.IsNullOrEmpty(localUsername) &&
                    string.Equals(username, localUsername, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            // Queue as nudge for LLM reasoning
            var nudge = $"[Chat from {username}]: {content}";
            QueueNudge(nudge);
            PerSpecDebug.Log($"[AgentMode] Chat event queued: {nudge}");
        }

        private void HandleBlurbDispatch(DigitRaver.BlurbPayload payload)
        {
            if (!_isRunning || string.IsNullOrEmpty(payload.TitleContent))
                return;

            // Queue as nudge for LLM reasoning
            var nudge = $"[Blurb FX from {payload.OwnerID}]: {payload.TitleContent}";
            QueueNudge(nudge);
            PerSpecDebug.Log($"[AgentMode] Blurb event queued: {nudge}");
        }

        #endregion

        private void DisableLoopbackWS()
        {
            var loopbackConfig = Resources.Load<LoopbackWSConfig>("LoopbackWSConfig");
            if (loopbackConfig != null && loopbackConfig.enabled)
            {
                PerSpecDebug.Log("[AgentMode] Disabling LoopbackWS (mutual exclusivity)");
                loopbackConfig.SetEnabled(false);
            }
        }

        private void DisableBridgeServer()
        {
            var bridgeConfig = Resources.Load<BridgeConfig>("BridgeConfig");
            if (bridgeConfig != null && bridgeConfig.bridgeRunning)
            {
                PerSpecDebug.Log("[AgentMode] Disabling BridgeServer (mutual exclusivity)");
                bridgeConfig.ToggleBridge(false);
            }
        }

        private async UniTaskVoid StartAgent()
        {
            PerSpecDebug.Log($"[AgentMode] StartAgent called, _isRunning={_isRunning}");

            if (_isRunning)
            {
                PerSpecDebug.Log("[AgentMode] Already running, skipping StartAgent");
                return;
            }

            try
            {
                PerSpecDebug.Log("[AgentMode] Initializing...");
                _cts = new CancellationTokenSource();
                _conversation = new ConversationManager();

                PerSpecDebug.Log($"[AgentMode] Creating provider: {_config.provider}");
                _provider = CreateProvider();
                _hasApiKey = !string.IsNullOrEmpty(_config.apiKey);
                PerSpecDebug.Log($"[AgentMode] Provider created, hasApiKey={_hasApiKey}");

                // Create handlers directly - no bridge/WebSocket needed for Agent Mode
                var handlers = CreateHandlers();
                PerSpecDebug.Log($"[AgentMode] Created {handlers.Count} handlers directly");

                _tools = ToolDefinitionBuilder.BuildFromHandlers(handlers);
                PerSpecDebug.Log($"[AgentMode] Built {_tools.Count} tool definitions");

                _toolExecutor = new DirectToolExecutor(handlers, _conversation, _config.apiTimeoutMs);

                // Set system prompt
                var systemPrompt = _config.GetFullSystemPrompt();
                if (!string.IsNullOrEmpty(systemPrompt))
                {
                    _conversation.SetSystemPrompt(systemPrompt);
                    PerSpecDebug.Log($"[AgentMode] System prompt set ({systemPrompt.Length} chars)");
                }

                // Auto-start: inject initial message so agent begins immediately
                _conversation.AddUserMessage("Begin. Follow the system prompt instructions.");
                PerSpecDebug.Log("[AgentMode] Auto-start message injected");

                _isRunning = true;
                PerSpecDebug.Log("[AgentMode] _isRunning = true");

                if (!_hasApiKey)
                {
                    PerSpecDebug.LogWarning("[AgentMode] API key not configured — agent started but ReasoningLoop skipped. Set key in AgentModeConfig or DebugUI.");
                    PerSpecDebug.Log("[AgentMode] Agent started (no API key — event queue only)");
                    return;
                }

                PerSpecDebug.Log("[AgentMode] Agent started with API key, entering ReasoningLoop");

                // Start reasoning loop
                await ReasoningLoop(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                PerSpecDebug.Log("[AgentMode] StartAgent cancelled");
            }
            catch (Exception ex)
            {
                PerSpecDebug.LogError($"[AgentMode] Agent error: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                PerSpecDebug.Log("[AgentMode] StartAgent finally block, setting _isRunning = false");
                _isRunning = false;
            }
        }

        private void StopAgent()
        {
            _isRunning = false;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            PerSpecDebug.Log("[AgentMode] Agent stopped");
        }

        private ILLMProvider CreateProvider()
        {
            var apiKey = _config.apiKey;
            var model = _config.model;
            var baseUrl = _config.baseUrl;
            var maxRetries = _config.maxRetries;

            return _config.provider switch
            {
                LLMProviderType.Claude => new ClaudeProvider(apiKey, model, baseUrl, maxRetries),
                LLMProviderType.Gemini => new GeminiProvider(apiKey, model, baseUrl, maxRetries),
                LLMProviderType.OpenAI => new OpenAIProvider(apiKey, model, baseUrl, maxRetries),
                _ => throw new ArgumentException($"Unknown provider: {_config.provider}")
            };
        }

        private Dictionary<string, IDomainHandler> CreateHandlers()
        {
            var handlers = new Dictionary<string, IDomainHandler>();
            var subscriptions = new SubscriptionManager();
            var nudgeManager = _bridgeServer.NudgeManager;  // Already created in BridgeServer.Awake()

            // Create all domain handlers with null transport (not needed for direct execution)
            var bridgeHandler = new BridgeDomainHandler(null, subscriptions, nudgeManager);
            handlers[bridgeHandler.Domain] = bridgeHandler;

            var fxHandler = new FXDomainHandler(null, subscriptions);
            handlers[fxHandler.Domain] = fxHandler;

            var authHandler = new AuthDomainHandler(null, subscriptions);
            handlers[authHandler.Domain] = authHandler;

            var navHandler = new NavDomainHandler(null, subscriptions);
            handlers[navHandler.Domain] = navHandler;

            var partyHandler = new PartyDomainHandler(null, subscriptions);
            handlers[partyHandler.Domain] = partyHandler;

            var emotionHandler = new EmotionDomainHandler(null, subscriptions);
            handlers[emotionHandler.Domain] = emotionHandler;

            var uiHandler = new UIDomainHandler(null, subscriptions);
            handlers[uiHandler.Domain] = uiHandler;

            var worldHandler = new WorldDomainHandler(null, subscriptions);
            handlers[worldHandler.Domain] = worldHandler;

            var visionHandler = new VisionDomainHandler(null, subscriptions);
            handlers[visionHandler.Domain] = visionHandler;

            // LoopbackWS handler (optional agent)
            var agent = _bridgeServer.GetComponent<LoopbackWSAgent>();
            var loopbackHandler = new LoopbackWSDomainHandler(null, subscriptions, agent);
            handlers[loopbackHandler.Domain] = loopbackHandler;

            return handlers;
        }

        private async UniTask ReasoningLoop(CancellationToken ct)
        {
            PerSpecDebug.Log("[AgentMode] ReasoningLoop started");

            while (!ct.IsCancellationRequested && _config.enabled)
            {
                // Wait for events, nudges, or timer
                await WaitForTrigger(ct);
                if (ct.IsCancellationRequested) break;

                var cycleStart = DateTime.UtcNow;
                _toolCallsThisCycle = 0;

                try
                {
                    // 1. Drain nudge queue
                    var nudgeCount = 0;
                    if (_bridgeServer?.NudgeManager != null)
                    {
                        while (_bridgeServer.NudgeManager.TryDequeue(out var nudge))
                        {
                            PerSpecDebug.Log($"[AgentMode] Dequeued nudge: {nudge}");
                            _conversation.AddUserMessage(nudge);
                            nudgeCount++;
                        }
                    }

                    // 2. Drain event queue
                    var eventCount = 0;
                    while (_eventQueue.TryDequeue(out var evt))
                    {
                        _conversation.AddEventContext(evt);
                        eventCount++;
                    }

                    PerSpecDebug.Log($"[AgentMode] Cycle start: {nudgeCount} nudges, {eventCount} events");

                    // 3. Skip if nothing to reason about (avoids empty contents error)
                    if (!_conversation.HasUserMessages())
                    {
                        PerSpecDebug.Log("[AgentMode] No user messages, skipping LLM call");
                        continue;
                    }

                    // 4. Send to LLM
                    PerSpecDebug.Log($"[AgentMode] Sending to LLM ({_config.provider}/{_config.model})...");
                    var response = await _provider.SendAsync(_conversation.GetMessages(), _tools, ct);
                    PerSpecDebug.Log($"[AgentMode] LLM response received: stopReason={response.StopReason}, contentBlocks={response.Content?.Count ?? 0}");
                    _conversation.AddAssistantMessage(response);

                    if (_config.logConversation)
                        LogResponse(response);

                    // 5. Process tool calls
                    while (response.StopReason == "tool_use" && _toolCallsThisCycle < _config.maxToolCallsPerCycle)
                    {
                        var toolCalls = response.GetToolCalls();
                        foreach (var toolCall in toolCalls)
                        {
                            _toolCallsThisCycle++;

                            if (_config.logToolCalls)
                                PerSpecDebug.Log($"[AgentMode] Tool call: {toolCall.Name} input={toolCall.Input}");

                            var result = await _toolExecutor.ExecuteAsync(toolCall, ct);
                            // null means DirectToolExecutor handled the result internally (e.g., vision image injection)
                            if (result != null)
                            {
                                _conversation.AddToolResult(toolCall.Id, toolCall.Name, result);

                                // Log tool result (truncated for large responses)
                                if (_config.logToolCalls)
                                {
                                    var truncated = result.Length > 500 ? result.Substring(0, 500) + "..." : result;
                                    PerSpecDebug.Log($"[AgentMode] Tool result: {toolCall.Name} → {truncated}");
                                }
                            }

                            if (_toolCallsThisCycle >= _config.maxToolCallsPerCycle)
                                break;
                        }

                        response = await _provider.SendAsync(_conversation.GetMessages(), _tools, ct);
                        _conversation.AddAssistantMessage(response);

                        if (_config.logConversation)
                            LogResponse(response);
                    }

                    // 5b. Auto-continue: if agent stopped without tool calls, nudge it to continue
                    if (response.StopReason != "tool_use" && _toolCallsThisCycle > 0)
                    {
                        // Agent made some tool calls this cycle but then stopped - prompt continuation
                        PerSpecDebug.Log("[AgentMode] Agent stopped mid-sequence, injecting continuation prompt");
                        _conversation.AddUserMessage("Continue. Execute the next step.");
                    }
                    else if (response.StopReason != "tool_use" && _toolCallsThisCycle == 0)
                    {
                        // Agent didn't make any tool calls - stronger prompt
                        PerSpecDebug.Log("[AgentMode] Agent made no tool calls, injecting action prompt");
                        _conversation.AddUserMessage("You must call a tool. Start with world__get_world_status if unsure.");
                    }

                    // 6. Prune context if over token limit
                    if (_conversation.EstimatedTokens > _config.maxContextTokens)
                        _conversation.PruneOldest(_config.keepSystemMessages);

                    _cycleCount++;
                    _lastCycleTime = DateTime.UtcNow;

                    // 7. Rate limit
                    var elapsed = (DateTime.UtcNow - cycleStart).TotalMilliseconds;
                    var remaining = _config.minCycleIntervalMs - elapsed;
                    if (remaining > 0)
                        await UniTask.Delay((int)remaining, cancellationToken: ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    PerSpecDebug.LogError($"[AgentMode] Cycle {_cycleCount} error: {ex.Message}");
                    await UniTask.Delay(5000, cancellationToken: ct);
                }
            }
        }

        private async UniTask WaitForTrigger(CancellationToken ct)
        {
            // Wait until we have events/nudges or minimum interval elapses
            var deadline = DateTime.UtcNow.AddMilliseconds(_config.minCycleIntervalMs);

            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                var nudgeDepth = _bridgeServer?.NudgeManager?.QueueDepth ?? 0;
                var hasEvents = !_eventQueue.IsEmpty;

                if (hasEvents)
                {
                    PerSpecDebug.Log("[AgentMode] WaitForTrigger: events detected, proceeding");
                    return;
                }
                if (nudgeDepth > 0)
                {
                    PerSpecDebug.Log($"[AgentMode] WaitForTrigger: {nudgeDepth} nudge(s) detected, proceeding");
                    return;
                }

                await UniTask.Delay(100, cancellationToken: ct);
            }
        }

        private void LogResponse(LLMResponse response)
        {
            if (response.Content == null || response.Content.Count == 0)
            {
                PerSpecDebug.Log("[AgentMode] Assistant: (no content blocks)");
                return;
            }

            foreach (var block in response.Content)
            {
                if (block.Type == "text")
                {
                    if (!string.IsNullOrEmpty(block.Text))
                        PerSpecDebug.Log($"[AgentMode] Assistant: {block.Text}");
                    else
                        PerSpecDebug.Log("[AgentMode] Assistant: (empty text block)");
                }
                else if (block.Type == "tool_use")
                    PerSpecDebug.Log($"[AgentMode] Assistant: [tool_use] {block.ToolName}");
            }
        }
    }
}
