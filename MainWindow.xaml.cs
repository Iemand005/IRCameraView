using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace IRCameraView
{
    /// <summary>
    /// The window that displays the camera feed.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private IRController _irController;
        //private bool _isRecording = false;

        public MainWindow()
        {
            InitializeComponent();

            StartCapture();
        }

        private void StartCapture()
        {
            imageElement.Source = new SoftwareBitmapSource();

            _irController = new IRController();
            _irController.OnFrameReady += IrController_OnFrameArrived;
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

        private void FrameFilter_SelectionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
        {
            if (sender is ComboBox && _irController != null)
            {
                var comboBox = sender as ComboBox;
                _irController.FrameFilter = (IRFrameFilter)comboBox.SelectedIndex;
            }
        }

        private async void TakePhoto_Click(object sender, RoutedEventArgs e)
        {
            //_irController.\

            StorageFile photoFile = await KnownFolders.PicturesLibrary.CreateFileAsync("IRPhoto.jpg", CreationCollisionOption.GenerateUniqueName);

            var encodingProperties = new ImageEncodingProperties
            {
                Subtype = "Y800"
            };

            using (var stream = await photoFile.OpenAsync(FileAccessMode.ReadWrite))
                await _irController.MediaCapture.CapturePhotoToStreamAsync(encodingProperties, stream);
        }

        static StorageFile videoFile;

        private async void TakeVideo_Click(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton)
            {
                ToggleButton toggleButton = sender as ToggleButton;
                if (toggleButton.IsChecked ?? true)
                {
                    videoFile = await KnownFolders.VideosLibrary.CreateFileAsync("IRRecording.mp4", CreationCollisionOption.GenerateUniqueName);
                    var encodingProfile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);

                    await _irController.MediaCapture.StartRecordToStorageFileAsync(encodingProfile, videoFile);
                }
                else
                {
                    await _irController.MediaCapture.StopRecordAsync();

                    ContentDialog successDialog = new ContentDialog()
                    {
                        Title = "Recording Saved",
                        Content = "Video saved to: " + videoFile.Path,
                        CloseButtonText = "OK"
                    };
                    await successDialog.ShowAsync();
                }
            }
        }
    }
}
