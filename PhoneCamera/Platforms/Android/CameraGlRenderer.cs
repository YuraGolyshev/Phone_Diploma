using Android.Graphics;
using Android.Opengl;
using Android.OS;
using Javax.Microedition.Khronos.Opengles;

namespace PhoneCamera.Platforms.Android;

/// <summary>
/// OpenGL ES 2.0 рендерер с SurfaceTexture: камера пишет напрямую в OES-текстуру (GPU).
///
/// Архитектура:
///   Camera2 → SurfaceTexture (GL_TEXTURE_EXTERNAL_OES) → samplerExternalOES (hardware YUV→RGB)
///   Экран: quad fill (SurfaceTexture matrix обрабатывает поворот)
///   FBO: целевое разрешение для стриминга/QR (glReadPixels)
/// </summary>
internal sealed class CameraGlRenderer : Java.Lang.Object, GLSurfaceView.IRenderer
{
    private readonly GLSurfaceView _glView;

    // GL-ресурсы (только GL-поток)
    private int _oesTexId;
    private int _program;
    private int _posAttr, _texCoordAttr;
    private int _texMatUniform, _sTextureUniform;
    private Java.Nio.FloatBuffer? _quadBuf;

    // SurfaceTexture
    private SurfaceTexture? _surfaceTexture;
    private readonly float[] _texMatrix = new float[16];
    private volatile bool _frameAvailable;
    private bool _hasFirstFrame;

    // Экран
    private int _surfaceW, _surfaceH;

    // FBO для стриминга
    private int _fboId, _fboTexId;
    private int _fboW, _fboH;
    private Java.Nio.ByteBuffer? _readBuf;
    private int _readBufSize;

    // FPS-диагностика
    private int _drawCount;
    private long _lastTimestampNs;
    private long _tsDeltaAccNs;
    private int _tsDeltaCount;
    private readonly System.Diagnostics.Stopwatch _fpsSw = System.Diagnostics.Stopwatch.StartNew();
    private long _lastFpsLogMs;

    // Внешние callback-и
    public Action<int, int>? AfterDrawFrame;
    public event Action? SurfaceTextureReady;

    public SurfaceTexture? CameraSurfaceTexture => _surfaceTexture;
    public int SurfaceWidth => _surfaceW;
    public int SurfaceHeight => _surfaceH;

    public CameraGlRenderer(GLSurfaceView glView) => _glView = glView;

    // ══════════════════════════════════════════════════════════════════════
    //  GLSurfaceView.IRenderer — GL-поток
    // ══════════════════════════════════════════════════════════════════════

    public void OnSurfaceCreated(IGL10? gl, Javax.Microedition.Khronos.Egl.EGLConfig? config)
    {
        var ids = new int[1];
        GLES20.GlGenTextures(1, ids, 0);
        _oesTexId = ids[0];
        GLES20.GlBindTexture(GLES11Ext.GlTextureExternalOes, _oesTexId);
        GLES20.GlTexParameteri(GLES11Ext.GlTextureExternalOes, GLES20.GlTextureWrapS, GLES20.GlClampToEdge);
        GLES20.GlTexParameteri(GLES11Ext.GlTextureExternalOes, GLES20.GlTextureWrapT, GLES20.GlClampToEdge);
        GLES20.GlTexParameteri(GLES11Ext.GlTextureExternalOes, GLES20.GlTextureMinFilter, GLES20.GlLinear);
        GLES20.GlTexParameteri(GLES11Ext.GlTextureExternalOes, GLES20.GlTextureMagFilter, GLES20.GlLinear);

        _surfaceTexture = new SurfaceTexture(_oesTexId);
        _surfaceTexture.SetOnFrameAvailableListener(
            new FrameListener(this), new Handler(Looper.MainLooper!));

        _program = BuildProgram(VertSrc, FragSrc);
        _posAttr = GLES20.GlGetAttribLocation(_program, "aPos");
        _texCoordAttr = GLES20.GlGetAttribLocation(_program, "aTexCoord");
        _texMatUniform = GLES20.GlGetUniformLocation(_program, "uTexMat");
        _sTextureUniform = GLES20.GlGetUniformLocation(_program, "sTexture");

        System.Diagnostics.Debug.WriteLine(
            $"[PCam][GL] OnSurfaceCreated: oesTex={_oesTexId} prog={_program}" +
            $" aPos={_posAttr} aTC={_texCoordAttr} uTexMat={_texMatUniform} sTex={_sTextureUniform}");

        float[] quad = {
            -1f, -1f, 0f, 0f,
             1f, -1f, 1f, 0f,
            -1f,  1f, 0f, 1f,
             1f,  1f, 1f, 1f,
        };
        var bb = Java.Nio.ByteBuffer.AllocateDirect(quad.Length * 4)!;
        bb.Order(Java.Nio.ByteOrder.NativeOrder()!);
        _quadBuf = bb.AsFloatBuffer()!;
        _quadBuf.Put(quad);
        _quadBuf.Position(0);

        GLES20.GlClearColor(0f, 0f, 0f, 1f);
        _hasFirstFrame = false;
        _fboId = 0;

        SurfaceTextureReady?.Invoke();
    }

    public void OnSurfaceChanged(IGL10? gl, int width, int height)
    {
        _surfaceW = width;
        _surfaceH = height;
        GLES20.GlViewport(0, 0, width, height);
        System.Diagnostics.Debug.WriteLine($"[PCam][GL] OnSurfaceChanged: {width}x{height}");
    }

    public void OnDrawFrame(IGL10? gl)
    {
        GLES20.GlClear(GLES20.GlColorBufferBit);

        if (_surfaceTexture == null || _program == 0) return;

        if (_frameAvailable)
        {
            _frameAvailable = false;
            try
            {
                _surfaceTexture.UpdateTexImage();
                _surfaceTexture.GetTransformMatrix(_texMatrix);
                _hasFirstFrame = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PCam][GL] UpdateTexImage error: {ex.Message}");
                return;
            }
        }

        if (!_hasFirstFrame) return;

        // FPS
        long ts = _surfaceTexture.Timestamp;
        if (ts > 0 && _lastTimestampNs > 0)
        {
            long delta = ts - _lastTimestampNs;
            if (delta > 0 && delta < 500_000_000L)
            {
                _tsDeltaAccNs += delta;
                _tsDeltaCount++;
            }
        }
        if (ts > 0) _lastTimestampNs = ts;

        _drawCount++;
        if (_drawCount % 60 == 0)
        {
            long now = _fpsSw.ElapsedMilliseconds;
            long elapsed = now - _lastFpsLogMs;
            _lastFpsLogMs = now;
            double timerFps = elapsed > 0 ? 60000.0 / elapsed : 0;
            double hwFps = 0;
            if (_tsDeltaCount > 0)
            {
                double avgMs = _tsDeltaAccNs / (double)_tsDeltaCount / 1_000_000.0;
                hwFps = avgMs > 0 ? 1000.0 / avgMs : 0;
                _tsDeltaAccNs = 0;
                _tsDeltaCount = 0;
            }
            System.Diagnostics.Debug.WriteLine(
                $"[PCam][GL] Frame #{_drawCount}  timerFPS~{timerFps:F1}  hwFPS~{hwFps:F1}");
        }

        // ── Рендер на экран (SurfaceTexture matrix обрабатывает поворот) ─
        RenderQuad();

        if (_drawCount <= 3)
        {
            int err = GLES20.GlGetError();
            System.Diagnostics.Debug.WriteLine(
                $"[PCam][GL] Drew frame #{_drawCount}: surface={_surfaceW}x{_surfaceH} glError={err}");
        }

        // ── Callback для стриминга/QR ───────────────────────────────────
        AfterDrawFrame?.Invoke(_surfaceW, _surfaceH);
    }

    private void RenderQuad()
    {
        GLES20.GlUseProgram(_program);

        _quadBuf!.Position(0);
        GLES20.GlVertexAttribPointer(_posAttr, 2, GLES20.GlFloat, false, 16, _quadBuf);
        _quadBuf.Position(2);
        GLES20.GlVertexAttribPointer(_texCoordAttr, 2, GLES20.GlFloat, false, 16, _quadBuf);

        GLES20.GlEnableVertexAttribArray(_posAttr);
        GLES20.GlEnableVertexAttribArray(_texCoordAttr);

        GLES20.GlActiveTexture(GLES20.GlTexture0);
        GLES20.GlBindTexture(GLES11Ext.GlTextureExternalOes, _oesTexId);
        GLES20.GlUniform1i(_sTextureUniform, 0);
        GLES20.GlUniformMatrix4fv(_texMatUniform, 1, false, _texMatrix, 0);

        GLES20.GlDrawArrays(GLES20.GlTriangleStrip, 0, 4);

        GLES20.GlDisableVertexAttribArray(_posAttr);
        GLES20.GlDisableVertexAttribArray(_texCoordAttr);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  FBO + ReadPixels
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Рендерит кадр в FBO и возвращает RGBA. GL-поток only.
    /// </summary>
    public byte[]? ReadPixelsAtResolution(int w, int h)
    {
        EnsureFbo(w, h);
        if (_fboId == 0) return null;

        GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, _fboId);
        GLES20.GlViewport(0, 0, w, h);
        GLES20.GlClear(GLES20.GlColorBufferBit);
        RenderQuad();

        int size = w * h * 4;
        if (_readBuf == null || _readBufSize != size)
        {
            _readBuf?.Dispose();
            _readBuf = Java.Nio.ByteBuffer.AllocateDirect(size)!;
            _readBuf.Order(Java.Nio.ByteOrder.NativeOrder()!);
            _readBufSize = size;
        }
        _readBuf.Position(0);
        GLES20.GlReadPixels(0, 0, w, h, GLES20.GlRgba, GLES20.GlUnsignedByte, _readBuf);

        GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, 0);
        GLES20.GlViewport(0, 0, _surfaceW, _surfaceH);

        int err = GLES20.GlGetError();
        if (err != GLES20.GlNoError)
        {
            System.Diagnostics.Debug.WriteLine($"[PCam][GL] ReadPixels FBO error: {err}");
            return null;
        }

        byte[] rgba = new byte[size];
        _readBuf.Position(0);
        _readBuf.Get(rgba, 0, size);
        return rgba;
    }

    private void EnsureFbo(int w, int h)
    {
        if (_fboId != 0 && _fboW == w && _fboH == h) return;

        if (_fboId != 0)
        {
            int[] del = { _fboId };
            GLES20.GlDeleteFramebuffers(1, del, 0);
            del[0] = _fboTexId;
            GLES20.GlDeleteTextures(1, del, 0);
        }

        var texIds = new int[1];
        GLES20.GlGenTextures(1, texIds, 0);
        _fboTexId = texIds[0];
        GLES20.GlBindTexture(GLES20.GlTexture2d, _fboTexId);
        GLES20.GlTexImage2D(GLES20.GlTexture2d, 0, GLES20.GlRgba, w, h, 0,
            GLES20.GlRgba, GLES20.GlUnsignedByte, null);
        GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMinFilter, GLES20.GlLinear);
        GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMagFilter, GLES20.GlLinear);

        var fboIds = new int[1];
        GLES20.GlGenFramebuffers(1, fboIds, 0);
        _fboId = fboIds[0];
        GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, _fboId);
        GLES20.GlFramebufferTexture2D(GLES20.GlFramebuffer, GLES20.GlColorAttachment0,
            GLES20.GlTexture2d, _fboTexId, 0);

        int status = GLES20.GlCheckFramebufferStatus(GLES20.GlFramebuffer);
        if (status != GLES20.GlFramebufferComplete)
        {
            System.Diagnostics.Debug.WriteLine($"[PCam][GL] FBO incomplete: {status}");
            GLES20.GlDeleteFramebuffers(1, fboIds, 0);
            _fboId = 0;
        }
        else
        {
            _fboW = w;
            _fboH = h;
            System.Diagnostics.Debug.WriteLine($"[PCam][GL] FBO created: {w}x{h}");
        }

        GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, 0);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  SurfaceTexture OnFrameAvailable
    // ══════════════════════════════════════════════════════════════════════

    private sealed class FrameListener : Java.Lang.Object, SurfaceTexture.IOnFrameAvailableListener
    {
        private readonly CameraGlRenderer _r;
        private int _logCount;

        public FrameListener(CameraGlRenderer r) => _r = r;

        public void OnFrameAvailable(SurfaceTexture? surfaceTexture)
        {
            _r._frameAvailable = true;
            _r._glView.RequestRender();

            if (++_logCount <= 3)
                System.Diagnostics.Debug.WriteLine($"[PCam][GL] OnFrameAvailable #{_logCount}");
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Вспомогательное
    // ══════════════════════════════════════════════════════════════════════

    private static int BuildProgram(string vertSrc, string fragSrc)
    {
        int vs = CompileShader(GLES20.GlVertexShader, vertSrc);
        int fs = CompileShader(GLES20.GlFragmentShader, fragSrc);
        int p = GLES20.GlCreateProgram();
        GLES20.GlAttachShader(p, vs);
        GLES20.GlAttachShader(p, fs);
        GLES20.GlLinkProgram(p);

        var status = new int[1];
        GLES20.GlGetProgramiv(p, GLES20.GlLinkStatus, status, 0);
        if (status[0] == 0)
            System.Diagnostics.Debug.WriteLine(
                $"[PCam][GL] Program link ERROR: {GLES20.GlGetProgramInfoLog(p)}");

        GLES20.GlDeleteShader(vs);
        GLES20.GlDeleteShader(fs);
        return p;
    }

    private static int CompileShader(int type, string src)
    {
        int s = GLES20.GlCreateShader(type);
        GLES20.GlShaderSource(s, src);
        GLES20.GlCompileShader(s);

        var status = new int[1];
        GLES20.GlGetShaderiv(s, GLES20.GlCompileStatus, status, 0);
        if (status[0] == 0)
            System.Diagnostics.Debug.WriteLine(
                $"[PCam][GL] Shader compile ERROR ({(type == GLES20.GlVertexShader ? "vert" : "frag")}): " +
                GLES20.GlGetShaderInfoLog(s));
        return s;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Шейдеры
    // ══════════════════════════════════════════════════════════════════════

    // SurfaceTexture matrix обрабатывает: Y-flip, поворот сенсора, кроп
    private const string VertSrc = @"
attribute vec4 aPos;
attribute vec2 aTexCoord;
uniform mat4 uTexMat;
varying vec2 vTexCoord;
void main() {
    gl_Position = aPos;
    vTexCoord = (uTexMat * vec4(aTexCoord, 0.0, 1.0)).xy;
}";

    private const string FragSrc = @"
#extension GL_OES_EGL_image_external : require
precision mediump float;
uniform samplerExternalOES sTexture;
varying vec2 vTexCoord;
void main() {
    gl_FragColor = texture2D(sTexture, vTexCoord);
}";
}
