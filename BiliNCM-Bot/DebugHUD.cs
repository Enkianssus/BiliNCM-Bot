using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Threading;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;
using System.Drawing.Text;
using System.Linq;

public class DebugHUD : Form
{
    private static DebugHUD _instance;
    private static readonly ManualResetEventSlim _readyEvent = new ManualResetEventSlim(false);
    
    private QueuePanel _displayPanel; 
    private ContextMenuStrip _menu;
    private BackgroundForm _bgForm; 
    private const int _borderSize = 10;
    private bool _isSyncing = false; 
    
    private HUDConfig _config = new HUDConfig();
    private string _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hud_settings.json");

    #region Win32 API
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_LAYERED = 0x80000;
    private const int WM_EXITSIZEMOVE = 0x0232; 
    #endregion

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED; 
            return cp;
        }
    }

    public class HUDConfig
    {
        public bool IsFullyTransparent { get; set; } = false; 
        public bool ClickThrough { get; set; } = false;
        public float FontSize { get; set; } = 10f;
        public double OpacityValue { get; set; } = 0.5;
        public int Width { get; set; } = 400;
        public int Height { get; set; } = 350;
        public int X { get; set; } = 100;
        public int Y { get; set; } = 100;
    }

    private class QueuePanel : Panel
    {
        private DebugHUD _parent;
        private List<string> _queueItems = new List<string>();
        private string _statusText = "等待点播...";
        private Color _statusColor = Color.Cyan;
        private readonly object _lock = new object();

        public QueuePanel(DebugHUD parent)
        {
            _parent = parent;
            this.DoubleBuffered = true;
            this.BackColor = Color.Black;
        }

        public void UpdateData(List<string> items, string status, Color? statusColor)
        {
            lock (_lock)
            {
                _queueItems = items ?? new List<string>();
                if (status != null)
                {
                    _statusText = status;
                    _statusColor = statusColor ?? Color.White;
                }
            }
            if (this.IsHandleCreated) this.BeginInvoke(new Action(() => this.Invalidate()));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // --- 核心修复：完美文字边缘方案 ---
            // 使用 SingleBitPerPixelGridFit 彻底禁用边缘平滑
            // 这样就不会有任何半透明像素与背景色混合，从而彻底消除黑边
            e.Graphics.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
            
            // 线条类依然可以使用抗锯齿，因为它们通常画在非透明区域或颜色较深
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            
            float padding = 10;
            float currentY = padding;

            // 推荐使用矢量清晰度高的字体，如 Cascadia Code 或 微软雅黑
            using (Font titleFont = new Font("Microsoft YaHei", _parent._config.FontSize + 2, FontStyle.Bold))
            using (Font itemFont = new Font("Cascadia Code", _parent._config.FontSize, FontStyle.Regular))
            using (Font statusFont = new Font("Microsoft YaHei", _parent._config.FontSize, FontStyle.Italic))
            {
                lock (_lock)
                {
                    // 1. 绘制标题
                    e.Graphics.DrawString("待播歌单 ──", titleFont, Brushes.Gray, padding, currentY);
                    currentY += e.Graphics.MeasureString("W", titleFont).Height + 5;

                    // 2. 绘制队列内容
                    if (_queueItems.Count == 0)
                    {
                        e.Graphics.DrawString("(空队列)", itemFont, Brushes.DarkGray, padding + 10, currentY);
                        currentY += e.Graphics.MeasureString("W", itemFont).Height + 5;
                    }
                    else
                    {
                        for (int i = 0; i < _queueItems.Count; i++)
                        {
                            if (currentY > this.Height - 65) {
                                e.Graphics.DrawString($"... 还有 {_queueItems.Count - i} 首", itemFont, Brushes.Gray, padding + 10, currentY);
                                break;
                            }
                            string text = $"{i + 1}. {_queueItems[i]}";
                            e.Graphics.DrawString(text, itemFont, Brushes.White, padding + 10, currentY);
                            currentY += e.Graphics.MeasureString(text, itemFont).Height + 2;
                        }
                    }

                    // 3. 绘制底部的动态信息（最近的一个）
                    float footerY = this.Height - 40;
                    // 分割线
                    e.Graphics.DrawLine(Pens.DimGray, padding, footerY - 5, this.Width - padding, footerY - 5);
                    
                    using (Brush statusBrush = new SolidBrush(_statusColor))
                    {
                        e.Graphics.DrawString(_statusText, statusFont, statusBrush, padding, footerY);
                    }
                }
            }
        }
    }

    private class BackgroundForm : Form
    {
        private DebugHUD _ownerHUD;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED;
                return cp;
            }
        }

        public BackgroundForm(DebugHUD owner)
        {
            _ownerHUD = owner;
            this.FormBorderStyle = FormBorderStyle.None;
            this.BackColor = Color.Black;
            this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.Manual;
            this.TopMost = true;

            this.MouseDown += (s, e) => {
                if (e.Button == MouseButtons.Left && !_ownerHUD._config.ClickThrough)
                {
                    ReleaseCapture();
                    SendMessage(this.Handle, 0xA1, 0x2, 0);
                }
            };
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x84;
            if (m.Msg == WM_NCHITTEST && !_ownerHUD._config.ClickThrough)
            {
                Point pos = this.PointToClient(new Point(m.LParam.ToInt32()));
                if (_ownerHUD.HandleResize(pos, out int result))
                {
                    m.Result = (IntPtr)result;
                    return;
                }
            }
            if (m.Msg == 0x0232) _ownerHUD.SaveSettings(); // WM_EXITSIZEMOVE
            base.WndProc(ref m);
        }
    }

    public static void Launch(string title = "Bili Queue HUD")
    {
        if (_instance != null) return;
        Thread thread = new Thread(() =>
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            _instance = new DebugHUD();
            _instance.Text = title;
            _readyEvent.Set();
            Application.Run(_instance);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        _readyEvent.Wait(2000);
    }

    public static void Update(List<string> queue, string status = null, Color? color = null)
    {
        _instance?.InternalUpdate(queue, status, color);
    }

    public DebugHUD()
    {
        LoadSettings();
        _bgForm = new BackgroundForm(this);
        this.FormBorderStyle = FormBorderStyle.None;
        this.TopMost = true;
        this.ShowInTaskbar = false;
        this.StartPosition = FormStartPosition.Manual;
        this.Location = new Point(_config.X, _config.Y);
        this.Size = new Size(_config.Width, _config.Height);
        _bgForm.Location = this.Location;
        _bgForm.Size = this.Size;
        this.MinimumSize = new Size(200, 150);
        this.DoubleBuffered = true;
        this.Padding = new Padding(_borderSize / 2);
        this.BackColor = Color.Black;
        this.TransparencyKey = Color.Black;
        this.Opacity = 0.99; 

        InitControls();
        
        this.LocationChanged += (s, e) => SyncToBackground();
        this.SizeChanged += (s, e) => SyncToBackground();
        _bgForm.LocationChanged += (s, e) => SyncToTextLayer();
        _bgForm.SizeChanged += (s, e) => SyncToTextLayer();

        ApplyTransparencyMode();
        ApplyClickThrough();

        this.FormClosing += (s, e) => { 
            SaveSettings(); 
            if (_bgForm != null && !_bgForm.IsDisposed) _bgForm.Dispose(); 
        };
        
        _bgForm.Show();
        this.Owner = _bgForm; 
    }

    private void SyncToBackground()
    {
        if (_isSyncing || _bgForm == null || _bgForm.IsDisposed || this.WindowState != FormWindowState.Normal) return;
        _isSyncing = true;
        _bgForm.Location = this.Location;
        _bgForm.Size = this.Size;
        UpdateConfigFromUI();
        _isSyncing = false;
    }

    private void SyncToTextLayer()
    {
        if (_isSyncing || this.IsDisposed || _bgForm == null || _bgForm.IsDisposed || _bgForm.WindowState != FormWindowState.Normal) return;
        _isSyncing = true;
        this.Location = _bgForm.Location;
        this.Size = _bgForm.Size;
        UpdateConfigFromUI();
        _isSyncing = false;
    }

    private void UpdateConfigFromUI()
    {
        if (_config.IsFullyTransparent) { _config.X = this.Location.X; _config.Y = this.Location.Y; _config.Width = this.Width; _config.Height = this.Height; }
        else { _config.X = _bgForm.Location.X; _config.Y = _bgForm.Location.Y; _config.Width = _bgForm.Width; _config.Height = _bgForm.Height; }
    }

    private void InitControls()
    {
        _displayPanel = new QueuePanel(this) { Dock = DockStyle.Fill };
        _displayPanel.MouseDown += (s, e) => {
            if (e.Button == MouseButtons.Left && !_config.ClickThrough) {
                ReleaseCapture();
                SendMessage(this.Handle, 0xA1, 0x2, 0);
            }
        };

        _menu = new ContextMenuStrip();
        _menu.Items.Add("切换 全透明(仅文字) / 半透明(带背景)", null, (s, e) => {
            _config.IsFullyTransparent = !_config.IsFullyTransparent;
            ApplyTransparencyMode();
            SaveSettings();
        });

        var clickThroughItem = new ToolStripMenuItem("鼠标点击穿透", null, (s, e) => {
            _config.ClickThrough = !_config.ClickThrough;
            ApplyClickThrough();
            SaveSettings();
        }) { Checked = _config.ClickThrough };
        _menu.Items.Add(clickThroughItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("增加背景不透明度", null, (s, e) => ChangeOpacity(0.05));
        _menu.Items.Add("减小背景不透明度", null, (s, e) => ChangeOpacity(-0.05));
        _menu.Items.Add("增大字体 (+)", null, (s, e) => ChangeFontSize(1f));
        _menu.Items.Add("减小字体 (-)", null, (s, e) => ChangeFontSize(-1f));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("退出 HUD", null, (s, e) => this.Close());
        
        _displayPanel.ContextMenuStrip = _menu;
        if (_bgForm != null) _bgForm.ContextMenuStrip = _menu;
        this.Controls.Add(_displayPanel);
    }

    private void ChangeOpacity(double delta)
    {
        _config.OpacityValue = Math.Max(0, Math.Min(1, _config.OpacityValue + delta));
        ApplyTransparencyMode();
        SaveSettings();
    }

    private void ChangeFontSize(float delta)
    {
        float newSize = _config.FontSize + delta;
        if (newSize < 6 || newSize > 48) return;
        _config.FontSize = newSize;
        _displayPanel.Invalidate();
        SaveSettings();
    }

    private void ApplyTransparencyMode()
    {
        if (_config.IsFullyTransparent) _bgForm.Visible = false;
        else { _bgForm.Visible = true; _bgForm.Opacity = _config.OpacityValue; _bgForm.Location = this.Location; _bgForm.Size = this.Size; }
        this.Update();
    }

    private void ApplyClickThrough()
    {
        ApplyFormClickThrough(this.Handle, _config.ClickThrough);
        ApplyFormClickThrough(_bgForm.Handle, _config.ClickThrough);
        if (_menu.Items.Count > 1 && _menu.Items[1] is ToolStripMenuItem item) item.Checked = _config.ClickThrough;
    }

    private void ApplyFormClickThrough(IntPtr handle, bool enable)
    {
        int exStyle = GetWindowLong(handle, GWL_EXSTYLE);
        if (enable) SetWindowLong(handle, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT | WS_EX_LAYERED);
        else SetWindowLong(handle, GWL_EXSTYLE, exStyle & ~WS_EX_TRANSPARENT);
    }

    private void InternalUpdate(List<string> queue, string status, Color? color)
    {
        if (this.IsDisposed) return;
        try { this.BeginInvoke(new Action(() => _displayPanel.UpdateData(queue, status, color))); } catch { }
    }

    public void SaveSettings()
    {
        if (this.IsDisposed) return;
        try {
            if (this.WindowState == FormWindowState.Normal) UpdateConfigFromUI();
            string json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configPath, json);
        } catch { }
    }

    private void LoadSettings()
    {
        try {
            if (File.Exists(_configPath)) {
                string json = File.ReadAllText(_configPath);
                _config = JsonSerializer.Deserialize<HUDConfig>(json) ?? new HUDConfig();
            }
        } catch { _config = new HUDConfig(); }
    }

    public bool HandleResize(Point pos, out int result)
    {
        result = 1;
        bool left = pos.X <= _borderSize, right = pos.X >= ClientSize.Width - _borderSize;
        bool top = pos.Y <= _borderSize, bottom = pos.Y >= ClientSize.Height - _borderSize;
        if (left && top) result = 13; else if (right && top) result = 14;
        else if (left && bottom) result = 16; else if (right && bottom) result = 17;
        else if (left) result = 10; else if (right) result = 11;
        else if (top) result = 12; else if (bottom) result = 15;
        else return false;
        return true;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x84 && !_config.ClickThrough)
        {
            Point pos = this.PointToClient(new Point(m.LParam.ToInt32()));
            if (HandleResize(pos, out int result)) { m.Result = (IntPtr)result; return; }
        }
        if (m.Msg == 0x0232) SaveSettings();
        base.WndProc(ref m);
    }
}