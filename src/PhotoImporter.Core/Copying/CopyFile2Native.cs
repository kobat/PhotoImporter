using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace PhotoImporter.Core.Copying
{
    internal interface ICopyFileOperation
    {
        void Copy(
            string sourcePath,
            string destinationPath,
            CancellationToken cancellationToken,
            CopyPauseController pauseController,
            Action<long> progress);
    }

    internal sealed class CopyFile2Native : ICopyFileOperation
    {
        private const uint CopyFileFailIfExists = 0x00000001;
        private const uint CopyFileResumeFromPause = 0x00004000;
        private const int ErrorRequestPaused = 3050;
        private static readonly int HResultRequestPaused =
            unchecked((int)(0x80070000u | ErrorRequestPaused));
        private const int CallbackChunkFinished = 2;
        private const int CallbackStreamFinished = 4;

        public void Copy(
            string sourcePath,
            string destinationPath,
            CancellationToken cancellationToken,
            CopyPauseController pauseController,
            Action<long> progress)
        {
            var context = new CallbackContext(cancellationToken, pauseController, progress);
            var contextHandle = GCHandle.Alloc(context);
            var cancelPointer = Marshal.AllocHGlobal(sizeof(int));
            Marshal.WriteInt32(cancelPointer, 0);
            var callback = new ProgressRoutine(ProgressCallback);

            try
            {
                using (cancellationToken.Register(() => Marshal.WriteInt32(cancelPointer, 1)))
                {
                    var parameters = new CopyFile2ExtendedParameters
                    {
                        Size = (uint)Marshal.SizeOf(typeof(CopyFile2ExtendedParameters)),
                        CopyFlags = CopyFileFailIfExists,
                        Cancel = cancelPointer,
                        ProgressRoutine = callback,
                        CallbackContext = GCHandle.ToIntPtr(contextHandle)
                    };

                    var resumeFromPause = false;
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        parameters.CopyFlags = CopyFileFailIfExists |
                            (resumeFromPause ? CopyFileResumeFromPause : 0u);

                        var hresult = CopyFile2(sourcePath, destinationPath, ref parameters);
                        GC.KeepAlive(callback);
                        if (context.FlushError != 0)
                            throw new Win32Exception(context.FlushError, "FlushFileBuffers failed.");
                        if (context.CallbackError != 0)
                            throw new InvalidOperationException("The CopyFile2 progress callback failed.");

                        if (hresult == HResultRequestPaused && pauseController != null)
                        {
                            pauseController.WaitAfterNativePause(cancellationToken);
                            resumeFromPause = true;
                            continue;
                        }

                        if (hresult < 0)
                            Marshal.ThrowExceptionForHR(hresult);
                        break;
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(cancelPointer);
                contextHandle.Free();
            }
        }

        private static CopyFile2MessageAction ProgressCallback(IntPtr message, IntPtr callbackContext)
        {
            CallbackContext context;
            try
            {
                context = (CallbackContext)GCHandle.FromIntPtr(callbackContext).Target;
            }
            catch
            {
                return CopyFile2MessageAction.Cancel;
            }

            try
            {
                var type = Marshal.ReadInt32(message);
                var info = IntPtr.Add(message, 8);
                if (type == CallbackChunkFinished)
                {
                    var chunk = (ChunkFinished)Marshal.PtrToStructure(info, typeof(ChunkFinished));
                    context.ReportProgress((long)Math.Min(chunk.TotalBytesTransferred, (ulong)long.MaxValue));
                }
                else if (type == CallbackStreamFinished)
                {
                    var stream = (StreamFinished)Marshal.PtrToStructure(info, typeof(StreamFinished));
                    if (!FlushFileBuffers(stream.DestinationFile))
                    {
                        var error = Marshal.GetLastWin32Error();
                        Interlocked.CompareExchange(ref context.FlushError, error == 0 ? 31 : error, 0);
                        return CopyFile2MessageAction.Cancel;
                    }
                }

                if (context.CancellationToken.IsCancellationRequested)
                    return CopyFile2MessageAction.Cancel;
                if (context.PauseController != null && context.PauseController.ShouldPauseCurrentFile())
                    return CopyFile2MessageAction.Pause;
                return CopyFile2MessageAction.Continue;
            }
            catch
            {
                Interlocked.CompareExchange(ref context.CallbackError, 1, 0);
                return CopyFile2MessageAction.Cancel;
            }
        }

        private sealed class CallbackContext
        {
            internal CallbackContext(
                CancellationToken cancellationToken,
                CopyPauseController pauseController,
                Action<long> progress)
            {
                CancellationToken = cancellationToken;
                PauseController = pauseController;
                Progress = progress;
            }

            internal readonly CancellationToken CancellationToken;
            internal readonly CopyPauseController PauseController;
            internal readonly Action<long> Progress;
            internal int FlushError;
            internal int CallbackError;

            internal void ReportProgress(long value)
            {
                if (Progress != null) Progress(value);
            }
        }

        private enum CopyFile2MessageAction
        {
            Continue = 0,
            Cancel = 1,
            Pause = 4
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate CopyFile2MessageAction ProgressRoutine(IntPtr message, IntPtr callbackContext);

        [StructLayout(LayoutKind.Sequential)]
        private struct CopyFile2ExtendedParameters
        {
            internal uint Size;
            internal uint CopyFlags;
            internal IntPtr Cancel;
            [MarshalAs(UnmanagedType.FunctionPtr)]
            internal ProgressRoutine ProgressRoutine;
            internal IntPtr CallbackContext;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ChunkFinished
        {
            internal uint StreamNumber;
            internal uint Flags;
            internal IntPtr SourceFile;
            internal IntPtr DestinationFile;
            internal ulong ChunkNumber;
            internal ulong ChunkSize;
            internal ulong StreamSize;
            internal ulong StreamBytesTransferred;
            internal ulong TotalFileSize;
            internal ulong TotalBytesTransferred;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StreamFinished
        {
            internal uint StreamNumber;
            internal uint Reserved;
            internal IntPtr SourceFile;
            internal IntPtr DestinationFile;
            internal ulong StreamSize;
            internal ulong StreamBytesTransferred;
            internal ulong TotalFileSize;
            internal ulong TotalBytesTransferred;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int CopyFile2(
            string existingFileName,
            string newFileName,
            ref CopyFile2ExtendedParameters extendedParameters);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FlushFileBuffers(IntPtr file);
    }
}
