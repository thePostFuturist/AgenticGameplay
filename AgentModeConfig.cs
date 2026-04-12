using System;
using System.Collections.Generic;
using CodeStage.AntiCheat.ObscuredTypes;
using CodeStage.AntiCheat.Storage;
using DigitRaver.Bridge.Shared;
using PerSpec;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace DigitRaver.Bridge.Agent
{
    [CreateAssetMenu(fileName = "AgentModeConfig", menuName = "DigitRaver/Bridge/AgentModeConfig")]
    public class AgentModeConfig : ScriptableObject
    {
        #region Enable

        [Header("Enable")]
        [Tooltip("Master switch — when false, AgentModeController does not start")]
        public bool enabled;

        #endregion

        #region Provider

        [Header("Provider")]
        [Tooltip("Persisted in PlayerPrefs")]
        public LoopbackWS.LLMProviderType provider = LoopbackWS.LLMProviderType.Claude;

        [Tooltip("Persisted in PlayerPrefs")]
        public string model = "claude-sonnet-4-20250514";

        [Tooltip("API key - persisted in PlayerPrefs")]
        public string apiKey;

        [Tooltip("Override API endpoint (empty = use provider default). Persisted in PlayerPrefs")]
        public string baseUrl;

        [Tooltip("Model for STT transcription (default: gemini-2.0-flash)")]
        public string sttModel = "gemini-2.0-flash";

        #endregion

        #region TTS Settings

        [Header("TTS Settings")]
        [Tooltip("Model for TTS synthesis (default: gemini-2.5-flash-preview-tts)")]
        public string ttsModel = "gemini-2.5-flash-preview-tts";

        [Tooltip("Voice pool for TTS - 6 distinct voices for hash-based assignment")]
        public string[] ttsVoicePool = new[] { "Puck", "Charon", "Fenrir", "Kore", "Orus", "Leda" };

        #endregion

        #region PlayerPrefs

        // PlayerPrefs keys
        private const string PrefKeyProvider = "AgentMode_Provider";
        private const string PrefKeyModel = "AgentMode_Model";
        private const string PrefKeyApiKey = "AgentMode_ApiKey";
        private const string PrefKeyBaseUrl = "AgentMode_BaseUrl";
        private const string PrefKeySystemPrompt = "AgentMode_SystemPrompt";

        #endregion

        #region System Prompt

        [Header("System Prompt")]
        [TextArea(10, 30)]
        [Tooltip("Agent skill instructions — describes available tools and behaviors")]
        public string systemPrompt = DefaultSystemPrompt;

        [TextArea(3, 10)]
        [Tooltip("Appended to system prompt at runtime")]
        public string additionalInstructions;

        #endregion

        #region Timing

        [Header("Timing")]
        [Tooltip("Minimum delay between reasoning cycles (ms)")]
        public int minCycleIntervalMs = 2000;

        [Tooltip("Per-request timeout for LLM API calls (ms)")]
        public int apiTimeoutMs = 30000;

        [Tooltip("Retry count on transient API failures")]
        public int maxRetries = 3;

        #endregion

        #region Context

        [Header("Context")]
        [Tooltip("Prune conversation when token estimate exceeds this")]
        public int maxContextTokens = 32000;

        [Tooltip("Safety cap on tool calls per reasoning cycle")]
        public int maxToolCallsPerCycle = 10;

        [Tooltip("Number of system messages preserved during pruning")]
        public int keepSystemMessages = 1;

        #endregion

        #region Logging

        [Header("Logging")]
        public bool logConversation;
        public bool logToolCalls = true;

        #endregion

        #region Waypoint Rewards

        [Header("Waypoint Rewards")]
        [Tooltip("Grant blurbs AND blasts when agent arrives at a named waypoint")]
        public bool waypointRewardEnabled = true;

        [Tooltip("Amount of blurbs AND blasts to grant per waypoint arrival")]
        [Range(1, 50)]
        public int waypointRewardAmount = 20;

        [Tooltip("XZ distance in meters to match a named waypoint")]
        [Range(1f, 10f)]
        public float waypointMatchTolerance = 3f;

        #endregion

        #region Agentic Build

        [Header("Agentic Build")]
        [Tooltip("When true, auto-login and hide canvas at start")]
        public bool isAgenticBuild;

        [Tooltip("Agent accounts for auto-login (random selection)")]
        public List<AgentCredential> agentCredentials = new();

        public AgentCredential GetRandomCredential()
        {
            if (agentCredentials == null || agentCredentials.Count == 0) return null;
            return agentCredentials[UnityEngine.Random.Range(0, agentCredentials.Count)];
        }

        #endregion

        #region Events

        public event Action OnAgentModeEnabled;
        public event Action OnAgentModeDisabled;
        public event Action OnConfigChanged;
        public event Action<string> OnSttTranscript;

        /// <summary>
        /// Raises the STT transcript event to integrate with agent mode.
        /// </summary>
        public void RaiseSttTranscript(string text)
        {
            OnSttTranscript?.Invoke(text);
        }

        public void SetEnabled(bool value)
        {
            if (enabled == value) return;
            enabled = value;

            // Update shared static flag for BridgeServer mutual exclusivity check
            AgentModeStatus.IsEnabled = value;

            if (value)
                OnAgentModeEnabled?.Invoke();
            else
                OnAgentModeDisabled?.Invoke();
        }

        public void NotifyConfigChanged()
        {
            SaveToPlayerPrefs();
            OnConfigChanged?.Invoke();
        }

        #endregion

        #region ObscuredPrefs Persistence

        /// <summary>
        /// Load provider settings from ObscuredPrefs (encrypted storage).
        /// Called automatically on OnEnable.
        /// </summary>
        public void LoadFromPlayerPrefs()
        {
#if UNITY_EDITOR
            return;
#endif
            PerSpecDebug.Log($"[AgentModeConfig] LoadFromPlayerPrefs - HasKey checks: " +
                $"Provider={ObscuredPrefs.HasKey(PrefKeyProvider)}, " +
                $"Model={ObscuredPrefs.HasKey(PrefKeyModel)}, " +
                $"ApiKey={ObscuredPrefs.HasKey(PrefKeyApiKey)}, " +
                $"BaseUrl={ObscuredPrefs.HasKey(PrefKeyBaseUrl)}");

            if (ObscuredPrefs.HasKey(PrefKeyProvider))
                provider = (LoopbackWS.LLMProviderType)ObscuredPrefs.Get(PrefKeyProvider, (int)provider);

            if (ObscuredPrefs.HasKey(PrefKeyModel))
                model = ObscuredPrefs.Get(PrefKeyModel, model);

            // SO apiKey is primary; ObscuredPrefs is fallback only if SO is empty
            if (string.IsNullOrEmpty(apiKey) && ObscuredPrefs.HasKey(PrefKeyApiKey))
            {
                var savedKey = ObscuredPrefs.Get(PrefKeyApiKey, "");
                if (!string.IsNullOrEmpty(savedKey))
                    apiKey = savedKey;
            }
            // else: keep SO-baked apiKey value

            if (ObscuredPrefs.HasKey(PrefKeyBaseUrl))
                baseUrl = ObscuredPrefs.Get(PrefKeyBaseUrl, baseUrl);

            if (ObscuredPrefs.HasKey(PrefKeySystemPrompt))
                systemPrompt = ObscuredPrefs.Get(PrefKeySystemPrompt, systemPrompt);

            PerSpecDebug.Log($"[AgentModeConfig] After load: apiKey length={apiKey?.Length ?? -1}, model={model}");
        }

        /// <summary>
        /// Save provider settings to ObscuredPrefs (encrypted storage).
        /// Called automatically when NotifyConfigChanged() is invoked.
        /// </summary>
        public void SaveToPlayerPrefs()
        {
#if UNITY_EDITOR
            return;
#endif
            PerSpecDebug.Log($"[AgentModeConfig] SaveToPlayerPrefs: apiKey length={apiKey?.Length ?? -1}, model={model}");
            ObscuredPrefs.Set(PrefKeyProvider, (int)provider);
            ObscuredPrefs.Set(PrefKeyModel, model ?? "");
            ObscuredPrefs.Set(PrefKeyApiKey, apiKey ?? "");
            ObscuredPrefs.Set(PrefKeyBaseUrl, baseUrl ?? "");
            ObscuredPrefs.Set(PrefKeySystemPrompt, systemPrompt ?? "");
            ObscuredPrefs.Save();
            PerSpecDebug.Log("[AgentModeConfig] SaveToPlayerPrefs complete (ObscuredPrefs.Save called)");
        }

        /// <summary>
        /// Clear all persisted provider settings from ObscuredPrefs.
        /// </summary>
        public void ClearPlayerPrefs()
        {
            ObscuredPrefs.DeleteKey(PrefKeyProvider);
            ObscuredPrefs.DeleteKey(PrefKeyModel);
            ObscuredPrefs.DeleteKey(PrefKeyApiKey);
            ObscuredPrefs.DeleteKey(PrefKeyBaseUrl);
            ObscuredPrefs.DeleteKey(PrefKeySystemPrompt);
            ObscuredPrefs.Save();
        }

        #endregion

        #region Boilerplate

        private void OnEnable()
        {
            LoadFromPlayerPrefs();

            // Update shared static flag for cross-assembly access
            AgentModeStatus.IsAgenticBuild = isAgenticBuild;
#if UNITY_EDITOR
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
#endif
        }

        private void OnDisable()
        {
#if UNITY_EDITOR
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
#endif
        }

#if UNITY_EDITOR
        private void OnPlayModeStateChanged(PlayModeStateChange stateChange)
        {
            if (stateChange == PlayModeStateChange.ExitingPlayMode)
            {
                ClearOnPlayModeExit();
            }
        }

        /// <summary>
        /// Clears sensitive data from ScriptableObject fields when exiting Play Mode.
        /// API key is persisted in ObscuredPrefs, so clearing the SO field prevents
        /// it from being visible in the Inspector or accidentally committed to version control.
        /// </summary>
        private void ClearOnPlayModeExit()
        {
            enabled = false;
            // apiKey = string.Empty;
        }

        [ContextMenu("Import System Prompt from MD")]
        private void ImportSystemPromptFromMD()
        {
            var scriptPath = AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(this));
            var directory = System.IO.Path.GetDirectoryName(scriptPath);
            var mdPath = System.IO.Path.Combine(directory, "AgentModeSystemPrompt.md");

            if (System.IO.File.Exists(mdPath))
            {
                Undo.RecordObject(this, "Import System Prompt");
                systemPrompt = System.IO.File.ReadAllText(mdPath);
                EditorUtility.SetDirty(this);
                Debug.Log($"Imported system prompt from: {mdPath}");
            }
            else
            {
                Debug.LogError($"System prompt MD file not found: {mdPath}");
            }
        }

        [ContextMenu("Export System Prompt to MD")]
        private void ExportSystemPromptToMD()
        {
            var scriptPath = AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(this));
            var directory = System.IO.Path.GetDirectoryName(scriptPath);
            var mdPath = System.IO.Path.Combine(directory, "AgentModeSystemPrompt.md");

            System.IO.File.WriteAllText(mdPath, systemPrompt ?? "");
            AssetDatabase.Refresh();
            Debug.Log($"Exported system prompt to: {mdPath}");
        }
#endif

        #endregion

        public string GetFullSystemPrompt()
        {
            var prompt = systemPrompt ?? "";
            if (!string.IsNullOrEmpty(additionalInstructions))
                prompt += "\n\n" + additionalInstructions;
            return prompt;
        }

        #region Default System Prompt

        private const string DefaultSystemPrompt = @"# DigitRaver Bridge Agent

You are an autonomous in-world agent for the DigitRaver metaverse. You can navigate virtual worlds, interact with other avatars, take screenshots for visual analysis, send chat messages, and control various effects.

## Available Tools

### Navigation (nav)
- `nav__walk_to` - Navigate to [x, y, z] coordinates
- `nav__get_position` - Get current position and ownerID
- `nav__get_map` - Get walkable area map (RLE grid + boundary polygon)
- `nav__validate_position` - Check if [x, z] is walkable, get snapped [x, y, z]
- `nav__look_delta` - Rotate camera by delta (x=degrees right, y=normalized up)
- `nav__set_look` - Set absolute camera orientation (yaw degrees, pitch 0-1)
- `nav__get_look` - Get current camera orientation
- `nav__platform_tap` - Move while on moving platform

### Vision (vision)
- `vision__take_screenshot` - Capture current view (maxWidth 512 for quick, 1024 for detailed)

### Chat (ui)
- `ui__send_chat` - Send chat message (broadcast or DM via targetUsername)
- `ui__get_chat_mode` - Query current chat settings
- `ui__set_chat_transport` - Set transport (public/private)
- `ui__set_chat_reach` - Set reach (global/earshot)
- `ui__show_popup` - Show in-game popup dialog
- `ui__select_reaction` - Select reaction type (blurbs/blasts/none)

### World (world)
- `world__get_world_status` - Query loading state
- `world__get_stations` - List available worlds
- `world__load_world` - Load world by station name
- `world__reload_world` - Reload current world
- `world__unload_world` - Unload current world

### Party (party)
- `party__get_members` - Query party roster
- `party__go_to_member` - Navigate to party member by ownerID

### Effects (fx)
- `fx__dispatch_blaster` - Fire projectile toward [x, y, z]
- `fx__dispatch_blurb` - Show text blurb above avatar
- `fx__set_mode` - Switch tap mode (walk/blaster/platform)

### Emotion (emotion)
- `emotion__change_face` - Change avatar face expression (1-based index)
- `emotion__get_face_state` - Query face state by ownerID or username

### Auth (auth)
- `auth__get_status` - Query auth state (isSignedIn, isPerformer, username)
- `auth__get_room_info` - Query room info
- `auth__get_room_users` - List users in room
- `auth__sign_in_email` - Sign in with email/password
- `auth__sign_out` - Sign out
- `auth__change_username` - Change display name

### Bridge (bridge)
- `bridge__nudge` - Send nudge message
- `bridge__get_tools` - Get all available tool schemas
- `bridge__fake_hit` - Trigger die animation

## Workflows

### Entering a World
1. `world__get_stations` - List available worlds
2. `world__load_world` with station name
3. `world__get_world_status` - Poll until loaded
4. `nav__get_position` - Verify avatar spawned

### Navigation
1. `nav__get_map` - Get walkable grid
2. Pick walkable cell, compute world coords
3. `nav__validate_position` - Get exact [x, y, z]
4. `nav__walk_to` - Navigate to position
5. Poll `nav__get_position` until within 2m of destination

### Visual Analysis
1. `nav__set_look` or `nav__look_delta` to orient camera
2. Wait 0.3s for camera to settle
3. `vision__take_screenshot` - Capture and analyze
4. Describe using 3x3 grid (top-left, center, btm-right)

### Finding Players
1. `party__get_members` - Get roster with positions
2. `party__go_to_member` - Navigate to player
3. Take screenshot to verify proximity
4. `ui__send_chat` - Greet them

## Guidelines
- Always check world status before navigation commands
- Use vision to verify arrival and understand surroundings
- Be conversational when chatting with players
- Describe what you see in screenshots comprehensively
- Handle errors gracefully and retry if needed";

        #endregion
    }

    /// <summary>
    /// Credentials for an agent account used in agentic builds.
    /// </summary>
    [Serializable]
    public class AgentCredential
    {
        [Tooltip("Agent email address")]
        public ObscuredString email;

        [Tooltip("Agent password")]
        public ObscuredString password;
    }
}
