using System;
using System.Threading;
using System.Threading.Tasks;
using PhotoImporter.Core.Copying;
using Xunit;

namespace PhotoImporter.Core.Tests
{
    public sealed class CopyPauseControllerTests
    {
        [Fact]
        public void KeepsPausePendingBeforeNativePauseDelay()
        {
            using (var controller = new CopyPauseController(TimeSpan.FromMinutes(1)))
            {
                Assert.True(controller.RequestPause());

                Assert.False(controller.ShouldPauseCurrentFile());
                Assert.Equal(CopyPauseState.PausePending, controller.State);
            }
        }

        [Fact]
        public void RequestsNativePauseAfterDelay()
        {
            using (var controller = new CopyPauseController(TimeSpan.Zero))
            {
                Assert.True(controller.RequestPause());

                Assert.True(controller.ShouldPauseCurrentFile());
                Assert.Equal(CopyPauseState.NativePauseRequested, controller.State);
            }
        }

        [Fact]
        public async Task WaitsAtFileBoundaryUntilResumed()
        {
            using (var controller = new CopyPauseController(TimeSpan.FromSeconds(3)))
            {
                Assert.True(controller.RequestPause());
                var wait = Task.Run(() => controller.WaitAtFileBoundary(CancellationToken.None));

                try
                {
                    Assert.True(SpinWait.SpinUntil(
                        () => controller.State == CopyPauseState.PausedBetweenFiles,
                        TimeSpan.FromSeconds(5)));
                    Assert.False(wait.IsCompleted);

                    Assert.True(controller.Resume());
                    Assert.Same(wait, await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(5))));
                    await wait;
                    Assert.Equal(CopyPauseState.Running, controller.State);
                }
                finally
                {
                    if (controller.IsPauseRequested) controller.Resume();
                }
            }
        }

        [Fact]
        public async Task WaitsAfterNativePauseUntilResumed()
        {
            using (var controller = new CopyPauseController(TimeSpan.Zero))
            {
                Assert.True(controller.RequestPause());
                Assert.True(controller.ShouldPauseCurrentFile());
                var wait = Task.Run(() => controller.WaitAfterNativePause(CancellationToken.None));

                try
                {
                    Assert.True(SpinWait.SpinUntil(
                        () => controller.State == CopyPauseState.PausedWithinFile,
                        TimeSpan.FromSeconds(5)));
                    Assert.False(wait.IsCompleted);

                    Assert.True(controller.Resume());
                    Assert.Same(wait, await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(5))));
                    await wait;
                }
                finally
                {
                    if (controller.IsPauseRequested) controller.Resume();
                }
            }
        }

        [Fact]
        public async Task CancellationReleasesPausedBoundaryWait()
        {
            using (var controller = new CopyPauseController(TimeSpan.FromSeconds(3)))
            using (var cancellation = new CancellationTokenSource())
            {
                Assert.True(controller.RequestPause());
                var wait = Task.Run(() => controller.WaitAtFileBoundary(cancellation.Token));

                Assert.True(SpinWait.SpinUntil(
                    () => controller.State == CopyPauseState.PausedBetweenFiles,
                    TimeSpan.FromSeconds(5)));
                cancellation.Cancel();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await wait);
            }
        }
    }
}
