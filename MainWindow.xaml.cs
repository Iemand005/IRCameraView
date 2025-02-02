using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Devices;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.System;
using Windows.UI.Core;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace IRCameraView
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private SoftwareBitmap _backBuffer;
        private bool _taskRunning = false;

        public MainWindow()
        {
            InitializeComponent();

            StartCapture();
        }

        private void StartCapture()
        {
            imageElement.Source = new SoftwareBitmapSource();

            IRController irController = new IRController();
            //irController.MediaFrameReader.FrameArrived += MediaFrameReader_FrameArrived;
            irController.OnFrameReady += IrController_OnFrameArrived;
        }

        private void IrController_OnFrameArrived(SoftwareBitmap bitmap)
        {
            if (imageElement.DispatcherQueue != null) imageElement.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    var imageSource = (SoftwareBitmapSource)imageElement.Source;
                    await imageSource.SetBitmapAsync(bitmap);
                    bitmap.Dispose(); // Important to dispose of.
                }
                catch { }
            });
        }

        private void MediaFrameReader_FrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
        {
            using (var frameReference = sender.TryAcquireLatestFrame())
            {
                var videoMediaFrame = frameReference?.VideoMediaFrame;
                var softwareBitmap = videoMediaFrame?.SoftwareBitmap;

                if (softwareBitmap == null) return;
                if (softwareBitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 || softwareBitmap.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
                    softwareBitmap = SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

                softwareBitmap = Interlocked.Exchange(ref _backBuffer, softwareBitmap);
                softwareBitmap?.Dispose();
                if (imageElement.DispatcherQueue != null) imageElement.DispatcherQueue.TryEnqueue(async () =>
                {
                    // Don't let two copies of this task run at the same time.
                    if (_taskRunning) return;
                    _taskRunning = true;

                    // Keep draining frames from the backbuffer until the backbuffer is empty.
                    SoftwareBitmap latestBitmap;
                    while ((latestBitmap = Interlocked.Exchange(ref _backBuffer, null)) != null)
                    {
                        var imageSource = (SoftwareBitmapSource)imageElement.Source;
                        await imageSource.SetBitmapAsync(latestBitmap);
                        latestBitmap.Dispose();
                    }

                    _taskRunning = false;
                });
            }
        }
    }
}
