using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using PhotoImporter.Core.Metadata;

namespace PhotoImporter.Core.Settings
{
    public sealed class PhotoImporterSettings
    {
        public const string DefaultTemplate = @"{ModifiedDate:yyyy-MM-dd}\{FileName}{Sequence}{Extension}";
        public const int DefaultInputHistoryLimit = 10;
        public const int MaximumInputHistoryLimit = 100;

        public PhotoImporterSettings()
        {
            TemplateText = DefaultTemplate;
            SourceFileSelectionMode = SourceFileSelectionMode.MediaOnly;
            AssociateSidecars = false;
            AnalyzeJpegOnlyForRawJpegPair = true;
            UseExifCache = true;
            ReadExifInformation = false;
            ShowImagePreview = false;
            InputHistoryLimit = DefaultInputHistoryLimit;
            SidecarExtensions = new List<string> { ".xmp" };
            PreviousExifCacheRoots = new List<string>();
        }

        public string SourceFolder { get; set; }
        public string DestinationFolder { get; set; }
        public string TemplateText { get; set; }
        public bool OverwriteExisting { get; set; }
        public SourceFileSelectionMode SourceFileSelectionMode { get; set; }
        public bool AssociateSidecars { get; set; }
        public bool AnalyzeJpegOnlyForRawJpegPair { get; set; }
        public bool UseExifCache { get; set; }
        public bool ReadExifInformation { get; set; }
        public bool ShowImagePreview { get; set; }
        public int InputHistoryLimit { get; set; }
        public string CustomExifCacheRoot { get; set; }
        public Guid? LastAppliedPresetId { get; set; }
        public IList<string> SidecarExtensions { get; }
        public IList<string> PreviousExifCacheRoots { get; }

        public string ResolveExifCacheRoot(string applicationBaseDirectory)
        {
            if (string.IsNullOrWhiteSpace(applicationBaseDirectory))
                throw new ArgumentException("The application base directory is required.", nameof(applicationBaseDirectory));

            return string.IsNullOrWhiteSpace(CustomExifCacheRoot)
                ? Path.GetFullPath(Path.Combine(applicationBaseDirectory, "ExifCache"))
                : Path.GetFullPath(CustomExifCacheRoot);
        }
    }

    public sealed class PhotoImporterSettingsStore
    {
        private const int CurrentVersion = 1;
        private readonly string _settingsPath;

        public PhotoImporterSettingsStore(string settingsPath)
        {
            if (string.IsNullOrWhiteSpace(settingsPath))
                throw new ArgumentException("The settings path is required.", nameof(settingsPath));
            _settingsPath = Path.GetFullPath(settingsPath);
        }

        public string SettingsPath => _settingsPath;

        public PhotoImporterSettings Load()
        {
            if (!File.Exists(_settingsPath)) return new PhotoImporterSettings();

            try
            {
                var document = XDocument.Load(_settingsPath, LoadOptions.None);
                var root = document.Root;
                int version;
                if (root == null || root.Name != "PhotoImporterSettings" ||
                    !int.TryParse((string)root.Attribute("version"), out version) ||
                    version != CurrentVersion)
                {
                    throw new InvalidDataException("設定ファイルの形式またはバージョンに対応していません。");
                }

                var settings = new PhotoImporterSettings
                {
                    SourceFolder = ReadOptional(root, "SourceFolder"),
                    DestinationFolder = ReadOptional(root, "DestinationFolder"),
                    TemplateText = ReadOptional(root, "TemplateText") ?? PhotoImporterSettings.DefaultTemplate,
                    OverwriteExisting = ReadBoolean(root, "OverwriteExisting", false),
                    SourceFileSelectionMode = ReadEnum(
                        root,
                        "SourceFileSelectionMode",
                        SourceFileSelectionMode.MediaOnly),
                    AssociateSidecars = ReadBoolean(root, "AssociateSidecars", false),
                    AnalyzeJpegOnlyForRawJpegPair = ReadBoolean(root, "AnalyzeJpegOnlyForRawJpegPair", true),
                    UseExifCache = ReadBoolean(root, "UseExifCache", true),
                    ReadExifInformation = ReadBoolean(root, "ReadExifInformation", false),
                    ShowImagePreview = ReadBoolean(root, "ShowImagePreview", false),
                    InputHistoryLimit = ReadInt32InRange(
                        root,
                        "InputHistoryLimit",
                        PhotoImporterSettings.DefaultInputHistoryLimit,
                        0,
                        PhotoImporterSettings.MaximumInputHistoryLimit),
                    CustomExifCacheRoot = NormalizeOptionalAbsolutePath(ReadOptional(root, "CustomExifCacheRoot")),
                    LastAppliedPresetId = ReadOptionalGuid(root, "LastAppliedPresetId")
                };

                var sidecarExtensions = root.Element("SidecarExtensions");
                if (sidecarExtensions != null)
                {
                    var policy = SidecarPolicy.Create(
                        settings.AssociateSidecars,
                        sidecarExtensions.Elements("Extension").Select(item => item.Value));
                    settings.SidecarExtensions.Clear();
                    foreach (var extension in policy.Extensions)
                        settings.SidecarExtensions.Add(extension);
                }

                var previousRoots = root.Element("PreviousExifCacheRoots");
                if (previousRoots != null)
                {
                    foreach (var path in previousRoots.Elements("Path")
                        .Select(item => NormalizeOptionalAbsolutePath(item.Value))
                        .Where(item => item != null)
                        .Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        settings.PreviousExifCacheRoots.Add(path);
                    }
                }

                return settings;
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is XmlException || ex is ArgumentException)
            {
                throw new InvalidDataException("設定ファイルを読み込めませんでした。", ex);
            }
        }

        public void Save(PhotoImporterSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (settings.InputHistoryLimit < 0 ||
                settings.InputHistoryLimit > PhotoImporterSettings.MaximumInputHistoryLimit)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(settings.InputHistoryLimit),
                    string.Format("入力履歴の保存件数は0～{0}件で指定してください。",
                        PhotoImporterSettings.MaximumInputHistoryLimit));
            }
            var sidecarPolicy = SidecarPolicy.Create(
                settings.AssociateSidecars,
                settings.SidecarExtensions);

            var directory = Path.GetDirectoryName(_settingsPath);
            if (string.IsNullOrEmpty(directory))
                throw new InvalidOperationException("設定ファイルの保存先を特定できません。");
            Directory.CreateDirectory(directory);

            var temporaryPath = Path.Combine(
                directory,
                ".settings_" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                var document = new XDocument(
                    new XElement("PhotoImporterSettings",
                        new XAttribute("version", CurrentVersion),
                        new XElement("SourceFolder", settings.SourceFolder ?? string.Empty),
                        new XElement("DestinationFolder", settings.DestinationFolder ?? string.Empty),
                        new XElement("TemplateText", string.IsNullOrEmpty(settings.TemplateText)
                            ? PhotoImporterSettings.DefaultTemplate
                            : settings.TemplateText),
                        new XElement("OverwriteExisting", settings.OverwriteExisting),
                        new XElement("SourceFileSelectionMode", settings.SourceFileSelectionMode),
                        new XElement("AssociateSidecars", settings.AssociateSidecars),
                        new XElement("SidecarExtensions",
                            sidecarPolicy.Extensions.Select(extension =>
                                new XElement("Extension", extension))),
                        new XElement("AnalyzeJpegOnlyForRawJpegPair", settings.AnalyzeJpegOnlyForRawJpegPair),
                        new XElement("UseExifCache", settings.UseExifCache),
                        new XElement("ReadExifInformation", settings.ReadExifInformation),
                        new XElement("ShowImagePreview", settings.ShowImagePreview),
                        new XElement("InputHistoryLimit", settings.InputHistoryLimit),
                        new XElement("LastAppliedPresetId",
                            settings.LastAppliedPresetId.HasValue
                                ? settings.LastAppliedPresetId.Value.ToString("D")
                                : string.Empty),
                        new XElement("CustomExifCacheRoot", settings.CustomExifCacheRoot ?? string.Empty),
                        new XElement("PreviousExifCacheRoots",
                            settings.PreviousExifCacheRoots
                                .Where(path => !string.IsNullOrWhiteSpace(path))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .Select(path => new XElement("Path", Path.GetFullPath(path))))));

                var xmlSettings = new XmlWriterSettings
                {
                    Encoding = new UTF8Encoding(true),
                    Indent = true,
                    NewLineChars = "\r\n",
                    NewLineHandling = NewLineHandling.Replace
                };
                using (var writer = XmlWriter.Create(temporaryPath, xmlSettings))
                    document.Save(writer);

                if (File.Exists(_settingsPath))
                    File.Replace(temporaryPath, _settingsPath, null);
                else
                    File.Move(temporaryPath, _settingsPath);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static string ReadOptional(XElement root, string name)
        {
            var element = root.Element(name);
            return element == null || string.IsNullOrWhiteSpace(element.Value) ? null : element.Value;
        }

        private static bool ReadBoolean(XElement root, string name, bool defaultValue)
        {
            var element = root.Element(name);
            bool value;
            return element != null && bool.TryParse(element.Value, out value) ? value : defaultValue;
        }

        private static int ReadInt32InRange(
            XElement root,
            string name,
            int defaultValue,
            int minimum,
            int maximum)
        {
            var element = root.Element(name);
            int value;
            return element != null &&
                   int.TryParse(element.Value, out value) &&
                   value >= minimum &&
                   value <= maximum
                ? value
                : defaultValue;
        }

        private static T ReadEnum<T>(XElement root, string name, T defaultValue)
            where T : struct
        {
            var element = root.Element(name);
            T value;
            return element != null &&
                   Enum.TryParse(element.Value, true, out value) &&
                   Enum.IsDefined(typeof(T), value)
                ? value
                : defaultValue;
        }

        private static Guid? ReadOptionalGuid(XElement root, string name)
        {
            var text = ReadOptional(root, name);
            if (text == null) return null;
            Guid value;
            return Guid.TryParse(text, out value) && value != Guid.Empty ? value : (Guid?)null;
        }

        private static string NormalizeOptionalAbsolutePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                if (!Path.IsPathRooted(path)) return null;
                var fullPath = Path.GetFullPath(path);
                return fullPath;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException ||
                                       ex is PathTooLongException)
            {
                return null;
            }
        }
    }
}
