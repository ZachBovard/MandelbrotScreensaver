using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using System.Numerics;
using System.Windows.Forms;

namespace MandelbrotScreensaver;

internal static class Program
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

    private static IWindow? window;
    private static GL? gl;
    private static IInputContext? input;

    private static uint shaderProgram;
    private static uint vertexArray;

    private static int resolutionLocation;
    private static int originLocation;
    private static int timeLocation;
    private static int journeyLocation;
    private static int journeyLengthLocation;
    private static int maxIterationsLocation;
    private static int centerLocation;

    // The window covers every monitor; the fractal fills all of it.
    private static System.Drawing.Rectangle desktopBounds;

    private static double startTime;
    private static Vector2 initialMouse;
    private static bool initialMouseSet;

    [STAThread]
    private static void Main(string[] args)
    {
        // Hint for NVIDIA Optimus laptops: a program that loads NVIDIA's API
        // library before creating its OpenGL window gets the GeForce instead
        // of the Intel chip. On PCs without NVIDIA this fails quietly.
        System.Runtime.InteropServices.NativeLibrary.TryLoad("nvapi64.dll", out _);

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

        desktopBounds = SystemInformation.VirtualScreen;

        zoomTarget = ZoomTargets[Random.Shared.Next(ZoomTargets.Length)];

        WindowOptions options = WindowOptions.Default;
        options.Title = "Mandelbrot Screensaver";
        options.Size = new Vector2D<int>(desktopBounds.Width, desktopBounds.Height);
        options.Position = new Vector2D<int>(desktopBounds.X, desktopBounds.Y);
        options.WindowBorder = WindowBorder.Hidden;
        options.WindowState = WindowState.Normal;
        options.VSync = true;
        options.ShouldSwapAutomatically = true;
        options.API = new GraphicsAPI(
            ContextAPI.OpenGL,
            ContextProfile.Core,
            ContextFlags.Default,
            new APIVersion(3, 3));

        window = Window.Create(options);

        window.Load += OnLoad;
        window.Render += OnRender;
        window.Closing += OnClosing;

        window.Run();
        window.Dispose();
    }

    private static void OnLoad()
    {
        if (window is null)
            return;

        IWindow w = window;

        gl = GL.GetApi(w);
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

        const string vertexShaderSource = """
            #version 330 core

            const vec2 vertices[3] = vec2[](
                vec2(-1.0, -1.0),
                vec2( 3.0, -1.0),
                vec2(-1.0,  3.0)
            );

            void main()
            {
                gl_Position = vec4(vertices[gl_VertexID], 0.0, 1.0);
            }
            """;

        const string fragmentShaderSource = """
            #version 330 core

            out vec4 FragColor;

            uniform vec2  uResolution;     // size of the drawing area (all monitors)
            uniform vec2  uOrigin;         // its bottom-left corner in window pixels
            uniform float uTime;
            uniform float uJourney;        // 0 .. uJourneyLength, computed on CPU in double
            uniform float uJourneyLength;
            uniform int   uMaxIterations;  // grows with zoom depth
            uniform vec2  uCenter;         // zoom target, picked at random on start

            const float LOG2 = 0.69314718056;

            vec3 palette(float value)
            {
                vec3 base      = vec3(0.08, 0.07, 0.15);
                vec3 amplitude = vec3(0.48, 0.42, 0.56);
                vec3 frequency = vec3(1.00, 0.82, 0.68);
                vec3 phase     = vec3(0.10, 0.29, 0.54);

                return base + amplitude *
                    cos(6.28318530718 * (frequency * value + phase));
            }

            // Smooth (fractional) escape count, or -1.0 for points inside the set.
            float calculateMandelbrot(vec2 c)
            {
                vec2 z = vec2(0.0);

                for (int i = 0; i < uMaxIterations; i++)
                {
                    float xx = z.x * z.x;
                    float yy = z.y * z.y;

                    if (xx + yy > 256.0)
                    {
                        float logMagnitude = 0.5 * log(xx + yy);
                        float correction = log(logMagnitude / LOG2) / LOG2;
                        return float(i) + 1.0 - correction;
                    }

                    z = vec2(xx - yy + c.x, 2.0 * z.x * z.y + c.y);
                }

                return -1.0;
            }

            vec3 renderFractal(vec2 coordinate)
            {
                vec2 uv = (2.0 * coordinate - uResolution) / uResolution.y;

                float zoom = exp2(1.0 + uJourney);

                vec2 center = uCenter;

                vec2 drift = vec2(
                    sin(uTime * 0.035),
                    cos(uTime * 0.029)
                ) * (0.008 / zoom);

                vec2 fractalCoordinate = center + drift + uv * (1.7 / zoom);

                float iterations = calculateMandelbrot(fractalCoordinate);

                if (iterations < 0.0)
                    return vec3(0.0015, 0.0020, 0.0070);

                float colorPosition = iterations * 0.018 + uTime * 0.0025;

                vec3 color = mix(
                    palette(colorPosition),
                    palette(colorPosition * 0.62 + 0.25),
                    0.25);

                color *= 0.86 + 0.14 * sin(iterations * 0.27);       // fine banding
                color *= 0.92 + 0.55 * exp(-iterations * 0.025);     // edge glow

                return color;
            }

            void main()
            {
                // gl_FragCoord is relative to the whole window; uOrigin is 0
                // now that the fractal fills every monitor.
                vec2 coordinate = gl_FragCoord.xy - uOrigin;

                vec3 color = renderFractal(coordinate);

                vec2 screenPosition =
                    (2.0 * coordinate - uResolution) / uResolution.y;

                float vignette = 1.0 - 0.11 * dot(screenPosition, screenPosition);
                color *= clamp(vignette, 0.45, 1.0);

                color = color / (1.0 + color);                       // tone map
                color = pow(max(color, vec3(0.0)), vec3(0.82));

                // Fade to black at the end of each journey and back in at the
                // start, instead of snapping from deep zoom to wide view.
                float fadeIn  = smoothstep(0.0, 0.3, uJourney);
                float fadeOut = 1.0 - smoothstep(uJourneyLength - 0.3, uJourneyLength, uJourney);
                color *= fadeIn * fadeOut;

                FragColor = vec4(color, 1.0);
            }
            """;

        uint vertexShader = CompileShader(ShaderType.VertexShader, vertexShaderSource);
        uint fragmentShader = CompileShader(ShaderType.FragmentShader, fragmentShaderSource);

        shaderProgram = gl.CreateProgram();

        gl.AttachShader(shaderProgram, vertexShader);
        gl.AttachShader(shaderProgram, fragmentShader);
        gl.LinkProgram(shaderProgram);

        gl.GetProgram(shaderProgram, ProgramPropertyARB.LinkStatus, out int linkStatus);

        if (linkStatus == 0)
        {
            throw new Exception(
                "Shader link error:\n" + gl.GetProgramInfoLog(shaderProgram));
        }

        gl.DetachShader(shaderProgram, vertexShader);
        gl.DetachShader(shaderProgram, fragmentShader);
        gl.DeleteShader(vertexShader);
        gl.DeleteShader(fragmentShader);

        // Look these up once instead of every frame.
        resolutionLocation    = gl.GetUniformLocation(shaderProgram, "uResolution");
        originLocation        = gl.GetUniformLocation(shaderProgram, "uOrigin");
        timeLocation          = gl.GetUniformLocation(shaderProgram, "uTime");
        journeyLocation       = gl.GetUniformLocation(shaderProgram, "uJourney");
        journeyLengthLocation = gl.GetUniformLocation(shaderProgram, "uJourneyLength");
        maxIterationsLocation = gl.GetUniformLocation(shaderProgram, "uMaxIterations");
        centerLocation        = gl.GetUniformLocation(shaderProgram, "uCenter");

        vertexArray = gl.GenVertexArray();
        gl.BindVertexArray(vertexArray);

        gl.ClearColor(0f, 0f, 0f, 1f);
    }

    private static void OnRender(double deltaTime)
    {
        if (window is null || gl is null || shaderProgram == 0)
            return;

        Vector2D<int> framebuffer = window.FramebufferSize;

        if (framebuffer.X <= 0 || framebuffer.Y <= 0)
            return;

        // Draw across the whole window, which spans every monitor.
        int areaX = 0;
        int areaY = 0;
        int areaWidth = framebuffer.X;
        int areaHeight = framebuffer.Y;

        double elapsed = window.Time - startTime;

        // Wrap in double on the CPU so the zoom stays smooth no matter how
        // long the screensaver has been running.
        double journey = (elapsed * ZoomSpeed) % JourneyLength;

        // Deeper zoom needs more iterations to keep edges detailed:
        // ~300 at the start, ~1500 at the deepest point.
        int maxIterations = 300 + (int)(journey * 100.0);

        gl.Viewport(areaX, areaY, (uint)areaWidth, (uint)areaHeight);

        gl.UseProgram(shaderProgram);
        gl.BindVertexArray(vertexArray);

        gl.Uniform2(resolutionLocation, (float)areaWidth, (float)areaHeight);
        gl.Uniform2(originLocation, (float)areaX, (float)areaY);
        gl.Uniform1(timeLocation, (float)elapsed);
        gl.Uniform1(journeyLocation, (float)journey);
        gl.Uniform1(journeyLengthLocation, (float)JourneyLength);
        gl.Uniform1(maxIterationsLocation, maxIterations);
        gl.Uniform2(centerLocation, (float)zoomTarget.X, (float)zoomTarget.Y);

        gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
    }

    private static uint CompileShader(ShaderType type, string source)
    {
        if (gl is null)
            throw new InvalidOperationException();

        uint shader = gl.CreateShader(type);

        gl.ShaderSource(shader, source);
        gl.CompileShader(shader);

        gl.GetShader(shader, ShaderParameterName.CompileStatus, out int compileStatus);

        if (compileStatus == 0)
        {
            string log = gl.GetShaderInfoLog(shader);
            gl.DeleteShader(shader);

            throw new Exception("Shader compilation error:\n" + log);
        }

        return shader;
    }

    private static void OnClosing()
    {
        if (gl is not null)
        {
            if (vertexArray != 0)
                gl.DeleteVertexArray(vertexArray);

            if (shaderProgram != 0)
                gl.DeleteProgram(shaderProgram);
        }

        input?.Dispose();
    }
}
