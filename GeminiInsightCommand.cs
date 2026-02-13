
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace GeminiRevit
{
    
    [Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
    public class GeminiInsightCommand : IExternalCommand
    {
        
        private const string GeminiApiKey = "EnterYourGeminiAPIKeyHere";
        private const string GeminiApiUrl = "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash-preview-05-20:generateContent";
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
           
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                // 1. COLLECT DATA FROM REVIT MODEL
                // This entire block of code must run on the main UI thread.
                var walls = new FilteredElementCollector(doc)
                                .OfCategory(BuiltInCategory.OST_Walls)
                                .WhereElementIsNotElementType()
                                .Cast<Wall>()
                                .ToList();

                // If no walls are found, inform the user and exit.
                if (walls.Count == 0)
                {
                    TaskDialog.Show("Gemini Insight", "No walls found in the model to analyze.");
                    return Result.Succeeded;
                }

                // 2. FORMAT DATA FOR GEMINI
                // Create a string prompt with structured data about the walls.
                StringBuilder promptBuilder = new StringBuilder();
                promptBuilder.AppendLine("As a BIM analyst, please provide a brief summary and insights based on the following data about walls in a Revit model. Highlight any potential anomalies or interesting patterns. The data format is 'ElementId, WallType, Length, Area, HostLevelName'.\n");

                foreach (var wall in walls)
                {
                    string levelName = "N/A";
                    if (wall.LevelId != null)
                    {
                        var level = doc.GetElement(wall.LevelId) as Level;
                        levelName = level?.Name ?? "N/A";
                    }

                    double length = wall.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0.0;
                    double area = wall.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0.0;

                    promptBuilder.AppendLine($"{wall.Id}, {wall.WallType.Name}, {length:F2} ft, {area:F2} sq.ft, {levelName}");
                }

                // 3. GET INSIGHTS FROM GEMINI SYNCHRONOUSLY
                // This call will block the Revit UI thread until the network request completes.
                string geminiInsight = GetGeminiInsightSync(promptBuilder.ToString());

                // 4. DISPLAY THE RESULTS IN REVIT ON THE MAIN UI THREAD
                TaskDialog.Show("Gemini Model Insight", geminiInsight);
            }
            catch (Exception ex)
            {
                // Catch any exceptions and provide a user-friendly error message.
                message = ex.Message;
                TaskDialog.Show("Error", $"An error occurred: {ex.Message}");
                return Result.Failed;
            }

            // This command returns after the UI-blocking operation is complete.
            return Result.Succeeded;
        }

        // Synchronously calls the Gemini API and waits for the response.
        private string GetGeminiInsightSync(string modelDataPrompt)
        {
            try
            {
                using (var httpClient = new HttpClient())
                {
                    // Construct the full API URL with the API key.
                    string fullUrl = $"{GeminiApiUrl}?key={GeminiApiKey}";

                    // The payload for the API request.
                    var payload = new
                    {
                        contents = new[]
                        {
                            new
                            {
                                parts = new[]
                                {
                                    new { text = modelDataPrompt }
                                }
                            }
                        },
                        systemInstruction = new
                        {
                            parts = new[] {
                                new { text = "You are a professional BIM analyst. Analyze the provided Revit data and provide a concise, single-paragraph summary of key findings, potential issues, or interesting patterns. Be specific and refer to the data given." }
                            }
                        }
                    };

                    // Serialize the payload to JSON.
                    string jsonPayload = JsonSerializer.Serialize(payload);
                    var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                    // Log the attempt to send the request
                    Debug.WriteLine("Gemini Insight: Sending POST request to Gemini API...");

                    // This blocks the current thread until the POST request is complete.
                    var response = httpClient.PostAsync(fullUrl, content).Result;

                    // Log the response status for debugging
                    Debug.WriteLine($"Gemini Insight: Received response. Status Code: {response.StatusCode}");

                    response.EnsureSuccessStatusCode();

                    // This blocks the current thread until the content is read.
                    var apiResponse = response.Content.ReadAsStringAsync().Result;

                    // Parse the JSON response to extract the generated text.
                    using (JsonDocument doc = JsonDocument.Parse(apiResponse))
                    {
                        JsonElement root = doc.RootElement;
                        var textElement = root
                            .GetProperty("candidates")[0]
                            .GetProperty("content")
                            .GetProperty("parts")[0]
                            .GetProperty("text");
                        return textElement.GetString() ?? "No insight was generated.";
                    }
                }
            }
            catch (AggregateException aex) when (aex.InnerException is HttpRequestException)
            {
                // Handle inner exceptions from the blocking .Result call
                return $"Failed to connect to Gemini. Please check your internet connection and proxy/firewall settings. Error: {aex.InnerException.Message}";
            }
            catch (AggregateException aex) when (aex.InnerException is System.Threading.Tasks.TaskCanceledException)
            {
                // Handle inner exceptions for timeouts.
                return "The request to Gemini timed out. This may be due to a slow or blocked connection.";
            }
            catch (Exception ex)
            {
                // Return a general error message for any other failures.
                return $"An unexpected error occurred when calling the Gemini API. Error: {ex.Message}";
            }
        }
    }
}
