using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PhotoImporter.App
{
    internal sealed class SystemMenuAboutCommand : IDisposable
    {
        private const int WmSysCommand = 0x0112;
        private const int SystemCommandMask = 0xFFF0;
        private const uint ScClose = 0xF060;
        private const uint AboutCommandId = 0x1A10;
        private const uint MfByCommand = 0x00000000;
        private const uint MfString = 0x00000000;
        private const uint MfSeparator = 0x00000800;

        private readonly Window _window;
        private readonly string _label;
        private readonly Action _execute;
        private HwndSource _source;
        private bool _disposed;

        public SystemMenuAboutCommand(Window window, string label, Action execute)
        {
            _window = window ?? throw new ArgumentNullException(nameof(window));
            _label = string.IsNullOrWhiteSpace(label)
                ? throw new ArgumentException("A menu label is required.", nameof(label))
                : label;
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));

            _window.SourceInitialized += Window_SourceInitialized;
            _window.Closed += Window_Closed;
        }

        private void Window_SourceInitialized(object sender, EventArgs e)
        {
            if (_disposed || _source != null) return;

            var handle = new WindowInteropHelper(_window).Handle;
            if (handle == IntPtr.Zero) return;

            var menu = GetSystemMenu(handle, false);
            if (menu == IntPtr.Zero) return;

            if (!InsertMenu(
                    menu,
                    ScClose,
                    MfByCommand | MfString,
                    new UIntPtr(AboutCommandId),
                    _label)) return;

            InsertMenu(
                menu,
                ScClose,
                MfByCommand | MfSeparator,
                UIntPtr.Zero,
                null);
            DrawMenuBar(handle);

            _source = HwndSource.FromHwnd(handle);
            _source?.AddHook(WndProc);
        }

        private IntPtr WndProc(
            IntPtr hwnd,
            int message,
            IntPtr wParam,
            IntPtr lParam,
            ref bool handled)
        {
            if (message == WmSysCommand &&
                (wParam.ToInt64() & SystemCommandMask) == AboutCommandId)
            {
                _execute();
                handled = true;
            }

            return IntPtr.Zero;
        }

        private void Window_Closed(object sender, EventArgs e) => Dispose();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _window.SourceInitialized -= Window_SourceInitialized;
            _window.Closed -= Window_Closed;
            if (_source != null)
            {
                _source.RemoveHook(WndProc);
                _source = null;
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetSystemMenu(IntPtr windowHandle, bool revert);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InsertMenu(
            IntPtr menu,
            uint position,
            uint flags,
            UIntPtr newItemId,
            string newItem);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DrawMenuBar(IntPtr windowHandle);
    }
}
