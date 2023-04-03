using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Redmine.Net.Api;
using Redmine.Net.Api.Types;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ReleaseNoteGenerator
{
    internal class Generator
    {
        #region Consts

        public const string GitRepositoryExtension = ".git";

        #endregion

        #region Members

        private ILogger _logger;
        private string _branchName;
        private string _repoCloneTmpDirectory;
        private string _username;
        private string _password;
        private AppConfiguration _appConfiguration;
        private RedmineManager _manager;
        private Dictionary<string, List<Issue>> _issueDictionary = new();

        #endregion

        #region Constructor
        public Generator(AppConfiguration appConfiguration, ILoggerFactory loggerFactory)
        {
            _appConfiguration = appConfiguration;
            _logger = loggerFactory.CreateLogger<Generator>();
            _branchName = appConfiguration.GitConfiguration.BranchName;
            _repoCloneTmpDirectory = appConfiguration.GitConfiguration.RepoCloneTmpDirectory;
            _username = appConfiguration.GitConfiguration.Username;
            _manager = new RedmineManager(appConfiguration.RedmineConfiguration.ServerUrl, appConfiguration.RedmineConfiguration.ApiKey);
        }

        #endregion

        #region Run

        public void Run()
        {
            try
            {
                // Get credentials
                Console.WriteLine($"{Environment.NewLine}--------------------------------");
                Console.WriteLine("Please enter your credentials:");
                Console.WriteLine($"Username: {_username}");
                Console.WriteLine("Password:");
                _password = GetPassword();
                var credentials = new UsernamePasswordCredentials
                {
                    Username = _username,
                    Password = _password
                };

                // get release note data
                foreach (GitRepository gitRepository in _appConfiguration.GitConfiguration.GitRepositories)
                {
                    // Fetch or clone git repository
                    string gitRepoName = gitRepository.Name;
                    string repoUrl = _appConfiguration.GitConfiguration.RepositoryUrlPrefix + gitRepoName + GitRepositoryExtension;
                    string repoCloneTmpPath = Path.Combine(_repoCloneTmpDirectory, gitRepoName);
                    Repository repo = CloneOrFetchRepository(gitRepoName, repoCloneTmpPath, repoUrl, credentials);
                    Console.WriteLine($"{Environment.NewLine}--------------------------------");

                    if (!_issueDictionary.ContainsKey(gitRepoName))
                        _issueDictionary.Add(gitRepoName, new List<Issue>());

                    // Get merge commits and generate release note
                    using (repo)
                    {
                        var branch = repo.Branches.FirstOrDefault(_B => _B.FriendlyName.Contains(_branchName));

                        if (branch == null)
                        {
                            Console.WriteLine($"Branch {_branchName} not found in repository {repoUrl}.");
                            return;
                        }

                        CommitFilter filter = new CommitFilter()
                        {
                            SortBy = CommitSortStrategies.Time,
                            IncludeReachableFrom = branch
                        };

                        IEnumerable<Commit> commits = repo.Commits
                            .QueryBy(filter)
                            .Where(c => c.Parents.Count() > 1
                            && c.Message.Contains($"into '{_branchName}'"));

                        Commit? startCommit = repo.Commits.FirstOrDefault(_C => _C.Sha == gitRepository.CommitStartSha);
                        if (startCommit == null)
                        {
                            _logger.LogError($"Could not find start commit with SHA {gitRepository.CommitStartSha} in repository {gitRepoName}, which will be skipped during generation.");
                            continue;
                        }

                        IEnumerable<Commit> sortedCommits = commits.Where(_C => _C.Author.When >= startCommit.Author.When);
                        foreach (Commit commit in sortedCommits)
                        {
                            if (!TryGetRedmineIssueNumber(commit.Message, out string redmineIssueId, out string mergeMessage))
                            {
                                _logger.LogWarning($"Could not find redmine issue for merge commit: {commit.Message}. Generator will skip this.");
                                continue;
                            }

                            if (!_issueDictionary[gitRepoName].Any(_I => _I.Id.ToString() == redmineIssueId))
                            {
                                if (!this.TryGetIssueFromOpenIssues(redmineIssueId, out Issue? foundIssue) || foundIssue is null)
                                    if (!this.TryGetIssueFromClosedIssues(redmineIssueId, out foundIssue) || foundIssue is null)
                                        throw new NotFoundException($"Could not find Redmine issue #{redmineIssueId}");

                                _issueDictionary[gitRepoName].Add(foundIssue);
                                Console.WriteLine($"- {commit.Author.When}| {this.IssueToReleaseNote(foundIssue)}");
                            }
                        }
                    }
                }

                // generate release note file
                this.GenerateRedmineReleaseNote(_appConfiguration.RedmineConfiguration.ReleaseNoteFileName);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error while accessing git repository: {ex.ToString()}");
            }
        }

        #region Git helpers

        private Repository CloneOrFetchRepository(string gitRepoName, string repoCloneTmpPath, string repoUrl, UsernamePasswordCredentials credentials)
        {
            Repository repository;
            if (Directory.Exists(repoCloneTmpPath))
            {
                // fetch
                repository = new Repository(repoCloneTmpPath);
                FetchOptions options = new()
                {
                    Prune = true,
                    TagFetchMode = TagFetchMode.Auto,
                    CredentialsProvider = (_url, _user, _cred) => credentials
                };
                var remote = repository.Network.Remotes["origin"];
                var refSpecs = remote.FetchRefSpecs.Select(x => x.Specification);
                Commands.Fetch(repository, remote.Name, refSpecs, options, "Fetching remote");
            }
            else
            {
                // clone
                Directory.CreateDirectory(repoCloneTmpPath);
                CloneOptions cloneOptions = new()
                {
                    CredentialsProvider = (_url, _user, _cred) => credentials,
                    BranchName = _branchName
                };
                Console.WriteLine($"Cloning repository {gitRepoName}...");
                Repository.Clone(repoUrl, repoCloneTmpPath, cloneOptions);
                repository = new Repository(repoCloneTmpPath);
                Console.WriteLine($"Clone of repository {gitRepoName} done.");

            }
            return repository;
        }

        private bool TryGetRedmineIssueNumber(string commitMessage, out string redmineIssueId, out string mergeMessage)
        {
            redmineIssueId = string.Empty;
            mergeMessage = string.Empty;
            Regex regex = new("#([0-9]+)");
            Match match = regex.Match(commitMessage);
            if (match.Success)
            {
                if (match.Groups.Count > 1)
                {
                    redmineIssueId = match.Groups.Values.ElementAt(1)?.Value;
                    return true;
                }
            }
            return false;
        }

        private string GetPassword()
        {
            string password = string.Empty;
            ConsoleKey key;
            do
            {
                var keyInfo = Console.ReadKey(intercept: true);
                key = keyInfo.Key;

                if (key == ConsoleKey.Backspace && password.Length > 0)
                {
                    Console.Write("\b \b");
                    password = password[0..^1];
                }
                else if (!char.IsControl(keyInfo.KeyChar))
                {
                    Console.Write("*");
                    password += keyInfo.KeyChar;
                }
            } while (key != ConsoleKey.Enter);
            return password;
        }

        #endregion

        #region Redmine helpers

        private bool TryGetIssueFromOpenIssues(string targetIssueId, out Issue? foundIssue)
        {
            var parameters = new NameValueCollection
            {
                { RedmineKeys.ISSUE_ID, targetIssueId }
            };
            return TryGetIssue(targetIssueId, parameters, out foundIssue);
        }

        private bool TryGetIssueFromClosedIssues(string targetIssueId, out Issue? foundIssue)
        {
            var parameters = new NameValueCollection
            {
                { RedmineKeys.ISSUE_ID, targetIssueId },
                { RedmineKeys.STATUS_ID, "closed" }
            };
            return TryGetIssue(targetIssueId, parameters, out foundIssue);
        }

        private bool TryGetIssue(string targetIssueId, NameValueCollection parameters, out Issue? foundIssue)
        {
            foundIssue = null;
            try
            {
                foundIssue = _manager.GetObjects<Issue>(parameters)?.FirstOrDefault();
                if (foundIssue is not null)
                    return true;
            }
            catch
            {
                // silent failure, found issue is null
            }
            return false;
        }

        private string IssueToReleaseNote(Issue foundIssue)
        {
            return $" - {foundIssue.Tracker.Name} #{foundIssue.Id}: {foundIssue.Subject}";
        }

        private void GenerateRedmineReleaseNote(string releaseNotefileName)
        {
            if (System.IO.File.Exists(releaseNotefileName))
                System.IO.File.Delete(releaseNotefileName);

            using (FileStream fs = System.IO.File.Create(releaseNotefileName))
            {
                string content;
                foreach (string gitRepoName in _issueDictionary.Keys)
                {
                    content = $"## {gitRepoName.FirstCharToUpper()}";
                    content += Environment.NewLine;
                    content += $"- {_appConfiguration.GitConfiguration.GitRepositories.First(_C => _C.Name == gitRepoName).ReleaseNoteVersion}";
                    content += Environment.NewLine;
                    foreach (Issue redmineIssue in _issueDictionary[gitRepoName].OrderBy(_I => _I.Tracker.Name))
                    {
                        content += this.IssueToReleaseNote(redmineIssue);
                        content += Environment.NewLine;
                    }
                    content += Environment.NewLine;

                    byte[] info = new UTF8Encoding(true).GetBytes(content);
                    fs.Write(info, 0, info.Length);
                }
            }
        }

        #endregion

        #endregion
    }
}
