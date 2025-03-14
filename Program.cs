using System.Text;
using System.Text.Json;
using HoppscotchBackup.Models;
using HoppscotchBackup.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HoppscotchBackup;

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
                services.AddTransient<HoppscotchAuthService>();
                services.AddTransient<GitService>();
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