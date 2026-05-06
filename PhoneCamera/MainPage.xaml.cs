using PhoneCamera.Controls;
using PhoneCamera.Services;

namespace PhoneCamera;

public partial class MainPage : ContentPage
{
    private bool _isRunning = false;
    private bool _dropdownOpen = false;
    private bool _isLocked = false;
    private bool _isCurrentlyLandscape;

    private bool _isScanning;
    private long _lastFrameMs;
    private string? _lastQrText;
    private long _lastQrDetectedMs;
    private readonly CameraStreamingService _streaming = new();

    public MainPage()
    {
        InitializeComponent();
        CameraPreview.ConfigurationsReady = OnConfigurationsReady;
        CameraPreview.QrCodeDetected = OnQrCodeDetected;

        // H.264 NAL chunks → CameraStreamingService.EnqueueRawBytes (тот же фреймер,
        // payload — Annex-B). Throttle не нужен: encoder сам пишет в нужном fps.
        // Gate _streamingActive: пока false, encoder работает но кадры не отправляются.
        CameraPreview.H264NalReady = (buf, len, isKey) =>
        {
            if (_streamingActive && _streaming.IsConnected)
                _streaming.EnqueueRawBytes(buf, len);
        };
    }

    // Управляет отправкой как для JPEG (через FrameReady), так и для H.264 (через H264NalReady).
    // Stop переводит в false; Start — в true. Connect/Disconnect это поле НЕ трогают.
    private volatile bool _streamingActive;

    private void OnH264CheckedChanged(object? sender, CheckedChangedEventArgs e)
    {
        // Переключатель определяет, какой handshake-байт мы пошлём при следующем Connect
        // и какой Format выставить на View после успешного подключения.
        // До нажатия "Подключиться" CameraPreview.Format остаётся JPEG, чтобы QR-сканер
        // (который требует CameraX YUV-пути) работал в любом случае.
        System.Diagnostics.Debug.WriteLine($"[PCam][UI] H.264 toggle: {e.Value}");
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        // Запрашиваем разрешение заранее. Камеру НЕ стартуем здесь —
        // SurfaceView ещё не нарисован, LockCanvas() вернёт null и превью не появится.
        // Камера запустится из OnConfigurationsReady, когда layout готов и конфиг известен.
        var status = await Permissions.CheckStatusAsync<Permissions.Camera>();
        if (status != PermissionStatus.Granted)
            status = await Permissions.RequestAsync<Permissions.Camera>();
        System.Diagnostics.Debug.WriteLine($"[PCam][UI] OnAppearing — camera permission: {status}");
        if (status != PermissionStatus.Granted)
            StatusLabel.Text = "Нет разрешения камеры";
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        var orientation = DeviceDisplay.Current.MainDisplayInfo.Orientation;
        if (orientation == DisplayOrientation.Unknown) return;
        if (_isLocked) return;
        bool nowLandscape = orientation == DisplayOrientation.Landscape;
        if (nowLandscape == _isCurrentlyLandscape) return;
        _isCurrentlyLandscape = nowLandscape;
        var cfg = CameraPreview.SelectedConfig;
        if (cfg == null) return;
        bool isConfigLandscape = cfg.Width >= cfg.Height;
        if (nowLandscape == isConfigLandscape) return;
        var newCfg = cfg with { Width = cfg.Height, Height = cfg.Width };
        System.Diagnostics.Debug.WriteLine($"[PCam][UI] Orientation → {(nowLandscape ? "Landscape" : "Portrait")}: {cfg.DisplayName} → {newCfg.DisplayName}");
        CameraPreview.SelectedConfig = newCfg;
        ConfigLabel.Text = newCfg.DisplayName;
        if (_isRunning)
            StatusLabel.Text = $"Capturing  {newCfg.DisplayName}";
    }

    private void OnConfigurationsReady(IReadOnlyList<CameraConfig> configs)
    {
        bool isDeviceLandscape = DeviceDisplay.Current.MainDisplayInfo.Orientation == DisplayOrientation.Landscape;
        _isCurrentlyLandscape = isDeviceLandscape;
        var cfg = CameraPreview.SelectedConfig;
        if (cfg != null)
        {
            bool isConfigLandscape = cfg.Width >= cfg.Height;
            if (isDeviceLandscape != isConfigLandscape)
                CameraPreview.SelectedConfig = cfg with { Width = cfg.Height, Height = cfg.Width };
        }
        System.Diagnostics.Debug.WriteLine($"[PCam][UI] ConfigurationsReady: {configs.Count} configs, landscape={isDeviceLandscape}, initial={CameraPreview.SelectedConfig?.DisplayName ?? "none"}");
        ConfigList.ItemsSource = configs;
        ConfigLabel.Text = CameraPreview.SelectedConfig?.DisplayName ?? "—";

        // Стартуем превью здесь: layout уже нарисован, конфиг известен.
        if (!CameraPreview.IsRunning)
        {
            System.Diagnostics.Debug.WriteLine("[PCam][UI] OnConfigurationsReady — autostart preview");
            CameraPreview.IsRunning = true;
        }
    }

    private void OnConfigHeaderTapped(object? sender, TappedEventArgs e)
    {
        if (ConfigList.ItemsSource == null) return;
        _dropdownOpen = !_dropdownOpen;
        ConfigList.IsVisible = _dropdownOpen;
        ConfigArrow.Text = _dropdownOpen ? "\u25b2" : "\u25bc";
        System.Diagnostics.Debug.WriteLine($"[PCam][UI] Dropdown {(_dropdownOpen ? "opened" : "closed")}");
    }

    private void OnConfigSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is CameraConfig cfg)
        {
            System.Diagnostics.Debug.WriteLine($"[PCam][UI] Config selected: {cfg.DisplayName}");
            CameraPreview.SelectedConfig = cfg;
            ConfigLabel.Text = cfg.DisplayName;
        }
        _dropdownOpen = false;
        ConfigList.IsVisible = false;
        ConfigArrow.Text = "\u25bc";
    }

    private void OnLockCheckedChanged(object? sender, CheckedChangedEventArgs e)
    {
        _isLocked = e.Value;
#if ANDROID
        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        if (activity != null)
            activity.RequestedOrientation = _isLocked
                ? Android.Content.PM.ScreenOrientation.Locked
                : Android.Content.PM.ScreenOrientation.FullSensor;
#endif
        System.Diagnostics.Debug.WriteLine($"[PCam][UI] Lock: {(_isLocked ? "locked" : "unlocked")}");
    }

    private async void OnStartStopClicked(object? sender, EventArgs e)
    {
        if (_dropdownOpen)
        {
            _dropdownOpen = false;
            ConfigList.IsVisible = false;
            ConfigArrow.Text = "\u25bc";
        }

        var status = await Permissions.CheckStatusAsync<Permissions.Camera>();
        if (status != PermissionStatus.Granted)
            status = await Permissions.RequestAsync<Permissions.Camera>();

        System.Diagnostics.Debug.WriteLine($"[PCam][UI] Camera permission status: {status}");

        if (status != PermissionStatus.Granted)
        {
            StatusLabel.Text = "Camera permission denied";
            return;
        }

        _isRunning = !_isRunning;
        System.Diagnostics.Debug.WriteLine($"[PCam][UI] {(_isRunning ? "Start" : "Stop")} → config={CameraPreview.SelectedConfig?.DisplayName ?? "none"}");
        if (_isRunning)
        {
            // Включаем gate — теперь H.264 NAL-чанки и JPEG-кадры пойдут на сервер.
            _streamingActive = true;

            int targetFps = (int)Math.Max(1.0, CameraPreview.SelectedConfig?.MaxFps ?? 30.0);
            long intervalMs = 1000 / targetFps;
            Volatile.Write(ref _lastFrameMs, 0L);
            CameraPreview.FrameReady = (buf, len) =>
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (now - Volatile.Read(ref _lastFrameMs) < intervalMs) return;
                Volatile.Write(ref _lastFrameMs, now);
                if (_streamingActive && _streaming.IsConnected)
                    _streaming.EnqueueFrame(buf, len);
            };

            // Если подключены и хотим H.264 — переводим Format на H264 (handler перестроит
            // pipeline на MediaCodec encoder Surface). Если выключатель снят или ещё не
            // подключены — остаёмся на CameraX (Format=Jpeg).
            CameraPreview.Format = (_streaming.IsConnected && H264CheckBox.IsChecked)
                ? StreamFormat.H264
                : StreamFormat.Jpeg;
        }
        else
        {
            // Stop: gate выключен → ни JPEG, ни H.264 кадры не идут на сервер.
            _streamingActive = false;
            CameraPreview.FrameReady = null;

            // Возвращаем Format=Jpeg → handler остановит H.264 pipeline и вернёт CameraX
            // preview, чтобы продолжать видеть картинку и сканировать QR.
            CameraPreview.Format = StreamFormat.Jpeg;

            if (_isScanning)
            {
                _isScanning = false;
                CameraPreview.IsScanning = false;
                ConnectBtn.Text = "Подключиться";
            }
        }

        if (_isRunning && CameraPreview.Format == StreamFormat.Jpeg)
        {
            // Только в JPEG-ветке нужен рестарт CameraX чтобы DispatchingAnalyzer подхватил
            // новый FrameReady. Для H.264 ветки рестартом ведает MapFormat.
            if (CameraPreview.IsRunning)
                CameraPreview.IsRunning = false;
            CameraPreview.IsRunning = true;
        }

        StartStopBtn.Text = _isRunning ? "Stop" : "Start";
        StatusLabel.Text = _isRunning ? $"Capturing  {CameraPreview.SelectedConfig?.DisplayName}" : "Preview";
    }

    private async void OnConnectClicked(object? sender, EventArgs e)
    {
        if (_isScanning)
        {
            _isScanning = false;
            CameraPreview.IsScanning = false;
            ConnectBtn.Text = "Подключиться";
            StatusLabel.Text = _isRunning ? $"Capturing  {CameraPreview.SelectedConfig?.DisplayName}" : "Press Start";
            return;
        }

        var status = await Permissions.CheckStatusAsync<Permissions.Camera>();
        if (status != PermissionStatus.Granted)
            status = await Permissions.RequestAsync<Permissions.Camera>();

        if (status != PermissionStatus.Granted)
        {
            ConnectionLabel.Text = "Нет разрешения камеры";
            return;
        }

        _isScanning = true;
        CameraPreview.IsScanning = true;

        if (!CameraPreview.IsRunning)
            CameraPreview.IsRunning = true;

        ConnectBtn.Text = "Отмена";
        StatusLabel.Text = "Наведите на QR-код...";
    }

    private void OnQrCodeDetected(string qrText)
    {
        System.Diagnostics.Debug.WriteLine($"[PCam][UI] QR detected: {qrText}");

        // Debounce: CAS-логика в FrameAnalyzer уже не пустит второй вызов за 200ms,
        // но на случай edge-race добавляем дополнительный барьер на уровне UI (2s).
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (qrText == _lastQrText && nowMs - _lastQrDetectedMs < 2000)
        {
            System.Diagnostics.Debug.WriteLine($"[PCam][UI] QR debounced (duplicate within 2s): {qrText}");
            return;
        }
        _lastQrText        = qrText;
        _lastQrDetectedMs  = nowMs;

        _isScanning = false;
        CameraPreview.IsScanning = false;
        ConnectBtn.Text = "Подключиться";
        StatusLabel.Text = _isRunning ? $"Capturing  {CameraPreview.SelectedConfig?.DisplayName}" : "Press Start";

        if (!qrText.StartsWith("cam://"))
        {
            ConnectionLabel.Text = "Неверный QR-код";
            return;
        }

        var addr = qrText["cam://".Length..];
        var parts = addr.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[1], out int port))
        {
            ConnectionLabel.Text = "Неверный QR-код";
            return;
        }

        string ip = parts[0];
        bool wantH264 = H264CheckBox.IsChecked;
        byte handshake = wantH264 ? (byte)0x02 : (byte)0x01;
        ConnectionLabel.Text = $"Подключение к {ip}:{port} ({(wantH264 ? "H.264" : "JPEG")})...";

        Task.Run(async () =>
        {
            bool ok = await _streaming.ConnectAsync(ip, port, handshake);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                ConnectionLabel.Text = ok
                    ? $"Подключено: {ip}:{port} ({(wantH264 ? "H.264" : "JPEG")})"
                    : $"Ошибка подключения к {ip}:{port}";
                // Format НЕ переключаем здесь — это произойдёт при Start (OnStartStopClicked),
                // чтобы стрим начинался строго по нажатию Start, а не сразу после connect.
                // Если стрим уже активен в момент connect — переключим прямо сейчас.
                if (ok && _streamingActive && wantH264)
                    CameraPreview.Format = StreamFormat.H264;
            });
        });
    }
}
