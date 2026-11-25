using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PrtShot
{
    internal static class DxgiCapture
    {
        private static readonly Guid Factory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        private static readonly Guid Output1 = new Guid("00cddea8-939b-4b83-a340-a685226666cc");
        private static readonly Guid Texture2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        private const int NotFound = unchecked((int)0x887A0002);
        private const int AccessLost = unchecked((int)0x887A0026);
        private const int WaitTimeout = unchecked((int)0x887A0027);
        private const uint Bgra8Unorm = 87; // DXGI_FORMAT_B8G8R8A8_UNORM

        public static Bitmap Take(Screen screen)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try { return TakeOutput(screen.DeviceName); }
                catch (ExternalException error)
                {
                    if (error.ErrorCode != AccessLost)
                        return null;
                }
            }
            return null;
        }

        private static Bitmap TakeOutput(string deviceName)
        {
            IntPtr factoryPointer;
            Guid id = Factory1;
            Check(CreateDXGIFactory1(ref id, out factoryPointer));
            using (var factory = new ComPointer(factoryPointer))
            {
                // IDXGIFactory1::EnumAdapters1 and IDXGIAdapter::EnumOutputs.
                var enumAdapters = factory.Method<Enumerate>(12);
                for (uint index = 0; ; index++)
                {
                    IntPtr adapterPointer;
                    int result = enumAdapters(factory.Handle, index, out adapterPointer);
                    if (result == NotFound)
                        return null;
                    Check(result);
                    using (var adapter = new ComPointer(adapterPointer))
                    {
                        var enumOutputs = adapter.Method<Enumerate>(7);
                        for (uint outputIndex = 0; ; outputIndex++)
                        {
                            IntPtr outputPointer;
                            result = enumOutputs(adapter.Handle, outputIndex, out outputPointer);
                            if (result == NotFound)
                                break;
                            Check(result);
                            using (var output = new ComPointer(outputPointer))
                            {
                                OutputDescription description;
                                Check(output.Method<GetOutputDesc>(7)(output.Handle, out description));
                                if (description.Attached && description.DeviceName == deviceName)
                                    return Duplicate(adapter, output, description.Rotation);
                            }
                        }
                    }
                }
            }
        }

        private static Bitmap Duplicate(ComPointer adapter, ComPointer output, int rotation)
        {
            IntPtr devicePointer, contextPointer;
            Check(D3D11CreateDevice(adapter.Handle, 0, IntPtr.Zero, 0, IntPtr.Zero, 0,
                7, out devicePointer, IntPtr.Zero, out contextPointer));
            using (var device = new ComPointer(devicePointer))
            using (var context = new ComPointer(contextPointer))
            using (var output1 = output.Query(Output1))
            {
                IntPtr duplicationPointer;
                // IDXGIOutput1::DuplicateOutput.
                Check(output1.Method<DuplicateOutput>(22)(output1.Handle, device.Handle, out duplicationPointer));
                using (var duplication = new ComPointer(duplicationPointer))
                {
                    // A pointer-only update can arrive before the first image.
                    var acquire = duplication.Method<AcquireFrame>(8);
                    var release = duplication.Method<ReleaseFrame>(14);
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        IntPtr surfacePointer;
                        FrameInfo info;
                        int result = acquire(duplication.Handle, 140, out info, out surfacePointer);
                        if (result == WaitTimeout)
                            return null;
                        Check(result);
                        try
                        {
                            if (surfacePointer == IntPtr.Zero)
                                return null;

                            using (var surface = new ComPointer(surfacePointer))
                            {
                                if (info.LastPresentTime == 0)
                                    continue;
                                using (var texture = surface.Query(Texture2D))
                                    return ReadTexture(device, context, texture, rotation);
                            }
                        }
                        finally { release(duplication.Handle); }
                    }
                    return null;
                }
            }
        }

        private static Bitmap ReadTexture(ComPointer device, ComPointer context, ComPointer texture, int rotation)
        {
            TextureDescription description;
            texture.Method<GetTextureDesc>(10)(texture.Handle, out description);
            if (description.Format != Bgra8Unorm)
                return null;

            description.Usage = 3;        // D3D11_USAGE_STAGING
            description.BindFlags = 0;
            description.CpuAccess = 0x20000;  // D3D11_CPU_ACCESS_READ
            description.MiscFlags = 0;

            IntPtr stagingPointer;
            // ID3D11Device::CreateTexture2D.
            Check(device.Method<CreateTexture>(5)(device.Handle, ref description, IntPtr.Zero, out stagingPointer));
            using (var staging = new ComPointer(stagingPointer))
            {
                // ID3D11DeviceContext::CopyResource, Map and Unmap.
                context.Method<CopyResource>(47)(context.Handle, staging.Handle, texture.Handle);
                MappedResource mapped;
                Check(context.Method<MapResource>(14)(context.Handle, staging.Handle, 0, 1, 0, out mapped));
                try
                {
                    return CopyPixels(mapped, checked((int)description.Width), checked((int)description.Height), rotation);
                }
                finally { context.Method<UnmapResource>(15)(context.Handle, staging.Handle, 0); }
            }
        }

        private static Bitmap CopyPixels(MappedResource mapped, int width, int height, int rotation)
        {
            if (mapped.Data == IntPtr.Zero || width <= 0 || height <= 0 || width > int.MaxValue / 4)
                return null;

            int rowBytes = width * 4;
            if (mapped.RowPitch < rowBytes || mapped.RowPitch > int.MaxValue ||
                (height - 1L) * mapped.RowPitch > int.MaxValue - rowBytes)
                return null;

            int rowPitch = (int)mapped.RowPitch;
            var image = new Bitmap(width, height, PixelFormat.Format32bppRgb);
            try
            {
                BitmapData pixels = image.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, image.PixelFormat);
                try
                {
                    long stride = Math.Abs((long)pixels.Stride);
                    if (pixels.Scan0 == IntPtr.Zero || stride < rowBytes ||
                        (height - 1L) * stride > int.MaxValue - rowBytes)
                        throw new ExternalException("Invalid bitmap stride for DXGI capture.");

                    var row = new byte[rowBytes];
                    for (int y = 0; y < height; y++)
                    {
                        Marshal.Copy(IntPtr.Add(mapped.Data, y * rowPitch), row, 0, row.Length);
                        Marshal.Copy(row, 0, IntPtr.Add(pixels.Scan0, y * pixels.Stride), row.Length);
                    }
                }
                finally { image.UnlockBits(pixels); }
                if (rotation == 2) image.RotateFlip(RotateFlipType.Rotate90FlipNone);
                if (rotation == 3) image.RotateFlip(RotateFlipType.Rotate180FlipNone);
                if (rotation == 4) image.RotateFlip(RotateFlipType.Rotate270FlipNone);
                return image;
            }
            catch
            {
                image.Dispose();
                throw;
            }
        }

        private static void Check(int result)
        {
            if (result < 0)
                throw new COMException("DXGI capture failed.", result);
        }

        private sealed class ComPointer : IDisposable
        {
            public IntPtr Handle { get; private set; }

            public ComPointer(IntPtr handle)
            {
                Handle = handle;
            }

            public T Method<T>(int index) where T : class
            {
                IntPtr slot = Marshal.ReadIntPtr(Marshal.ReadIntPtr(Handle), index * IntPtr.Size);
                return (T)(object)Marshal.GetDelegateForFunctionPointer(slot, typeof(T));
            }

            public ComPointer Query(Guid id)
            {
                IntPtr value;
                Check(Marshal.QueryInterface(Handle, ref id, out value));
                return new ComPointer(value);
            }

            public void Dispose()
            {
                if (Handle != IntPtr.Zero)
                {
                    Marshal.Release(Handle);
                    Handle = IntPtr.Zero;
                }
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OutputDescription
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public int Left, Top, Right, Bottom;
            [MarshalAs(UnmanagedType.Bool)] public bool Attached;
            public int Rotation;
            public IntPtr Monitor;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FrameInfo
        {
            public long LastPresentTime, LastMouseUpdateTime;
            public uint AccumulatedFrames, RectanglesCoalesced, ProtectedContentMaskedOut;
            public int PointerX, PointerY;
            public uint PointerVisible, TotalMetadataBufferSize, PointerShapeBufferSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TextureDescription
        {
            public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality;
            public uint Usage, BindFlags, CpuAccess, MiscFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MappedResource
        {
            public IntPtr Data;
            public uint RowPitch, DepthPitch;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Enumerate(IntPtr instance, uint index, out IntPtr pointer);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetOutputDesc(IntPtr instance, out OutputDescription description);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DuplicateOutput(IntPtr instance, IntPtr device, out IntPtr output);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int AcquireFrame(IntPtr instance, uint timeout, out FrameInfo info, out IntPtr surface);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ReleaseFrame(IntPtr instance);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void GetTextureDesc(IntPtr instance, out TextureDescription description);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateTexture(IntPtr instance, ref TextureDescription description, IntPtr initialData, out IntPtr resource);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void CopyResource(IntPtr instance, IntPtr destination, IntPtr source);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int MapResource(IntPtr instance, IntPtr resource, uint index, uint mapType, uint flags, out MappedResource mapped);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void UnmapResource(IntPtr instance, IntPtr resource, uint index);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("dxgi.dll")]
        private static extern int CreateDXGIFactory1(ref Guid id, out IntPtr factory);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("d3d11.dll")]
        private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
            IntPtr featureLevels, uint levelCount, uint sdkVersion, out IntPtr device, IntPtr selectedFeatureLevel, out IntPtr context);
    }
}
