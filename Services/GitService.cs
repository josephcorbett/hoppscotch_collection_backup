using LibGit2Sharp;
using HoppscotchBackup.Models;

namespace HoppscotchBackup.Services;

public class GitService
{
    private readonly BackupSettings _settings;

    public GitService(BackupSettings settings)
    {
        _settings = settings;
    }

    public void PushToGithub(string timestamp)
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
}