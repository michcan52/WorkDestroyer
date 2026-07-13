using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NexusLive.Core.Inference;
using NexusLive.Core.State;

namespace NexusLive.Core.Processors
{
    public class PostSessionResult
    {
        public string Summary { get; set; } = string.Empty;
        public List<IssueUpdateItem> Issues { get; set; } = new();
    }

    public class IssueUpdateItem
    {
        public string Id { get; set; } = string.Empty;
        public string Status { get; set; } = "Pending"; // "Pending" or "Resolved"
        public string? ResolutionSummary { get; set; }
        public string? Description { get; set; } // For new issues
        public string? Category { get; set; }
    }

    public class PostSessionProcessor
    {
        private readonly ILlmInferenceService _llmService;
        private readonly IssueStateManager _stateManager;
        private readonly string _analyticalModelName;

        public PostSessionProcessor(ILlmInferenceService llmService, IssueStateManager stateManager, string analyticalModelName = "llama-3-8b")
        {
            _llmService = llmService ?? throw new ArgumentNullException(nameof(llmService));
            _stateManager = stateManager ?? throw new ArgumentNullException(nameof(stateManager));
            _analyticalModelName = analyticalModelName;
        }

        public async Task<string> ProcessSessionEndAsync(string fullTranscript, string outputStateFilePath, CancellationToken cancellationToken = default)
        {
            Console.WriteLine("[POST-SESSION LOG] Beginning session conclusion processing...");
            
            if (string.IsNullOrWhiteSpace(fullTranscript))
            {
                Console.WriteLine("[POST-SESSION WARNING] Transcript was empty. Aborting summary generation.");
                return "Transcript is empty. No summary generated.";
            }

            string currentIssuesJson = _stateManager.ToJsonString();
            Console.WriteLine($"[POST-SESSION LOG] Loaded current issue state. Pending issues count: {_stateManager.GetAllIssues().Count}");

            string systemPrompt = GetAnalyticalSystemPrompt();
            string userPrompt = $"=== CURRENT ISSUE STATES (JSON) ===\n{currentIssuesJson}\n\n=== FULL MEETING TRANSCRIPT ===\n{fullTranscript}";

            Console.WriteLine("[POST-SESSION LOG] Switching local server model to Analytical model...");
            await _llmService.SwitchModelAsync(_analyticalModelName, cancellationToken).ConfigureAwait(false);

            Console.WriteLine("[POST-SESSION LOG] Requesting summary and issue resolution JSON from LLM...");
            string rawLlmResponse = await _llmService.GenerateLiveSuggestionAsync(systemPrompt, userPrompt, _analyticalModelName, cancellationToken).ConfigureAwait(false);

            Console.WriteLine("======================= RAW LLM ANALYTICAL RESPONSE =======================");
            Console.WriteLine(rawLlmResponse);
            Console.WriteLine("==========================================================================");

            if (string.IsNullOrWhiteSpace(rawLlmResponse))
            {
                Console.WriteLine("[POST-SESSION ERROR] Received empty response from local LLM.");
                return "Failed to get an analytical response from the local LLM.";
            }

            try
            {
                string cleanedResponse = CleanJsonResponse(rawLlmResponse);
                Console.WriteLine("[POST-SESSION LOG] Attempting to parse cleaned JSON response...");
                Console.WriteLine($"[POST-SESSION LOG] Cleaned JSON: {cleanedResponse}");

                var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                var result = JsonSerializer.Deserialize<PostSessionResult>(cleanedResponse, options);

                if (result != null)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("[POST-SESSION SUCCESS] JSON parsed successfully. Applying updates to state...");
                    Console.ResetColor();

                    Console.WriteLine($"[POST-SESSION LOG] Meeting Summary: {result.Summary}");
                    Console.WriteLine($"[POST-SESSION LOG] Number of issues returned by LLM: {result.Issues.Count}");

                    foreach (var updatedIssue in result.Issues)
                    {
                        var existing = _stateManager.GetIssue(updatedIssue.Id);
                        if (existing != null)
                        {
                            if (Enum.TryParse<IssueStatus>(updatedIssue.Status, true, out var status))
                            {
                                Console.WriteLine($"[POST-SESSION LOG] Updating issue '{updatedIssue.Id}': Status={status}, Resolution={updatedIssue.ResolutionSummary}");
                                existing.Status = status;
                            }
                            existing.ResolutionSummary = updatedIssue.ResolutionSummary;
                            _stateManager.AddOrUpdateIssue(existing);
                        }
                        else if (!string.IsNullOrWhiteSpace(updatedIssue.Description))
                        {
                            var newIssue = new IssueInfo
                            {
                                Id = string.IsNullOrWhiteSpace(updatedIssue.Id) ? $"ISS-{Guid.NewGuid().ToString()[..6].ToUpper()}" : updatedIssue.Id,
                                Description = updatedIssue.Description,
                                Category = updatedIssue.Category ?? "General",
                                Status = IssueStatus.Pending
                            };
                            Console.WriteLine($"[POST-SESSION LOG] Discovered new issue: ID={newIssue.Id}, Category={newIssue.Category}, Desc='{newIssue.Description}'");
                            _stateManager.AddOrUpdateIssue(newIssue);
                        }
                    }

                    // Save the updated state to the JSON file
                    Console.WriteLine($"[POST-SESSION LOG] Saving updated issue list to: {outputStateFilePath}");
                    await _stateManager.SaveToFileAsync(outputStateFilePath, cancellationToken).ConfigureAwait(false);

                    return result.Summary;
                }
            }
            catch (JsonException ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[POST-SESSION ERROR] JSON Parsing failed: {ex.Message}");
                Console.WriteLine($"[POST-SESSION ERROR] Raw text that failed to parse was: {rawLlmResponse}");
                Console.ResetColor();
            }

            return "Session processed. Failed to update issue status due to JSON parse errors, but the meeting transcript has finished processing.";
        }

        private string CleanJsonResponse(string rawResponse)
        {
            if (string.IsNullOrWhiteSpace(rawResponse)) return string.Empty;

            string cleaned = rawResponse.Trim();
            
            // Remove markdown code blocks if present (e.g. ```json ... ```)
            if (cleaned.StartsWith("```"))
            {
                int firstNewline = cleaned.IndexOf('\n');
                if (firstNewline != -1)
                {
                    cleaned = cleaned[firstNewline..].Trim();
                }
                else
                {
                    cleaned = cleaned[3..].Trim();
                }
            }
            
            if (cleaned.EndsWith("```"))
            {
                cleaned = cleaned[..^3].Trim();
            }

            // Extract only the content enclosed by the first '{' and the last '}'
            int firstBrace = cleaned.IndexOf('{');
            int lastBrace = cleaned.LastIndexOf('}');
            if (firstBrace != -1 && lastBrace != -1 && lastBrace > firstBrace)
            {
                cleaned = cleaned[firstBrace..(lastBrace + 1)];
            }

            return cleaned;
        }

        private string GetAnalyticalSystemPrompt()
        {
            return @"Eres el analizador de reuniones de NexusLive.
Tu tarea es analizar la transcripción de la reunión y:
1. Escribir un resumen detallado y profesional en español.
2. Identificar si alguno de los problemas activos se ha resuelto.
3. Identificar nuevos problemas o puntos de acción surgidos.

Debes responder ÚNICAMENTE con un objeto JSON válido con la siguiente estructura (no agregues texto de introducción ni bloques de código markdown, solo el objeto JSON):
{
  ""summary"": ""Resumen detallado de la reunión..."",
  ""issues"": [
    {
      ""id"": ""ISS-001"",
      ""status"": ""Resolved"",
      ""resolutionSummary"": ""Se resolvió el problema del servidor local redireccionando los puertos...""
    },
    {
      ""id"": ""ISS-NEW"",
      ""description"": ""Crear script para reiniciar el commanager"",
      ""status"": ""Pending"",
      ""category"": ""Soporte""
    }
  ]
}";
        }
    }
}
