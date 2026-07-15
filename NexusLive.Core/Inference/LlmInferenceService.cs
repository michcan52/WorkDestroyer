using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace NexusLive.Core.Inference
{
    public class ChatMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }

    public class ResponseFormat
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "json_object";
    }

    public class LlmRequestPayload
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = "phi-3.5-mini";

        [JsonPropertyName("messages")]
        public List<ChatMessage> Messages { get; set; } = new();

        [JsonPropertyName("temperature")]
        public double Temperature { get; set; } = 0.2;

        [JsonPropertyName("max_tokens")]
        public int MaxTokens { get; set; } = 150;

        [JsonPropertyName("response_format")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ResponseFormat? ResponseFormat { get; set; }
    }

    public interface ILlmInferenceService
    {
        Task<string> GenerateLiveSuggestionAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken);
        Task<string> GenerateLiveSuggestionAsync(string systemPrompt, string userPrompt, string modelName, CancellationToken cancellationToken);
        Task SwitchModelAsync(string modelName, CancellationToken cancellationToken);
        Task UnloadModelAsync(string modelName, CancellationToken cancellationToken);
        Task<List<string>> GetLoadedModelInstanceIdsAsync(CancellationToken cancellationToken);
    }

    public class LlmInferenceService : ILlmInferenceService
    {
        private readonly HttpClient _httpClient;
        private readonly LlmOptions _options;
        private readonly string _endpointUrl;
        private readonly string _switchEndpointUrl;

        public LlmInferenceService(HttpClient httpClient)
            : this(httpClient, new LlmOptions())
        {
        }

        public LlmInferenceService(HttpClient httpClient, LlmOptions options)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _endpointUrl = $"{_options.BaseAddress.TrimEnd('/')}/v1/chat/completions";
            
            string controlPath = _options.ModelControlEndpoint ?? "/v1/models";
            if (!controlPath.StartsWith('/'))
            {
                controlPath = "/" + controlPath;
            }
            _switchEndpointUrl = $"{_options.BaseAddress.TrimEnd('/')}{controlPath}";
        }

        public Task<string> GenerateLiveSuggestionAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
        {
            // Default to live model name from options
            return GenerateLiveSuggestionAsync(systemPrompt, userPrompt, _options.LiveModelName, cancellationToken);
        }

        public async Task<string> GenerateLiveSuggestionAsync(string systemPrompt, string userPrompt, string modelName, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(userPrompt)) return string.Empty;

            Console.WriteLine($"[LM STUDIO LOG] Generating completions for model: '{modelName}' (Prompt length: {userPrompt.Length + systemPrompt.Length} chars)...");

            string detectedLang = LanguageDetector.Detect(userPrompt);
            string forcedInstruction = detectedLang == "es"
                ? "\n[IMPORTANT: Respond ONLY in Spanish/Español. The last segment was in Spanish.]"
                : "\n[IMPORTANT: Respond ONLY in English. The last segment was in English.]";

            var payload = new LlmRequestPayload
            {
                Model = modelName,
                Messages = new List<ChatMessage>
                {
                    new ChatMessage { Role = "system", Content = systemPrompt + forcedInstruction },
                    new ChatMessage { Role = "user", Content = userPrompt }
                },
                Temperature = 0.2,
                MaxTokens = modelName == _options.AnalyticalModelName ? 2048 : 250
            };

            string jsonPayload = JsonSerializer.Serialize(payload);
            using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            try
            {
                using var response = await _httpClient.PostAsync(_endpointUrl, content, cancellationToken).ConfigureAwait(false);
                
                if (!response.IsSuccessStatusCode)
                {
                    string details = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[LM STUDIO ERROR] Completion request failed. Status: {response.StatusCode}. Details: {details}");
                    Console.ResetColor();
                    return string.Empty;
                }

                string responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                
                using var doc = JsonDocument.Parse(responseBody);
                var root = doc.RootElement;
                if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var choice = choices[0];
                    if (choice.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var contentProp))
                    {
                        string result = contentProp.GetString()?.Trim() ?? string.Empty;
                        Console.WriteLine($"[LM STUDIO SUCCESS] Completion success. Received response (Length: {result.Length} chars).");
                        return result == "[NO_SUGGESTION]" ? string.Empty : result;
                    }
                }

                Console.WriteLine("[LM STUDIO WARNING] Response did not contain valid completion choices.");
                return string.Empty;
            }
            catch (HttpRequestException ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[LM STUDIO ERROR] Completion request HTTP connection failed: {ex.Message}");
                Console.ResetColor();
                return string.Empty;
            }
            catch (JsonException ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[LM STUDIO ERROR] Failed to parse LLM Response JSON: {ex.Message}");
                Console.ResetColor();
                return string.Empty;
            }
            catch (TaskCanceledException)
            {
                Console.WriteLine("[LM STUDIO LOG] Completion request was canceled.");
                return string.Empty;
            }
        }

        public async Task SwitchModelAsync(string modelName, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(modelName)) throw new ArgumentException("Model name cannot be empty.", nameof(modelName));

            Console.WriteLine($"[LM STUDIO LOG] Switching model to: '{modelName}'...");

            // Eject the other model first to avoid having multiple models loaded in the GPU concurrently
            string otherModel = modelName == _options.LiveModelName ? _options.AnalyticalModelName : _options.LiveModelName;
            if (otherModel != modelName)
            {
                await UnloadModelAsync(otherModel, cancellationToken).ConfigureAwait(false);
            }

            // Check if target model is already loaded before triggering a load command
            var loadedInstances = await GetLoadedModelInstanceIdsAsync(cancellationToken).ConfigureAwait(false);
            if (loadedInstances.Contains(modelName))
            {
                Console.WriteLine($"[LM STUDIO LOG] Model '{modelName}' is already loaded. Skipping load call.");
                return;
            }

            var switchPayload = new
            {
                model = modelName
            };

            string jsonPayload = JsonSerializer.Serialize(switchPayload);
            using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            try
            {
                Console.WriteLine($"[LM STUDIO LOG] Sending load command to: {modelName} via {_switchEndpointUrl}...");
                using var response = await _httpClient.PostAsync(_switchEndpointUrl, content, cancellationToken).ConfigureAwait(false);
                
                if (!response.IsSuccessStatusCode)
                {
                    string errorDetails = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[LM STUDIO ERROR] Failed to load model '{modelName}'. Details: {errorDetails}");
                    Console.ResetColor();
                    await LogAvailableModelsAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"[LM STUDIO SUCCESS] Model '{modelName}' loaded successfully.");
                    Console.ResetColor();
                }
            }
            catch (HttpRequestException ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[LM STUDIO ERROR] Load request failed: {ex.Message}");
                Console.ResetColor();
            }
            catch (TaskCanceledException)
            {
                Console.WriteLine("[LM STUDIO LOG] Load request timed out or was canceled.");
            }
        }

        public async Task UnloadModelAsync(string modelName, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(modelName)) return;

            Console.WriteLine($"[LM STUDIO LOG] Checking if model '{modelName}' is loaded before unloading...");
            var loadedInstances = await GetLoadedModelInstanceIdsAsync(cancellationToken).ConfigureAwait(false);
            if (!loadedInstances.Contains(modelName))
            {
                Console.WriteLine($"[LM STUDIO LOG] Model '{modelName}' is NOT currently loaded. Skipping unload call.");
                return;
            }

            Console.WriteLine($"[LM STUDIO LOG] Model '{modelName}' is active. Sending unload command...");
            var payload = new { instance_id = modelName };
            string jsonPayload = JsonSerializer.Serialize(payload);
            using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            string unloadUrl = _switchEndpointUrl.Replace("/load", "/unload");

            try
            {
                using var response = await _httpClient.PostAsync(unloadUrl, content, cancellationToken).ConfigureAwait(false);
                
                if (response.IsSuccessStatusCode)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"[LM STUDIO SUCCESS] Model '{modelName}' unloaded successfully.");
                    Console.ResetColor();
                }
                else
                {
                    string details = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[LM STUDIO WARNING] Unload failed for '{modelName}': {response.StatusCode}. Details: {details}");
                    Console.ResetColor();
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[LM STUDIO ERROR] Exception while sending unload for '{modelName}': {ex.Message}");
                Console.ResetColor();
            }
        }

        public async Task<List<string>> GetLoadedModelInstanceIdsAsync(CancellationToken cancellationToken)
        {
            var loadedIds = new List<string>();
            try
            {
                string listUrl = _switchEndpointUrl.Replace("/load", "");
                using var response = await _httpClient.GetAsync(listUrl, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    string content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(content);
                    var root = doc.RootElement;
                    
                    if (root.TryGetProperty("models", out var modelsList) && modelsList.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in modelsList.EnumerateArray())
                        {
                            if (item.TryGetProperty("loaded_instances", out var loadedInstances) && 
                                loadedInstances.ValueKind == JsonValueKind.Array && 
                                loadedInstances.GetArrayLength() > 0)
                            {
                                // Add the parent model key
                                if (item.TryGetProperty("key", out var keyProp))
                                {
                                    string? key = keyProp.GetString();
                                    if (!string.IsNullOrEmpty(key))
                                    {
                                        loadedIds.Add(key);
                                    }
                                }

                                // Add any child instance IDs
                                foreach (var instance in loadedInstances.EnumerateArray())
                                {
                                    if (instance.TryGetProperty("instance_id", out var instIdProp))
                                    {
                                        string? instanceId = instIdProp.GetString();
                                        if (!string.IsNullOrEmpty(instanceId) && !loadedIds.Contains(instanceId))
                                        {
                                            loadedIds.Add(instanceId);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LM STUDIO ERROR] Failed to list loaded models: {ex.Message}");
            }
            return loadedIds;
        }

        private async Task LogAvailableModelsAsync(CancellationToken cancellationToken)
        {
            try
            {
                string modelsUrl = $"{_options.BaseAddress.TrimEnd('/')}/v1/models";
                using var response = await _httpClient.GetAsync(modelsUrl, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    string content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(content);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    {
                        var names = new List<string>();
                        foreach (var item in data.EnumerateArray())
                        {
                            if (item.TryGetProperty("id", out var idProp))
                            {
                                names.Add(idProp.GetString() ?? string.Empty);
                            }
                        }
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"[LM STUDIO DIAGNOSTIC] Available models loaded in LM Studio: {string.Join(", ", names)}");
                        Console.WriteLine($"[LM STUDIO DIAGNOSTIC] Please update your 'appsettings.json' to use one of these model names.");
                        Console.ResetColor();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Could not load available models list: {ex.Message}");
            }
        }
    }
}
