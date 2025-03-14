using System.Text;
using System.Text.Json;
using HoppscotchBackup.Models;
using Microsoft.Extensions.Options;

namespace HoppscotchBackup.Services;

public class HoppscotchBackupService
{
    private readonly BackupSettings _settings;
    private readonly HttpClient _httpClient;
    private readonly string GraphQLUrl;
    private readonly GitService _gitService;

    public HoppscotchBackupService(IOptions<BackupSettings> settings)
    {
        _settings = settings.Value;
        _httpClient = new HttpClient();
        GraphQLUrl = $"{_settings.HoppscotchApiBaseUrl}/graphql";
        _gitService = new GitService(_settings);
        
        // Set up JWT authentication for GraphQL
        _httpClient.DefaultRequestHeaders.Authorization = 
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.HoppscotchJWT);
        
        Console.WriteLine("Using JWT Token for GraphQL API");
    }

    public async Task ValidateAccessTokenAsync()
    {
        try {
            // GraphQL query to get teams
            var query = @"
            query GetMyTeams {
              myTeams {
                id
                name
              }
            }";
            
            var payload = new { 
                query = query,
                operationName = "GetMyTeams"
            };
            
            var content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");
            
            var response = await _httpClient.PostAsync(GraphQLUrl, content);
            
            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"Failed to authenticate: {response.StatusCode}");
            }
            
            var responseContent = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<JsonDocument>(responseContent);
            
            if (result?.RootElement.TryGetProperty("data", out var data) ?? false)
            {
                Console.WriteLine("GraphQL API authentication successful!");
            }
            else if (result?.RootElement.TryGetProperty("errors", out var errors) ?? false)
            {
                throw new Exception($"GraphQL API returned errors: {errors}");
            }
            else
            {
                throw new Exception("Unexpected GraphQL API response format");
            }
            
            Console.WriteLine("Authentication successful!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Authentication error: {ex.Message}");
            throw new Exception($"Failed to authenticate: {ex.Message}", ex);
        }
    }

    public async Task BackupCollectionsAsync()
    {
        await ValidateAccessTokenAsync();

        try
        {
            // Change from just date to date with time
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm");
            var backupPath = Path.Combine(_settings.RepoPath, _settings.BackupSubPath, timestamp);
            
            // Create backup directory
            Directory.CreateDirectory(backupPath);
            
            // Get collections via GraphQL using the exportCollectionsToJSON query
            Console.WriteLine("Fetching collections via GraphQL API...");
            
            string teamId = _settings.TeamId;
            
            if (string.IsNullOrEmpty(teamId))
            {
                // Get teams the user belongs to
                var teamsQuery = @"
                query MyTeams {
                  myTeams {
                    id
                    name
                  }
                }";
                
                var payload = new { 
                    query = teamsQuery,
                    operationName = "MyTeams"
                };
                
                var content = new StringContent(
                    JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json");
                
                var response = await _httpClient.PostAsync(GraphQLUrl, content);
                
                if (!response.IsSuccessStatusCode)
                {
                    throw new Exception($"Failed to get teams: {response.StatusCode}");
                }
                
                var responseContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"Teams response: {responseContent}");
                
                var result = JsonSerializer.Deserialize<JsonDocument>(responseContent);
                
                // Check for errors in the response
                if (result?.RootElement.TryGetProperty("errors", out var errors) ?? false)
                {
                    throw new Exception($"GraphQL API returned errors: {errors}");
                }
                
                if (result?.RootElement.TryGetProperty("data", out var data) ?? false)
                {
                    if (data.TryGetProperty("myTeams", out var teams) && teams.GetArrayLength() > 0)
                    {
                        teamId = teams[0].GetProperty("id").GetString();
                        var teamName = teams[0].GetProperty("name").GetString();
                        Console.WriteLine($"Using team: {teamName} (ID: {teamId})");
                    }
                    else
                    {
                        throw new Exception("No teams found for the user");
                    }
                }
                else
                {
                    throw new Exception("Unexpected GraphQL API response format");
                }
            }
            
            // Use the exportCollectionsToJSON query which is designed for exporting collections
            var exportQuery = @"
            query ExportCollections($teamID: ID!) {
              exportCollectionsToJSON(teamID: $teamID)
            }";
            
            var exportPayload = new { 
                query = exportQuery,
                variables = new { teamID = teamId },
                operationName = "ExportCollections"
            };
            
            var exportContent = new StringContent(
                JsonSerializer.Serialize(exportPayload),
                Encoding.UTF8,
                "application/json");
            
            var exportResponse = await _httpClient.PostAsync(GraphQLUrl, exportContent);
            
            if (!exportResponse.IsSuccessStatusCode)
            {
                throw new Exception($"Failed to export collections: {exportResponse.StatusCode}");
            }
            
            var exportResponseContent = await exportResponse.Content.ReadAsStringAsync();
            Console.WriteLine($"Export response received, length: {exportResponseContent.Length} characters");
            
            var exportResult = JsonSerializer.Deserialize<JsonDocument>(exportResponseContent);
            
            // Check for errors in the response
            if (exportResult?.RootElement.TryGetProperty("errors", out var exportErrors) ?? false)
            {
                throw new Exception($"GraphQL API returned errors: {exportErrors}");
            }
            
            if (exportResult?.RootElement.TryGetProperty("data", out var exportData) ?? false)
            {
                if (exportData.TryGetProperty("exportCollectionsToJSON", out var collectionsJson))
                {
                    // The API returns a JSON string, so we need to parse it
                    var collectionsString = collectionsJson.GetString();
                    if (!string.IsNullOrEmpty(collectionsString))
                    {
                        // Save the entire export as a single file
                        var exportFilePath = Path.Combine(backupPath, $"{_settings.WorkspaceName}_collections_export.json");
                        await File.WriteAllTextAsync(exportFilePath, collectionsString);
                        Console.WriteLine($"Saved collections export to: {exportFilePath}");
                        
                        // Also parse and save individual collections if needed
                        var collectionsDoc = JsonSerializer.Deserialize<JsonDocument>(collectionsString);
                        if (collectionsDoc != null)
                        {
                            // Assuming the export is an array of collections
                            foreach (var collection in collectionsDoc.RootElement.EnumerateArray())
                            {
                                if (collection.TryGetProperty("name", out var nameElement))
                                {
                                    var collectionName = nameElement.GetString() ?? "Untitled";
                                    
                                    // Sanitize filename
                                    var safeTitle = string.Join("_", collectionName.Split(Path.GetInvalidFileNameChars()));
                                    var filePath = Path.Combine(backupPath, $"{safeTitle}.json");
                                    
                                    await File.WriteAllTextAsync(
                                        filePath,
                                        JsonSerializer.Serialize(collection, new JsonSerializerOptions { WriteIndented = true }));
                                    
                                    Console.WriteLine($"Saved collection: {collectionName}");
                                }
                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine("Warning: exportCollectionsToJSON returned empty string");
                    }
                }
                else
                {
                    Console.WriteLine("Warning: Could not find exportCollectionsToJSON in the response");
                }
            }

            _gitService.PushToGithub(timestamp);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during backup: {ex.Message}");
            throw;
        }
    }

    public async Task ExploreGraphQLSchemaAsync()
    {
        try
        {
            Console.WriteLine("Exploring GraphQL schema...");
            
            // Introspection query to get schema details
            var query = @"
            query IntrospectionQuery {
              __schema {
                queryType {
                  name
                  fields {
                    name
                    description
                    args {
                      name
                      description
                      type {
                        name
                        kind
                        ofType {
                          name
                          kind
                        }
                      }
                    }
                  }
                }
              }
            }";
            
            var payload = new { 
                query = query,
                operationName = "IntrospectionQuery"
            };
            
            var content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");
            
            var response = await _httpClient.PostAsync(GraphQLUrl, content);
            
            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"Failed to get schema: {response.StatusCode}");
            }
            
            var responseContent = await response.Content.ReadAsStringAsync();
            
            // Save the schema to a file for easier exploration
            var schemaPath = Path.Combine(_settings.RepoPath, "hoppscotch-schema.json");
            await File.WriteAllTextAsync(schemaPath, responseContent);
            
            Console.WriteLine($"Schema saved to: {schemaPath}");
            
            // Extract and display available queries and mutations
            var schema = JsonSerializer.Deserialize<JsonDocument>(responseContent);
            if (schema != null)
            {
                ExtractOperations(schema.RootElement);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error exploring schema: {ex.Message}");
            throw;
        }
    }

    private void ExtractOperations(JsonElement schemaRoot)
    {
        try
        {
            var queryType = schemaRoot
                .GetProperty("data")
                .GetProperty("__schema")
                .GetProperty("queryType");
            
            var fields = queryType.GetProperty("fields");
            
            Console.WriteLine("\nAvailable Query Operations:");
            foreach (var field in fields.EnumerateArray())
            {
                var name = field.GetProperty("name").GetString();
                Console.WriteLine($"- {name}");
                
                // Print arguments if any
                if (field.TryGetProperty("args", out var args) && args.GetArrayLength() > 0)
                {
                    Console.WriteLine("  Arguments:");
                    foreach (var arg in args.EnumerateArray())
                    {
                        var argName = arg.GetProperty("name").GetString();
                        var typeName = "unknown";
                        
                        if (arg.TryGetProperty("type", out var typeInfo))
                        {
                            if (typeInfo.TryGetProperty("name", out var typeNameElement) && 
                                typeNameElement.ValueKind != JsonValueKind.Null)
                            {
                                typeName = typeNameElement.GetString() ?? "unknown";
                            }
                            else if (typeInfo.TryGetProperty("ofType", out var ofType) && 
                                    ofType.ValueKind != JsonValueKind.Null &&
                                    ofType.TryGetProperty("name", out var ofTypeName))
                            {
                                typeName = ofTypeName.GetString() ?? "unknown";
                            }
                        }
                        
                        Console.WriteLine($"    - {argName}: {typeName}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error extracting operations: {ex.Message}");
        }
    }
}