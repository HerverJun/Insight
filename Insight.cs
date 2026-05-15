using System.Reflection;
using System.Runtime.InteropServices;
using Insight.Bridge;
using Insight.Handlers;
using Insight.Services;
using Microsoft.Web.WebView2.Core;

namespace Insight
{
    public partial class Insight : Form
    {
        private readonly CancellationTokenSource _lifetimeCts = new();
        private WebViewMessageDispatcher? _dispatcher;
        private InsightApplication? _application;

        public const int WM_NCLBUTTONDOWN = 0xA1;
        public const int HT_CAPTION = 0x2;

        [DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        public Insight()
        {
            InitializeComponent();

            FormBorderStyle = FormBorderStyle.Sizable;
            Text = "Insight";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new System.Drawing.Size(1280, 800);
            WindowState = FormWindowState.Maximized;
            FormClosed += (_, _) => DisposeApplicationServices();

            InitializeWebView();
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        private static bool UseImmersiveDarkMode(IntPtr handle, bool enabled)
        {
            if (IsWindows10OrGreater(17763))
            {
                var attribute = DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1;
                if (IsWindows10OrGreater(18985))
                {
                    attribute = DWMWA_USE_IMMERSIVE_DARK_MODE;
                }

                int useImmersiveDarkMode = enabled ? 1 : 0;
                return DwmSetWindowAttribute(handle, attribute, ref useImmersiveDarkMode, sizeof(int)) == 0;
            }

            return false;
        }

        private static bool IsWindows10OrGreater(int build = -1)
        {
            return Environment.OSVersion.Version.Major >= 10 && Environment.OSVersion.Version.Build >= build;
        }

        private async void InitializeWebView()
        {
            try
            {
                var userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Insight", "WebView2");

                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await webView21.EnsureCoreWebView2Async(env);

                webView21.CoreWebView2.Settings.IsZoomControlEnabled = false;
                webView21.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;

                ConfigureMessageBridge();
                webView21.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

                var html = GetEmbeddedResource("index.html");
                webView21.CoreWebView2.NavigateToString(html);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"WebView2 初始化失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ConfigureMessageBridge()
        {
            var ui = new WinFormsUiDispatcher(this);
            var messenger = new WebViewFrontendMessenger(ui, () => webView21.CoreWebView2);
            var dialogs = new WinFormsDialogService(this, ui);
            _application = new InsightApplication(messenger, ui, dialogs);
            _dispatcher = WebViewCompositionRoot.CreateDispatcher(_application, messenger);
        }

        private async void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (_dispatcher == null) return;
            await _dispatcher.DispatchAsync(e.TryGetWebMessageAsString(), _lifetimeCts.Token);
        }

        private string GetEmbeddedResource(string fileName)
        {
#if DEBUG
            var debugPath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", fileName));
            if (File.Exists(debugPath))
            {
                return File.ReadAllText(debugPath);
            }
#endif
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = $"Insight.{fileName}";

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }

            var filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
            if (File.Exists(filePath))
            {
                return File.ReadAllText(filePath);
            }

            throw new FileNotFoundException($"Embedded resource '{resourceName}' or file '{filePath}' not found.");
        }

        private void DisposeApplicationServices()
        {
            _lifetimeCts.Cancel();
            _application?.Dispose();
            _application = null;
            _lifetimeCts.Dispose();
        }
    }
}
