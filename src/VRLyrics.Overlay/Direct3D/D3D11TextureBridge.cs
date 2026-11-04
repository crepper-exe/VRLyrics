using SkiaSharp;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace VRLyrics.Overlay.Direct3D;

/// <summary>
/// Manages a Direct3D 11 device and 2D shader-resource texture so that SkiaSharp RGBA8888 bitmaps
/// can be uploaded to the GPU and passed to OpenVR's <c>IVROverlay::SetOverlayTexture</c>.
/// </summary>
public sealed class D3D11TextureBridge : IDisposable
{
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Texture2D? _texture;
    private int _width;
    private int _height;
    private bool _disposed;

    public nint TextureHandle => _texture?.NativePointer ?? 0;

    public int Width => _width;

    public int Height => _height;

    public void InitializeDevice(int preferredAdapterIndex = -1)
    {
        if (_device is not null) return;

        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];

        if (preferredAdapterIndex >= 0)
        {
            try
            {
                using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
                if (factory.EnumAdapters1((uint)preferredAdapterIndex, out var adapter).Success && adapter is not null)
                {
                    using (adapter)
                    {
                        var adapterHr = D3D11.D3D11CreateDevice(
                            adapter,
                            DriverType.Unknown,
                            DeviceCreationFlags.BgraSupport,
                            levels,
                            out _device,
                            out _context);

                        if (adapterHr.Success && _device is not null)
                            return;
                    }
                }
            }
            catch
            {
                // Fall back to default hardware adapter below.
            }
        }

        var hr = D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            levels,
            out _device,
            out _context);

        if (hr.Failure || _device is null)
        {
            D3D11.D3D11CreateDevice(
                null,
                DriverType.Warp,
                DeviceCreationFlags.BgraSupport,
                levels,
                out _device,
                out _context).CheckError();
        }
    }

    /// <summary>
    /// Uploads the pixels of <paramref name="bitmap"/> (<see cref="SKColorType.Rgba8888"/>) into the D3D11 texture
    /// and returns its native <c>ID3D11Texture2D*</c> pointer for OpenVR.
    /// </summary>
    public nint UploadBitmap(SKBitmap bitmap)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        InitializeDevice();

        EnsureTexture(bitmap.Width, bitmap.Height);

        var pixels = bitmap.GetPixels();
        if (pixels == 0)
            throw new InvalidOperationException("SKBitmap has no pixel buffer.");

        _context!.UpdateSubresource(
            _texture!,
            0,
            null,
            pixels,
            (uint)bitmap.RowBytes,
            (uint)bitmap.ByteCount);

        // Flush ensures the texture update is visible to the SteamVR compositor across devices.
        _context.Flush();
        return _texture!.NativePointer;
    }

    private void EnsureTexture(int width, int height)
    {
        if (_texture is not null && _width == width && _height == height)
            return;

        _texture?.Dispose();
        _width = width;
        _height = height;

        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };

        _texture = _device!.CreateTexture2D(desc);
    }

    public void Reset()
    {
        _texture?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        _texture = null;
        _context = null;
        _device = null;
        _width = 0;
        _height = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Reset();
    }
}
