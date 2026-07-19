using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PhotoImporter.Core.Metadata
{
    public sealed class FileSystemTimestampPolicy
    {
        private const long ExFatResolutionTicks = TimeSpan.TicksPerMillisecond * 10;
        private const long FatResolutionTicks = TimeSpan.TicksPerSecond * 2;

        private enum TimestampStorage
        {
            ExactUtc,
            TruncatedUtc,
            TruncatedLocal,
            Unsupported
        }

        private readonly TimestampStorage _storage;
        private readonly long _resolutionTicks;
        private readonly TimeZoneInfo _localTimeZone;

        private FileSystemTimestampPolicy(
            string fileSystemName,
            TimestampStorage storage,
            long resolutionTicks,
            TimeZoneInfo localTimeZone)
        {
            FileSystemName = fileSystemName ?? string.Empty;
            _storage = storage;
            _resolutionTicks = resolutionTicks;
            _localTimeZone = localTimeZone;
        }

        public string FileSystemName { get; }
        public bool IsSupported => _storage != TimestampStorage.Unsupported;

        public static FileSystemTimestampPolicy Create(string fileSystemName) =>
            Create(fileSystemName, TimeZoneInfo.Local);

        internal static FileSystemTimestampPolicy Create(string fileSystemName, TimeZoneInfo localTimeZone)
        {
            if (localTimeZone == null) throw new ArgumentNullException(nameof(localTimeZone));

            var normalized = (fileSystemName ?? string.Empty).Trim();
            if (string.Equals(normalized, "NTFS", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "ReFS", StringComparison.OrdinalIgnoreCase))
                return new FileSystemTimestampPolicy(normalized, TimestampStorage.ExactUtc, 1, localTimeZone);

            if (string.Equals(normalized, "exFAT", StringComparison.OrdinalIgnoreCase))
                return new FileSystemTimestampPolicy(
                    normalized, TimestampStorage.TruncatedUtc, ExFatResolutionTicks, localTimeZone);

            if (string.Equals(normalized, "FAT", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "FAT32", StringComparison.OrdinalIgnoreCase))
                return new FileSystemTimestampPolicy(
                    normalized, TimestampStorage.TruncatedLocal, FatResolutionTicks, localTimeZone);

            return new FileSystemTimestampPolicy(
                normalized, TimestampStorage.Unsupported, 0, localTimeZone);
        }

        public bool Matches(DateTime expectedUtc, DateTime actualUtc)
        {
            EnsureUtc(expectedUtc, nameof(expectedUtc));
            EnsureUtc(actualUtc, nameof(actualUtc));

            switch (_storage)
            {
                case TimestampStorage.ExactUtc:
                    return expectedUtc == actualUtc;
                case TimestampStorage.TruncatedUtc:
                    return Truncate(expectedUtc, _resolutionTicks).Ticks ==
                           Truncate(actualUtc, _resolutionTicks).Ticks;
                case TimestampStorage.TruncatedLocal:
                    var expectedLocal = TimeZoneInfo.ConvertTimeFromUtc(expectedUtc, _localTimeZone);
                    var actualLocal = TimeZoneInfo.ConvertTimeFromUtc(actualUtc, _localTimeZone);
                    return Truncate(expectedLocal, _resolutionTicks).Ticks ==
                           Truncate(actualLocal, _resolutionTicks).Ticks;
                default:
                    return false;
            }
        }

        private static DateTime Truncate(DateTime value, long resolutionTicks) =>
            new DateTime(value.Ticks - value.Ticks % resolutionTicks, value.Kind);

        private static void EnsureUtc(DateTime value, string name)
        {
            if (value.Kind != DateTimeKind.Utc)
                throw new ArgumentException("The timestamp must be UTC.", name);
        }
    }

    public sealed class VolumeInfo
    {
        public VolumeInfo(
            string rootPath,
            uint serialNumber,
            string label,
            string fileSystemName,
            DriveType driveType,
            ulong totalBytes)
        {
            if (string.IsNullOrWhiteSpace(rootPath)) throw new ArgumentException("A volume root is required.", nameof(rootPath));

            RootPath = EnsureTrailingSeparator(Path.GetFullPath(rootPath));
            SerialNumber = serialNumber;
            Label = label ?? string.Empty;
            FileSystemName = fileSystemName ?? string.Empty;
            TimestampPolicy = FileSystemTimestampPolicy.Create(FileSystemName);
            DriveType = driveType;
            TotalBytes = totalBytes;
        }

        public string RootPath { get; }
        public uint SerialNumber { get; }
        public string SerialNumberHex => SerialNumber.ToString("X8");
        public string Label { get; }
        public string FileSystemName { get; }
        public FileSystemTimestampPolicy TimestampPolicy { get; }
        public DriveType DriveType { get; }
        public ulong TotalBytes { get; }

        private static string EnsureTrailingSeparator(string path) =>
            path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ||
            path.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? path
                : path + Path.DirectorySeparatorChar;
    }

    public interface IVolumeInfoReader
    {
        VolumeInfo Read(string path);
    }

    public sealed class WindowsVolumeInfoReader : IVolumeInfoReader
    {
        public VolumeInfo Read(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));

            var fullPath = Path.GetFullPath(path);
            var volumeRoot = new StringBuilder(1024);
            if (!GetVolumePathName(fullPath, volumeRoot, (uint)volumeRoot.Capacity))
                throw NewWin32Exception("ボリュームルートを取得できませんでした。");

            var label = new StringBuilder(261);
            var fileSystemName = new StringBuilder(261);
            uint serialNumber;
            uint maximumComponentLength;
            uint fileSystemFlags;
            if (!GetVolumeInformation(
                volumeRoot.ToString(),
                label,
                (uint)label.Capacity,
                out serialNumber,
                out maximumComponentLength,
                out fileSystemFlags,
                fileSystemName,
                (uint)fileSystemName.Capacity))
                throw NewWin32Exception("ボリューム情報を取得できませんでした。");

            ulong freeBytesAvailable;
            ulong totalBytes;
            ulong totalFreeBytes;
            if (!GetDiskFreeSpaceEx(volumeRoot.ToString(), out freeBytesAvailable, out totalBytes, out totalFreeBytes))
                throw NewWin32Exception("ボリューム容量を取得できませんでした。");

            return new VolumeInfo(
                volumeRoot.ToString(),
                serialNumber,
                label.ToString(),
                fileSystemName.ToString(),
                (DriveType)GetDriveType(volumeRoot.ToString()),
                totalBytes);
        }

        private static Win32Exception NewWin32Exception(string message)
        {
            var error = Marshal.GetLastWin32Error();
            return new Win32Exception(error, message + " " + new Win32Exception(error).Message);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetVolumePathName(
            string fileName,
            StringBuilder volumePathName,
            uint bufferLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetVolumeInformation(
            string rootPathName,
            StringBuilder volumeNameBuffer,
            uint volumeNameSize,
            out uint volumeSerialNumber,
            out uint maximumComponentLength,
            out uint fileSystemFlags,
            StringBuilder fileSystemNameBuffer,
            uint fileSystemNameSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetDiskFreeSpaceEx(
            string directoryName,
            out ulong freeBytesAvailable,
            out ulong totalNumberOfBytes,
            out ulong totalNumberOfFreeBytes);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetDriveType(string rootPathName);
    }
}
