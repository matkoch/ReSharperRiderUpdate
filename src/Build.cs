using System.Linq;
using System.Text.RegularExpressions;
using NuGet.Versioning;
using Nuke.Common;
using Nuke.Common.ChangeLog;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Utilities;
using Nuke.Common.Utilities.Collections;
using Octokit;
using Serilog;
using static Nuke.Common.ChangeLog.ChangelogTasks;
using static Nuke.Common.EnvironmentInfo;
using static Nuke.Common.IO.XmlTasks;
using static Nuke.Common.Tools.Git.GitTasks;

// TODO: check file size
class Build : NukeBuild, IGlobalTool
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode
    public static int Main() => Execute<Build>(x => x.Versions);

    Target Versions => _ => _
        .Executes(() =>
        {
            Log.Information("{Name} = {Value}", nameof(ReSharperVersion), ReSharperVersion);
            Log.Information("{Name} = {Value}", nameof(IdeaPrereleaseTag), IdeaPrereleaseTag);
            Log.Information("{Name} = {Value}", nameof(IdeaVersion), IdeaVersion);
            Log.Information("{Name} = {Value}", nameof(RdGenVersion), RdGenVersion);
            Log.Information("{Name} = {Value}", nameof(GradlePluginVersion), GradlePluginVersion);
            Log.Information("{Name} = {Value}", nameof(KotlinJvmVersion), KotlinJvmVersion);
        });

#if UNIX
    Tool Gradle => ToolResolver.GetTool(WorkingDirectory / "gradlew");
#else
    Tool Gradle => ToolResolver.GetTool(WorkingDirectory / "gradlew.bat");
#endif

    [PathVariable("powershell")] readonly Tool PowerShell;

    [LatestNuGetVersion("JetBrains.ReSharper.SDK", IncludePrerelease = true)] readonly NuGetVersion ReSharperVersion;

    string ReSharperShortVersion => $"{ReSharperVersion.Major}.{ReSharperVersion.Minor}";
    AbsolutePath PluginPropsFile => WorkingDirectory / "src" / "dotnet" / "Plugin.props";

    bool HasReSharperImplementation => PluginPropsFile.Exists();

    Target UpdateReSharperSdk => _ => _
        .OnlyWhenStatic(() => HasReSharperImplementation)
        .Executes(() =>
        {
            XmlPoke(
                path: PluginPropsFile,
                xpath: ".//SdkVersion",
                ReSharperVersion);
        });

    AbsolutePath GradleBuildFile => WorkingDirectory / "build.gradle";
    AbsolutePath GradlePropertiesFile => WorkingDirectory / "gradle.properties";

    string IdeaPrereleaseTag => ReSharperVersion.IsPrerelease
        ? $"-{ReSharperVersion.ReleaseLabels.Single().Replace("eap0", "eap").ToUpperInvariant()}-SNAPSHOT"
        : null;

    string IdeaVersion => $"{ReSharperVersion.Major}.{ReSharperVersion.Minor}{IdeaPrereleaseTag}";

    string RdGenVersion
    {
        get
        {
            // var client = new GitHubClient(new ProductHeaderValue(nameof(NukeBuild)), new InMemoryCredentialStore(new Credentials("TOKEN")));
            var releases = new GitHubClient(new ProductHeaderValue(nameof(NukeBuild))).Repository.Release.GetAll("JetBrains", "rd").GetAwaiter().GetResult();
            return releases.First(x => Regex.IsMatch(x.Name, @$"^{ReSharperVersion.Major}\.{ReSharperVersion.Minor}\.\d+$")).TagName;
        }
    }

    // [LatestGitHubRelease("JetBrains/gradle-intellij-plugin", TrimPrefix = true)]
    readonly string GradlePluginVersion = "1.12.0";

    [LatestMavenVersion(
        repository: "plugins.gradle.org/m2",
        groupId: "org.jetbrains.kotlin.jvm",
        artifactId: "org.jetbrains.kotlin.jvm.gradle.plugin")]
    readonly string KotlinJvmVersion;

    bool HasRiderImplementation => GradlePropertiesFile.Exists() && GradleBuildFile.Exists();

    Target UpdateGradleBuild => _ => _
        .Requires(() => KotlinJvmVersion)
        // .Requires(() => GradlePluginVersion)
        .OnlyWhenStatic(() => HasRiderImplementation)
        .Executes(() =>
        {
            GradlePropertiesFile.UpdateText(_ => _
                .ReplaceRegex(@"^ProductVersion=.+$", _ => $"ProductVersion={IdeaVersion}",
                    RegexOptions.Multiline));

            GradleBuildFile.UpdateText(_ => _
                .ReplaceRegex(@"id 'com\.jetbrains\.rdgen' version '\d+\.\d+(\.\d+)?'", _ => $"id 'com.jetbrains.rdgen' version '{RdGenVersion.NotNull()}'")
                .ReplaceRegex(@"id 'org\.jetbrains\.kotlin\.jvm' version '\d+\.\d+(\.\d+)?'", _ => $"id 'org.jetbrains.kotlin.jvm' version '{KotlinJvmVersion}'")
                .ReplaceRegex(@"id 'org\.jetbrains\.intellij' version '\d+\.\d+(\.\d+)?'", _ => $@"id 'org.jetbrains.intellij' version '{GradlePluginVersion}'"));
        });

    AbsolutePath ChangelogFile => WorkingDirectory / "CHANGELOG.md";
    ChangeLog Changelog => ReadChangelog(ChangelogFile);

    Target UpdateChangelog => _ => _
        .OnlyWhenStatic(() => Changelog.Unreleased == null)
        .Executes(() =>
        {
            var products = new[]
            {
                HasReSharperImplementation ? "ReSharper" : null,
                HasRiderImplementation ? "Rider" : null
            }.WhereNotNull();

            var changelogLines = ChangelogFile.ReadAllLines().ToList();
            changelogLines.InsertRange(
                Changelog.ReleaseNotes[^1].StartIndex,
                collection: new[]
                {
                    $"## vNext",
                    $"- Added support for {products.Join(" and ")} {ReSharperShortVersion}",
                    string.Empty
                });
            ChangelogFile.WriteAllLines(changelogLines);
        });

    Target FinalizeChangelog => _ => _
        .After(UpdateChangelog)
        .OnlyWhenStatic(() => !ReSharperVersion.IsPrerelease)
        .OnlyWhenStatic(() => Changelog.Unreleased != null)
        .Executes(() =>
        {
            ChangelogTasks.FinalizeChangelog(ChangelogFile, ReSharperVersion.ToString());
        });

    Target Update => _ => _
        .WhenSkipped(DependencyBehavior.Skip)
        .DependsOn(UpdateReSharperSdk)
        .DependsOn(UpdateGradleBuild)
        .DependsOn(UpdateChangelog);

    Target Compile => _ => _
        .DependsOn(Update)
        .Produces(WorkingDirectory / "output" / "*")
        .Executes(() =>
        {
            if (GradleBuildFile.Exists())
                Gradle($":buildPlugin -PPluginVersion={ReSharperVersion} --no-daemon");
            else
                PowerShell($".\\buildPlugin.ps1 -Version {ReSharperVersion}");
        });

    [Parameter] readonly string PublishToken;

    Target Publish => _ => _
        .DependsOn(Update)
        .DependsOn(FinalizeChangelog)
        // .Triggers(Commit)
        .WhenSkipped(DependencyBehavior.Skip)
        .Produces(WorkingDirectory / "output" / "*")
        .Requires(() => PublishToken)
        .Executes(() =>
        {
            if (GradleBuildFile.Exists())
                Gradle($":publishPlugin -PPluginVersion={ReSharperVersion} -PPublishToken={PublishToken} --no-daemon", logInvocation: false);
            else
                PowerShell($".\\publishPlugin.ps1 -Version {ReSharperVersion} -ApiKey {PublishToken}", logInvocation: false);
        });

    // [GitRepository] readonly GitRepository GitRepository;
    // [Parameter] readonly string CommitUsername;
    // [Parameter] readonly string CommitAuthToken;
    // string RemoteUrl => $"https://{CommitUsername}:{CommitAuthToken}@github.com/{GitRepository.Identifier}";

    Target Commit => _ => _
        .DependsOn(Update)
        .OnlyWhenDynamic(() => !GitHasCleanWorkingCopy())
        .Executes(() =>
        {
            if (GradleBuildFile.Exists())
            {
                Git($"add {GradleBuildFile}");
                Git($"add {GradlePropertiesFile}");
            }

            Git($"add {PluginPropsFile}");
            Git($"add {ChangelogFile}");

            Git($"commit -m {$"Update SDK to {ReSharperVersion}".DoubleQuote()}");
            Git($"tag {ReSharperVersion}");

            // Git($"remote set-url origin {RemoteUrl}", logInvocation: false);
            // Git($"push origin {Branch}");
        });
}
