using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PhotoImporter.Core.Metadata
{
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
            DriveType = driveType;
            TotalBytes = totalBytes;
        }

        public string RootPath { get; }
        public uint SerialNumber { get; }
        public string SerialNumberHex => SerialNumber.ToString("X8");
        public string Label { get; }
        public string FileSystemName { get; }
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
