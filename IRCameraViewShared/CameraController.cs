using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Devices;
using Windows.Storage;
using Windows.Storage.Streams;

namespace IRCameraView
{
	public enum IRFrameFilter
	{
		None,
		Raw,
		Illuminated
	}

	public enum IRMappingMode
	{
		None,
		Green
	}

	public sealed class CameraController : IDisposable
	{
		private MediaFrameReader? _mediaFrameReader;
		private MediaCapture? _mediaCapture;
		private MediaFrameSource? _frameSource;
		private SoftwareBitmap? _latestBitmap;
		private readonly object _bitmapLock = new();
		private bool _disposed;
		private int _consecutiveFilteredFrames;
		private bool _filterFallbackActive;

		public MediaFrameReader? MediaFrameReader => _mediaFrameReader;
		public MediaCapture? MediaCapture => _mediaCapture;
		public MediaFrameSource? FrameSource => _frameSource;
		public MediaFrameSourceGroup? SourceGroup { get; private set; }
		public List<MediaFrameSourceGroup> Devices { get; private set; } = new();
		public MediaFrameSourceKind ActiveKind { get; private set; } = MediaFrameSourceKind.Infrared;

		public IRFrameFilter FrameFilter { get; set; } = IRFrameFilter.None;
		public IRMappingMode MappingMode { get; set; } = IRMappingMode.None;

		/// <summary>
		/// A copy of the most recent frame, owned by the controller. Callers must
		/// copy it (see <see cref="GetLatestBitmapCopy"/>) before using it across
		/// frames; this property is replaced by the frame thread.
		/// </summary>
		public SoftwareBitmap? LatestBitmap
		{
			get
			{
				lock (_bitmapLock)
					return _latestBitmap;
			}
		}

		public VideoDeviceController? Controller => _mediaCapture?.VideoDeviceController;

		public event Action<SoftwareBitmap>? OnFrameReady;
		public event Action<string>? OnError;

		public async Task<List<MediaFrameSourceGroup>> LoadCamerasAsync(MediaFrameSourceKind kind)
		{
			ThrowIfDisposed();
			var groups = await MediaFrameSourceGroup.FindAllAsync();

			// Keep the OS enumeration order: for a Windows Hello module the
			// built-in camera group is listed before virtual cameras such as
			// DroidCam, so the built-in one becomes the default selection.
			Devices = groups
				.Where(g => g.SourceInfos.Any(si => si.SourceKind == kind))
				.GroupBy(g => g.Id)
				.Select(g => g.First())
				.ToList();

			return Devices;
		}

		public IReadOnlyList<string> GetDeviceNames()
		{
			return Devices.Select(d => d.DisplayName).ToList();
		}

		/// <summary>
		/// Opens the requested frame source kind. Combined groups (for example a
		/// Windows Hello module exposing Color + Infrared) contain multiple
		/// sources; selecting by kind is essential.
		/// </summary>
		public async Task SelectDeviceAsync(MediaFrameSourceGroup sourceGroup, MediaFrameSourceKind kind, bool exclusive = true)
		{
			ThrowIfDisposed();

			if (kind == MediaFrameSourceKind.Depth)
				throw new NotSupportedException("Depth preview is not implemented yet. Please choose Infrared or Color.");

			await StopAsync();

			SourceGroup = sourceGroup;
			ActiveKind = kind;

			var capture = new MediaCapture();
			var settings = new MediaCaptureInitializationSettings
			{
				SourceGroup = sourceGroup,
				SharingMode = exclusive ? MediaCaptureSharingMode.ExclusiveControl : MediaCaptureSharingMode.SharedReadOnly,
				StreamingCaptureMode = StreamingCaptureMode.Video,
				MemoryPreference = MediaCaptureMemoryPreference.Cpu,
			};

			try
			{
				try
				{
					await capture.InitializeAsync(settings);
				}
				catch (Exception) when (exclusive)
				{
					// Windows Hello, Camera, Teams, etc. may already hold the
					// device. Shared read-only still gives us frames.
					capture.Dispose();
					capture = new MediaCapture();
					settings.SharingMode = MediaCaptureSharingMode.SharedReadOnly;
					await capture.InitializeAsync(settings);
				}
			}
			catch
			{
				capture.Dispose();
				throw;
			}

			try
			{
				var frameSource = capture.FrameSources.Values
					.Where(s => s.Info.SourceKind == kind)
					.OrderBy(s => s.Info.MediaStreamType == MediaStreamType.VideoPreview ? 0 : 1)
					.FirstOrDefault();

				if (frameSource == null)
					throw new InvalidOperationException($"This camera does not expose a {kind} source.");

				var format = ChooseFormat(frameSource, kind);
				if (format != null)
				{
					try
					{
						await frameSource.SetFormatAsync(format);
					}
					catch (Exception ex)
					{
						Debug.WriteLine($"Could not set format: {ex.Message}");
					}
				}

				var reader = await capture.CreateFrameReaderAsync(frameSource);
				reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
				reader.FrameArrived += FrameArrived;
				await reader.StartAsync();

				_mediaCapture = capture;
				_frameSource = frameSource;
				_mediaFrameReader = reader;
			}
			catch
			{
				capture.Dispose();
				throw;
			}
		}

		public Task SelectDeviceByIndexAsync(int index, MediaFrameSourceKind kind)
		{
			if (index < 0 || index >= Devices.Count)
				throw new ArgumentOutOfRangeException(nameof(index), "Invalid device index.");

			return SelectDeviceAsync(Devices[index], kind);
		}

		public async Task StopAsync()
		{
			var reader = Interlocked.Exchange(ref _mediaFrameReader, null);
			if (reader != null)
			{
				reader.FrameArrived -= FrameArrived;
				try { await reader.StopAsync(); }
				catch (Exception ex) { Debug.WriteLine($"Stopping frame reader failed: {ex.Message}"); }
				reader.Dispose();
			}

			var capture = Interlocked.Exchange(ref _mediaCapture, null);
			capture?.Dispose();
			_frameSource = null;
		}

		public SoftwareBitmap? GetLatestBitmapCopy()
		{
			lock (_bitmapLock)
				return _latestBitmap == null ? null : SoftwareBitmap.Copy(_latestBitmap);
		}

		public async Task<string> CapturePhotoAsync()
		{
			var bitmap = GetLatestBitmapCopy()
				?? throw new InvalidOperationException("No camera frame is available yet.");

			try
			{
				var folder = await GetPicturesFolderAsync();
				var file = await folder.CreateFileAsync(
					$"Infrared_{DateTime.Now:yyyyMMdd_HHmmss}.jpg",
					CreationCollisionOption.GenerateUniqueName);

				using (IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite))
				{
					var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);
					encoder.SetSoftwareBitmap(bitmap);

					var propertySet = new BitmapPropertySet();
					propertySet.Add("ImageQuality", new BitmapTypedValue(0.92f, PropertyType.Single));
					await encoder.FlushAsync();
				}

				return file.Path;
			}
			finally
			{
				bitmap.Dispose();
			}
		}

		public static SoftwareBitmap ConvertToGreenOnly(SoftwareBitmap inputBitmap)
		{
			if (inputBitmap == null)
				throw new ArgumentNullException(nameof(inputBitmap));

			var bitmap = inputBitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8
				? SoftwareBitmap.Copy(inputBitmap)
				: SoftwareBitmap.Convert(inputBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

			int width = bitmap.PixelWidth;
			int height = bitmap.PixelHeight;

			byte[] pixels = new byte[width * height * 4];
			bitmap.CopyToBuffer(pixels.AsBuffer());

			for (int i = 0; i < pixels.Length; i += 4)
			{
				byte g = pixels[i + 1];
				byte a = pixels[i + 3];

				pixels[i + 0] = 0;
				pixels[i + 1] = g;
				pixels[i + 2] = 0;
				pixels[i + 3] = a;
			}

			var result = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
			result.CopyFromBuffer(pixels.AsBuffer());

			bitmap.Dispose();
			return result;
		}

		private void FrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
		{
			if (_disposed)
				return;

			SoftwareBitmap? outgoing = null;
			try
			{
				using var frameReference = sender.TryAcquireLatestFrame();
				var videoFrame = frameReference?.VideoMediaFrame;
				var softwareBitmap = videoFrame?.SoftwareBitmap;
				if (softwareBitmap == null)
					return;

				outgoing = ConvertToBgra8(softwareBitmap);
				if (outgoing == null)
					return;

				if (MappingMode == IRMappingMode.Green)
				{
					var green = ConvertToGreenOnly(outgoing);
					outgoing.Dispose();
					outgoing = green;
				}

				bool? isIlluminated = null;
				try
				{
					isIlluminated = videoFrame!.InfraredMediaFrame?.IsIlluminated;
				}
				catch (Exception)
				{
					// Colour sources do not expose an InfraredMediaFrame.
				}

				if (!ShouldDeliverFrame(isIlluminated))
				{
					outgoing.Dispose();
					return;
				}

				lock (_bitmapLock)
				{
					_latestBitmap?.Dispose();
					_latestBitmap = SoftwareBitmap.Copy(outgoing);
				}

				var handler = OnFrameReady;
				if (handler == null)
				{
					outgoing.Dispose();
					return;
				}

				// Ownership is transferred to the subscriber. The UI disposes it
				// after SoftwareBitmapSource.SetBitmapAsync finishes.
				handler(outgoing);
				outgoing = null;
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"Frame processing failed: {ex}");
				OnError?.Invoke(ex.Message);
			}
			finally
			{
				outgoing?.Dispose();
			}
		}

		private bool ShouldDeliverFrame(bool? isIlluminated)
		{
			if (FrameFilter == IRFrameFilter.None || isIlluminated == null)
			{
				_consecutiveFilteredFrames = 0;
				_filterFallbackActive = false;
				return true;
			}

			var wantsIlluminated = FrameFilter == IRFrameFilter.Illuminated;
			if (isIlluminated.Value == wantsIlluminated)
			{
				_consecutiveFilteredFrames = 0;
				_filterFallbackActive = false;
				return true;
			}

			// Some cameras report a constant illumination flag. Never let the
			// preview freeze permanently, and do not degrade to a 2 fps preview
			// if the selected state never occurs: fall back to all frames until
			// a matching frame is seen again.
			if (_filterFallbackActive)
				return true;

			if (++_consecutiveFilteredFrames >= 8)
			{
				_consecutiveFilteredFrames = 0;
				_filterFallbackActive = true;
				return true;
			}

			return false;
		}

		private static SoftwareBitmap ConvertToBgra8(SoftwareBitmap bitmap)
		{
			if (bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8 &&
				bitmap.BitmapAlphaMode != BitmapAlphaMode.Straight)
			{
				return SoftwareBitmap.Copy(bitmap);
			}

			return SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
		}

		private static MediaFrameFormat? ChooseFormat(MediaFrameSource source, MediaFrameSourceKind kind)
		{
			var formats = source.SupportedFormats;
			if (formats == null || formats.Count == 0)
				return null;

			if (kind == MediaFrameSourceKind.Infrared)
			{
				return formats
					.OrderByDescending(f => f.FrameRate.Numerator / (double)Math.Max(1, f.FrameRate.Denominator))
					.First();
			}

			// Prefer a 1280x720-class preview: it keeps the UI responsive and
			// avoids converting full 1080p frames on every tick.
			return formats
				.OrderBy(f => f.VideoFormat.Width > 1280 || f.VideoFormat.Height > 720 ? 1 : 0)
				.ThenByDescending(f => f.FrameRate.Numerator / (double)Math.Max(1, f.FrameRate.Denominator))
				.ThenBy(f => (long)f.VideoFormat.Width * f.VideoFormat.Height)
				.First();
		}

		private static async Task<StorageFolder> GetPicturesFolderAsync()
		{
			try
			{
				return await KnownFolders.PicturesLibrary.CreateFolderAsync(
					"IRCameraView", CreationCollisionOption.OpenIfExists);
			}
			catch (Exception)
			{
				// Unpackaged debug builds (and machines where the library
				// capability is unavailable) can still use the real folder.
				var path = Path.Combine(
					Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
					"IRCameraView");
				Directory.CreateDirectory(path);
				return await StorageFolder.GetFolderFromPathAsync(path);
			}
		}

		private void ThrowIfDisposed()
		{
			if (_disposed)
				throw new ObjectDisposedException(nameof(CameraController));
		}

		public void Dispose()
		{
			if (_disposed)
				return;

			_disposed = true;

			var reader = _mediaFrameReader;
			if (reader != null)
			{
				reader.FrameArrived -= FrameArrived;
				try { reader.StopAsync().AsTask().GetAwaiter().GetResult(); }
				catch (Exception) { }
				reader.Dispose();
				_mediaFrameReader = null;
			}

			_mediaCapture?.Dispose();
			_mediaCapture = null;
			_frameSource = null;

			lock (_bitmapLock)
			{
				_latestBitmap?.Dispose();
				_latestBitmap = null;
			}
		}
	}
}
