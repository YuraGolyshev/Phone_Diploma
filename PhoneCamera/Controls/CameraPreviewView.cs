namespace PhoneCamera.Controls;

public enum StreamFormat
{
    /// <summary>JPEG-кадры через CameraX ImageAnalysis (legacy путь, до 30 fps на YUV).</summary>
    Jpeg,
    /// <summary>H.264 NAL-чанки через MediaCodec encoder Surface (60 fps на 1080p).</summary>
    H264,
}

public class CameraPreviewView : View
{
    public static readonly BindableProperty IsRunningProperty =
        BindableProperty.Create(nameof(IsRunning), typeof(bool), typeof(CameraPreviewView), false);

    public static readonly BindableProperty SelectedConfigProperty =
        BindableProperty.Create(nameof(SelectedConfig), typeof(CameraConfig), typeof(CameraPreviewView), null);

    /// <summary>Формат потока (JPEG / H.264). Хендлер при смене переключает pipeline.</summary>
    public static readonly BindableProperty FormatProperty =
        BindableProperty.Create(nameof(Format), typeof(StreamFormat), typeof(CameraPreviewView), StreamFormat.Jpeg);

    /// <summary>QR-сканирование. На время сканирования принудительно используется CameraX
    /// (через ImageAnalysis YUV доступ к плоскости Y), даже если выбран Format=H264.</summary>
    public static readonly BindableProperty IsScanningProperty =
        BindableProperty.Create(nameof(IsScanning), typeof(bool), typeof(CameraPreviewView), false,
            propertyChanged: (b, o, n) => ((CameraPreviewView)b)._isScanning = (bool)n);

    public bool IsRunning
    {
        get => (bool)GetValue(IsRunningProperty);
        set => SetValue(IsRunningProperty, value);
    }

    public CameraConfig? SelectedConfig
    {
        get => (CameraConfig?)GetValue(SelectedConfigProperty);
        set => SetValue(SelectedConfigProperty, value);
    }

    public StreamFormat Format
    {
        get => (StreamFormat)GetValue(FormatProperty);
        set => SetValue(FormatProperty, value);
    }

    /// <summary>
    /// Вызывается хендлером, когда список доступных конфигураций готов.
    /// SelectedConfig уже установлен на дефолт (1080p 30fps) к этому моменту.
    /// </summary>
    public Action<IReadOnlyList<CameraConfig>>? ConfigurationsReady { get; set; }

    /// <summary>
    /// Вызывается с каждым кадром во время стриминга.
    /// buf — внутренний буфер MemoryStream (не копировать, не хранить дольше вызова).
    /// len — реальная длина JPEG в байтах (buf может быть длиннее).
    /// volatile: фоновые воркеры читают это поле без lock — нужна немедленная видимость.
    /// </summary>
    private volatile Action<byte[], int>? _frameReady;
    public Action<byte[], int>? FrameReady
    {
        get => _frameReady;
        set => _frameReady = value;
    }

    /// <summary>
    /// Вызывается с каждым H.264 NAL-чанком когда Format=H264 (изолированный путь
    /// через MediaCodec, обходит ImageReader/30fps cap). data/len валидны до возврата.
    /// </summary>
    private volatile Action<byte[], int, bool>? _h264NalReady;
    public Action<byte[], int, bool>? H264NalReady
    {
        get => _h264NalReady;
        set => _h264NalReady = value;
    }

    /// <summary>Вызывается, когда найден QR-код (передаётся его текст).</summary>
    public Action<string>? QrCodeDetected { get; set; }

    /// <summary>Когда true — FrameAnalyzer ищет QR-коды вместо стриминга.</summary>
    /// volatile: фоновые воркеры читают это поле без lock — нужна немедленная видимость.
    private volatile bool _isScanning;
    public bool IsScanning
    {
        get => (bool)GetValue(IsScanningProperty);
        set => SetValue(IsScanningProperty, value);
    }
}
