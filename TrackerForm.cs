using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Runtime.InteropServices;
using System.Drawing;
using System.IO;
using System.Diagnostics;
using System.Windows.Forms;

namespace WarframeTracker;

public sealed class TrackerForm : Form
{
    private readonly Panel titleBar = new() { Dock = DockStyle.Top, Height = 36, BackColor = Color.FromArgb(22, 27, 34) };
    private readonly WebView2 browser = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(13, 17, 23) };
    private readonly string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WarframeTracker", "WebView2");
    private readonly SemaphoreSlim updateLock = new(1, 1);

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hWnd, int msg, nint wParam, nint lParam);
    private const int WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 0x2;

    public TrackerForm()
    {
        Text = "Warframe Tracker";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(460, 900);
        MinimumSize = new Size(360, 500);
        BackColor = Color.FromArgb(13, 17, 23);
        BuildTitleBar();
        Controls.Add(browser);
        Controls.Add(titleBar);
        Shown += async (_, _) => await InitializeBrowserAsync();
    }


    private async Task CheckForFileUpdatesAsync(bool showNoUpdates = false)
    {
        if (!await updateLock.WaitAsync(0)) return;
        try
        {
            if (showNoUpdates) await SetFileUpdateStatusAsync("Проверка файлов GitHub…");

            var updater = new GitHubFileUpdater(AppContext.BaseDirectory);
            var updatedFiles = await updater.CheckAndApplyAsync();
            if (updatedFiles.Count == 0)
            {
                await SetFileUpdateStatusAsync("Файлы программы актуальны.");
                if (showNoUpdates)
                    MessageBox.Show("Изменённых файлов в репозитории q7vych/w-tracker не обнаружено.", "Проверка обновлений", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var list = string.Join(Environment.NewLine, updatedFiles.Select(f => "• " + f));
            await SetFileUpdateStatusAsync("Обновлено файлов: " + updatedFiles.Count + ". Требуется перезапуск.");
            var answer = MessageBox.Show(
                "Обновлены файлы приложения:" + Environment.NewLine + list + Environment.NewLine + Environment.NewLine +
                "Изменения будут применены после перезапуска. Перезапустить сейчас?",
                "Обновление Warframe Tracker", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (answer == DialogResult.Yes) Application.Restart();
        }
        catch (HttpRequestException ex)
        {
            var details = ex.StatusCode is null
                ? ex.Message
                : $"HTTP {(int)ex.StatusCode.Value} ({ex.StatusCode.Value}). {ex.Message}";
            await SetFileUpdateStatusAsync("Ошибка GitHub: " + details);
            if (showNoUpdates)
                MessageBox.Show("Не удалось проверить содержимое репозитория q7vych/w-tracker.\n\n" + details +
                    "\n\nПроверь интернет-соединение, публичный доступ к репозиторию и ветку main.",
                    "Проверка обновлений", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            await SetFileUpdateStatusAsync("Ошибка проверки: " + ex.Message);
            if (showNoUpdates)
                MessageBox.Show("Не удалось проверить обновления из q7vych/w-tracker. Программа продолжит работу с текущей версией.\n\n" +
                    ex.GetType().Name + ": " + ex.Message,
                    "Проверка обновлений", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            updateLock.Release();
        }
    }

    private async Task SetFileUpdateStatusAsync(string message)
    {
        if (browser.CoreWebView2 == null) return;
        var json = System.Text.Json.JsonSerializer.Serialize(message);
        await browser.CoreWebView2.ExecuteScriptAsync($"document.getElementById('file-update-status').textContent = {json};");
    }

    private void BuildTitleBar()
    {
        var title = new Label { Text = "WARFRAME TRACKER", ForeColor = Color.FromArgb(230, 237, 243), Font = new Font("Segoe UI", 9, FontStyle.Bold), AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill, Padding = new Padding(12, 0, 0, 0) };
        title.MouseDown += DragWindow;
        titleBar.MouseDown += DragWindow;
        var close = MakeButton("×"); close.Click += (_, _) => Close();
        var minimize = MakeButton("—"); minimize.Click += (_, _) => WindowState = FormWindowState.Minimized;
        titleBar.Controls.Add(title);
        titleBar.Controls.Add(close);
        titleBar.Controls.Add(minimize);
    }

    private static Button MakeButton(string text) => new() { Text = text, Dock = DockStyle.Right, Width = 42, FlatStyle = FlatStyle.Flat, ForeColor = Color.FromArgb(230, 237, 243), BackColor = Color.FromArgb(22, 27, 34), Font = new Font("Segoe UI", 12), TabStop = false, FlatAppearance = { BorderSize = 0 } };
    private void DragWindow(object? sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0); } }

    private async Task InitializeBrowserAsync()
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
            await browser.EnsureCoreWebView2Async(env);
            browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            browser.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("type", out var type))
                    {
                        var messageType = type.GetString();
                        if (messageType == "resize")
                        {
                            var w = Math.Clamp(root.GetProperty("width").GetInt32(), 360, 1200);
                            var h = Math.Clamp(root.GetProperty("height").GetInt32(), 500, 1600);
                            BeginInvoke(new Action(() => { ClientSize = new Size(w, h); }));
                        }
                        else if (messageType == "checkFileUpdates")
                        {
                            _ = CheckForFileUpdatesAsync(showNoUpdates: true);
                        }
                        else if (messageType == "openUrl" &&
                                 root.TryGetProperty("url", out var urlElement) &&
                                 Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var uri) &&
                                 uri.Scheme == Uri.UriSchemeHttps &&
                                 (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                                  uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
                        {
                            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                        }
                    }
                }
                catch { }
            };
            browser.CoreWebView2.SetVirtualHostNameToFolderMapping("wftracker.local", AppContext.BaseDirectory, CoreWebView2HostResourceAccessKind.Allow);
            browser.Source = new Uri("https://wftracker.local/index.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось запустить WebView2. Установи Microsoft Edge WebView2 Runtime.\n\n" + ex.Message, "Warframe Tracker", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
