using AVFoundation;
using CoreFoundation;
using CoreMedia;
using Foundation;
using Microsoft.Maui.Handlers;
using PhoneCamera.Controls;
using UIKit;

namespace PhoneCamera.Platforms.iOS;

public class CameraPreviewHandler : ViewHandler<CameraPreviewView, CameraPreviewUIView>
{
    public static PropertyMapper<CameraPreviewView, CameraPreviewHandler> Mapper =
        new PropertyMapper<CameraPreviewView, CameraPreviewHandler>(ViewMapper)
        {
            [nameof(CameraPreviewView.IsRunning)] = MapIsRunning,
            [nameof(CameraPreviewView.SelectedConfig)] = MapSelectedConfig,
        };

    public CameraPreviewHandler() : base(Mapper) { }

    protected override CameraPreviewUIView CreatePlatformView() => new CameraPreviewUIView();

    protected override void ConnectHandler(CameraPreviewUIView platformView)
    {
        base.ConnectHandler(platformView);
        System.Diagnostics.Debug.WriteLine("[PCam][iOS] ConnectHandler — handler attached");
        Task.Run(QueryConfigs);
        if (VirtualView.IsRunning)
            platformView.StartCamera(VirtualView.SelectedConfig);
    }

    protected override void DisconnectHandler(CameraPreviewUIView platformView)
    {
        System.Diagnostics.Debug.WriteLine("[PCam][iOS] DisconnectHandler — handler detached");
        platformView.StopCamera();
        base.DisconnectHandler(platformView);
    }

    private void QueryConfigs()
    {
        System.Diagnostics.Debug.WriteLine("[PCam][iOS] QueryConfigs starting...");
        try
        {
            var device = AVCaptureDevice.GetDefaultDevice(AVMediaTypes.Video);
            if (device == null)
            {
                System.Diagnostics.Debug.WriteLine("[PCam][iOS] QueryConfigs ERROR: no camera device");
                return;
            }

            var configs = new List<CameraConfig>();
            var seen = new HashSet<(int, int, double, double)>();

            foreach (var format in device.Formats)
            {
                var vdesc = format.FormatDescription as CMVideoFormatDescription;
                if (vdesc == null) continue;

                var dims = vdesc.Dimensions;

                foreach (var range in format.VideoSupportedFrameRateRanges)
                {
                    double minFps = Math.Round(range.MinFrameRate, 1);
                    double maxFps = Math.Round(range.MaxFrameRate, 1);
                    if (seen.Add((dims.Width, dims.Height, minFps, maxFps)))
                        configs.Add(new CameraConfig(dims.Width, dims.Height, minFps, maxFps));
                }
            }

            var sorted = configs
                .OrderByDescending(c => (long)c.Width * c.Height)
                .ThenByDescending(c => c.MaxFps)
                .ToList();

            var defaultConfig = CameraConfig.FindDefault(sorted);

            System.Diagnostics.Debug.WriteLine($"[PCam][iOS] QueryConfigs done: {sorted.Count} configs, default={defaultConfig?.DisplayName ?? "none"}");

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (VirtualView == null) return;
                VirtualView.SelectedConfig = defaultConfig;
                VirtualView.ConfigurationsReady?.Invoke(sorted);
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PCam][iOS] QueryConfigs ERROR: {ex.Message}");
        }
    }

    private static void MapIsRunning(CameraPreviewHandler handler, CameraPreviewView view)
    {
        System.Diagnostics.Debug.WriteLine($"[PCam][iOS] MapIsRunning → IsRunning={view.IsRunning}");
        if (view.IsRunning)
            handler.PlatformView.StartCamera(view.SelectedConfig);
        else
            handler.PlatformView.StopCamera();
    }

    private static void MapSelectedConfig(CameraPreviewHandler handler, CameraPreviewView view)
    {
        System.Diagnostics.Debug.WriteLine($"[PCam][iOS] MapSelectedConfig → config={view.SelectedConfig?.DisplayName ?? "none"}, running={view.IsRunning}");
        if (view.IsRunning)
            handler.PlatformView.StartCamera(view.SelectedConfig);
    }
}

public class CameraPreviewUIView : UIView
{
    private AVCaptureSession? captureSession;
    private AVCaptureVideoPreviewLayer? previewLayer;
    private AVCaptureVideoDataOutput? videoOutput;
    private SampleBufferDelegate? sampleDelegate;
    private readonly DispatchQueue cameraQueue = new DispatchQueue("phonecamera.camera");
    private readonly DispatchQueue outputQueue = new DispatchQueue("phonecamera.output");

    public void StartCamera(CameraConfig? config = null)
    {
        System.Diagnostics.Debug.WriteLine($"[PCam][iOS] StartCamera → config={config?.DisplayName ?? "none"}");
        cameraQueue.DispatchAsync(() =>
        {
            if (captureSession != null)
                TeardownSession();
            SetupSession(config);
            captureSession?.StartRunning();
            System.Diagnostics.Debug.WriteLine("[PCam][iOS] StartCamera ✓ session started");
        });
    }

    public void StopCamera()
    {
        System.Diagnostics.Debug.WriteLine("[PCam][iOS] StopCamera");
        cameraQueue.DispatchAsync(() => captureSession?.StopRunning());
    }

    private void TeardownSession()
    {
        System.Diagnostics.Debug.WriteLine("[PCam][iOS] TeardownSession");
        captureSession?.StopRunning();
        videoOutput?.Dispose();
        videoOutput = null;
        captureSession?.Dispose();
        captureSession = null;

        var layer = previewLayer;
        previewLayer = null;
        if (layer != null)
        {
            DispatchQueue.MainQueue.DispatchSync(() =>
            {
                layer.RemoveFromSuperLayer();
                layer.Dispose();
            });
        }
    }

    private void SetupSession(CameraConfig? config)
    {
        System.Diagnostics.Debug.WriteLine($"[PCam][iOS] SetupSession → config={config?.DisplayName ?? "none"}");
        captureSession = new AVCaptureSession();

        var device = AVCaptureDevice.GetDefaultDevice(AVMediaTypes.Video);
        if (device == null)
        {
            System.Diagnostics.Debug.WriteLine("[PCam][iOS] SetupSession ERROR: no camera found");
            return;
        }

        var input = new AVCaptureDeviceInput(device, out NSError? inputError);
        if (inputError != null)
        {
            System.Diagnostics.Debug.WriteLine($"[PCam][iOS] SetupSession ERROR input: {inputError.LocalizedDescription}");
            return;
        }

        captureSession.BeginConfiguration();

        if (captureSession.CanAddInput(input))
            captureSession.AddInput(input);

        if (config != null)
        {
            var (bestFormat, frameTime) = FindBestFormat(device, config);
            if (bestFormat != null)
            {
                System.Diagnostics.Debug.WriteLine($"[PCam][iOS] SetupSession ✓ format found, applying {config.Width}×{config.Height} @ {config.MaxFps:F0}fps");
                captureSession.SessionPreset = AVCaptureSession.PresetInputPriority;
                if (device.LockForConfiguration(out _))
                {
                    device.ActiveFormat = bestFormat;
                    device.ActiveVideoMinFrameDuration = frameTime;
                    device.ActiveVideoMaxFrameDuration = frameTime;
                    device.UnlockForConfiguration();
                    System.Diagnostics.Debug.WriteLine("[PCam][iOS] SetupSession ✓ device format locked");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[PCam][iOS] SetupSession WARN: LockForConfiguration failed");
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[PCam][iOS] SetupSession WARN: no matching format for {config.Width}×{config.Height} @ {config.MaxFps:F0}fps, using PresetHigh");
                captureSession.SessionPreset = AVCaptureSession.PresetHigh;
            }
        }
        else
        {
            captureSession.SessionPreset = AVCaptureSession.PresetHigh;
        }

        sampleDelegate ??= new SampleBufferDelegate();
        videoOutput = new AVCaptureVideoDataOutput();
        videoOutput.SetSampleBufferDelegate(sampleDelegate, outputQueue);
        if (captureSession.CanAddOutput(videoOutput))
            captureSession.AddOutput(videoOutput);

        captureSession.CommitConfiguration();

        bool isLandscape = config == null || config.Width >= config.Height;
        var session = captureSession;
        DispatchQueue.MainQueue.DispatchSync(() =>
        {
            previewLayer = new AVCaptureVideoPreviewLayer(session)
            {
                VideoGravity = AVLayerVideoGravity.ResizeAspectFill,
                Frame = Bounds
            };
            if (previewLayer.Connection != null)
            {
                previewLayer.Connection.VideoOrientation = isLandscape
                    ? AVCaptureVideoOrientation.LandscapeRight
                    : AVCaptureVideoOrientation.Portrait;
                System.Diagnostics.Debug.WriteLine($"[PCam][iOS] previewLayer orientation → {(isLandscape ? "LandscapeRight" : "Portrait")}");
            }
            Layer.AddSublayer(previewLayer);
            SetNeedsLayout();
        });
        System.Diagnostics.Debug.WriteLine("[PCam][iOS] SetupSession complete");
    }

    private static (AVCaptureDeviceFormat? format, CMTime frameTime) FindBestFormat(
        AVCaptureDevice device, CameraConfig config)
    {
        foreach (bool swapped in new[] { false, true })
        {
            int targetW = swapped ? config.Height : config.Width;
            int targetH = swapped ? config.Width : config.Height;

            foreach (var format in device.Formats)
            {
                var vdesc = format.FormatDescription as CMVideoFormatDescription;
                if (vdesc == null) continue;

                var dims = vdesc.Dimensions;
                if (dims.Width != targetW || dims.Height != targetH) continue;

                foreach (var range in format.VideoSupportedFrameRateRanges)
                {
                    if (Math.Abs(Math.Round(range.MaxFrameRate, 1) - config.MaxFps) < 0.5)
                    {
                        int fps = (int)Math.Round(config.MaxFps);
                        return (format, new CMTime(1, fps));
                    }
                }
            }
        }
        return (null, CMTime.Invalid);
    }

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        if (previewLayer != null)
            previewLayer.Frame = Bounds;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            captureSession?.StopRunning();
            videoOutput?.Dispose();
            videoOutput = null;
            captureSession?.Dispose();
            captureSession = null;
            previewLayer?.RemoveFromSuperLayer();
            previewLayer?.Dispose();
            previewLayer = null;
        }
        base.Dispose(disposing);
    }

    private sealed class SampleBufferDelegate : AVCaptureVideoDataOutputSampleBufferDelegate
    {
        private int _count;
        public override void DidOutputSampleBuffer(
            AVCaptureOutput captureOutput,
            CMSampleBuffer sampleBuffer,
            AVCaptureConnection connection)
        {
            if (++_count % 30 == 0)
                System.Diagnostics.Debug.WriteLine($"[PCam][iOS] Frame #{_count}");
            // Future: process and send frame to server
        }
    }
}
