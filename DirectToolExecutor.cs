using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using DigitRaver.Bridge.LoopbackWS;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PerSpec;

namespace DigitRaver.Bridge.Agent
{
    /// <summary>
    /// Executes tool calls by directly invoking domain handlers without WebSocket.
    /// </summary>
    public class DirectToolExecutor
    {
        private readonly IReadOnlyDictionary<string, IDomainHandler> _handlers;
        private readonly ConversationManager _conversation;
        private readonly int _timeoutMs;

        public DirectToolExecutor(
            IReadOnlyDictionary<string, IDomainHandler> handlers,
            ConversationManager conversation,
            int timeoutMs = 30000)
        {
            _handlers = handlers;
            _conversation = conversation;
            _timeoutMs = timeoutMs;
        }

        /// <summary>
        /// Execute a tool call by directly invoking the appropriate domain handler.
        /// Returns null if the result was handled internally (e.g., vision image injection).
        /// </summary>
        public async UniTask<string> ExecuteAsync(ToolCall toolCall, CancellationToken ct)
        {
            // Parse tool name: domain__action → domain, action
            var parts = toolCall.Name.Split(new[] { "__" }, 2, StringSplitOptions.None);
            if (parts.Length != 2)
            {
                return JsonConvert.SerializeObject(new { error = $"Invalid tool name format: {toolCall.Name}" });
            }

            var domain = parts[0];
            var action = parts[1];

            // Find handler
            if (!_handlers.TryGetValue(domain, out var handler))
            {
                return JsonConvert.SerializeObject(new { error = $"Unknown domain: {domain}" });
            }

            if (!handler.IsAvailable)
            {
                return JsonConvert.SerializeObject(new { error = $"Domain handler not available: {domain}" });
            }

            // Build envelope
            var envelope = new MessageEnvelope
            {
                Id = Guid.NewGuid().ToString(),
                Type = MessageType.command,
                Domain = domain,
                Action = action,
                Payload = toolCall.Input ?? new JObject(),
                Timestamp = DateTime.UtcNow.ToString("o")
            };

            try
            {
                // Execute with timeout
                var timeoutCts = new CancellationTokenSource(_timeoutMs);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                var result = await handler.HandleCommandAsync(envelope, null);

                // Handle vision screenshot special case - inject image into conversation
                if (toolCall.Name == "vision__take_screenshot" && _conversation != null && result?.Payload != null)
                {
                    var imageBase64 = result.Payload["image"]?.Value<string>();
                    if (!string.IsNullOrEmpty(imageBase64))
                    {
                        var width = result.Payload["width"]?.Value<int>() ?? 0;
                        var height = result.Payload["height"]?.Value<int>() ?? 0;
                        var sizeBytes = result.Payload["sizeBytes"]?.Value<int>() ?? 0;
                        var estimatedTokens = result.Payload["estimatedTokens"]?.Value<int>() ?? 0;
                        var summaryText = $"Screenshot captured: {width}x{height}, {sizeBytes} bytes, ~{estimatedTokens} tokens";

                        _conversation.AddToolResultWithImage(toolCall.Id, toolCall.Name, summaryText, imageBase64, width, height);
                        return null; // Signal that tool result was handled internally
                    }
                }

                // Return result
                if (result == null)
                {
                    return JsonConvert.SerializeObject(new { error = $"No response from handler: {domain}.{action}" });
                }

                if (result.Type == MessageType.error)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        error = result.Payload?["error"]?.Value<string>() ?? "Unknown error",
                        domain = domain,
                        action = action
                    });
                }

                if (result.Payload != null)
                    return result.Payload.ToString(Formatting.Indented);

                return MessageSerializer.Serialize(result);
            }
            catch (OperationCanceledException)
            {
                return JsonConvert.SerializeObject(new
                {
                    error = $"Tool call timed out after {_timeoutMs}ms",
                    tool = toolCall.Name
                });
            }
            catch (Exception ex)
            {
                PerSpecDebug.LogError($"[AgentMode:DirectToolExecutor] Error executing {toolCall.Name}: {ex.Message}");
                return JsonConvert.SerializeObject(new
                {
                    error = ex.Message,
                    tool = toolCall.Name
                });
            }
        }

        #region Compound Tools

        /// <summary>
        /// Load a world and wait for it to become ready.
        /// </summary>
        public async UniTask<string> LoadWorldAndWaitAsync(string station, int pollIntervalMs, int maxWaitMs, CancellationToken ct)
        {
            // First, load the world
            var loadCall = new ToolCall
            {
                Id = Guid.NewGuid().ToString(),
                Name = "world__load_world",
                Input = new JObject { ["station"] = station }
            };

            var loadResult = await ExecuteAsync(loadCall, ct);
            if (loadResult == null || loadResult.Contains("error"))
                return loadResult ?? JsonConvert.SerializeObject(new { error = "Load world failed" });

            // Poll for world status
            var deadline = DateTime.UtcNow.AddMilliseconds(maxWaitMs);
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                await UniTask.Delay(pollIntervalMs, cancellationToken: ct);

                var statusCall = new ToolCall
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = "world__get_world_status",
                    Input = new JObject()
                };

                var statusResult = await ExecuteAsync(statusCall, ct);
                if (statusResult == null) continue;

                try
                {
                    var statusJson = JObject.Parse(statusResult);
                    var isLoaded = statusJson["isWorldLoaded"]?.Value<bool>() ?? false;
                    var isLoading = statusJson["isWorldLoading"]?.Value<bool>() ?? false;

                    if (isLoaded && !isLoading)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            station = station,
                            status = statusJson
                        });
                    }
                }
                catch
                {
                    // Continue polling
                }
            }

            return JsonConvert.SerializeObject(new
            {
                error = $"World load timed out after {maxWaitMs}ms",
                station = station
            });
        }

        /// <summary>
        /// Walk to a position and wait until arrival (within tolerance).
        /// </summary>
        public async UniTask<string> WalkToAndWaitAsync(float[] destination, float toleranceMeters, int pollIntervalMs, int maxWaitMs, CancellationToken ct)
        {
            if (destination == null || destination.Length != 3)
            {
                return JsonConvert.SerializeObject(new { error = "destination must be [x, y, z]" });
            }

            // First, issue walk command
            var walkCall = new ToolCall
            {
                Id = Guid.NewGuid().ToString(),
                Name = "nav__walk_to",
                Input = new JObject
                {
                    ["destination"] = new JArray(destination[0], destination[1], destination[2])
                }
            };

            var walkResult = await ExecuteAsync(walkCall, ct);
            if (walkResult == null || walkResult.Contains("error"))
                return walkResult ?? JsonConvert.SerializeObject(new { error = "Walk command failed" });

            // Poll position until within tolerance
            var deadline = DateTime.UtcNow.AddMilliseconds(maxWaitMs);
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                await UniTask.Delay(pollIntervalMs, cancellationToken: ct);

                var posCall = new ToolCall
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = "nav__get_position",
                    Input = new JObject()
                };

                var posResult = await ExecuteAsync(posCall, ct);
                if (posResult == null) continue;

                try
                {
                    var posJson = JObject.Parse(posResult);
                    var position = posJson["position"]?.ToObject<float[]>();
                    if (position != null && position.Length >= 3)
                    {
                        var dx = position[0] - destination[0];
                        var dz = position[2] - destination[2];
                        var distance = (float)Math.Sqrt(dx * dx + dz * dz);

                        if (distance <= toleranceMeters)
                        {
                            return JsonConvert.SerializeObject(new
                            {
                                success = true,
                                destination = destination,
                                finalPosition = position,
                                distance = distance
                            });
                        }
                    }
                }
                catch
                {
                    // Continue polling
                }
            }

            return JsonConvert.SerializeObject(new
            {
                error = $"Walk timed out after {maxWaitMs}ms",
                destination = destination
            });
        }

        /// <summary>
        /// Initialize agent by gathering essential state (auth, world status, position, party).
        /// </summary>
        public async UniTask<string> InitChecklistAsync(CancellationToken ct)
        {
            var results = new JObject();

            // Auth status
            try
            {
                var authCall = new ToolCall
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = "auth__get_status",
                    Input = new JObject()
                };
                var authResult = await ExecuteAsync(authCall, ct);
                if (!string.IsNullOrEmpty(authResult) && !authResult.Contains("error"))
                    results["auth"] = JObject.Parse(authResult);
            }
            catch { }

            // World status
            try
            {
                var worldCall = new ToolCall
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = "world__get_world_status",
                    Input = new JObject()
                };
                var worldResult = await ExecuteAsync(worldCall, ct);
                if (!string.IsNullOrEmpty(worldResult) && !worldResult.Contains("error"))
                    results["world"] = JObject.Parse(worldResult);
            }
            catch { }

            // Position (only if world loaded)
            var isWorldLoaded = results["world"]?["isWorldLoaded"]?.Value<bool>() ?? false;
            if (isWorldLoaded)
            {
                try
                {
                    var posCall = new ToolCall
                    {
                        Id = Guid.NewGuid().ToString(),
                        Name = "nav__get_position",
                        Input = new JObject()
                    };
                    var posResult = await ExecuteAsync(posCall, ct);
                    if (!string.IsNullOrEmpty(posResult) && !posResult.Contains("error"))
                        results["position"] = JObject.Parse(posResult);
                }
                catch { }

                // Party members
                try
                {
                    var partyCall = new ToolCall
                    {
                        Id = Guid.NewGuid().ToString(),
                        Name = "party__get_members",
                        Input = new JObject()
                    };
                    var partyResult = await ExecuteAsync(partyCall, ct);
                    if (!string.IsNullOrEmpty(partyResult) && !partyResult.Contains("error"))
                        results["party"] = JObject.Parse(partyResult);
                }
                catch { }
            }

            return results.ToString(Formatting.Indented);
        }

        #endregion
    }
}
