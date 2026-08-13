using Microsoft.Web.WebView2.WinForms;
using VrcCounter.Models;
using VrcCounter.Services;

namespace VrcCounter.UI;

public sealed class MainForm : Form
{
    private readonly AppState _state; private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private System.Windows.Forms.Timer? _resizeTimer;

    public MainForm(AppState state, AppConfig config, string url)
    {
        _state = state; Text = config.WebviewTitle; Width = config.LastWindowWidth ?? config.WebviewWidth; Height = config.LastWindowHeight ?? config.WebviewHeight;
        MinimumSize = new Size(config.WebviewMinWidth, config.WebviewMinHeight); FormBorderStyle = config.WebviewFrameless ? FormBorderStyle.None : FormBorderStyle.Sizable;
        Controls.Add(_web); Shown += async (_, _) => await InitializeAsync(url);
        if (config.RememberWindowSize)
        {
            _resizeTimer = new System.Windows.Forms.Timer { Interval = 500 };
            _resizeTimer.Tick += (_, _) => { _resizeTimer.Stop(); if (WindowState == FormWindowState.Normal) _state.SetWindowSize(Width, Height); };
            ResizeEnd += (_, _) => { _resizeTimer.Stop(); _resizeTimer.Start(); };
        }
    }

    private async Task InitializeAsync(string url)
    {
        try { await _web.EnsureCoreWebView2Async(); _web.CoreWebView2.Navigate(url); }
        catch (Exception ex) { MessageBox.Show($"WebView2 could not open.\n\n{ex.Message}", "VRChat Counter", MessageBoxButtons.OK, MessageBoxIcon.Error); Close(); }
    }
    protected override void Dispose(bool disposing) { if (disposing) { _resizeTimer?.Dispose(); _web.Dispose(); } base.Dispose(disposing); }
}
