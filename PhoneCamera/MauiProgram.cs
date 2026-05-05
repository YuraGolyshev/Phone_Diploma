using Microsoft.Extensions.Logging;
using PhoneCamera.Controls;

namespace PhoneCamera
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                })
                .ConfigureMauiHandlers(handlers =>
                {
#if ANDROID
                    handlers.AddHandler<CameraPreviewView, PhoneCamera.Platforms.Android.CameraPreviewHandler>();
#elif IOS
                    handlers.AddHandler<CameraPreviewView, PhoneCamera.Platforms.iOS.CameraPreviewHandler>();
#endif
                });

#if DEBUG
            builder.Logging.AddDebug();
#endif

            return builder.Build();
        }
    }
}
