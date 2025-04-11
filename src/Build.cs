using System.Linq;
using System.Text.RegularExpressions;
using Nuke.Common;
using Nuke.Common.ChangeLog;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Utilities;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.ChangeLog.ChangelogTasks;
using static Nuke.Common.EnvironmentInfo;
using static Nuke.Common.IO.XmlTasks;
using static Nuke.Common.Tools.Git.GitTasks;

// TODO: check file size
partial class Build : NukeBuild, IGlobalTool
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode
    public static int Main() => Execute<Build>(x => x.Versions);

    AbsolutePath PluginPropsFile => WorkingDirectory / "src" / "dotnet" / "Plugin.props";
    AbsolutePath GradleBuildFile => WorkingDirectory / "build.gradle.kts";
    AbsolutePath LibsVersionsTomlFile => WorkingDirectory / "gradle" / "libs.versions.toml";
    AbsolutePath GradlePropertiesFile => WorkingDirectory / "gradle.properties";

    bool HasReSharperImplementation => PluginPropsFile.Exists();
    bool HasRiderImplementation => GradlePropertiesFile.Exists() && GradleBuildFile.Exists();

    Target UpdateReSharper => _ => _
        .OnlyWhenStatic(() => HasReSharperImplementation)
        .Executes(() =>
        {
            XmlPoke(
                path: PluginPropsFile,
                xpath: ".//SdkVersion",
                ReSharperVersion);
        });

    Target UpdateRider => _ => _
        .Requires(() => KotlinJvmVersion != null)
        // .Requires(() => GradlePluginVersion)
        .Requires(() => RdGenVersion != null)
        .OnlyWhenStatic(() => HasRiderImplementation)
        .Executes(() =>
        {
            GradlePropertiesFile.UpdateText(_ => _
                .ReplaceRegex("^ProductVersion=.+$", _ => $"ProductVersion={IdeaVersion}", RegexOptions.Multiline));

            GradleBuildFile.UpdateText(_ => _
                .ReplaceRegex(@"id\(""org.jetbrains.intellij.platform""\) version ""\d+\.\d+(\.\d+)?(-\w+)?""",
                    _ => $@"id(""org.jetbrains.intellij.platform"") version ""{GradlePluginVersion}"""));

            LibsVersionsTomlFile.UpdateText(_ => _
                .ReplaceRegex(@"kotlin = ""\d+\.\d+(\.\d+)?(-\w+)?""", _ => $@"kotlin = ""{KotlinJvmVersion}""")
                .ReplaceRegex(@"rdGen = ""\d+\.\d+(\.\d+)?(-\w+)?""", _ => $@"rdGen = ""{RdGenVersion.NotNull()}"""));
        });

    AbsolutePath ChangelogFile => WorkingDirectory / "CHANGELOG.md";
    ChangeLog Changelog => ReadChangelog(ChangelogFile);

    Target UpdateChangelog => _ => _
        .OnlyWhenStatic(() => ChangelogFile.Exists() && Changelog.Unreleased == null)
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
        .OnlyWhenStatic(() => ChangelogFile.Exists() && Changelog.Unreleased != null)
        .OnlyWhenStatic(() => !ReSharperVersion.IsPrerelease)
        .Executes(() =>
        {
            ChangelogTasks.FinalizeChangelog(ChangelogFile, ReSharperVersion.ToString());
        });

    Target Update => _ => _
        .WhenSkipped(DependencyBehavior.Skip)
        .DependsOn(Versions)
        .DependsOn(UpdateReSharper)
        .DependsOn(UpdateRider)
        .DependsOn(UpdateChangelog);

    [LocalPath(windowsPath: "gradlew.bat", unixPath: "gradlew")] readonly Tool Gradle;
    [PathVariable("powershell")] readonly Tool PowerShell;

    Target Compile => _ => _
        .DependsOn(Update)
        .Produces(WorkingDirectory / "output" / "*")
        .Executes(() =>
        {
            if (HasRiderImplementation)
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
            if (HasRiderImplementation)
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
        .DependsOn(FinalizeChangelog)
        .OnlyWhenDynamic(() => !GitHasCleanWorkingCopy())
        .Executes(() =>
        {
            if (HasRiderImplementation)
            {
                Git($"add {GradleBuildFile}");
                Git($"add {GradlePropertiesFile}");
                Git($"add {LibsVersionsTomlFile}");
            }

            Git($"add {PluginPropsFile}");
            Git($"add {ChangelogFile}");

            Git($"commit -m {$"build: update SDK to {ReSharperVersion}".DoubleQuote()}");
            Git($"tag {ReSharperVersion}");

            // Git($"remote set-url origin {RemoteUrl}", logInvocation: false);
            // Git($"push origin {Branch}");
        });
}
