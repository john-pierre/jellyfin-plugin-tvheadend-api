// Serialization compatibility tests for the deprecated mediainfo-cache configuration flags.

using System.IO;
using System.Xml.Serialization;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Configuration;

/// <summary>
/// The two mediainfo-cache flags are deprecated (no backend effect) via XML-doc note only:
/// an <c>[Obsolete]</c> attribute was verified to make <see cref="XmlSerializer"/> SKIP the
/// members entirely, which would silently drop the values from existing configuration XML.
/// These tests pin the required round-trip behavior.
/// </summary>
public sealed class PluginConfigurationSerializationTests
{
    [Fact]
    public void XmlSerializer_RoundTrips_ObsoleteMediaInfoCacheFlags()
    {
        var config = new PluginConfiguration
        {
            EnableMediaInfoCacheWrite = false,       // non-default on purpose
            EnableMediaInfoCacheValidation = false,  // non-default on purpose
        };

        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        string xml;
        using (var writer = new StringWriter())
        {
            serializer.Serialize(writer, config);
            xml = writer.ToString();
        }

        Assert.Contains("EnableMediaInfoCacheWrite", xml);
        Assert.Contains("EnableMediaInfoCacheValidation", xml);

        using var reader = new StringReader(xml);
        var roundTripped = (PluginConfiguration)serializer.Deserialize(reader)!;

        Assert.False(roundTripped.EnableMediaInfoCacheWrite);
        Assert.False(roundTripped.EnableMediaInfoCacheValidation);
    }

    [Fact]
    public void XmlSerializer_Deserializes_LegacyXmlWithFlags()
    {
        // Simulates a pre-existing configuration file written by an older plugin version.
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        var defaults = new PluginConfiguration();
        string xml;
        using (var writer = new StringWriter())
        {
            serializer.Serialize(writer, defaults);
            xml = writer.ToString();
        }

        xml = xml
            .Replace("<EnableMediaInfoCacheWrite>true</EnableMediaInfoCacheWrite>", "<EnableMediaInfoCacheWrite>false</EnableMediaInfoCacheWrite>")
            .Replace("<EnableMediaInfoCacheValidation>true</EnableMediaInfoCacheValidation>", "<EnableMediaInfoCacheValidation>false</EnableMediaInfoCacheValidation>");

        using var reader = new StringReader(xml);
        var loaded = (PluginConfiguration)serializer.Deserialize(reader)!;

        Assert.False(loaded.EnableMediaInfoCacheWrite);
        Assert.False(loaded.EnableMediaInfoCacheValidation);
    }
}
