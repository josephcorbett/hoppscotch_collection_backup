using System.Text;
using System.Text.Json;

namespace HoppscotchBackup.Services;

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