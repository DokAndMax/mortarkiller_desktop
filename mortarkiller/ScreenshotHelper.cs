using HPPH;
using ScreenCapture.NET;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

using static mortarkiller.NativeMethods;

namespace mortarkiller;

internal class ScreenshotHelper
{
    // ===== Мінімальний DX11-стан (одна зона на primary screen) =====
    private static readonly object _dxLock = new();

    private static byte[] _dxBuffer = [];
    private static DX11ScreenCapture _dxCapture = default!;
    private static int _dxHeight;
    private static DX11ScreenCaptureService _dxService = default!;
    private static int _dxWidth;
    private static CaptureZone<ColorBGRA> _dxZone = default!;

    public static Bitmap CaptureFullScreen()
    {
        try
        {
            EnsureDxForPrimaryScreen();

            // Пробуємо захопити кадр; інколи перший виклик може повернути false — просто повторимо
            while (!_dxCapture.CaptureScreen())
            {
                Debug.WriteLine("DX11 capture failed, retrying...");
            }

            using (_dxZone.Lock())
            {
                int bytes = _dxWidth * _dxHeight * 4;
                if (_dxBuffer.Length != bytes)
                    _dxBuffer = new byte[bytes];

                _dxZone.RawBuffer.CopyTo(_dxBuffer);

                // Проставляємо альфу в 255
                for (int i = 3; i < _dxBuffer.Length; i += 4)
                    _dxBuffer[i] = 255;

                var bmp = new Bitmap(_dxWidth, _dxHeight, PixelFormat.Format32bppArgb);
                var data = bmp.LockBits(new Rectangle(0, 0, _dxWidth, _dxHeight),
                                        ImageLockMode.WriteOnly,
                                        PixelFormat.Format32bppArgb);
                try
                {
                    int stride = _dxWidth * 4;
                    for (int y = 0; y < _dxHeight; y++)
                    {
                        IntPtr dstRow = IntPtr.Add(data.Scan0, y * data.Stride);
                        Marshal.Copy(_dxBuffer, y * stride, dstRow, stride);
                    }
                }
                finally
                {
                    bmp.UnlockBits(data);
                }

                return bmp;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"DX11 capture error: {ex.Message}");
            return null;
        }
    }

    // Головний “розумний” захват (без змін логіки)
    public static (Bitmap bmp, WindowMode mode) CaptureSmart(string processName)
    {
        var proc = Process.GetProcessesByName(processName).LastOrDefault();
        if (proc == null || proc.MainWindowHandle == IntPtr.Zero)
            return (null, WindowMode.Unknown);

        var hwnd = proc.MainWindowHandle;
        var mode = DetectWindowMode(hwnd);

        return mode switch
        {
            WindowMode.FullScreenMinimized => (null, mode),
            WindowMode.FullScreen => (CaptureFullScreen(), mode),
            _ => (CaptureWindow(hwnd), mode),
        };
    }

    // ==== Capture helpers (без змін)
    public static Bitmap CaptureWindow(string processName)
    {
        IntPtr? hwndNull = Process.GetProcessesByName(processName).LastOrDefault()?.MainWindowHandle;
        if (hwndNull is not IntPtr hwnd || hwnd == IntPtr.Zero)
        {
            Console.WriteLine("Вікно не знайдено!");
            return null;
        }
        return CaptureWindow(hwnd);
    }

    public static Bitmap CaptureWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;

        if (!GetWindowRect(hwnd, out RECT rc))
            return null;

        int width = Math.Max(1, rc.Right - rc.Left);
        int height = Math.Max(1, rc.Bottom - rc.Top);

        Bitmap bmp = new(width, height, PixelFormat.Format32bppArgb);
        using Graphics gfxBmp = Graphics.FromImage(bmp);
        IntPtr hdcBitmap = gfxBmp.GetHdc();
        bool succeeded = PrintWindow(hwnd, hdcBitmap, 0);
        gfxBmp.ReleaseHdc(hdcBitmap);

        if (!succeeded)
        {
            Console.WriteLine("PrintWindow не спрацював. Можливо гра в повноекранному режимі.");
        }
        return bmp;
    }

    public static WindowMode DetectWindowMode(string processName)
    {
        IntPtr? hwndNull = Process.GetProcessesByName(processName).LastOrDefault()?.MainWindowHandle;
        if (hwndNull is not IntPtr hwnd || hwnd == IntPtr.Zero)
            return WindowMode.Unknown;
        return DetectWindowMode(hwnd);
    }

    public static WindowMode DetectWindowMode(IntPtr hwnd)
    {
        int style = GetWindowLongSafe(hwnd, GWL_STYLE);
        int ex = GetWindowLongSafe(hwnd, GWL_EXSTYLE);

        static bool Has(int val, int flag) => (val & flag) == flag;

        bool isVisible = Has(style, WS_VISIBLE);
        bool isPopup = Has(style, WS_POPUP);
        bool isMinimized = Has(style, WS_MINIMIZE);
        bool hasClipSiblings = Has(style, WS_CLIPSIBLINGS);
        bool isTopmost = Has(ex, WS_EX_TOPMOST);
        bool isAppWindow = Has(ex, WS_EX_APPWINDOW);

        if (isVisible && hasClipSiblings && isPopup && isMinimized && isAppWindow)
            return WindowMode.FullScreenMinimized;

        if (isVisible && hasClipSiblings && !isPopup && !isMinimized && isTopmost && isAppWindow)
            return WindowMode.FullScreen;

        if (isVisible && hasClipSiblings && isPopup && !isMinimized && isAppWindow && !isTopmost)
            return WindowMode.Borderless;

        if (isMinimized)
            return WindowMode.FullScreenMinimized;

        return WindowMode.Unknown;
    }

    private static void DisposeDx()
    {
        try { _dxCapture?.Dispose(); } catch { }
        try { _dxService?.Dispose(); } catch { }
        _dxZone = default!;
        _dxCapture = default!;
        _dxService = default!;
        _dxWidth = _dxHeight = 0;
        _dxBuffer = [];
    }

    // ===== Проста ініціалізація DX під розмір primary screen =====
    private static void EnsureDxForPrimaryScreen()
    {
        lock (_dxLock)
        {
            var scr = Screen.PrimaryScreen;
            var bounds = scr.Bounds;

            bool needInit =
                _dxService == null ||
                _dxZone == null ||
                _dxWidth != bounds.Width ||
                _dxHeight != bounds.Height;

            if (!needInit) return;

            DisposeDx();

            _dxService = new DX11ScreenCaptureService();

            // Вибираємо дисплей, що збігається з розміром primary screen, або перший доступний
            var cards = _dxService.GetGraphicsCards().ToArray();
            var displays = cards.SelectMany(c => _dxService.GetDisplays(c)).ToArray();
            if (displays.Length == 0)
                throw new InvalidOperationException("No DX11 displays found.");

            var display = displays
                .Where(d => d.Width == bounds.Width && d.Height == bounds.Height)
                .DefaultIfEmpty(displays.First())
                .First();

            _dxCapture = _dxService.GetScreenCapture(display);
            _dxZone = _dxCapture.RegisterCaptureZone(0, 0, display.Width, display.Height);
            _dxWidth = display.Width;
            _dxHeight = display.Height;
            _dxBuffer = new byte[_dxWidth * _dxHeight * 4];
        }
    }

    private static int GetWindowLongSafe(IntPtr hWnd, int nIndex)
    {
        long val64 = GetWindowLongPtrCompat(hWnd, nIndex).ToInt64();
        return unchecked((int)val64);
    }
}

public enum WindowMode
{
    Unknown = 0,
    Borderless,
    FullScreen,
    FullScreenMinimized
}