namespace ReleaseNoteGenerator
{
    internal class AppConfiguration
    {
        public GitConfiguration GitConfiguration { get; set; } = new();
        public RedmineConfiguration RedmineConfiguration { get; set; } = new();
    }

    internal class RedmineConfiguration
    {
        public string ServerUrl { get; set; } = null!;
        public string ApiKey { get; set; } = null!;
        public string TargetUserId { get; set; } = null!;
        public string ReleaseNoteFileName { get; set; } = null!;
    }

    internal class GitConfiguration
    {
        public string Username { get; set; } = null!;
        public string PAT { get; set; } = null!;
        public string RepositoryUrlPrefix { get; set; } = null!;
        public string RepoCloneTmpDirectory { get; set; } = null!;
        public List<GitRepository> GitRepositories { get; set; } = null!;
    }

    internal class GitRepository
    {
        public string Name { get; set; } = null!;
        public string BranchName { get; set; } = null!;
        public string CommitStartSha { get; set; } = null!;
        public string ReleaseNoteVersion { get; set; } = null!;
    }
}
