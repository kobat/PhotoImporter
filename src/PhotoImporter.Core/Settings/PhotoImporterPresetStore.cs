using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace PhotoImporter.Core.Settings
{
    public sealed class PhotoImporterPresetStore
    {
        public const int CurrentVersion = 1;
        public const int MaximumPresetCount = 100;
        public const int MaximumNameLength = 100;
        private const string UtcFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
        private static readonly HashSet<string> KnownPresetAttributes = new HashSet<string>(
            new[] { "id", "name", "createdUtc", "updatedUtc", "lastUsedUtc" },
            StringComparer.Ordinal);
        private static readonly HashSet<string> KnownPresetElements = new HashSet<string>(
            new[]
            {
                "SourceFolder", "DestinationFolder", "TemplateText", "OverwriteExisting",
                "SourceFileSelectionMode", "AssociateSidecars", "SidecarExtensions",
                "AnalyzeJpegOnlyForRawJpegPair", "ReadExifInformation"
            },
            StringComparer.Ordinal);

        private readonly string _presetsPath;
        private readonly TimeSpan _lockTimeout;

        public PhotoImporterPresetStore(string presetsPath, TimeSpan? lockTimeout = null)
        {
            if (string.IsNullOrWhiteSpace(presetsPath))
                throw new ArgumentException("The presets path is required.", nameof(presetsPath));
            var timeout = lockTimeout ?? TimeSpan.FromSeconds(10);
            if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
                throw new ArgumentOutOfRangeException(nameof(lockTimeout));
            _presetsPath = Path.GetFullPath(presetsPath);
            _lockTimeout = timeout;
        }

        public string PresetsPath => _presetsPath;

        public PresetLoadResult Load()
        {
            if (!File.Exists(_presetsPath))
                return new PresetLoadResult(Enumerable.Empty<PhotoImporterPreset>(), null, false);

            try
            {
                var document = ReadDocument(_presetsPath);
                if (document.UnsupportedVersion)
                    return new PresetLoadResult(
                        Enumerable.Empty<PhotoImporterPreset>(),
                        "プリセットファイルのバージョンに対応していません。ファイルは変更せずに残しました。",
                        true);
                return new PresetLoadResult(document.Presets, null, false);
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                return RecoverCorruptFile(ex);
            }
        }

        public IReadOnlyList<PhotoImporterPreset> Add(PhotoImporterPreset preset)
        {
            if (preset == null) throw new ArgumentNullException(nameof(preset));
            return Mutate(document =>
            {
                ValidatePreset(preset);
                if (document.Presets.Count >= MaximumPresetCount)
                    throw new InvalidOperationException("プリセットは100件まで保存できます。");
                EnsureUnique(document.Presets, preset.Id, preset.Name, null);
                document.Presets.Add(preset.Clone());
            });
        }

        public IReadOnlyList<PhotoImporterPreset> Update(PhotoImporterPreset preset)
        {
            if (preset == null) throw new ArgumentNullException(nameof(preset));
            return Mutate(document =>
            {
                ValidatePreset(preset);
                var index = document.Presets.FindIndex(item => item.Id == preset.Id);
                if (index < 0)
                    throw new InvalidOperationException("対象のプリセットは別の PhotoImporter で削除されています。");
                EnsureUnique(document.Presets, preset.Id, preset.Name, preset.Id);
                var existing = document.Presets[index];
                var replacement = preset.Clone();
                replacement.Id = existing.Id;
                replacement.CreatedUtc = existing.CreatedUtc;
                MergeExtensionData(existing, replacement);
                document.Presets[index] = replacement;
            });
        }

        public IReadOnlyList<PhotoImporterPreset> Delete(Guid id)
        {
            return Mutate(document =>
            {
                var index = document.Presets.FindIndex(item => item.Id == id);
                if (index < 0)
                    throw new InvalidOperationException("対象のプリセットは別の PhotoImporter で削除されています。");
                document.Presets.RemoveAt(index);
            });
        }

        public IReadOnlyList<PhotoImporterPreset> TouchLastUsed(Guid id, DateTime utcNow)
        {
            return Mutate(document =>
            {
                var preset = document.Presets.FirstOrDefault(item => item.Id == id);
                if (preset == null)
                    throw new InvalidOperationException("対象のプリセットは別の PhotoImporter で削除されています。");
                preset.LastUsedUtc = EnsureUtc(utcNow);
            });
        }

        public PhotoImporterPreset ReadExportFile(string path)
        {
            var document = ReadDocument(Path.GetFullPath(path));
            if (document.UnsupportedVersion)
                throw new InvalidDataException("インポートするプリセットのバージョンに対応していません。");
            if (document.Presets.Count != 1)
                throw new InvalidDataException("インポートファイルにはプリセットを1件だけ含めてください。");
            return document.Presets[0].Clone();
        }

        public void WriteExportFile(string path, PhotoImporterPreset preset)
        {
            if (preset == null) throw new ArgumentNullException(nameof(preset));
            ValidatePreset(preset);
            var document = new PresetDocument();
            document.Presets.Add(preset.Clone());
            WriteDocumentAtomic(Path.GetFullPath(path), document);
        }

        public static string NormalizeName(string name)
        {
            var normalized = (name ?? string.Empty).Trim();
            if (normalized.Length == 0)
                throw new ArgumentException("プリセット名を入力してください。", nameof(name));
            if (normalized.Length > MaximumNameLength)
                throw new ArgumentException("プリセット名は100文字以内で入力してください。", nameof(name));
            if (normalized.Any(char.IsControl))
                throw new ArgumentException("プリセット名に制御文字は使用できません。", nameof(name));
            return normalized;
        }

        internal static string CreateMutexName(string presetsPath)
        {
            var normalized = Path.GetFullPath(presetsPath).ToUpperInvariant();
            byte[] hash;
            using (var sha256 = SHA256.Create())
                hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(normalized));
            return "PhotoImporter.Presets." +
                   string.Concat(hash.Take(12).Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
        }

        private PresetLoadResult RecoverCorruptFile(Exception originalError)
        {
            Mutex mutex = null;
            var ownsMutex = false;
            try
            {
                mutex = new Mutex(false, CreateMutexName(_presetsPath));
                try
                {
                    ownsMutex = mutex.WaitOne(_lockTimeout);
                }
                catch (AbandonedMutexException)
                {
                    ownsMutex = true;
                }
                if (!ownsMutex)
                    return new PresetLoadResult(
                        Enumerable.Empty<PhotoImporterPreset>(),
                        "プリセットファイルを読み込めず、別の PhotoImporter が使用中のため退避できませんでした。",
                        false);

                if (!File.Exists(_presetsPath))
                    return new PresetLoadResult(Enumerable.Empty<PhotoImporterPreset>(), null, false);

                try
                {
                    var retried = ReadDocument(_presetsPath);
                    if (retried.UnsupportedVersion)
                        return new PresetLoadResult(
                            Enumerable.Empty<PhotoImporterPreset>(),
                            "プリセットファイルのバージョンに対応していません。ファイルは変更せずに残しました。",
                            true);
                    return new PresetLoadResult(retried.Presets, null, false);
                }
                catch (Exception ex) when (IsReadFailure(ex))
                {
                    var badPath = CreateBadPath();
                    try
                    {
                        File.Move(_presetsPath, badPath);
                    }
                    catch (Exception moveError) when (moveError is IOException || moveError is UnauthorizedAccessException)
                    {
                        return new PresetLoadResult(
                            Enumerable.Empty<PhotoImporterPreset>(),
                            "プリセットファイルを読み込めず、退避もできませんでした。元ファイルは変更していません。 " +
                            moveError.Message,
                            false);
                    }
                    try
                    {
                        WriteDocumentAtomic(_presetsPath, new PresetDocument());
                        return new PresetLoadResult(
                            Enumerable.Empty<PhotoImporterPreset>(),
                            "プリセットファイルを読み込めなかったため、" + Path.GetFileName(badPath) + " へ退避しました。",
                            false);
                    }
                    catch (Exception createError) when (createError is IOException ||
                                                         createError is UnauthorizedAccessException ||
                                                         createError is InvalidOperationException)
                    {
                        return new PresetLoadResult(
                            Enumerable.Empty<PhotoImporterPreset>(),
                            "プリセットファイルを " + Path.GetFileName(badPath) +
                            " へ退避しましたが、新しいファイルを作成できませんでした。 " + createError.Message,
                            false);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new PresetLoadResult(
                    Enumerable.Empty<PhotoImporterPreset>(),
                    "プリセットファイルを読み込めませんでした。元ファイルは変更していません。 " +
                    originalError.Message,
                    false);
            }
            finally
            {
                if (ownsMutex) mutex.ReleaseMutex();
                mutex?.Dispose();
            }
        }

        private IReadOnlyList<PhotoImporterPreset> Mutate(Action<PresetDocument> mutation)
        {
            Mutex mutex = null;
            var ownsMutex = false;
            try
            {
                mutex = new Mutex(false, CreateMutexName(_presetsPath));
                try
                {
                    ownsMutex = mutex.WaitOne(_lockTimeout);
                }
                catch (AbandonedMutexException)
                {
                    ownsMutex = true;
                }
                if (!ownsMutex)
                    throw new TimeoutException("別の PhotoImporter がプリセットを使用しています。しばらく待ってから再試行してください。");

                var document = File.Exists(_presetsPath)
                    ? ReadDocument(_presetsPath)
                    : new PresetDocument();
                if (document.UnsupportedVersion)
                    throw new InvalidDataException("プリセットファイルのバージョンに対応していないため更新できません。");
                mutation(document);
                ValidateDocument(document);
                WriteDocumentAtomic(_presetsPath, document);
                return document.Presets.Select(item => item.Clone()).ToList().AsReadOnly();
            }
            finally
            {
                if (ownsMutex) mutex.ReleaseMutex();
                mutex?.Dispose();
            }
        }

        private static PresetDocument ReadDocument(string path)
        {
            try
            {
                var xml = XDocument.Load(path, LoadOptions.None);
                var root = xml.Root;
                if (root == null || root.Name != "PhotoImporterPresets")
                    throw new InvalidDataException("プリセットファイルのルート要素が正しくありません。");
                int version;
                if (!int.TryParse((string)root.Attribute("version"), NumberStyles.None,
                    CultureInfo.InvariantCulture, out version))
                    throw new InvalidDataException("プリセットファイルのバージョンが正しくありません。");

                var result = new PresetDocument { UnsupportedVersion = version != CurrentVersion };
                if (result.UnsupportedVersion) return result;
                foreach (var attribute in root.Attributes().Where(item => item.Name != "version"))
                    result.ExtensionAttributes.Add(new XAttribute(attribute));
                foreach (var element in root.Elements())
                {
                    if (element.Name == "Preset") result.Presets.Add(ReadPreset(element));
                    else result.ExtensionElements.Add(new XElement(element));
                }
                ValidateDocument(result);
                return result;
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is XmlException || ex is ArgumentException)
            {
                throw new InvalidDataException("プリセットファイルを読み込めませんでした。", ex);
            }
        }

        private static PhotoImporterPreset ReadPreset(XElement element)
        {
            Guid id;
            if (!Guid.TryParse((string)element.Attribute("id"), out id))
                throw new InvalidDataException("プリセットの id が正しくありません。");
            var preset = new PhotoImporterPreset
            {
                Id = id,
                Name = NormalizeName((string)element.Attribute("name")),
                CreatedUtc = ReadUtcAttribute(element, "createdUtc", false).Value,
                UpdatedUtc = ReadUtcAttribute(element, "updatedUtc", false).Value,
                LastUsedUtc = ReadUtcAttribute(element, "lastUsedUtc", true),
                DestinationFolder = ReadRequiredElement(element, "DestinationFolder"),
                TemplateText = ReadRequiredElement(element, "TemplateText"),
                OverwriteExisting = ReadBooleanElement(element, "OverwriteExisting"),
                SourceFileSelectionMode = ReadEnumElement<SourceFileSelectionMode>(element, "SourceFileSelectionMode"),
                AssociateSidecars = ReadBooleanElement(element, "AssociateSidecars"),
                AnalyzeJpegOnlyForRawJpegPair = ReadBooleanElement(element, "AnalyzeJpegOnlyForRawJpegPair"),
                ReadExifInformation = ReadBooleanElement(element, "ReadExifInformation")
            };
            var source = element.Element("SourceFolder");
            bool saved;
            if (source == null || !bool.TryParse((string)source.Attribute("saved"), out saved))
                throw new InvalidDataException("SourceFolder の saved 属性が正しくありません。");
            preset.SaveSourceFolder = saved;
            preset.SourceFolder = source.Value;
            var sidecars = element.Element("SidecarExtensions");
            if (sidecars == null)
                throw new InvalidDataException("SidecarExtensions がありません。");
            foreach (var extension in sidecars.Elements("Extension"))
                preset.SidecarExtensions.Add(extension.Value);
            foreach (var attribute in element.Attributes().Where(item => !KnownPresetAttributes.Contains(item.Name.LocalName)))
                preset.ExtensionAttributes.Add(new XAttribute(attribute));
            foreach (var child in element.Elements().Where(item => !KnownPresetElements.Contains(item.Name.LocalName)))
                preset.ExtensionElements.Add(new XElement(child));
            return preset;
        }

        private static void WriteDocumentAtomic(string destinationPath, PresetDocument document)
        {
            var directory = Path.GetDirectoryName(destinationPath);
            if (string.IsNullOrEmpty(directory))
                throw new InvalidOperationException("プリセットの保存先を特定できません。");
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(directory, ".presets_" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                var root = new XElement("PhotoImporterPresets", new XAttribute("version", CurrentVersion));
                foreach (var attribute in document.ExtensionAttributes) root.Add(new XAttribute(attribute));
                foreach (var preset in document.Presets) root.Add(WritePreset(preset));
                foreach (var element in document.ExtensionElements) root.Add(new XElement(element));
                var xmlSettings = new XmlWriterSettings
                {
                    Encoding = new UTF8Encoding(true),
                    Indent = true,
                    NewLineChars = "\r\n",
                    NewLineHandling = NewLineHandling.Replace
                };
                using (var writer = XmlWriter.Create(temporaryPath, xmlSettings))
                    new XDocument(root).Save(writer);
                if (File.Exists(destinationPath)) File.Replace(temporaryPath, destinationPath, null);
                else File.Move(temporaryPath, destinationPath);
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

        private static XElement WritePreset(PhotoImporterPreset preset)
        {
            var element = new XElement("Preset",
                new XAttribute("id", preset.Id.ToString("D")),
                new XAttribute("name", preset.Name),
                new XAttribute("createdUtc", FormatUtc(preset.CreatedUtc)),
                new XAttribute("updatedUtc", FormatUtc(preset.UpdatedUtc)));
            if (preset.LastUsedUtc.HasValue)
                element.Add(new XAttribute("lastUsedUtc", FormatUtc(preset.LastUsedUtc.Value)));
            foreach (var attribute in preset.ExtensionAttributes)
                if (!KnownPresetAttributes.Contains(attribute.Name.LocalName)) element.Add(new XAttribute(attribute));
            element.Add(
                new XElement("SourceFolder", new XAttribute("saved", preset.SaveSourceFolder),
                    preset.SaveSourceFolder ? preset.SourceFolder ?? string.Empty : string.Empty),
                new XElement("DestinationFolder", preset.DestinationFolder ?? string.Empty),
                new XElement("TemplateText", preset.TemplateText ?? string.Empty),
                new XElement("OverwriteExisting", preset.OverwriteExisting),
                new XElement("SourceFileSelectionMode", preset.SourceFileSelectionMode),
                new XElement("AssociateSidecars", preset.AssociateSidecars),
                new XElement("SidecarExtensions", preset.SidecarExtensions.Select(value => new XElement("Extension", value))),
                new XElement("AnalyzeJpegOnlyForRawJpegPair", preset.AnalyzeJpegOnlyForRawJpegPair),
                new XElement("ReadExifInformation", preset.ReadExifInformation));
            foreach (var extension in preset.ExtensionElements)
                if (!KnownPresetElements.Contains(extension.Name.LocalName)) element.Add(new XElement(extension));
            return element;
        }

        private string CreateBadPath()
        {
            var directory = Path.GetDirectoryName(_presetsPath);
            var baseName = Path.Combine(directory, "presets.bad.xml");
            if (!File.Exists(baseName)) return baseName;
            for (var index = 1; ; index++)
            {
                var candidate = Path.Combine(directory,
                    "presets.bad." + index.ToString(CultureInfo.InvariantCulture) + ".xml");
                if (!File.Exists(candidate)) return candidate;
            }
        }

        private static void ValidateDocument(PresetDocument document)
        {
            if (document.Presets.Count > MaximumPresetCount)
                throw new InvalidDataException("プリセット件数が100件を超えています。");
            var ids = new HashSet<Guid>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var preset in document.Presets)
            {
                ValidatePreset(preset);
                if (!ids.Add(preset.Id)) throw new InvalidDataException("同じ id のプリセットが複数あります。");
                if (!names.Add(preset.Name)) throw new InvalidDataException("同じ名前のプリセットが複数あります。");
            }
        }

        private static void ValidatePreset(PhotoImporterPreset preset)
        {
            if (preset.Id == Guid.Empty) throw new InvalidDataException("プリセットの id が空です。");
            preset.Name = NormalizeName(preset.Name);
            preset.CreatedUtc = EnsureUtc(preset.CreatedUtc);
            preset.UpdatedUtc = EnsureUtc(preset.UpdatedUtc);
            if (preset.LastUsedUtc.HasValue) preset.LastUsedUtc = EnsureUtc(preset.LastUsedUtc.Value);
            if (!Enum.IsDefined(typeof(SourceFileSelectionMode), preset.SourceFileSelectionMode))
                throw new InvalidDataException("対象ファイル列挙モードが正しくありません。");
        }

        private static void EnsureUnique(
            IEnumerable<PhotoImporterPreset> presets,
            Guid id,
            string name,
            Guid? excludedId)
        {
            if (presets.Any(item => item.Id == id && (!excludedId.HasValue || item.Id != excludedId.Value)))
                throw new InvalidOperationException("同じ id のプリセットが既にあります。");
            if (presets.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase) &&
                                    (!excludedId.HasValue || item.Id != excludedId.Value)))
                throw new InvalidOperationException("同じ名前のプリセットが既にあります。");
        }

        private static void MergeExtensionData(PhotoImporterPreset existing, PhotoImporterPreset replacement)
        {
            if (replacement.ExtensionAttributes.Count == 0)
                foreach (var attribute in existing.ExtensionAttributes)
                    replacement.ExtensionAttributes.Add(new XAttribute(attribute));
            if (replacement.ExtensionElements.Count == 0)
                foreach (var element in existing.ExtensionElements)
                    replacement.ExtensionElements.Add(new XElement(element));
        }

        private static DateTime? ReadUtcAttribute(XElement element, string name, bool optional)
        {
            var text = (string)element.Attribute(name);
            if (optional && string.IsNullOrEmpty(text)) return null;
            DateTime value;
            if (string.IsNullOrEmpty(text) || !DateTime.TryParseExact(
                text, UtcFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value))
                throw new InvalidDataException(name + " が正しくありません。");
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        private static string ReadRequiredElement(XElement root, string name)
        {
            var element = root.Element(name);
            if (element == null) throw new InvalidDataException(name + " がありません。");
            return element.Value;
        }

        private static bool ReadBooleanElement(XElement root, string name)
        {
            bool value;
            if (!bool.TryParse(ReadRequiredElement(root, name), out value))
                throw new InvalidDataException(name + " が正しくありません。");
            return value;
        }

        private static T ReadEnumElement<T>(XElement root, string name) where T : struct
        {
            T value;
            if (!Enum.TryParse(ReadRequiredElement(root, name), true, out value) ||
                !Enum.IsDefined(typeof(T), value))
                throw new InvalidDataException(name + " が正しくありません。");
            return value;
        }

        private static DateTime EnsureUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Unspecified)
                throw new InvalidDataException("日時には UTC を指定してください。");
            return value.ToUniversalTime();
        }

        private static string FormatUtc(DateTime value) =>
            EnsureUtc(value).ToString(UtcFormat, CultureInfo.InvariantCulture);

        private static bool IsReadFailure(Exception ex) =>
            ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException;

        private sealed class PresetDocument
        {
            public bool UnsupportedVersion { get; set; }
            public List<PhotoImporterPreset> Presets { get; } = new List<PhotoImporterPreset>();
            public List<XAttribute> ExtensionAttributes { get; } = new List<XAttribute>();
            public List<XElement> ExtensionElements { get; } = new List<XElement>();
        }
    }
}
