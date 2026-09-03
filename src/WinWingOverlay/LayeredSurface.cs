using System.Drawing.Imaging;

namespace WinWingOverlay;

/// <summary>
/// A reusable 32-bit ARGB drawing surface presented with UpdateLayeredWindow.
///
/// The pixels live in a DIB section that GDI+ and GDI share, so a frame costs one draw pass
/// and one blit — no per-frame bitmap allocation or copy. The surface is rebuilt only when
/// the window size changes.
/// </summary>
internal sealed class LayeredSurface : IDisposable
{
    private IntPtr _screenDc;
    private IntPtr _memDc;
    private IntPtr _dib;
    private IntPtr _oldObject;
    private Bitmap? _bitmap;
    private Graphics? _graphics;
    private Size _size;

    /// <summary>Draw here, then call <see cref="Present"/>. Lives as long as the surface.</summary>
    public Graphics? Graphics => _graphics;

    /// <summary>Make sure the surface matches <paramref name="size"/>. Returns false if it could not.</summary>
    public bool Ensure(Size size)
    {
        if (size.Width <= 0 || size.Height <= 0) return false;
        if (_bitmap is not null && _size == size) return true;

        Release();

        _screenDc = Native.GetDC(IntPtr.Zero);
        if (_screenDc == IntPtr.Zero) return false;

        _memDc = Native.CreateCompatibleDC(_screenDc);
        if (_memDc == IntPtr.Zero) { Release(); return false; }

        var header = new Native.BITMAPINFOHEADER
        {
            biSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            biWidth = size.Width,
            biHeight = -size.Height,   // negative: top-down, matching GDI+ row order
            biPlanes = 1,
            biBitCount = 32,
            biCompression = Native.BI_RGB
        };

        _dib = Native.CreateDIBSection(_screenDc, ref header, Native.DIB_RGB_COLORS, out IntPtr bits,
            IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero || bits == IntPtr.Zero) { Release(); return false; }

        _oldObject = Native.SelectObject(_memDc, _dib);
        // PArgb, not Argb: UpdateLayeredWindow with AC_SRC_ALPHA consumes PREMULTIPLIED alpha.
        // Letting GDI+ write premultiplied pixels straight into the DIB avoids a conversion pass
        // and stops translucent pixels from washing out.
        _bitmap = new Bitmap(size.Width, size.Height, size.Width * 4, PixelFormat.Format32bppPArgb, bits);
        _graphics = Graphics.FromImage(_bitmap);
        _size = size;
        return true;
    }

    /// <summary>Blit the surface to the window. <paramref name="constantAlpha"/> scales every pixel.</summary>
    public void Present(IntPtr hwnd, byte constantAlpha)
    {
        if (_bitmap is null || _memDc == IntPtr.Zero) return;

        var size = new Native.SIZE(_size.Width, _size.Height);
        var source = new Native.POINT(0, 0);
        var blend = new Native.BLENDFUNCTION
        {
            BlendOp = Native.AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = constantAlpha,
            AlphaFormat = Native.AC_SRC_ALPHA
        };

        Native.UpdateLayeredWindow(hwnd, _screenDc, IntPtr.Zero, ref size, _memDc, ref source, 0,
            ref blend, Native.ULW_ALPHA);
    }

    private void Release()
    {
        _graphics?.Dispose();
        _graphics = null;
        _bitmap?.Dispose();
        _bitmap = null;

        if (_memDc != IntPtr.Zero)
        {
            if (_oldObject != IntPtr.Zero) Native.SelectObject(_memDc, _oldObject);
            Native.DeleteDC(_memDc);
            _memDc = IntPtr.Zero;
            _oldObject = IntPtr.Zero;
        }

        if (_dib != IntPtr.Zero)
        {
            Native.DeleteObject(_dib);
            _dib = IntPtr.Zero;
        }

        if (_screenDc != IntPtr.Zero)
        {
            Native.ReleaseDC(IntPtr.Zero, _screenDc);
            _screenDc = IntPtr.Zero;
        }

        _size = Size.Empty;
    }

    public void Dispose() => Release();
}
