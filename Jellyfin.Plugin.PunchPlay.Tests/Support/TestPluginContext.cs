using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.PunchPlay.Tests.Support;

internal sealed class TestPluginContext : IDisposable
{
    private readonly string _rootPath;

    public TestPluginContext()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "PunchPlayTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootPath);

        Plugin = new Plugin(new TestApplicationPaths(_rootPath), new TestXmlSerializer());
        Plugin.UpdateConfiguration(new PluginConfiguration
        {
            PunchPlayUrl = "https://punchplay.example",
            ProgressIntervalSeconds = 15,
            ServerId = "test-server-id"
        });
    }

    public Plugin Plugin { get; }

    public void Dispose()
    {
        BaseItem.LibraryManager = null!;

        try
        {
            if (Directory.Exists(_rootPath))
            {
                Directory.Delete(_rootPath, true);
            }
        }
        catch
        {
            // Best-effort cleanup for test scratch directories.
        }
    }

    private sealed class TestApplicationPaths : IApplicationPaths
    {
        private readonly string _rootPath;

        public TestApplicationPaths(string rootPath)
        {
            _rootPath = rootPath;
        }

        public string ProgramDataPath => Path.Combine(_rootPath, "program-data");

        public string WebPath => Path.Combine(_rootPath, "web");

        public string ProgramSystemPath => Path.Combine(_rootPath, "program-system");

        public string DataPath => Path.Combine(_rootPath, "data");

        public string ImageCachePath => Path.Combine(_rootPath, "image-cache");

        public string PluginsPath => Path.Combine(_rootPath, "plugins");

        public string PluginConfigurationsPath => Path.Combine(_rootPath, "plugin-configurations");

        public string LogDirectoryPath => Path.Combine(_rootPath, "logs");

        public string ConfigurationDirectoryPath => Path.Combine(_rootPath, "config");

        public string SystemConfigurationFilePath => Path.Combine(ConfigurationDirectoryPath, "system.xml");

        public string CachePath => Path.Combine(_rootPath, "cache");

        public string TempDirectory => Path.Combine(_rootPath, "temp");

        public string VirtualDataPath => Path.Combine(_rootPath, "virtual-data");

        public string TrickplayPath => Path.Combine(_rootPath, "trickplay");

        public string BackupPath => Path.Combine(_rootPath, "backup");

        public void MakeSanityCheckOrThrow()
        {
            Directory.CreateDirectory(ProgramDataPath);
            Directory.CreateDirectory(WebPath);
            Directory.CreateDirectory(ProgramSystemPath);
            Directory.CreateDirectory(DataPath);
            Directory.CreateDirectory(ImageCachePath);
            Directory.CreateDirectory(PluginsPath);
            Directory.CreateDirectory(PluginConfigurationsPath);
            Directory.CreateDirectory(LogDirectoryPath);
            Directory.CreateDirectory(ConfigurationDirectoryPath);
            Directory.CreateDirectory(CachePath);
            Directory.CreateDirectory(TempDirectory);
            Directory.CreateDirectory(VirtualDataPath);
            Directory.CreateDirectory(TrickplayPath);
            Directory.CreateDirectory(BackupPath);
        }

        public void CreateAndCheckMarker(string path, string markerName, bool recursive = false)
        {
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, markerName), string.Empty);
        }
    }

    private sealed class TestXmlSerializer : IXmlSerializer
    {
        public object DeserializeFromStream(Type type, Stream stream)
        {
            var serializer = new System.Xml.Serialization.XmlSerializer(type);
            return serializer.Deserialize(stream) ?? Activator.CreateInstance(type)!;
        }

        public void SerializeToStream(object obj, Stream stream)
        {
            var serializer = new System.Xml.Serialization.XmlSerializer(obj.GetType());
            serializer.Serialize(stream, obj);
        }

        public void SerializeToFile(object obj, string file)
        {
            var directory = Path.GetDirectoryName(file);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var stream = File.Create(file);
            SerializeToStream(obj, stream);
        }

        public object DeserializeFromFile(Type type, string file)
        {
            using var stream = File.OpenRead(file);
            return DeserializeFromStream(type, stream);
        }

        public object DeserializeFromBytes(Type type, byte[] buffer)
        {
            using var stream = new MemoryStream(buffer);
            return DeserializeFromStream(type, stream);
        }
    }
}
