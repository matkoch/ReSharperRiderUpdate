using System.Collections.Generic;
using System.Linq;
using AngleSharp;
using NuGet.Common;
using NuGet.Versioning;
using Nuke.Common;
using Nuke.Common.Tooling;
using Serilog;

partial class Build
{
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

    [LatestNuGetVersion("JetBrains.ReSharper.SDK", IncludePrerelease = true)]
    readonly NuGetVersion ReSharperVersion;

    string ReSharperShortVersion => $"{ReSharperVersion.Major}.{ReSharperVersion.Minor}";

    string IdeaPrereleaseTag => ReSharperVersion.IsPrerelease
        ? $"-{ReSharperVersion.ReleaseLabels.Single().Replace("eap0", "eap").ToUpperInvariant()}-SNAPSHOT"
        : null;

    string IdeaVersion => $"{ReSharperVersion.Major}.{ReSharperVersion.Minor}{IdeaPrereleaseTag}";

    [LatestGitHubRelease("JetBrains/rd", UseTagName = true)]
    readonly NuGetVersion[] AllRdGenVersions;

    NuGetVersion RdGenVersion => AllRdGenVersions.FirstOrDefault(x => !x.IsPrerelease);

    [LatestGitHubRelease("JetBrains/gradle-intellij-plugin", IncludePrerelease = false)]
    readonly NuGetVersion GradlePluginVersion;

    readonly AsyncLazy<List<(string PlatformVersion, string StdLibVersion)>> KotlinStdLibVersions = new(async () =>
    {
        var url = "https://plugins.jetbrains.com/docs/intellij/using-kotlin.html#kotlin-standard-library";

        var config = Configuration.Default.WithDefaultLoader();
        var context = BrowsingContext.New(config);
        var document = await context.OpenAsync(url);
        return document
            .QuerySelectorAll("table")
            .Where(x => x.QuerySelectorAll("span").Any(x => x.TextContent == "stdlib"))
            .SelectMany(x => x.GetElementsByTagName("tbody"))
            .SelectMany(x => x.QuerySelectorAll("p"))
            .Select(x => x.TextContent)
            .Chunk(2)
            .Select(x => (x[0], x[1])).ToList();
    });

    // [LatestMavenVersion(
    //     repository: "plugins.gradle.org/m2",
    //     groupId: "org.jetbrains.kotlin.jvm",
    //     artifactId: "org.jetbrains.kotlin.jvm.gradle.plugin")]
    string KotlinJvmVersion => KotlinStdLibVersions.GetAwaiter().GetResult()
        .FirstOrDefault(x => x.PlatformVersion == $"{ReSharperVersion.Major}.{ReSharperVersion.Minor}")
        .StdLibVersion;
}
