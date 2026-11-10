using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace MandelbrotScreensaver;

internal static unsafe class Program
{
    // Zoom speed in "doublings" per second. One journey = JourneyLength doublings
    // (12 / 0.055 ≈ 218 s ≈ 3.6 minutes), then it fades out and starts again.
    private const double ZoomSpeed = 0.055;
    private const double JourneyLength = 12.0;

    // Known detailed spots on the edge of the set. One is picked at random
    // each time the screensaver starts. (A truly random point is usually
    // plain black or plain color, so it would be a boring zoom.)
    private static readonly Vector2D<double>[] ZoomTargets =
    [
        new(-0.743643887037151,  0.131825904205330),   // seahorse valley
        new(-0.77568377,         0.13646737),          // seahorse spiral
        new(-0.16070135,         1.0375665),           // top spiral
        new(-0.10109636384562,   0.95628651080914),    // dendrite branch
        new(-1.25066,            0.02012),             // left-side spiral
        new( 0.001643721971153, -0.822467633298876),   // lower filaments
        new( 0.3602404434376143,-0.6413130610648031),  // right-side spirals
    ];

    private static Vector2D<double> zoomTarget;

    // Ignore mouse jitter for the first second, then exit on a 15 px move.
    private const double MouseGraceSeconds = 1.0;
    private const float MouseMoveThreshold = 15f;

    // Must match the cbuffer in the shader (two 16-byte rows).
    [StructLayout(LayoutKind.Sequential)]
    private struct FractalParams
    {
        public Vector2 Resolution;
        public float Time;
        public float Journey;

        public float JourneyLength;
        public int MaxIterations;
        public Vector2 Center;
    }

    private static IWindow? window;
    private static IInputContext? input;

    private static DXGI? dxgi;
    private static D3D11? d3d11;
    private static D3DCompiler? compiler;

    private static ComPtr<IDXGIFactory6> factory = default;
    private static ComPtr<IDXGIAdapter> adapter = default;
    private static ComPtr<ID3D11Device> device = default;
    private static ComPtr<ID3D11DeviceContext> deviceContext = default;
    private static ComPtr<IDXGISwapChain1> swapchain = default;
    private static ComPtr<ID3D11VertexShader> vertexShader = default;
    private static ComPtr<ID3D11PixelShader> pixelShader = default;
    private static ComPtr<ID3D11Buffer> constantBuffer = default;

    private static readonly float[] Black = [0f, 0f, 0f, 1f];

    private static double startTime;
    private static Vector2 initialMouse;
    private static bool initialMouseSet;

    [STAThread]
    private static void Main(string[] args)
    {
        // Must run before reading screen sizes, or the window won't cover
        // the whole desktop when Windows display scaling is above 100%.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        string option = args.Length > 0
            ? args[0].ToLowerInvariant()
            : "/s";

        if (option.StartsWith("/c"))
        {
            MessageBox.Show(
                "GPU Mandelbrot Screensaver\n\n" +
                "Slow cinematic zoom with animated colors.",
                "Mandelbrot Screensaver",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            return;
        }

        // /p is the little preview in Screen Saver Settings. Anything other
        // than /s must not open a full-screen window.
        if (!option.StartsWith("/s"))
            return;

        System.Drawing.Rectangle desktop = SystemInformation.VirtualScreen;

        zoomTarget = ZoomTargets[Random.Shared.Next(ZoomTargets.Length)];

        WindowOptions options = WindowOptions.Default;
        options.Title = "Mandelbrot Screensaver";
        options.Size = new Vector2D<int>(desktop.Width, desktop.Height);
        options.Position = new Vector2D<int>(desktop.X, desktop.Y);
        options.WindowBorder = WindowBorder.Hidden;
        options.WindowState = WindowState.Normal;
        options.API = GraphicsAPI.None;   // Direct3D 11, not OpenGL

        window = Window.Create(options);

        window.Load += OnLoad;
        window.Render += OnRender;
        window.FramebufferResize += OnFramebufferResize;
        window.Closing += OnClosing;

        window.Run();
        window.Dispose();
    }

    private static void OnLoad()
    {
        if (window is null)
            return;

        IWindow w = window;

        startTime = w.Time;

        input = w.CreateInput();

        foreach (IKeyboard keyboard in input.Keyboards)
        {
            keyboard.KeyDown += (_, _, _) => w.Close();
        }

        foreach (IMouse mouse in input.Mice)
        {
            mouse.Cursor.CursorMode = CursorMode.Hidden;
            mouse.MouseDown += (_, _) => w.Close();
            mouse.Scroll += (_, _) => w.Close();

            mouse.MouseMove += (_, position) =>
            {
                // During the grace period keep re-anchoring, so the
                // jitter from the screensaver starting is absorbed.
                if (!initialMouseSet ||
                    w.Time - startTime < MouseGraceSeconds)
                {
                    initialMouse = position;
                    initialMouseSet = true;
                    return;
                }

                if (Vector2.DistanceSquared(position, initialMouse) >
                    MouseMoveThreshold * MouseMoveThreshold)
                {
                    w.Close();
                }
            };
        }

        dxgi = DXGI.GetApi(w, false);
        d3d11 = D3D11.GetApi(w, false);
        compiler = D3DCompiler.GetApi();

        // Ask Windows for the high-performance GPU (the GeForce on an
        // Optimus laptop) and build the device on it directly. This is
        // the step OpenGL could not do.
        factory = dxgi.CreateDXGIFactory1<IDXGIFactory6>();

        int adapterResult = factory.EnumAdapterByGpuPreference(
            0, GpuPreference.HighPerformance, out adapter);

        if (adapterResult >= 0)
        {
            SilkMarshal.ThrowHResult(
                d3d11.CreateDevice(
                    adapter,
                    D3DDriverType.Unknown,      // required when an adapter is given
                    default,
                    0,
                    null,
                    0,
                    D3D11.SdkVersion,
                    ref device,
                    null,
                    ref deviceContext));
        }
        else
        {
            // No adapter list available: let Windows pick.
            SilkMarshal.ThrowHResult(
                d3d11.CreateDevice(
                    default(ComPtr<IDXGIAdapter>),
                    D3DDriverType.Hardware,
                    default,
                    0,
                    null,
                    0,
                    D3D11.SdkVersion,
                    ref device,
                    null,
                    ref deviceContext));
        }

        var swapChainDesc = new SwapChainDesc1
        {
            BufferCount = 2,
            Format = Format.FormatB8G8R8A8Unorm,
            BufferUsage = DXGI.UsageRenderTargetOutput,
            SwapEffect = SwapEffect.Discard,   // blit model, like OpenGL; flip model stuttered across mixed monitors
            SampleDesc = new SampleDesc(1, 0)
        };

        SilkMarshal.ThrowHResult(
            factory.CreateSwapChainForHwnd(
                device,
                w.Native!.DXHandle!.Value,
                in swapChainDesc,
                null,
                ref Unsafe.NullRef<IDXGIOutput>(),
                ref swapchain));

        const string shaderSource = """
            cbuffer FractalParams : register(b0)
            {
                float2 uResolution;     // size of the drawing area (all monitors)
                float  uTime;
                float  uJourney;        // 0 .. uJourneyLength, computed on CPU in double
                float  uJourneyLength;
                int    uMaxIterations;  // grows with zoom depth
                float2 uCenter;         // zoom target, picked at random on start
            };

            static const float LOG2 = 0.69314718056;

            // One triangle that covers the whole screen.
            float4 vs_main(uint id : SV_VertexID) : SV_POSITION
            {
                float2 uv = float2((id << 1) & 2, id & 2);
                return float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
            }

            float3 palette(float value)
            {
                float3 baseColor = float3(0.08, 0.07, 0.15);
                float3 amplitude = float3(0.48, 0.42, 0.56);
                float3 frequency = float3(1.00, 0.82, 0.68);
                float3 phase     = float3(0.10, 0.29, 0.54);

                return baseColor + amplitude *
                    cos(6.28318530718 * (frequency * value + phase));
            }

            // Smooth (fractional) escape count, or -1.0 for points inside the set.
            float calculateMandelbrot(float2 c)
            {
                float2 z = float2(0.0, 0.0);

                [loop]
                for (int i = 0; i < uMaxIterations; i++)
                {
                    float xx = z.x * z.x;
                    float yy = z.y * z.y;

                    if (xx + yy > 256.0)
                    {
                        float logMagnitude = 0.5 * log(xx + yy);
                        float correction = log(logMagnitude / LOG2) / LOG2;
                        return (float)i + 1.0 - correction;
                    }

                    z = float2(xx - yy + c.x, 2.0 * z.x * z.y + c.y);
                }

                return -1.0;
            }

            float3 renderFractal(float2 coordinate)
            {
                float2 uv = (2.0 * coordinate - uResolution) / uResolution.y;

                float zoom = exp2(1.0 + uJourney);

                float2 drift = float2(
                    sin(uTime * 0.035),
                    cos(uTime * 0.029)
                ) * (0.008 / zoom);

                float2 fractalCoordinate = uCenter + drift + uv * (1.7 / zoom);

                float iterations = calculateMandelbrot(fractalCoordinate);

                if (iterations < 0.0)
                    return float3(0.0015, 0.0020, 0.0070);

                float colorPosition = iterations * 0.018 + uTime * 0.0025;

                float3 color = lerp(
                    palette(colorPosition),
                    palette(colorPosition * 0.62 + 0.25),
                    0.25);

                color *= 0.86 + 0.14 * sin(iterations * 0.27);       // fine banding
                color *= 0.92 + 0.55 * exp(-iterations * 0.025);     // edge glow

                return color;
            }

            float4 ps_main(float4 position : SV_POSITION) : SV_TARGET
            {
                // Direct3D counts pixels from the top; flip so the image
                // matches the OpenGL version (counted from the bottom).
                float2 coordinate = float2(position.x, uResolution.y - position.y);

                float3 color = renderFractal(coordinate);

                float2 screenPosition =
                    (2.0 * coordinate - uResolution) / uResolution.y;

                float vignette = 1.0 - 0.11 * dot(screenPosition, screenPosition);
                color *= clamp(vignette, 0.45, 1.0);

                color = color / (1.0 + color);                       // tone map
                color = pow(max(color, (float3)0.0), (float3)0.82);

                // Fade to black at the end of each journey and back in at the
                // start, instead of snapping from deep zoom to wide view.
                float fadeIn  = smoothstep(0.0, 0.3, uJourney);
                float fadeOut = 1.0 - smoothstep(uJourneyLength - 0.3, uJourneyLength, uJourney);
                color *= fadeIn * fadeOut;

                return float4(color, 1.0);
            }
            """;

        byte[] shaderBytes = Encoding.ASCII.GetBytes(shaderSource);

        ComPtr<ID3D10Blob> vertexCode = CompileShader(shaderBytes, "vs_main", "vs_5_0");
        ComPtr<ID3D10Blob> pixelCode = CompileShader(shaderBytes, "ps_main", "ps_5_0");

        SilkMarshal.ThrowHResult(
            device.CreateVertexShader(
                vertexCode.GetBufferPointer(),
                vertexCode.GetBufferSize(),
                ref Unsafe.NullRef<ID3D11ClassLinkage>(),
                ref vertexShader));

        SilkMarshal.ThrowHResult(
            device.CreatePixelShader(
                pixelCode.GetBufferPointer(),
                pixelCode.GetBufferSize(),
                ref Unsafe.NullRef<ID3D11ClassLinkage>(),
                ref pixelShader));

        vertexCode.Dispose();
        pixelCode.Dispose();

        var bufferDesc = new BufferDesc
        {
            ByteWidth = (uint)sizeof(FractalParams),
            Usage = Usage.Default,
            BindFlags = (uint)BindFlag.ConstantBuffer
        };

        SilkMarshal.ThrowHResult(
            device.CreateBuffer(in bufferDesc, null, ref constantBuffer));
    }

    private static ComPtr<ID3D10Blob> CompileShader(byte[] source, string entryPoint, string target)
    {
        if (compiler is null)
            throw new InvalidOperationException();

        ComPtr<ID3D10Blob> code = default;
        ComPtr<ID3D10Blob> errors = default;

        HResult hr = compiler.Compile(
            in source[0],
            (nuint)source.Length,
            "MandelbrotShader",
            null,
            ref Unsafe.NullRef<ID3DInclude>(),
            entryPoint,
            target,
            0,
            0,
            ref code,
            ref errors);

        if (hr.IsFailure)
        {
            string message = errors.Handle is not null
                ? SilkMarshal.PtrToString((nint)errors.GetBufferPointer()) ?? ""
                : "";

            errors.Dispose();
            throw new Exception($"Shader compilation error ({entryPoint}):\n{message}");
        }

        errors.Dispose();
        return code;
    }

    private static void OnRender(double deltaTime)
    {
        if (window is null || swapchain.Handle is null)
            return;

        Vector2D<int> size = window.FramebufferSize;

        if (size.X <= 0 || size.Y <= 0)
            return;

        double elapsed = window.Time - startTime;

        // Wrap in double on the CPU so the zoom stays smooth no matter how
        // long the screensaver has been running.
        double journey = (elapsed * ZoomSpeed) % JourneyLength;

        // Deeper zoom needs more iterations to keep edges detailed:
        // ~300 at the start, ~1500 at the deepest point.
        int maxIterations = 300 + (int)(journey * 100.0);

        var parameters = new FractalParams
        {
            Resolution = new Vector2(size.X, size.Y),
            Time = (float)elapsed,
            Journey = (float)journey,
            JourneyLength = (float)JourneyLength,
            MaxIterations = maxIterations,
            Center = new Vector2((float)zoomTarget.X, (float)zoomTarget.Y)
        };

        deviceContext.UpdateSubresource(constantBuffer, 0, (Box*)null, &parameters, 0, 0);

        using var backBuffer = swapchain.GetBuffer<ID3D11Texture2D>(0);

        ComPtr<ID3D11RenderTargetView> renderTargetView = default;
        SilkMarshal.ThrowHResult(
            device.CreateRenderTargetView(backBuffer, null, ref renderTargetView));

        deviceContext.ClearRenderTargetView(renderTargetView, ref Black[0]);

        var viewport = new Viewport(0, 0, size.X, size.Y, 0, 1);
        deviceContext.RSSetViewports(1, in viewport);

        deviceContext.OMSetRenderTargets(
            1, ref renderTargetView, ref Unsafe.NullRef<ID3D11DepthStencilView>());

        deviceContext.IASetPrimitiveTopology(
            D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);

        deviceContext.VSSetShader(
            vertexShader, ref Unsafe.NullRef<ComPtr<ID3D11ClassInstance>>(), 0);
        deviceContext.PSSetShader(
            pixelShader, ref Unsafe.NullRef<ComPtr<ID3D11ClassInstance>>(), 0);
        deviceContext.PSSetConstantBuffers(0, 1, ref constantBuffer);

        deviceContext.Draw(3, 0);

        // 1 = wait for vsync.
        swapchain.Present(1, 0);

        renderTargetView.Dispose();
    }

    private static void OnFramebufferResize(Vector2D<int> size)
    {
        if (swapchain.Handle is null || size.X <= 0 || size.Y <= 0)
            return;

        // The back buffer can't be resized while it is still bound.
        deviceContext.ClearState();

        swapchain.ResizeBuffers(
            0, (uint)size.X, (uint)size.Y, Format.FormatB8G8R8A8Unorm, 0);
    }

    private static void OnClosing()
    {
        constantBuffer.Dispose();
        pixelShader.Dispose();
        vertexShader.Dispose();
        swapchain.Dispose();
        deviceContext.Dispose();
        device.Dispose();
        adapter.Dispose();
        factory.Dispose();

        compiler?.Dispose();
        d3d11?.Dispose();
        dxgi?.Dispose();

        input?.Dispose();
    }
}
