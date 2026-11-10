using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Devices;
using Windows.Media.MediaProperties;
using Windows.Storage;

#if NETFX_CORE
// UWP code
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;
#else
// WinUI 3 code
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media.Animation;
#endif

namespace IRCameraView
{
    public sealed partial class CameraPage : Page
    {
        private CameraController? camera;
        private StorageFile? videoFile;
        private SoftwareBitmap? _pendingFrame;
        private int _uiUpdateQueued;
        private bool _initialized;
        private bool _suppressDeviceSelection;
        private bool _isRecording;
        private MediaFrameSourceKind _selectedKind = MediaFrameSourceKind.Infrared;
        private DateTime _lastErrorAt = DateTime.MinValue;
        private DispatcherTimer? _recordingTimer;
        private DateTime _recordingStarted;

        public CameraPage()
        {
            InitializeComponent();
            Loaded += CameraPage_Loaded;
            Unloaded += CameraPage_Unloaded;
        }

        public Grid GetTitleBar()
        {
            return TitleBarGripper;
        }

        private async void CameraPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_initialized)
                return;

            _initialized = true;
            camera = new CameraController();
            camera.OnFrameReady += OnFrameReady;
            camera.OnError += OnCameraError;
            ImageElement.Source = new SoftwareBitmapSource();

            await InitializeCameraAsync(MediaFrameSourceKind.Infrared);
        }

        private void CameraPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _initialized = false;

            var controller = camera;
            camera = null;
            if (controller == null)
                return;

            controller.OnFrameReady -= OnFrameReady;
            controller.OnError -= OnCameraError;
            controller.Dispose();
            StopRecordingTimer();

            Interlocked.Exchange(ref _pendingFrame, null)?.Dispose();
        }

        private async Task InitializeCameraAsync(MediaFrameSourceKind kind)
        {
            if (camera == null)
                return;

            _selectedKind = kind;

            // Most Windows Hello IR cameras alternate an illuminated and a
            // dark frame. Showing both makes the preview flicker and look
            // black half the time, so default IR to the illuminated stream.
            FrameFilterBox.SelectedIndex = kind == MediaFrameSourceKind.Infrared ? 2 : 0;
            HideStatus();

            try
            {
                await camera.StopAsync();
                var devices = await camera.LoadCamerasAsync(kind);
                PopulateDeviceComboBox(devices);

                if (devices.Count == 0)
                {
                    TorchControlGrid.Visibility = Visibility.Collapsed;
                    PhotoModeGrid.Visibility = Visibility.Collapsed;
                    ShowStatus($"No {kind.ToString().ToLowerInvariant()} camera was found. " +
                               "Connect a camera or enable camera access in Windows Settings > Privacy & security > Camera.");
                    return;
                }

                await SelectDeviceAsync(0);
            }
            catch (Exception ex)
            {
                ShowStatus($"Could not start the camera: {ex.Message}");
            }
        }

        private void PopulateDeviceComboBox(System.Collections.Generic.IReadOnlyList<MediaFrameSourceGroup> devices)
        {
            _suppressDeviceSelection = true;
            try
            {
                DeviceComboBox.Items.Clear();
                foreach (var device in devices)
                    DeviceComboBox.Items.Add(device.DisplayName);

                DeviceComboBox.SelectedIndex = devices.Count > 0 ? 0 : -1;
            }
            finally
            {
                _suppressDeviceSelection = false;
            }
        }

        private async Task SelectDeviceAsync(int index)
        {
            if (camera == null || index < 0 || index >= camera.Devices.Count)
                return;

            try
            {
                await camera.SelectDeviceAsync(camera.Devices[index], _selectedKind);
                UpdateCameraControls();
                HideStatus();
            }
            catch (Exception ex)
            {
                ShowStatus($"Could not open '{camera.Devices[index].DisplayName}': {ex.Message}");
            }
        }

        private void UpdateCameraControls()
        {
            TorchControlGrid.Visibility = Visibility.Collapsed;
            PhotoModeGrid.Visibility = Visibility.Collapsed;

            var controller = camera?.Controller;
            if (controller == null)
                return;

            try
            {
                var torchControl = controller.InfraredTorchControl;
                if (torchControl.IsSupported && torchControl.SupportedModes.Count > 0)
                {
                    IRTorchBox.ItemsSource = torchControl.SupportedModes;
                    IRTorchBox.SelectedIndex = 0;
                    TorchControlGrid.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                // Many cameras (including the USB2.0 IR UVC WebCam) throw
                // "The request is not supported" here. That is not fatal.
                Debug.WriteLine($"IR torch is not supported: {ex.Message}");
            }

            try
            {
                var photoControl = controller.AdvancedPhotoControl;
                if (photoControl.Supported)
                {
                    PhotoModeBox.Items.Clear();
                    foreach (var mode in photoControl.SupportedModes)
                        PhotoModeBox.Items.Add(mode);

                    if (PhotoModeBox.Items.Count > 0)
                        PhotoModeBox.SelectedIndex = 0;

                    PhotoModeGrid.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Advanced photo mode is not supported: {ex.Message}");
            }
        }

        private void ShowStatus(string message)
        {
            StatusText.Text = message;
            StatusBanner.Visibility = Visibility.Visible;
        }

        private void HideStatus()
        {
            StatusBanner.Visibility = Visibility.Collapsed;
        }

        private void OnCameraError(string message)
        {
            // The frame thread can report the same conversion failure many
            // times per second; only surface it occasionally.
            if ((DateTime.UtcNow - _lastErrorAt).TotalSeconds < 5)
                return;

            _lastErrorAt = DateTime.UtcNow;
            _ = InvokeOnMainThread(() =>
            {
                ShowStatus($"Camera error: {message}");
                return Task.CompletedTask;
            });
        }

        private void OnFrameReady(SoftwareBitmap bitmap)
        {
            // Coalesce frames: keep only the newest one while the UI is busy.
            // Ownership of the incoming bitmap moves into _pendingFrame.
            var previous = Interlocked.Exchange(ref _pendingFrame, bitmap);
            previous?.Dispose();

            if (Interlocked.Exchange(ref _uiUpdateQueued, 1) == 0)
                _ = InvokeOnMainThread(ProcessPendingFrameAsync);
        }

        private async Task ProcessPendingFrameAsync()
        {
            try
            {
                var bitmap = Interlocked.Exchange(ref _pendingFrame, null);
                if (bitmap != null)
                {
                    try
                    {
                        if (ImageElement.Source is SoftwareBitmapSource source)
                            await source.SetBitmapAsync(bitmap);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Displaying frame failed: {ex.Message}");
                    }
                    finally
                    {
                        bitmap.Dispose();
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _uiUpdateQueued, 0);

                if (Volatile.Read(ref _pendingFrame) != null &&
                    Interlocked.Exchange(ref _uiUpdateQueued, 1) == 0)
                {
                    _ = InvokeOnMainThread(ProcessPendingFrameAsync);
                }
            }
        }

        private Task InvokeOnMainThread(Func<Task> callback)
        {
            var tcs = new TaskCompletionSource<object?>();

#if NETFX_CORE
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                try
                {
                    await callback();
                    tcs.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
#else
            if (!DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await callback();
                    tcs.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }))
            {
                tcs.TrySetCanceled();
            }
#endif

            return tcs.Task;
        }

        private async void DeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressDeviceSelection || camera == null)
                return;

            await SelectDeviceAsync(DeviceComboBox.SelectedIndex);
        }

        private async void CameraType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized || camera == null)
                return;

            var kind = GetSelectedCameraKind();
            if (kind == _selectedKind)
                return;

            await InitializeCameraAsync(kind);
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            if (camera == null)
                return;

            await InitializeCameraAsync(_selectedKind);
        }

        private void FrameFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox comboBox && camera != null && comboBox.SelectedIndex >= 0)
                camera.FrameFilter = (IRFrameFilter)Math.Min(comboBox.SelectedIndex, 2);
        }

        private void ComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox comboBox && camera != null)
                camera.MappingMode = comboBox.SelectedIndex == 1 ? IRMappingMode.Green : IRMappingMode.None;
        }

        private void PreviewModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PreviewModeBox.SelectedIndex == 1)
                EnableMargin();
            else if (PreviewModeBox.SelectedIndex == 0)
                DisableMargin();
        }

        private void PhotoModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (camera?.Controller == null || PhotoModeBox.SelectedItem is not AdvancedPhotoMode mode)
                return;

            try
            {
                camera.Controller.AdvancedPhotoControl.Configure(new AdvancedPhotoCaptureSettings
                {
                    Mode = mode
                });
            }
            catch (Exception ex)
            {
                ShowStatus($"Could not change photo mode: {ex.Message}");
            }
        }

        private void IRTorchBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (camera?.Controller == null || IRTorchBox.SelectedItem is not InfraredTorchMode mode)
                return;

            try
            {
                camera.Controller.InfraredTorchControl.CurrentMode = mode;
            }
            catch (Exception ex)
            {
                ShowStatus($"Could not set the IR torch: {ex.Message}");
            }
        }

        private async void TakePhoto_Click(object sender, RoutedEventArgs e)
        {
            if (camera == null)
                return;

            try
            {
                var path = await camera.CapturePhotoAsync();
                await FlashScreenAsync();
                ShowStatus($"Photo saved to {path}");
            }
            catch (Exception ex)
            {
                ShowStatus($"Photo capture failed: {ex.Message}");
            }
        }

        private async void TakeVideo_Click(object sender, RoutedEventArgs e)
        {
            if (camera == null || sender is not ToggleButton toggleButton)
                return;

            try
            {
                if (toggleButton.IsChecked == true)
                {
                    if (_isRecording)
                        return;

                    var folder = await GetVideosFolderAsync();
                    videoFile = await folder.CreateFileAsync(
                        $"IRRecording_{DateTime.Now:yyyyMMdd_HHmmss}.mp4",
                        CreationCollisionOption.GenerateUniqueName);

                    var encodingProfile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
                    var profiles = camera.Controller?
                        .GetAvailableMediaStreamProperties(MediaStreamType.VideoRecord)?
                        .OfType<VideoEncodingProperties>()
                        .ToList();

                    if (profiles == null || profiles.Count == 0)
                        throw new InvalidOperationException("This camera does not expose a video recording profile.");

                    var first = profiles[0];
                    encodingProfile.Video.Width = first.Width;
                    encodingProfile.Video.Height = first.Height;
                    encodingProfile.Video.FrameRate.Numerator = first.FrameRate.Numerator;
                    encodingProfile.Video.FrameRate.Denominator = first.FrameRate.Denominator;
                    encodingProfile.Audio = null;

                    await camera.MediaCapture!.StartRecordToStorageFileAsync(encodingProfile, videoFile);
                    _isRecording = true;
                    StartRecordingTimer();
                    ShowStatus("Recording…");
                }
                else
                {
                    if (!_isRecording)
                        return;

                    await camera.MediaCapture!.StopRecordAsync();
                    _isRecording = false;
                    StopRecordingTimer();
                    ShowStatus($"Recording saved to {videoFile?.Path}");
                }
            }
            catch (Exception ex)
            {
                _isRecording = false;
                StopRecordingTimer();
                toggleButton.IsChecked = false;
                ShowStatus($"Video recording failed: {ex.Message}");
            }
        }

        private void StartRecordingTimer()
        {
            StopRecordingTimer();
            _recordingStarted = DateTime.Now;
            VideoTimeLabel.Text = "00:00";

            _recordingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _recordingTimer.Tick += RecordingTimer_Tick;
            _recordingTimer.Start();
        }

        private void RecordingTimer_Tick(object? sender, object e)
        {
            var elapsed = DateTime.Now - _recordingStarted;
            VideoTimeLabel.Text = elapsed.TotalHours >= 1
                ? elapsed.ToString(@"hh\:mm\:ss")
                : elapsed.ToString(@"mm\:ss");
        }

        private void StopRecordingTimer()
        {
            if (_recordingTimer == null)
                return;

            _recordingTimer.Stop();
            _recordingTimer.Tick -= RecordingTimer_Tick;
            _recordingTimer = null;
        }

        private async Task FlashScreenAsync()
        {
            FlashOverlay.Opacity = 1;

            await Task.Delay(100);

            var fadeDuration = TimeSpan.FromMilliseconds(200);
            var animation = new DoubleAnimation
            {
                From = 1,
                To = 0,
                Duration = new Duration(fadeDuration),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);

            Storyboard.SetTarget(animation, FlashOverlay);
            Storyboard.SetTargetProperty(animation, "Opacity");

            storyboard.Begin();

            await Task.Delay(fadeDuration);
        }

        private MediaFrameSourceKind GetSelectedCameraKind()
        {
            switch (CameraType.SelectedIndex)
            {
                default:
                case 0: return MediaFrameSourceKind.Infrared;
                case 1: return MediaFrameSourceKind.Color;
                case 2: return MediaFrameSourceKind.Depth;
            }
        }

        private static async Task<StorageFolder> GetVideosFolderAsync()
        {
            try
            {
                return await KnownFolders.VideosLibrary.CreateFolderAsync(
                    "IRCameraView", CreationCollisionOption.OpenIfExists);
            }
            catch (Exception)
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                    "IRCameraView");
                Directory.CreateDirectory(path);
                return await StorageFolder.GetFolderFromPathAsync(path);
            }
        }

        private void EnableMargin()
        {
            var margin = new Thickness(20);
            margin.Top += 32;
            margin.Right *= 2;
            margin.Right += PhotoButton.Width;
            ImageGrid.Margin = margin;

            ImageViewbox.Stretch = Stretch.Uniform;
#if NETFX_CORE
            ImageBorder.CornerRadius = new CornerRadius(0);
#else
            ImageBorder.CornerRadius = new CornerRadius(6);
#endif
        }

        private void DisableMargin()
        {
            ImageGrid.Margin = new Thickness(0);
            ImageBorder.CornerRadius = new CornerRadius(0);

            ImageViewbox.Stretch = Stretch.UniformToFill;
        }
    }
}
