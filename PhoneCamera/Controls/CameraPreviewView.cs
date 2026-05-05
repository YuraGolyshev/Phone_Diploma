namespace PhoneCamera.Controls;

public class CameraPreviewView : View
{
    public static readonly BindableProperty IsRunningProperty =
        BindableProperty.Create(nameof(IsRunning), typeof(bool), typeof(CameraPreviewView), false);

    public static readonly BindableProperty SelectedConfigProperty =
        BindableProperty.Create(nameof(SelectedConfig), typeof(CameraConfig), typeof(CameraPreviewView), null);

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

    /// <summary>Вызывается, когда найден QR-код (передаётся его текст).</summary>
    public Action<string>? QrCodeDetected { get; set; }

    /// <summary>Когда true — FrameAnalyzer ищет QR-коды вместо стриминга.</summary>
    /// volatile: фоновые воркеры читают это поле без lock — нужна немедленная видимость.
    private volatile bool _isScanning;
    public bool IsScanning
    {
        get => _isScanning;
        set => _isScanning = value;
    }
}
