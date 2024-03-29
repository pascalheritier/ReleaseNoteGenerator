﻿using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using NLog.Extensions.Logging;
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

        private const string GitRepositoryExtension = ".git";
        private const string RemoteBranchPrefix = "origin/";

        #endregion

        #region Members

        private ILogger _logger;
        private string _repoCloneTmpDirectory;
        private string _username;
        private string _password;
        private AppConfiguration _appConfiguration;
        private RedmineManager _manager;
        private Dictionary<string, List<Issue>> _issueDictionary = new();
        private Dictionary<string, Repository> _gitRepositoryDictionary = new();

        #endregion

        #region Constructor
        public Generator(AppConfiguration appConfiguration, ILoggerFactory loggerFactory)
        {
            _appConfiguration = appConfiguration;
            _logger = loggerFactory.CreateLogger<Generator>();
            _repoCloneTmpDirectory = appConfiguration.GitConfiguration.RepoCloneTmpDirectory;
            _username = appConfiguration.GitConfiguration.Username;
            _password = appConfiguration.GitConfiguration.PAT;
            _manager = new RedmineManager(appConfiguration.RedmineConfiguration.ServerUrl, appConfiguration.RedmineConfiguration.ApiKey);
        }

        #endregion

        #region Run

        public void Run()
        {
            try
            {
                // Get credentials
                if (_password is null)
                    _password = AskUserPassword(_username);

                var credentials = new UsernamePasswordCredentials
                {
                    Username = _username,
                    Password = _password
                };

                // pull or clone git repository
                foreach (GitRepository gitRepository in _appConfiguration.GitConfiguration.GitRepositories)
                {
                    Repository repository = CloneOrPullRepository(
                        gitRepository.Name, 
                        gitRepository.BranchName, 
                        GetRepositoryGitTempPath(gitRepository.Name),
                        GetRepositoryUrl(gitRepository.Name), 
                        credentials);
                    _gitRepositoryDictionary.Add(gitRepository.Name, repository);
                }

                // get release note data
                foreach (GitRepository gitRepository in _appConfiguration.GitConfiguration.GitRepositories)
                {
                    _logger.LogInformation($"Start retrieving release note data for git repo '{gitRepository.Name}' in branch '{gitRepository.BranchName}':");

                    if (!_gitRepositoryDictionary.ContainsKey(gitRepository.Name))
                        throw new NotFoundException("Dictionaries not properly initialized");
                    Repository repository = _gitRepositoryDictionary[gitRepository.Name];

                    if (!_issueDictionary.ContainsKey(gitRepository.Name))
                        _issueDictionary.Add(gitRepository.Name, new List<Issue>());

                    // Get merge commits and generate release note
                    using (repository)
                    {
                        var branch = repository.Branches.FirstOrDefault(_B => _B.FriendlyName.Contains(gitRepository.BranchName));
                        if (branch == null)
                        {
                            _logger.LogError($"Repository '{gitRepository.Name}': Branch '{gitRepository.BranchName}' not found in repository {GetRepositoryUrl(gitRepository.Name)}.");
                            return;
                        }

                        CommitFilter filter = new CommitFilter()
                        {
                            SortBy = CommitSortStrategies.Time,
                            IncludeReachableFrom = branch
                        };

                        IEnumerable<Commit> commits = repository.Commits
                            .QueryBy(filter)
                            .Where(c => c.Parents.Count() > 1
                            && c.Message.Contains($"into '{gitRepository.BranchName}'"));

                        Commit? startCommit = repository.Commits.FirstOrDefault(_C => _C.Sha == gitRepository.CommitStartSha);
                        if (startCommit == null)
                        {
                            _logger.LogError($"Repository '{gitRepository.Name}': Could not find start commit with SHA {gitRepository.CommitStartSha}, which will be skipped during generation.");
                            continue;
                        }

                        IEnumerable<Commit> sortedCommits = commits.Where(_C => _C.Author.When >= startCommit.Author.When);
                        foreach (Commit commit in sortedCommits)
                        {
                            if (!TryGetRedmineIssueNumber(commit.Message, out string redmineIssueId, out string mergeMessage))
                            {
                                _logger.LogWarning($"Repository '{gitRepository.Name}': Could not find redmine issue for merge commit: {commit.Message}. Generator will skip this.");
                                continue;
                            }

                            if (!_issueDictionary[gitRepository.Name].Any(_I => _I.Id.ToString() == redmineIssueId))
                            {
                                if (!this.TryGetIssueFromOpenIssues(redmineIssueId, out Issue? foundIssue) || foundIssue is null)
                                    if (!this.TryGetIssueFromClosedIssues(redmineIssueId, out foundIssue) || foundIssue is null)
                                        _logger.LogError($"Repository '{gitRepository.Name}': Could not find Redmine issue #{redmineIssueId}");

                                if(foundIssue is not null)
                                {
                                    _issueDictionary[gitRepository.Name].Add(foundIssue);
                                    _logger.LogInformation($"- {commit.Author.When}| {this.IssueToReleaseNote(foundIssue)}");
                                }
                            }
                        }
                    }

                    _logger.LogInformation($"End of retrieving release note data for git repo '{gitRepository.Name}' in branch '{gitRepository.BranchName}'.");
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

        private Repository CloneOrPullRepository(
            string gitRepoName,
            string branchName,
            string repoCloneTmpPath,
            string repoUrl,
            UsernamePasswordCredentials credentials)
        {
            Repository repository;
            if (Directory.Exists(repoCloneTmpPath))
            {
                // pull
                repository = new Repository(repoCloneTmpPath);
                Signature signature = new(new Identity(credentials.Username, $"{credentials.Username}@site.com"), DateTimeOffset.Now);
                PullOptions pullOptions = new PullOptions
                {
                    FetchOptions = new FetchOptions
                    {
                        Prune = true,
                        TagFetchMode = TagFetchMode.Auto,
                        CredentialsProvider = (_url, _user, _cred) => credentials
                    },
                    MergeOptions = new MergeOptions
                    {
                        FailOnConflict = true,
                    }
                };
                Branch? localBranch = repository.Branches.FirstOrDefault(b => b.FriendlyName == branchName);
                Branch? trackedRemoteBranch = repository.Branches.FirstOrDefault(b => b.FriendlyName == RemoteBranchPrefix + branchName);
                if (trackedRemoteBranch is null || !trackedRemoteBranch.IsRemote)
                    throw new NotFoundException($"Could not find remote branch '{branchName}' in repository {gitRepoName}.");

                if (localBranch is null || !localBranch.IsCurrentRepositoryHead)
                {
                    _logger.LogInformation($"Checking out branch '{branchName}' in repository {gitRepoName}...");
                    if (localBranch is null)
                        localBranch = repository.CreateBranch(branchName, trackedRemoteBranch.Tip);

                    Branch branch = Commands.Checkout(repository, localBranch);
                    _logger.LogInformation($"Branch '{branchName}' checked out in repository {gitRepoName}.");
                }

                if (!localBranch.IsTracking)
                    repository.Branches.Update(localBranch, b => b.TrackedBranch = trackedRemoteBranch.CanonicalName);

                _logger.LogInformation($"Pulling latest commits for branch '{branchName}' in repository '{gitRepoName}'...");
                Commands.Pull(repository, signature, pullOptions);
                _logger.LogInformation($"Latest commits for branch '{branchName}' pulled in repository '{gitRepoName}'.");
            }
            else
            {
                // clone
                Directory.CreateDirectory(repoCloneTmpPath);
                CloneOptions cloneOptions = new()
                {
                    CredentialsProvider = (_url, _user, _cred) => credentials,
                    BranchName = branchName
                };
                _logger.LogInformation($"Cloning repository '{gitRepoName}'...");
                Repository.Clone(repoUrl, repoCloneTmpPath, cloneOptions);
                repository = new Repository(repoCloneTmpPath);
                _logger.LogInformation($"Clone of repository '{gitRepoName}' done, checked out branch '{branchName}'.");

            }
            return repository;
        }

        private string AskUserPassword(string username)
        {
            Console.WriteLine(Environment.NewLine);
            Console.WriteLine($"--------------------------------");
            Console.WriteLine("Please enter your credentials:");
            Console.WriteLine($"Username: {username}");
            Console.WriteLine("Password:");
            string password = GetHiddenInput();
            Console.WriteLine($"--------------------------------");
            return password;
        }

        /// <summary>
        /// Hide user input while he is typing it.
        /// </summary>
        /// <returns></returns>
        private string GetHiddenInput()
        {
            string hiddenInput = string.Empty;
            ConsoleKey key;
            do
            {
                var keyInfo = Console.ReadKey(intercept: true);
                key = keyInfo.Key;

                if (key == ConsoleKey.Backspace && hiddenInput.Length > 0)
                {
                    Console.Write("\b \b");
                    hiddenInput = hiddenInput[0..^1];
                }
                else if (!char.IsControl(keyInfo.KeyChar))
                {
                    Console.Write("*");
                    hiddenInput += keyInfo.KeyChar;
                }
            } while (key != ConsoleKey.Enter);
            return hiddenInput;
        }

        private string GetRepositoryUrl(string gitRepoName)
        {
            return _appConfiguration.GitConfiguration.RepositoryUrlPrefix + gitRepoName + GitRepositoryExtension;
        }

        private string GetRepositoryGitTempPath(string gitRepoName)
        {
            return Path.Combine(_repoCloneTmpDirectory, gitRepoName);
        }

        #endregion

        #region Redmine helpers

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

        private bool TryGetIssueFromOpenIssues(string targetIssueId, out Issue? foundIssue)
        {
            var parameters = new NameValueCollection
            {
                { RedmineKeys.ISSUE_ID, targetIssueId }
            };
            return TryGetIssue(parameters, out foundIssue);
        }

        private bool TryGetIssueFromClosedIssues(string targetIssueId, out Issue? foundIssue)
        {
            var parameters = new NameValueCollection
            {
                { RedmineKeys.ISSUE_ID, targetIssueId },
                { RedmineKeys.STATUS_ID, "closed" }
            };
            return TryGetIssue(parameters, out foundIssue);
        }

        private bool TryGetIssue(NameValueCollection parameters, out Issue? foundIssue)
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
