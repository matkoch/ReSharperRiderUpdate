using System;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Nuke.Common;
using Nuke.Common.Git;
using Nuke.Common.Tools.GitHub;
using Nuke.Common.Utilities;
using Nuke.Common.ValueInjection;
using Octokit;

[PublicAPI]
public class LatestGitHubReleaseAttribute : ValueInjectionAttributeBase
{
    private readonly string _identifier;

    public LatestGitHubReleaseAttribute(string identifier)
    {
        _identifier = identifier;
    }

    public bool IncludePrerelease { get; set; }
    public bool TrimPrefix { get; set; }
    public string NamePattern { get; set; }

    public override object GetValue(MemberInfo member, object instance)
    {
        var repository = GitRepository.FromUrl($"https://github.com/{_identifier}");
        return GetLatestRelease(repository, IncludePrerelease, TrimPrefix, NamePattern).GetAwaiter().GetResult();
    }

    async Task<string> GetLatestRelease(GitRepository repository, bool includePrerelease = false,
        bool trimPrefix = false, string namePattern = null)
    {
        Assert.True(repository.IsGitHubRepository());
        var client = new GitHubClient(new ProductHeaderValue(nameof(NukeBuild)));
        var releases = await client.Repository.Release
            .GetAll(repository.GetGitHubOwner(), repository.GetGitHubName());
        var release = releases.First(x => (!x.Prerelease || includePrerelease) && (namePattern == null || Regex.IsMatch(x.Name, namePattern)));
        return release.TagName.TrimStart(trimPrefix ? "v" : string.Empty);
    }
}
