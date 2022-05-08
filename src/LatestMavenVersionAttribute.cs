using System.Linq;
using System.Reflection;
using JetBrains.Annotations;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Utilities;
using Nuke.Common.ValueInjection;

[PublicAPI]
public class LatestMavenVersionAttribute : ValueInjectionAttributeBase
{
    private readonly string _repository;
    private readonly string _groupId;
    private readonly string _artifactId;

    public LatestMavenVersionAttribute(string repository, string groupId, string artifactId = null)
    {
        _repository = repository;
        _groupId = groupId;
        _artifactId = artifactId;
    }

    public bool IncludePrerelease { get; set; }

    public override object GetValue(MemberInfo member, object instance)
    {
        var rssFile = NukeBuild.TemporaryDirectory / $"{_groupId}-{_artifactId ?? _groupId}.xml";
        HttpTasks.HttpDownloadFile($"https://{_repository.TrimStart("https").TrimStart("http").TrimStart("://").TrimEnd("/")}/m2/{_groupId.Replace(".", "/")}/{_artifactId ?? _groupId}/maven-metadata.xml", rssFile);
        return XmlTasks.XmlPeek(rssFile, ".//version").Last(x => !x.Contains('-') || IncludePrerelease);
    }
}
