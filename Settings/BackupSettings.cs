public class BackupSettings
{
    public required string HoppscotchJWT { get; set; }
    public required string GithubToken { get; set; }
    public required string GithubUsername { get; set; }
    public required string RepoPath { get; set; }
    public required string BackupSubPath { get; set; }
    public required string WorkspaceName { get; set; }
    public string? TeamId { get; set; }
    public string HoppscotchApiBaseUrl { get; set; } = "https://api.hoppscotch.io";
}
