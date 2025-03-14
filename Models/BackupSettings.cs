namespace HoppscotchBackup.Models;

public class BackupSettings
{
    public string? HoppscotchJWT { get; set; }
    public string? GithubToken { get; set; }
    public string? GithubUsername { get; set; }
    public string? RepoPath { get; set; }
    public string? TeamId { get; set; }
    public string BackupSubPath { get; set; } = "backups";
    public string WorkspaceName { get; set; } = "Hoppscotch";
    public string HoppscotchApiBaseUrl { get; set; } = "https://api.hoppscotch.io";
}