using PhoneCamera.Controls;
using PhoneCamera.Services;

namespace PhoneCamera;

public partial class MainPage : ContentPage
{
    private bool _isRunning;
    private bool _isLocked;
    private bool _isCurrentlyLandscape;

    private bool _isScanning;
    private string? _lastQrText;
    private long _lastQrDetectedMs;
    private readonly CameraStreamingService _streaming = new();

    // Источник правды для всех конфигов от QueryConfigs
    private IReadOnlyList<CameraConfig>? _allConfigs;

    // Текущий выбор хранится в landscape-каноне (W >= H). При portrait
    // ApplyConfigToCamera сам swap'ает width/height перед публикацией в SelectedConfig.
    private int _curW = 1920, _curH = 1080;
    private int _curFps = 30;
    private StreamFormat _selectedFormat = StreamFormat.H264;

    // Состояние трёх dropdown'ов — открыта может быть только одна шторка одновременно
    private bool _resDropdownOpen, _fpsDropdownOpen, _fmtDropdownOpen;

    // Управляет отправкой как для JPEG (через FrameReady), так и для H.264 (через H264NalReady).
    // Stop переводит в false; Start — в true. Connect/Disconnect это поле НЕ трогают.
    private volatile bool _streamingActive;
    // Защита от двойного открытия SettingsPage при быстрых тапах — без неё
    // PushModalAsync успевает встать в очередь второй раз пока первый ещё идёт.
    private bool _settingsOpening;

    // Формат, с которым последний раз был отправлен handshake серверу.
    // Если при следующем Start пользователь выбрал другой формат — TCP-сессию надо
    // переоткрыть с новым handshake-байтом (сервер ветвится по нему один раз).
    private StreamFormat? _lastConnectedFormat;

    public MainPage()
    {
        InitializeComponent();

#if ANDROID
        // Подставляем РЕАЛЬНУЮ высоту системной шторки (вычислена в MainActivity)
        // в backdrop и поднимаем TopOverlayLayout на эту высоту, чтобы шторки
        // не залезали под status bar.
        int sbHeight = PhoneCamera.MainActivity.StatusBarHeightDp;
        StatusBarBackdrop.HeightRequest = sbHeight;
        TopOverlayLayout.Margin = new Thickness(0, sbHeight, 0, 0);
#endif

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

        // Прокидываем статистику backpressure от сервиса → во view → подписчикам
        // (H.264 pipeline через handler). Сервис тикает раз в ~2 сек, AIMD-контроллер
        // в pipeline'е сам решает менять ли битрейт.
        _streaming.OnBackpressureWindow = (blocked, total) =>
        {
            try { CameraPreview.BackpressureWindowReady?.Invoke(blocked, total); } catch { }
        };

        UpdateLabels();
        UpdateFormatHeaderEnabled();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
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
        bool nowLandscape = orientation == DisplayOrientation.Landscape;

        // Переключаем layout верхних шторок: в landscape — три колонки в ряд,
        // в portrait — три строки друг под другом. Делаем это даже при _isLocked,
        // чтобы UI всегда соответствовал реальной ориентации экрана.
        ApplyTopOverlayLayout(nowLandscape);

        if (_isLocked) return;
        if (nowLandscape == _isCurrentlyLandscape) return;
        _isCurrentlyLandscape = nowLandscape;
        if (_allConfigs == null) return;

        // _curW/_curH хранятся в landscape canon — менять их не нужно. ApplyConfigToCamera
        // сам разместит W/H в текущей ориентации перед публикацией в SelectedConfig.
        ApplyConfigToCamera();
        if (_isRunning)
            StatusLabel.Text = $"Capturing  {_curW}×{_curH}, {_curFps} fps";
    }

    // Текущая раскладка — чтобы не пересобирать Grid каждый OnSizeAllocated впустую.
    private bool? _overlayLayoutLandscape;

    private void ApplyTopOverlayLayout(bool landscape)
    {
        if (_overlayLayoutLandscape == landscape) return;
        _overlayLayoutLandscape = landscape;

        TopOverlayLayout.RowDefinitions.Clear();
        TopOverlayLayout.ColumnDefinitions.Clear();

        if (landscape)
        {
            // 1 строка: [Resolution*] [Fps*] [Format*] [Settings Auto]
            TopOverlayLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < 3; i++)
                TopOverlayLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
            TopOverlayLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetRow(ResolutionSlot, 0); Grid.SetColumn(ResolutionSlot, 0); Grid.SetColumnSpan(ResolutionSlot, 1);
            Grid.SetRow(FpsSlot,        0); Grid.SetColumn(FpsSlot,        1); Grid.SetColumnSpan(FpsSlot,        1);
            Grid.SetRow(FormatSlot,     0); Grid.SetColumn(FormatSlot,     2); Grid.SetColumnSpan(FormatSlot,     1);
            Grid.SetRow(SettingsSlot,   0); Grid.SetColumn(SettingsSlot,   3); Grid.SetColumnSpan(SettingsSlot,   1);

            // Landscape: НЕ растягиваем по высоте. Иначе при открытии любой из
            // соседних шторок (Resolution/FPS/Format) их dropdown увеличивает
            // высоту строки, и SettingsSlot тянется вместе с ней. Здесь нам это
            // не нужно — кнопка должна оставаться квадратной/компактной.
            SettingsSlot.HorizontalOptions = LayoutOptions.Fill;
            SettingsSlot.VerticalOptions   = LayoutOptions.Start;
        }
        else
        {
            // Portrait: первая строка — [Resolution*][Settings Auto]; ниже Fps и Format.
            for (int i = 0; i < 3; i++)
                TopOverlayLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            TopOverlayLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
            TopOverlayLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetRow(ResolutionSlot, 0); Grid.SetColumn(ResolutionSlot, 0); Grid.SetColumnSpan(ResolutionSlot, 1);
            Grid.SetRow(SettingsSlot,   0); Grid.SetColumn(SettingsSlot,   1); Grid.SetColumnSpan(SettingsSlot,   1);

            Grid.SetRow(FpsSlot,    1); Grid.SetColumn(FpsSlot,    0);
            Grid.SetColumnSpan(FpsSlot, 2);
            Grid.SetRow(FormatSlot, 2); Grid.SetColumn(FormatSlot, 0);
            Grid.SetColumnSpan(FormatSlot, 2);

            SettingsSlot.HorizontalOptions = LayoutOptions.Fill;
            SettingsSlot.VerticalOptions   = LayoutOptions.Fill;
        }

        // Чекбоксы внизу: в portrait одна группа уходит за правые кнопки → ставим
        // вертикально (друг под другом). В landscape места хватает → бок о бок.
        if (CheckboxRow != null)
        {
            CheckboxRow.Orientation = landscape ? StackOrientation.Horizontal : StackOrientation.Vertical;
            CheckboxRow.Spacing     = landscape ? 20 : 4;
        }
    }

    // Разрешения, которые умеет принимать VirtualCamFilter (DirectShow, native).
    // Должно совпадать с kSupportedSizes в C:\cwl\VirtualCamFilter\VirtualCamFilter.cpp.
    // По умолчанию список разрешений в UI фильтруется этим набором — иначе можно
    // выбрать формат, который VCam не сможет передать в Discord/OBS/Zoom напрямую.
    private static readonly (int W, int H)[] VCamSupportedSizes = new[]
    {
        (3840, 2160),
        (2560, 1440),
        (1920, 1080),
        (1280, 720),
        (960,  540),
        (854,  480),
        (640,  480),
    };

    // Перестраивает список разрешений в шторке на основе _allConfigs.
    // Если CameraSettings.ShowAllResolutions=false (по умолчанию) — фильтрует
    // до пересечения с VCamSupportedSizes. Иначе — все разрешения камеры.
    private void RebuildResolutionList()
    {
        if (_allConfigs == null) { ResolutionList.ItemsSource = new List<ResolutionOption>(); return; }

        // Уникальные разрешения (landscape canon: max×min), отсортированные по убыванию площади.
        var all = _allConfigs
            .Select(c => new ResolutionOption(Math.Max(c.Width, c.Height), Math.Min(c.Width, c.Height)))
            .Distinct()
            .ToList();

#if ANDROID
        bool showAll = PhoneCamera.Platforms.Android.CameraSettings.ShowAllResolutions;
#else
        bool showAll = true;
#endif

        IReadOnlyList<ResolutionOption> filtered;
        if (showAll)
        {
            filtered = all.OrderByDescending(r => (long)r.Width * r.Height).ToList();
        }
        else
        {
            // Оставляем только те, что VCam умеет принимать.
            var vcamSet = new HashSet<(int, int)>();
            foreach (var (w, h) in VCamSupportedSizes) vcamSet.Add((w, h));

            filtered = all
                .Where(r => vcamSet.Contains((r.Width, r.Height)))
                .OrderByDescending(r => (long)r.Width * r.Height)
                .ToList();

            // Если пересечение пустое (камера не умеет ни одного VCam-разрешения) —
            // лучше показать весь список целиком, чем оставить пустой выбор.
            if (filtered.Count == 0)
                filtered = all.OrderByDescending(r => (long)r.Width * r.Height).ToList();
        }

        ResolutionList.ItemsSource = filtered;
    }

    private void OnConfigurationsReady(IReadOnlyList<CameraConfig> configs)
    {
        _allConfigs = configs;
        _isCurrentlyLandscape =
            DeviceDisplay.Current.MainDisplayInfo.Orientation == DisplayOrientation.Landscape;

        RebuildResolutionList();
        var resolutions = (IReadOnlyList<ResolutionOption>)ResolutionList.ItemsSource;

        // Дефолт — 1920×1080 если есть, иначе ближайший по площади
        var defRes = resolutions.FirstOrDefault(r => r.Width == 1920 && r.Height == 1080)
                  ?? resolutions.FirstOrDefault();
        if (defRes != null) { _curW = defRes.Width; _curH = defRes.Height; }

        // Заполняем FPS-список для текущего разрешения. Дефолт fps — 30 если есть.
        RefreshFpsList();
        var fpsOptions = AvailableFpsFor(_curW, _curH);
        _curFps = fpsOptions.Contains(30) ? 30
                : fpsOptions.Count > 0    ? fpsOptions[0]
                : 30;

        // Список форматов фиксирован — три варианта. Дефолт — H.264.
        FormatList.ItemsSource = new List<FormatOption>
        {
            new FormatOption(StreamFormat.H264),
            new FormatOption(StreamFormat.H265),
            new FormatOption(StreamFormat.Jpeg),
        };

        System.Diagnostics.Debug.WriteLine(
            $"[PCam][UI] ConfigurationsReady: {configs.Count} configs, " +
            $"resolutions={resolutions.Count}, default={_curW}×{_curH} @ {_curFps} fps, format={_selectedFormat}");

        UpdateLabels();
        ApplyConfigToCamera();

        // Стартуем превью. CameraPreview.Format остаётся Jpeg (default) — для CameraX preview;
        // переключение на H.264 произойдёт только при Start (если выбран H.264 в шторке).
        if (!CameraPreview.IsRunning)
        {
            System.Diagnostics.Debug.WriteLine("[PCam][UI] OnConfigurationsReady — autostart preview");
            CameraPreview.IsRunning = true;
        }
    }

    // ── Обработчики шторок ──────────────────────────────────────────────────

    private void OnResolutionHeaderTapped(object? sender, TappedEventArgs e)
    {
        if (ResolutionList.ItemsSource == null) return;
        SetDropdownOpen(res: !_resDropdownOpen, fps: false, fmt: false);
    }

    private void OnFpsHeaderTapped(object? sender, TappedEventArgs e)
    {
        if (FpsList.ItemsSource == null) return;
        SetDropdownOpen(res: false, fps: !_fpsDropdownOpen, fmt: false);
    }

    private void OnFormatHeaderTapped(object? sender, TappedEventArgs e)
    {
        if (_streamingActive)
        {
            // Заблокировано на время стрима — игнорируем тап.
            System.Diagnostics.Debug.WriteLine("[PCam][UI] Format dropdown blocked (streaming active)");
            return;
        }
        if (FormatList.ItemsSource == null) return;
        SetDropdownOpen(res: false, fps: false, fmt: !_fmtDropdownOpen);
    }

    private void SetDropdownOpen(bool res, bool fps, bool fmt)
    {
        _resDropdownOpen = res;
        _fpsDropdownOpen = fps;
        _fmtDropdownOpen = fmt;
        ResolutionList.IsVisible = res;
        FpsList.IsVisible = fps;
        FormatList.IsVisible = fmt;
        ResolutionArrow.Text = res ? "▲" : "▼";
        FpsArrow.Text        = fps ? "▲" : "▼";
        FormatArrow.Text     = fmt ? "▲" : "▼";
    }

    private void OnResolutionSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not ResolutionOption r) return;
        System.Diagnostics.Debug.WriteLine($"[PCam][UI] Resolution selected: {r.DisplayName}");
        _curW = r.Width;
        _curH = r.Height;

        // Если текущий FPS не доступен на новом разрешении — выбираем ближайший.
        var avail = AvailableFpsFor(_curW, _curH);
        if (!avail.Contains(_curFps))
            _curFps = avail.Contains(30) ? 30 : (avail.Count > 0 ? avail[0] : 30);

        RefreshFpsList();
        UpdateLabels();
        ApplyConfigToCamera();
        SetDropdownOpen(false, false, false);
    }

    private void OnFpsSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not FpsOption f) return;
        System.Diagnostics.Debug.WriteLine($"[PCam][UI] FPS selected: {f.Fps}");
        _curFps = f.Fps;
        UpdateLabels();
        ApplyConfigToCamera();
        SetDropdownOpen(false, false, false);
    }

    private void OnFormatSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not FormatOption f) return;
        if (_streamingActive) return; // safety

        System.Diagnostics.Debug.WriteLine($"[PCam][UI] Format selected: {f.Format}");
        _selectedFormat = f.Format;
        UpdateLabels();
        SetDropdownOpen(false, false, false);
        // CameraPreview.Format переключается только при Start — здесь только запоминаем выбор.
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private List<int> AvailableFpsFor(int w, int h)
    {
        if (_allConfigs == null) return new();
        int bigSide   = Math.Max(w, h);
        int smallSide = Math.Min(w, h);
        return _allConfigs
            .Where(c => Math.Max(c.Width, c.Height) == bigSide
                     && Math.Min(c.Width, c.Height) == smallSide)
            .Select(c => (int)Math.Round(c.MaxFps))
            .Distinct()
            .OrderByDescending(x => x)
            .ToList();
    }

    private void RefreshFpsList()
    {
        var fps = AvailableFpsFor(_curW, _curH).Select(f => new FpsOption(f)).ToList();
        FpsList.ItemsSource = fps;
    }

    private void ApplyConfigToCamera()
    {
        // Учитываем текущую ориентацию: portrait → swap W и H.
        int w = _isCurrentlyLandscape ? _curW : _curH;
        int h = _isCurrentlyLandscape ? _curH : _curW;

        var cfg = _allConfigs?.FirstOrDefault(c =>
                       c.Width == w && c.Height == h && (int)Math.Round(c.MaxFps) == _curFps)
                  ?? new CameraConfig(w, h, _curFps, _curFps);

        // BindableProperty с record-типом сравнивается structurally — если значения те же
        // (например, после ротации UI обновился, а конфиг тот же), MAUI не триггерит MapSelectedConfig.
        CameraPreview.SelectedConfig = cfg;
    }

    private static string FormatName(StreamFormat f) => f switch
    {
        StreamFormat.H264 => "H.264",
        StreamFormat.H265 => "H.265",
        _                 => "JPEG",
    };

    private static byte HandshakeByteFor(StreamFormat f) => f switch
    {
        StreamFormat.H264 => 0x02,
        StreamFormat.H265 => 0x03,
        _                 => 0x01, // JPEG legacy — handshake-байт совпадает с первым байтом FRAME_START
    };

    private void UpdateLabels()
    {
        ResolutionLabel.Text = $"Разрешение: {_curW}×{_curH}";
        FpsLabel.Text        = $"FPS: {_curFps}";
        FormatLabel.Text     = $"Формат: {FormatName(_selectedFormat)}";
    }

    private void UpdateFormatHeaderEnabled()
    {
        // Визуально «затемняем» шторку формата и кнопку настроек на время стрима.
        FormatHeader.Opacity = _streamingActive ? 0.4 : 1.0;
        FormatLabel.Text =
            $"Формат: {FormatName(_selectedFormat)}" +
            (_streamingActive ? "  (заблокировано)" : "");
        // Если на момент блокировки шторка была открыта — закрываем её.
        if (_streamingActive && _fmtDropdownOpen)
            SetDropdownOpen(_resDropdownOpen, _fpsDropdownOpen, false);

        // Кнопка настроек: на время стрима затемнена, тапы игнорируются (см. OnSettingsTapped).
        SettingsSlot.Opacity = _streamingActive ? 0.4 : 1.0;
    }

    // ── Настройки ──────────────────────────────────────────────────────────
    private async void OnSettingsTapped(object? sender, TappedEventArgs e)
    {
        if (_streamingActive)
        {
            // Заблокировано на время стрима — менять параметры в полёте не имеет смысла,
            // их пришлось бы применять рестартом камеры.
            System.Diagnostics.Debug.WriteLine("[PCam][UI] Settings blocked (streaming active)");
            return;
        }
        // Защита от двойного открытия: PushModalAsync конструирует страницу
        // долго (~100-500 мс из-за большого XAML), и быстрый второй тап успевает
        // запустить второй PushModalAsync до того как первая страница успеет
        // подняться — две страницы оверлеем поверх друг друга, обе требуют Pop.
        if (_settingsOpening) return;
        _settingsOpening = true;
        SettingsSlot.Opacity = 0.4; // визуальный feedback пока инициализируется

        try
        {
            // Закрываем все открытые шторки и открываем модал настроек.
            SetDropdownOpen(false, false, false);

            var page = new SettingsPage
            {
                OnApplied = OnSettingsApplied,
            };
            await Navigation.PushModalAsync(page);
        }
        finally
        {
            _settingsOpening = false;
            SettingsSlot.Opacity = _streamingActive ? 0.4 : 1.0;
        }
    }

    private void OnSettingsApplied()
    {
        // Перестраиваем список разрешений с учётом нового ShowAllResolutions.
        // Если текущее выбранное разрешение выпало из отфильтрованного списка —
        // переключаемся на 1920×1080 (или первое доступное).
        RebuildResolutionList();
        if (ResolutionList.ItemsSource is IReadOnlyList<ResolutionOption> resolutions)
        {
            bool stillThere = resolutions.Any(r => r.Width == _curW && r.Height == _curH);
            if (!stillThere && resolutions.Count > 0)
            {
                var def = resolutions.FirstOrDefault(r => r.Width == 1920 && r.Height == 1080)
                       ?? resolutions[0];
                _curW = def.Width;
                _curH = def.Height;
                RefreshFpsList();
                var fpsOpts = AvailableFpsFor(_curW, _curH);
                _curFps = fpsOpts.Contains(_curFps) ? _curFps
                        : fpsOpts.Contains(30)     ? 30
                        : fpsOpts.Count > 0        ? fpsOpts[0]
                        : 30;
                UpdateLabels();
                ApplyConfigToCamera();
            }
        }

        // Перезапускаем камеру чтобы новые настройки попали в CaptureRequest.
        System.Diagnostics.Debug.WriteLine("[PCam][UI] Settings applied — restarting camera");
        try
        {
            if (CameraPreview.IsRunning)
            {
                CameraPreview.IsRunning = false;
                CameraPreview.IsRunning = true;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PCam][UI] Camera restart failed: {ex.Message}");
        }
    }

    // ── Кнопки ──────────────────────────────────────────────────────────────

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

    private void OnPreviewCheckedChanged(object? sender, CheckedChangedEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"[PCam][UI] Preview: {(e.Value ? "enabled" : "disabled")}");
        CameraPreview.PreviewEnabled = e.Value;
    }

    private async void OnStartStopClicked(object? sender, EventArgs e)
    {
        SetDropdownOpen(false, false, false);

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
        System.Diagnostics.Debug.WriteLine(
            $"[PCam][UI] {(_isRunning ? "Start" : "Stop")} → {_curW}×{_curH} @ {_curFps} fps, format={_selectedFormat}");

        if (_isRunning)
        {
            // Включаем gate — JPEG-кадры и H.264 NAL-чанки пойдут на сервер.
            _streamingActive = true;
            UpdateFormatHeaderEnabled();

            // Авто-снять «Показать превью» при старте стрима, если опция включена.
            // Снимаем через IsChecked — это вызовет OnPreviewCheckedChanged, который
            // сам прокинет PreviewEnabled=false в нативную камеру. Если галка уже
            // снята вручную — ничего не меняем.
#if ANDROID
            if (PhoneCamera.Platforms.Android.CameraSettings.AutoDisablePreviewOnStream && PreviewCheckBox.IsChecked)
            {
                PreviewCheckBox.IsChecked = false;
            }
#endif

            // Без software-throttle: камера сама задаёт fps через AE FPS range.
            // Раньше тут стоял "if now - last < 1000/fps return", но при реальных
            // 28fps камеры с jitter ±5ms и intervalMs=33 каждый второй кадр выпадал
            // (см. логи: workerFPS≈15 при combinedFPS≈28 — это и была эта дырка).
            CameraPreview.FrameReady = (buf, len) =>
            {
                if (_streamingActive && _streaming.IsConnected)
                    _streaming.EnqueueFrame(buf, len);
            };

            // Если формат изменился с момента последнего connect — переподключаемся,
            // чтобы серверный диспетчер прочитал новый handshake-байт и встал
            // в нужную ветку (JPEG/H.264). Без этого сервер бы остался в старой ветке
            // и не смог разобрать новые данные.
            if (_streaming.IsConnected
                && _lastConnectedFormat.HasValue
                && _lastConnectedFormat.Value != _selectedFormat)
            {
                string? ip   = _streaming.ServerIp;
                int     port = _streaming.ServerPort;
                if (ip != null)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[PCam][UI] Format changed {_lastConnectedFormat.Value} → {_selectedFormat}, reconnecting to {ip}:{port}");
                    _streaming.Disconnect();
                    byte newHandshake = HandshakeByteFor(_selectedFormat);
                    ConnectionLabel.Text = $"Переподключение к {ip}:{port} ({FormatName(_selectedFormat)})...";
                    bool ok = await _streaming.ConnectAsync(ip, port, newHandshake);
                    if (ok)
                    {
                        _lastConnectedFormat = _selectedFormat;
                        ConnectionLabel.Text = $"Подключено: {ip}:{port} ({FormatName(_selectedFormat)})";
                    }
                    else
                    {
                        ConnectionLabel.Text = $"Ошибка переподключения к {ip}:{port}";
                    }
                }
            }

            // Если уже подключены и выбран H.264/H.265 — переключаем pipeline на encoder-ветку.
            // Иначе остаёмся на CameraX (Format=Jpeg) — стрим всё равно будет JPEG.
            bool encoderFormat = _selectedFormat == StreamFormat.H264 || _selectedFormat == StreamFormat.H265;
            CameraPreview.Format = (_streaming.IsConnected && encoderFormat)
                ? _selectedFormat
                : StreamFormat.Jpeg;
        }
        else
        {
            // Stop: gate выключен → ничего не идёт на сервер.
            _streamingActive = false;
            CameraPreview.FrameReady = null;

            // Возвращаем Format=Jpeg → handler переключит pipeline на CameraX preview.
            CameraPreview.Format = StreamFormat.Jpeg;

            UpdateFormatHeaderEnabled();

            if (_isScanning)
            {
                _isScanning = false;
                CameraPreview.IsScanning = false;
                ConnectBtn.Text = "Подключиться";
            }
        }

        if (_isRunning && CameraPreview.Format == StreamFormat.Jpeg)
        {
            // В JPEG-ветке нужен рестарт CameraX, чтобы DispatchingAnalyzer подхватил
            // новый FrameReady. Для H.264 ветки рестартом ведает MapFormat.
            if (CameraPreview.IsRunning)
                CameraPreview.IsRunning = false;
            CameraPreview.IsRunning = true;
        }

        StartStopBtn.Text = _isRunning ? "Stop" : "Start";
        StatusLabel.Text  = _isRunning
            ? $"Capturing  {_curW}×{_curH}, {_curFps} fps"
            : "Preview";
    }

    private async void OnConnectClicked(object? sender, EventArgs e)
    {
        if (_isScanning)
        {
            _isScanning = false;
            CameraPreview.IsScanning = false;
            ConnectBtn.Text = "Подключиться";
            StatusLabel.Text = _isRunning
                ? $"Capturing  {_curW}×{_curH}, {_curFps} fps"
                : "Press Start";
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
        StatusLabel.Text = _isRunning
            ? $"Capturing  {_curW}×{_curH}, {_curFps} fps"
            : "Press Start";

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
        bool wantEncoder = _selectedFormat == StreamFormat.H264 || _selectedFormat == StreamFormat.H265;
        byte handshake = HandshakeByteFor(_selectedFormat);
        string fmtName = FormatName(_selectedFormat);
        ConnectionLabel.Text = $"Подключение к {ip}:{port} ({fmtName})...";

        Task.Run(async () =>
        {
            bool ok = await _streaming.ConnectAsync(ip, port, handshake);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                ConnectionLabel.Text = ok
                    ? $"Подключено: {ip}:{port} ({fmtName})"
                    : $"Ошибка подключения к {ip}:{port}";
                if (ok)
                {
                    _lastConnectedFormat = _selectedFormat;
                    // Format переключается при Start; если стрим уже активен в момент
                    // подключения — переключаем сразу.
                    if (_streamingActive && wantEncoder)
                        CameraPreview.Format = _selectedFormat;
                }
            });
        });
    }
}

// ── Записи для шторок ────────────────────────────────────────────────────────

public record ResolutionOption(int Width, int Height)
{
    public string DisplayName => $"{Width}×{Height}";
}

public record FpsOption(int Fps)
{
    public string DisplayName => $"{Fps} fps";
}

public record FormatOption(StreamFormat Format)
{
    public string DisplayName => Format switch
    {
        StreamFormat.H264 => "H.264",
        StreamFormat.H265 => "H.265",
        _                 => "JPEG",
    };
}
