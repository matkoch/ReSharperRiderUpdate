using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NuGet.Versioning;
using Nuke.Common;
using Nuke.Common.ChangeLog;
using Nuke.Common.CI;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Utilities;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.ChangeLog.ChangelogTasks;
using static Nuke.Common.IO.TextTasks;
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
            Logger.Normal($"{nameof(ReSharperVersion)} = {ReSharperVersion}");
            Logger.Normal($"{nameof(IdeaPrereleaseTag)} = {IdeaPrereleaseTag}");
            Logger.Normal($"{nameof(IdeaVersion)} = {IdeaVersion}");
            Logger.Normal($"{nameof(RdGenVersion)} = {RdGenVersion}");
            Logger.Normal($"{nameof(GradlePluginVersion)} = {GradlePluginVersion}");
        });

    AbsolutePath WorkingDirectory => (AbsolutePath) EnvironmentInfo.WorkingDirectory;

#if UNIX
    Tool Gradle => ToolResolver.GetLocalTool(WorkingDirectory / "gradlew");
#else
    Tool Gradle => ToolResolver.GetLocalTool(WorkingDirectory / "gradlew.bat");
#endif

    [PathExecutable("powershell")] readonly Tool PowerShell;

    [LatestNuGetVersion("JetBrains.ReSharper.SDK", IncludePrerelease = true)] readonly NuGetVersion ReSharperVersion;

    string ReSharperShortVersion => $"{ReSharperVersion.Major}.{ReSharperVersion.Minor}";
    string PluginPropsFile => WorkingDirectory / "src" / "dotnet" / "Plugin.props";

    bool HasReSharperImplementation => File.Exists(PluginPropsFile);

    Target UpdateReSharperSdk => _ => _
        .OnlyWhenStatic(() => HasReSharperImplementation)
        .Executes(() =>
        {
            XmlPoke(
                path: PluginPropsFile,
                xpath: ".//SdkVersion",
                ReSharperVersion);
        });

    string GradleBuildFile => WorkingDirectory / "build.gradle";
    string GradlePropertiesFile => WorkingDirectory / "gradle.properties";

    string IdeaPrereleaseTag => ReSharperVersion.IsPrerelease
        ? $"-{ReSharperVersion.ReleaseLabels.Single().Replace("eap0", "eap").ToUpperInvariant()}-SNAPSHOT"
        : null;

    string IdeaVersion => $"{ReSharperVersion.Major}.{ReSharperVersion.Minor}{IdeaPrereleaseTag}";

    [LatestMyGetVersion("rd-snapshots", "rd-gen")] readonly string RdGenVersion;

    [LatestGitHubRelease("JetBrains/gradle-intellij-plugin", TrimPrefix = true)] readonly string GradlePluginVersion;

    bool HasRiderImplementation => File.Exists(GradlePropertiesFile) && File.Exists(GradleBuildFile);

    Target UpdateGradleBuild => _ => _
        .OnlyWhenStatic(() => HasRiderImplementation)
        .Executes(() =>
        {
            WriteAllText(
                GradlePropertiesFile,
                ReadAllText(GradlePropertiesFile)
                    .ReplaceRegex(
                        @"^ProductVersion=.+$",
                        x => $"ProductVersion={IdeaVersion}", RegexOptions.Multiline));

            WriteAllText(
                GradleBuildFile,
                ReadAllText(GradleBuildFile)
                    .ReplaceRegex(
                        @"com\.jetbrains\.rd:rd-gen:\d+\.\d+\.\d+",
                        x => $"com.jetbrains.rd:rd-gen:{RdGenVersion}")
                    .ReplaceRegex(
                        @"id 'org\.jetbrains\.intellij' version '\d+\.\d+\.\d+'",
                        x => $@"id 'org.jetbrains.intellij' version '{GradlePluginVersion}'"));
        });

    string ChangelogFile => WorkingDirectory / "CHANGELOG.md";
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

            var changelogLines = ReadAllLines(ChangelogFile).ToList();
            changelogLines.InsertRange(
                Changelog.ReleaseNotes[^1].StartIndex,
                collection: new[]
                {
                    $"## vNext",
                    $"- Added support for {products.JoinCommaAnd()} {ReSharperShortVersion}",
                    string.Empty
                });
            WriteAllLines(ChangelogFile, changelogLines);
        });

    Target FinalizeChangelog => _ => _
        .After(UpdateChangelog)
        .OnlyWhenStatic(() => !ReSharperVersion.IsPrerelease)
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
            if (File.Exists(GradleBuildFile))
                Gradle($":buildPlugin -PPluginVersion={ReSharperVersion} --no-daemon");
            else
                PowerShell($".\\buildPlugin.ps1 -Version {ReSharperVersion}");
        });

    [Parameter] readonly string PublishToken;

    Target Publish => _ => _
        .DependsOn(Update)
        .DependsOn(FinalizeChangelog)
        .OnlyWhenDynamic(() => !GitHasCleanWorkingCopy())
        .Triggers(Commit)
        .WhenSkipped(DependencyBehavior.Skip)
        .Produces(WorkingDirectory / "output" / "*")
        .Requires(() => PublishToken)
        .Executes(() =>
        {
            if (File.Exists(GradleBuildFile))
                Gradle($":publishPlugin -PPluginVersion={ReSharperVersion} -PPublishToken={PublishToken} --no-daemon");
            else
                PowerShell($".\\publishPlugin.ps1 -Version {ReSharperVersion} -ApiKey {PublishToken}");
        });

    // [GitRepository] readonly GitRepository GitRepository;
    // [Parameter] readonly string CommitUsername;
    // [Parameter] readonly string CommitAuthToken;
    // string RemoteUrl => $"https://{CommitUsername}:{CommitAuthToken}@github.com/{GitRepository.Identifier}";

    [Parameter] readonly bool AddTag;
    [Parameter] readonly string Branch = "master";

    Target Commit => _ => _
        .DependsOn(Update)
        .Executes(() =>
        {
            if (File.Exists(GradleBuildFile))
            {
                Git($"add {GradleBuildFile}");
                Git($"add {GradlePropertiesFile}");
            }

            Git($"add {PluginPropsFile}");
            Git($"add {ChangelogFile}");

            Git($"commit -m {$"Update SDK to {ReSharperVersion}".DoubleQuote()}");

            if (AddTag)
                Git($"tag {ReSharperVersion}");

            // Git($"remote set-url origin {RemoteUrl}", logInvocation: false);
            Git($"push origin {Branch}");
        });
}
