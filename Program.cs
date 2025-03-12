using System.Text;
using System.Text.Json;
using LibGit2Sharp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

class Program
{
    static async Task Main(string[] args)
    {
        var host = Host.CreateDefaultBuilder(args)
            .ConfigureServices((context, services) =>
            {
                // Print the current configuration values with masked sensitive data
                var backupSettings = context.Configuration.GetSection("BackupSettings").Get<BackupSettings>();
                Console.WriteLine("Configuration values:");
                Console.WriteLine($"HoppscotchJWT: {MaskToken(backupSettings?.HoppscotchJWT)}");
                Console.WriteLine($"GithubToken: {MaskToken(backupSettings?.GithubToken)}");
                Console.WriteLine($"GithubUsername: {backupSettings?.GithubUsername ?? "null"}");
                Console.WriteLine($"RepoPath: {backupSettings?.RepoPath ?? "null"}");

                services.Configure<BackupSettings>(
                    context.Configuration.GetSection("BackupSettings"));
                services.AddTransient<HoppscotchBackupService>();
            })
            .ConfigureAppConfiguration((hostingContext, config) =>
            {
                // Explicitly add JSON configuration files
                config.SetBasePath(Directory.GetCurrentDirectory())
                      .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                      .AddJsonFile($"appsettings.Development.json", optional: true, reloadOnChange: true)
                      .AddEnvironmentVariables();

                // Print the configuration sources
                Console.WriteLine("\nConfiguration sources:");
                foreach (var source in config.Sources)
                {
                    Console.WriteLine($"- {source.GetType().Name}");
                }
            })
            .Build();

        var backupService = host.Services.GetRequiredService<HoppscotchBackupService>();
        
        // Check command line arguments
        if (args.Length > 0)
        {
            switch (args[0].ToLower())
            {
                case "explore-schema":
                    try 
                    {
                        await backupService.ExploreGraphQLSchemaAsync();
                        Console.WriteLine("Schema exploration completed successfully.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Schema exploration failed: {ex.Message}");
                        Environment.Exit(1);
                    }
                    return;
                
                case "test-auth":
                    try 
                    {
                        await backupService.ValidateAccessTokenAsync();
                        Console.WriteLine("Authentication test completed successfully.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Authentication test failed: {ex.Message}");
                        Environment.Exit(1);
                    }
                    return;
            }
        }
        
        // Existing backup logic
        try 
        {
            await backupService.BackupCollectionsAsync();
            Console.WriteLine("Backup completed successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Backup failed: {ex.Message}");
            Environment.Exit(1);
        }
    }

    // Add this helper method to mask tokens
    private static string MaskToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return "null";
        if (token.Length <= 8) return "****";
        return $"{token[..4]}...{token[^4..]}";
    }
}

public class HoppscotchBackupService
{
    private readonly BackupSettings _settings;
    private readonly HttpClient _httpClient;
    private readonly string GraphQLUrl;

    public HoppscotchBackupService(IOptions<BackupSettings> settings)
    {
        _settings = settings.Value;
        _httpClient = new HttpClient();
        GraphQLUrl = $"{_settings.HoppscotchApiBaseUrl}/graphql";
        
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
            
            Console.WriteLine($"GraphQL Response Status: {response.StatusCode}");
            var responseContent = await response.Content.ReadAsStringAsync();
            
            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"GraphQL API authentication failed: {response.StatusCode}");
            }
            
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
                    if (data.TryGetProperty("myTeams", out var myTeamsElement) && 
                        myTeamsElement.ValueKind != JsonValueKind.Null && 
                        myTeamsElement.GetArrayLength() > 0)
                    {
                        teamId = myTeamsElement[0].GetProperty("id").GetString();
                        var name = myTeamsElement[0].GetProperty("name").GetString();
                        Console.WriteLine($"Using first team: {name} (ID: {teamId})");
                    }
                }
                
                if (string.IsNullOrEmpty(teamId))
                {
                    throw new Exception("No team ID found. Please specify TeamId in settings or ensure you have access to at least one team.");
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

            PushToGithub(timestamp);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during backup: {ex.Message}");
            throw;
        }
    }

    private async Task<JsonDocument> GetAllCollectionsWithRequestsAsync(string teamId)
    {
        // Create a list to hold our collections
        var collectionsList = new List<object>();
        
        // Get root collections
        var rootCollectionsQuery = @"
        query RootCollectionsOfTeam($teamID: ID!) {
          rootCollectionsOfTeam(teamID: $teamID) {
            id
            title
          }
        }";
        
        var payload = new { 
            query = rootCollectionsQuery,
            variables = new { teamID = teamId },
            operationName = "RootCollectionsOfTeam"
        };
        
        var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");
        
        var response = await _httpClient.PostAsync(GraphQLUrl, content);
        
        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"Failed to get root collections: {response.StatusCode}");
        }
        
        var responseContent = await response.Content.ReadAsStringAsync();
        var rootCollectionsDoc = JsonSerializer.Deserialize<JsonDocument>(responseContent);

        var rootCollections = new JsonElement(); // Initialize with empty JsonElement
        if (rootCollectionsDoc?.RootElement.TryGetProperty("data", out var data) ?? false)
        {
            data.TryGetProperty("rootCollectionsOfTeam", out rootCollections);
        }

        foreach (var collection in rootCollections.EnumerateArray())
        {
            var collectionId = collection.GetProperty("id").GetString();
            var collectionTitle = collection.GetProperty("title").GetString();
            
            // Fix the query based on the error messages
            var requestsQuery = @"
            query GetCollectionWithRequests($collectionID: ID!) {
              collection(collectionID: $collectionID) {
                id
                title
                folders {
                  id
                  title
                }
                requests {
                  id
                  title
                  request
                }
              }
            }";
            
            var requestsPayload = new { 
                query = requestsQuery,
                variables = new { collectionID = collectionId },
                operationName = "GetCollectionWithRequests"
            };
            
            var requestsContent = new StringContent(
                JsonSerializer.Serialize(requestsPayload),
                Encoding.UTF8,
                "application/json");
            
            var requestsResponse = await _httpClient.PostAsync(GraphQLUrl, requestsContent);
            
            if (!requestsResponse.IsSuccessStatusCode)
            {
                Console.WriteLine($"Failed to get requests for collection {collectionTitle}: {requestsResponse.StatusCode}");
                // Print the response content to see the error details
                var errorContent = await requestsResponse.Content.ReadAsStringAsync();
                Console.WriteLine($"Error details: {errorContent}");
                continue;
            }
            
            var requestsResponseContent = await requestsResponse.Content.ReadAsStringAsync();
            Console.WriteLine($"Collection response for {collectionTitle}: {requestsResponseContent.Substring(0, Math.Min(100, requestsResponseContent.Length))}...");
            
            var requestsDoc = JsonSerializer.Deserialize<JsonDocument>(requestsResponseContent);

            // Check for errors in the response
            if (requestsDoc?.RootElement.TryGetProperty("errors", out var errors) ?? false)
            {
                Console.WriteLine($"GraphQL error for collection {collectionTitle}: {errors}");
                continue;
            }

            // Add this collection with its requests to our result
            collectionsList.Add(JsonSerializer.Deserialize<object>(requestsDoc.RootElement.GetProperty("data").GetProperty("collection").GetRawText()));
        }
        
        // Convert our list to a JSON document
        var jsonString = JsonSerializer.Serialize(collectionsList);
        return JsonSerializer.Deserialize<JsonDocument>(jsonString);
    }

    private void PushToGithub(string timestamp)
    {
        using var repo = new Repository(_settings.RepoPath);
        var branchName = $"backup/{timestamp}";

        Console.WriteLine($"\nAttempting to create branch: {branchName}");
        Console.WriteLine($"Current branch: {repo.Head.FriendlyName}");
        Console.WriteLine($"Repository path: {repo.Info.WorkingDirectory}");
        
        try 
        {
            // First checkout main/master branch
            var defaultBranch = repo.Branches["main"] ?? repo.Branches["master"];
            if (defaultBranch == null)
            {
                throw new Exception("Neither 'main' nor 'master' branch found");
            }
            Commands.Checkout(repo, defaultBranch);
            
            // Now we can safely delete the existing branch
            var existingBranch = repo.Branches[branchName];
            if (existingBranch != null)
            {
                Console.WriteLine($"Branch {branchName} already exists, deleting it...");
                repo.Branches.Remove(existingBranch);
            }

            // Create and checkout new branch
            var branch = repo.CreateBranch(branchName);
            Commands.Checkout(repo, branch);
            
            // Get the backup directory path
            var backupDir = Path.Combine(_settings.RepoPath, _settings.BackupSubPath, timestamp);
            var files = Directory.GetFiles(backupDir, "*.*", SearchOption.AllDirectories);
            
            Console.WriteLine($"\nFound {files.Length} files to stage in {backupDir}");
            
            // Stage all files
            foreach (var file in files)
            {
                var relativePath = Path.GetRelativePath(_settings.RepoPath, file);
                Console.WriteLine($"\nProcessing file: {relativePath}");
                Console.WriteLine($"Full path: {file}");
                
                var fileInfo = new FileInfo(file);
                Console.WriteLine($"File exists: {fileInfo.Exists}, Size: {fileInfo.Length} bytes");
                
                try
                {
                    Commands.Stage(repo, relativePath);
                    Console.WriteLine($"File staged successfully: {repo.Index.Count > 0}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error staging file: {ex.Message}");
                }
            }

            // Create commit
            var signature = new Signature(_settings.GithubUsername, $"{_settings.GithubUsername}@users.noreply.github.com", DateTimeOffset.Now);
            
            if (repo.Index.Count == 0)
            {
                throw new Exception("No changes were staged despite finding files to stage");
            }

            repo.Commit($"Backup collections for {timestamp}", signature, signature);
            Console.WriteLine("Changes committed successfully");

            // Push changes
            var remote = repo.Network.Remotes["origin"];
            var pushOptions = new PushOptions
            {
                CredentialsProvider = (url, user, cred) =>
                    new UsernamePasswordCredentials
                    {
                        Username = _settings.GithubUsername,
                        Password = _settings.GithubToken
                    }
            };

            repo.Network.Push(remote, $"refs/heads/{branchName}", pushOptions);
            Console.WriteLine($"Successfully pushed branch: {branchName}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in Git operations: {ex.Message}");
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

public class HoppscotchAuthService
{
    private readonly HttpClient _httpClient;
    private const string AuthUrl = "https://api.hoppscotch.io/graphql";

    public HoppscotchAuthService()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<string> GetJwtTokenAsync(string email, string password)
    {
        // GraphQL mutation for login
        var loginMutation = @"
            mutation Login($email: String!, $password: String!) {
                login(email: $email, password: $password) {
                    token
                    user {
                        uid
                        displayName
                        email
                    }
                }
            }";

        var variables = new { email, password };
        var content = new StringContent(
            JsonSerializer.Serialize(new { 
                query = loginMutation, 
                variables 
            }),
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient.PostAsync(AuthUrl, content);
        var responseContent = await response.Content.ReadAsStringAsync();
        
        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"Authentication failed with status code: {response.StatusCode}");
        }

        var result = JsonSerializer.Deserialize<JsonDocument>(responseContent);
        
        // Check for GraphQL errors
        if (result?.RootElement.TryGetProperty("errors", out var errors) ?? false)
        {
            var errorMessage = errors.ToString();
            throw new Exception($"Authentication failed: {errorMessage}");
        }

        // Extract the JWT token
        var token = result?.RootElement
            .GetProperty("data")
            .GetProperty("login")
            .GetProperty("token")
            .GetString();

        if (string.IsNullOrEmpty(token))
        {
            throw new Exception("Failed to retrieve JWT token");
        }

        return token;
    }
}
