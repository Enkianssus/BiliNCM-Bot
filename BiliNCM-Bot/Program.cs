using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Buffers.Binary;
using Newtonsoft.Json.Linq;
using NeteaseCloudMusicApi;
using System.Text.Json;
using Velopack;
using Velopack.Sources;

namespace BiliNetEaseIntegratedApp
{
    class Program
    {
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        
        private const int SW_HIDE = 0;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;

        public class PermissionConfig {
            public bool AllowManager { get; set; } = true;    
            public int MinGuardType { get; set; } = 0;        
            public int MinMedalLevel { get; set; } = 0;       
            public string MedalName { get; set; } = "";       
        }

        public class AppConfig {
            public string BiliCookie { get; set; } = "";
            public long BiliUid { get; set; } = 0;
            public long RoomId { get; set; } = 0;
            
            public string AuthJson { get; set; } = "";
            public int CooldownMinutes { get; set; } = 0;
            public int AltHoldMs { get; set; } = 0; 
            public bool ShowDebugLogs { get; set; } = false;
            public bool ShowAllDanmaku { get; set; } = false;

            public bool EnableCDP { get; set; } = true;
            public int CdpPort { get; set; } = 9222;

            public bool EnableHUD { get; set; } = true; 
            public int RestoreDelayMs { get; set; } = 800; 
            public int MonitorIntervalMs { get; set; } = 300; 
            public List<string> SuperUsers { get; set; } = new List<string>(); 
            
            public PermissionConfig OrderPermission { get; set; } = new PermissionConfig { MinMedalLevel = 0 };
            public PermissionConfig SkipPermission { get; set; } = new PermissionConfig { AllowManager = true, MinMedalLevel = 0 };
            public PermissionConfig PriorityPermission { get; set; } = new PermissionConfig { MinGuardType = 0 };
            public PermissionConfig CancelPermission { get; set; } = new PermissionConfig { MinMedalLevel = 0 };
            
            public PermissionConfig ToggleAcceptPermission { get; set; } = new PermissionConfig { AllowManager = true, MinMedalLevel = 0, MinGuardType = 0 };
            public PermissionConfig ForceControlPermission { get; set; } = new PermissionConfig { AllowManager = true, MinMedalLevel = 0, MinGuardType = -1 };
            
            public JsonElement? OverlayUIConfig { get; set; }
        }

        public struct UserInfo {
            public string Name { get; set; }
            public string Uid { get; set; }
            public bool IsManager { get; set; }
            public int GuardType { get; set; }
            public int MedalLevel { get; set; }
            public string MedalName { get; set; }
        }

        public struct SongInfo {
            public string Id { get; set; }
            public string SongName { get; set; }
            public string ArtistName { get; set; }
            public string OrderedBy { get; set; }
            public string OrderedByUid { get; set; }
            public string OrderedByAvatar { get; set; }
            public string FullTitle => $"{ArtistName} - {SongName}";
        }

        public struct LogEntry {
            public string Time { get; set; }
            public string Message { get; set; }
            public string Color { get; set; }
        }

        static AppConfig _config = new AppConfig();
        static CloudMusicApi _neteaseApi = new CloudMusicApi();
        static List<SongInfo> _targetQueue = new List<SongInfo>(); 
        static HashSet<string> _withdrawnSongIds = new HashSet<string>(); // [新增] 撤回/残余歌曲黑名单
        static readonly object _queueLock = new object();
        
        static ConcurrentDictionary<string, DateTime> _userCooldowns = new ConcurrentDictionary<string, DateTime>();
        static ConcurrentDictionary<string, string> _avatarCache = new ConcurrentDictionary<string, string>();
        
        static string? _lastTrackTitle = null; 
        static SongInfo? _currentPlayingSong = null; 
        static SongInfo? _expectedSong = null; 
        static bool _isMonitoring = true;
        static bool _isIntercepting = false;
        
        static bool _acceptingRequests = true; 
        static bool _isPlayingEnabled = true;
        static bool _isPinned = true;
        
        static CancellationTokenSource _biliCts = new CancellationTokenSource();
        
        static string _qrCodeBase64 = "";
        static string _qrLoginStatus = "等待获取二维码...";
        static bool _isQrLoggingIn = false;

        static class OverlayState {
            public static string Status = "等待中...";
            public static SongInfo? CurrentPlaying = null;
            public static List<SongInfo> Queue = new List<SongInfo>();
            public static string LastToastMsg = "";
            public static long LastToastTime = 0;
        }
        static HttpListener _overlayListener;
        static string _webRootPath = "";

        static List<LogEntry> _sysLogs = new List<LogEntry>();
        static readonly object _consoleLock = new object();

        static DateTime _lastForcePlayTime = DateTime.MinValue;
        static DateTime _lastPushToQueueTime = DateTime.MinValue; 
        static string _lastInsertedNextSongId = null;
        static DateTime _lastInsertTime = DateTime.MinValue;
        
        static volatile bool _queueDirty = false;

        static void MarkQueueDirty() {
            _queueDirty = true;
            _lastInsertedNextSongId = null;
            _lastInsertTime = DateTime.MinValue;
        }

        [STAThread] 
        static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) => {
                File.WriteAllText("crash.log", $"发生未捕获的严重错误：\n{e.ExceptionObject}");
            };

            using (Mutex mutex = new Mutex(true, "Global\\BiliNCM_Bot_Mutex_Unique", out bool createdNew))
            {
                if (!createdNew)
                {
                    LaunchFrontendApp();
                    return;
                }

                try { VelopackApp.Build().Run(); } catch { }

                var handle = GetConsoleWindow();
                ShowWindow(handle, SW_HIDE);

                if (!LoadConfig()) { SaveConfig(); }

                LocateWebRoot();
                
                StartWebServer();
                SyncHUD("后端 API 已就绪");
                WriteLog(">>> [系统] 点歌机核心服务启动成功！", ConsoleColor.Green);

                _neteaseApi.Cookies.Add(new Cookie("os", "pc", "/", ".music.163.com"));
                _neteaseApi.Cookies.Add(new Cookie("appver", "2.10.6", "/", ".music.163.com"));

                RestartNCMWithDebugPort();

                _ = Task.Run(MonitorLoopAsync);

                if (_config.RoomId > 0 && !string.IsNullOrEmpty(_config.BiliCookie)) {
                    WriteLog($">>> [系统] 检测到历史房间配置，自动尝试连接直播间: {_config.RoomId}", ConsoleColor.Yellow);
                    _ = Task.Run(() => ConnectToLiveRoom(_config.RoomId));
                }

                LaunchFrontendApp();
                Thread.Sleep(Timeout.Infinite);
            }
        }

        static void LaunchFrontendApp()
        {
            try {
                Process.Start(new ProcessStartInfo {
                    FileName = "msedge.exe",
                    Arguments = "--app=http://localhost:5555/ --window-size=380,580",
                    UseShellExecute = true
                });
            } catch {
                Process.Start(new ProcessStartInfo {
                    FileName = "http://localhost:5555/",
                    UseShellExecute = true
                });
            }
        }

        static void LocateWebRoot() {
            string currentDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] possiblePaths = { Path.Combine(currentDir, "public"), Path.Combine(currentDir, "dist"), Path.Combine(currentDir, "bili-overlay", "dist"), @"E:\CSProject\BiliNCM-Bot\bili-overlay\dist" };
            foreach (var path in possiblePaths) {
                if (Directory.Exists(path) && File.Exists(Path.Combine(path, "index.html"))) { _webRootPath = path; return; }
            }
        }

        static void StartWebServer() {
            try {
                _overlayListener = new HttpListener(); _overlayListener.Prefixes.Add("http://localhost:5555/"); _overlayListener.Start();
                Task.Run(async () => { while (_overlayListener != null && _overlayListener.IsListening) { try { var context = await _overlayListener.GetContextAsync(); ProcessWebRequest(context); } catch { } } });
            } catch { }
        }

        static async void ProcessWebRequest(HttpListenerContext context) {
            var req = context.Request; var res = context.Response;
            res.AppendHeader("Access-Control-Allow-Origin", "*"); res.AppendHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS"); res.AppendHeader("Access-Control-Allow-Headers", "*");
            if (req.HttpMethod == "OPTIONS") { res.StatusCode = 200; res.Close(); return; }

            try {
                string urlPath = req.Url.AbsolutePath;

                if (urlPath == "/api/debug/insert_next" && req.HttpMethod == "POST") {
                    using var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                    var doc = JsonDocument.Parse(reader.ReadToEnd());
                    string keyword = doc.RootElement.GetProperty("keyword").GetString();
                    bool success = false;
                    try {
                        var resApi = await _neteaseApi.RequestAsync(CloudMusicApiProviders.Search, new Dictionary<string, object> { { "keywords", keyword }, { "limit", 1 } });
                        var songs = resApi["result"]?["songs"] as JArray;
                        if (songs != null && songs.Count > 0) {
                            string songId = songs[0]["id"]?.ToString();
                            await InsertNextSongViaCDP(songId); 
                            WriteLog($"[Debug] 成功搜索并预加载: {songs[0]["name"]} (ID:{songId})", ConsoleColor.Magenta);
                            success = true;
                        } else {
                            WriteLog($"[Debug] 测试失败: 未找到对应歌曲", ConsoleColor.Red);
                        }
                    } catch (Exception ex) { WriteLog($"[Debug] 异常: {ex.Message}", ConsoleColor.Red); }
                    
                    res.ContentType = "application/json; charset=utf-8";
                    byte[] ebytes = Encoding.UTF8.GetBytes($"{{\"success\":{success.ToString().ToLower()}}}");
                    res.OutputStream.Write(ebytes, 0, ebytes.Length);
                    return;
                }

                if (urlPath == "/api/debug/play_next" && req.HttpMethod == "POST") {
                    await TrySkipSongAsync();
                    res.ContentType = "application/json; charset=utf-8";
                    byte[] ebytes = Encoding.UTF8.GetBytes("{\"success\":true}");
                    res.OutputStream.Write(ebytes, 0, ebytes.Length);
                    return;
                }

                if (urlPath == "/api/sys/restart_ncm" && req.HttpMethod == "POST") {
                    RestartNCMWithDebugPort();
                    res.ContentType = "application/json; charset=utf-8"; byte[] ebytes = Encoding.UTF8.GetBytes("{\"success\":true}"); res.OutputStream.Write(ebytes, 0, ebytes.Length); return;
                }

                if (urlPath == "/api/sys/topmost" && req.HttpMethod == "POST") {
                    _isPinned = !_isPinned;
                    EnumWindows((hwnd, lParam) => {
                        StringBuilder sb = new StringBuilder(256);
                        GetWindowText(hwnd, sb, 256);
                        string title = sb.ToString();
                        if (title.Contains("直播点歌机")) {
                            IntPtr insertAfter = _isPinned ? HWND_TOPMOST : HWND_NOTOPMOST;
                            SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
                        }
                        return true;
                    }, IntPtr.Zero);
                    res.ContentType = "application/json; charset=utf-8"; byte[] ebytes = Encoding.UTF8.GetBytes($"{{\"success\":true, \"pinned\":{_isPinned.ToString().ToLower()}}}"); res.OutputStream.Write(ebytes, 0, ebytes.Length); return;
                }

                if (urlPath == "/api/sys/open_admin" && req.HttpMethod == "POST") {
                    Process.Start(new ProcessStartInfo { FileName = "http://localhost:5555/?admin=true", UseShellExecute = true });
                    res.ContentType = "application/json; charset=utf-8"; byte[] ebytes = Encoding.UTF8.GetBytes("{\"success\":true}"); res.OutputStream.Write(ebytes, 0, ebytes.Length); return;
                }

                if (urlPath == "/api/exit" && req.HttpMethod == "POST") {
                    res.ContentType = "application/json; charset=utf-8"; byte[] ebytes = Encoding.UTF8.GetBytes("{\"success\":true}"); res.OutputStream.Write(ebytes, 0, ebytes.Length); res.Close();
                    Environment.Exit(0);
                    return;
                }

                if (urlPath == "/api/state/toggle" && req.HttpMethod == "POST") {
                    _acceptingRequests = !_acceptingRequests; SyncHUD(); WriteLog($">>> [系统] 接收点歌功能目前已 {(_acceptingRequests ? "开启" : "暂停")}", _acceptingRequests ? ConsoleColor.Green : ConsoleColor.Red);
                    res.ContentType = "application/json; charset=utf-8"; byte[] ebytes = Encoding.UTF8.GetBytes("{\"success\":true}"); res.OutputStream.Write(ebytes, 0, ebytes.Length); return;
                }

                if (urlPath == "/api/state/toggle_play" && req.HttpMethod == "POST") {
                    _isPlayingEnabled = !_isPlayingEnabled;
                    if (!_isPlayingEnabled && _currentPlayingSong != null) {
                        lock (_queueLock) { _targetQueue.Insert(0, _currentPlayingSong.Value); _currentPlayingSong = null; } WriteLog(">>> [系统] 暂停播放，当前歌曲已自动退回队列顶部", ConsoleColor.Yellow); 
                        _ = TrySkipSongAsync();
                    }
                    SyncHUD(); WriteLog($">>> [系统] 自动播放队列功能目前已 {(_isPlayingEnabled ? "开启" : "暂停")}", _isPlayingEnabled ? ConsoleColor.Green : ConsoleColor.Red);
                    res.ContentType = "application/json; charset=utf-8"; byte[] ebytes = Encoding.UTF8.GetBytes("{\"success\":true}"); res.OutputStream.Write(ebytes, 0, ebytes.Length); return;
                }

                if (urlPath == "/api/queue/action" && req.HttpMethod == "POST") {
                    using var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                    var doc = JsonDocument.Parse(reader.ReadToEnd());
                    string action = doc.RootElement.GetProperty("action").GetString();

                    lock (_queueLock) {
                        if (action == "reorder") {
                            int from = doc.RootElement.GetProperty("from").GetInt32();
                            int to = doc.RootElement.GetProperty("to").GetInt32();
                            if (from >= 0 && from < _targetQueue.Count && to >= 0 && to < _targetQueue.Count) {
                                var item = _targetQueue[from];
                                _targetQueue.RemoveAt(from);
                                _targetQueue.Insert(to, item);
                            }
                        } else if (action == "push_current_to_queue") {
                            if (_currentPlayingSong.HasValue) {
                                var target = _currentPlayingSong.Value;
                                bool wasEmpty = (_targetQueue.Count == 0); 
                                _targetQueue.Add(target);
                                _currentPlayingSong = null; 
                                _lastPushToQueueTime = DateTime.Now; 
                                WriteLog($">>> [系统] 播放被强行中断，已退回队尾: {target.SongName}", ConsoleColor.Yellow);
                                
                                _ = TrySkipSongAsync();

                                if (wasEmpty && _isPlayingEnabled) {
                                    _ = Task.Run(async () => {
                                        await Task.Delay(1500); 
                                        await InsertNextSongViaCDP(target.Id);
                                    });
                                }
                            }
                        } else if (action == "skip_current") {
                            if (_currentPlayingSong.HasValue) {
                                WriteLog($">>> [系统] 强行移除了正在播放的歌曲", ConsoleColor.Yellow);
                                _currentPlayingSong = null; 
                                _ = TrySkipSongAsync();
                            }
                        } else {
                            if (doc.RootElement.TryGetProperty("index", out var idxProp)) {
                                int index = idxProp.GetInt32();
                                if (index >= 0 && index < _targetQueue.Count) {
                                    if (action == "delete") {
                                        var target = _targetQueue[index];
                                        _targetQueue.RemoveAt(index);
                                        _withdrawnSongIds.Add(target.Id); // [新增] 加入撤回黑名单
                                        _ = RemoveSongFromPlaylistViaCDP(target.Id);
                                    } else if (action == "top") {
                                        var item = _targetQueue[index];
                                        _targetQueue.RemoveAt(index);
                                        _targetQueue.Insert(0, item);
                                    } else if (action == "play_now") {
                                        var target = _targetQueue[index];
                                        _targetQueue.RemoveAt(index);
                                        
                                        if (_currentPlayingSong.HasValue) {
                                            WriteLog($"[系统] 原播放歌曲已被凭空丢弃: {_currentPlayingSong.Value.SongName}", ConsoleColor.DarkGray);
                                        }

                                        _isPlayingEnabled = true;
                                        Task.Run(() => ForcePlaySongAsync(target));
                                    }
                                }
                            }
                        }
                    }
                    MarkQueueDirty();
                    SyncHUD();
                    res.ContentType = "application/json; charset=utf-8";
                    byte[] okBytes = Encoding.UTF8.GetBytes("{\"success\":true}");
                    res.OutputStream.Write(okBytes, 0, okBytes.Length);
                    return;
                }

                if (urlPath == "/api/logs" && req.HttpMethod == "GET") {
                    res.ContentType = "application/json; charset=utf-8";
                    List<LogEntry> currentLogs;
                    lock(_consoleLock) { currentLogs = new List<LogEntry>(_sysLogs); }
                    byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(currentLogs));
                    res.OutputStream.Write(bytes, 0, bytes.Length);
                    return;
                }

                if (urlPath == "/api/room" && req.HttpMethod == "POST") {
                    using var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                    string body = reader.ReadToEnd();
                    var doc = JsonDocument.Parse(body);
                    long newRoomId = doc.RootElement.GetProperty("roomId").GetInt64();
                    bool success = await ConnectToLiveRoom(newRoomId);
                    res.ContentType = "application/json; charset=utf-8";
                    byte[] buffer = Encoding.UTF8.GetBytes($"{{\"success\":{success.ToString().ToLower()}}}");
                    res.OutputStream.Write(buffer, 0, buffer.Length);
                    return;
                }

                if (urlPath == "/api/update/check" && req.HttpMethod == "GET") {
                    res.ContentType = "application/json; charset=utf-8";
                    try {
                        var source = new GithubSource("https://github.com/Enkianssus/BiliNCM-Bot", null, false);
                        var mgr = new UpdateManager(source);
                        var newVersion = await mgr.CheckForUpdatesAsync();
                        string jsonStr = newVersion != null ? $"{{\"hasUpdate\":true, \"version\":\"{newVersion.TargetFullRelease.Version}\"}}" : "{\"hasUpdate\":false}";
                        byte[] buffer = Encoding.UTF8.GetBytes(jsonStr); res.OutputStream.Write(buffer, 0, buffer.Length);
                    } catch (Exception ex) {
                        byte[] buffer = Encoding.UTF8.GetBytes($"{{\"error\":\"{ex.Message}\"}}"); res.OutputStream.Write(buffer, 0, buffer.Length);
                    }
                    return;
                }

                if (urlPath == "/api/update/apply" && req.HttpMethod == "POST") {
                    res.ContentType = "application/json; charset=utf-8"; byte[] buffer = Encoding.UTF8.GetBytes("{\"status\":\"downloading\"}"); res.OutputStream.Write(buffer, 0, buffer.Length); res.Close();
                    try {
                        WriteLog(">>> [系统] 开始下载 Velopack 更新...", ConsoleColor.Cyan);
                        var source = new GithubSource("https://github.com/Enkianssus/BiliNCM-Bot", null, false); var mgr = new UpdateManager(source);
                        var newVersion = await mgr.CheckForUpdatesAsync();
                        if (newVersion != null) { await mgr.DownloadUpdatesAsync(newVersion); mgr.ApplyUpdatesAndRestart(newVersion); }
                    } catch(Exception ex) { WriteLog($"[更新异常] {ex.Message}", ConsoleColor.Red); } return;
                }

                if (urlPath == "/api/config") {
                    if (req.HttpMethod == "GET") {
                        res.ContentType = "application/json; charset=utf-8";
                        var wrap = new { 
                            config = _config, 
                            roomId = _config.RoomId, 
                            accepting = _acceptingRequests,
                            playing = _isPlayingEnabled,
                            biliLogin = !string.IsNullOrEmpty(_config.BiliCookie), 
                            uid = _config.BiliUid,
                            version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0"
                        };
                        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(wrap));
                        res.OutputStream.Write(bytes, 0, bytes.Length);
                    } 
                    else if (req.HttpMethod == "POST") {
                        using var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                        string body = reader.ReadToEnd();
                        var newConf = JsonSerializer.Deserialize<AppConfig>(body);
                        newConf.BiliCookie = _config.BiliCookie; newConf.BiliUid = _config.BiliUid; newConf.RoomId = _config.RoomId; newConf.AuthJson = _config.AuthJson;
                        _config = newConf;
                        SaveConfig();
                        res.ContentType = "application/json; charset=utf-8";
                        byte[] bytes = Encoding.UTF8.GetBytes("{\"success\":true}");
                        res.OutputStream.Write(bytes, 0, bytes.Length);
                    }
                    return;
                }

                if (urlPath == "/api/bili/qrstart" && req.HttpMethod == "POST") {
                    if (!_isQrLoggingIn) { _ = Task.Run(() => BiliQrLoginAsync()); }
                    res.ContentType = "application/json; charset=utf-8";
                    byte[] bytes = Encoding.UTF8.GetBytes("{\"success\":true}"); res.OutputStream.Write(bytes, 0, bytes.Length);
                    return;
                }
                
                if (urlPath == "/api/bili/qrstatus" && req.HttpMethod == "GET") {
                    res.ContentType = "application/json; charset=utf-8";
                    var obj = new { qrBase64 = _qrCodeBase64, status = _qrLoginStatus, isLogin = !string.IsNullOrEmpty(_config.BiliCookie) };
                    byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(obj));
                    res.OutputStream.Write(bytes, 0, bytes.Length);
                    return;
                }

                if (req.HttpMethod == "POST" && urlPath == "/api/uiconfig") {
                    using var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                    _config.OverlayUIConfig = JsonSerializer.Deserialize<JsonElement>(reader.ReadToEnd()); SaveConfig();
                    res.ContentType = "application/json; charset=utf-8"; byte[] okBytes = Encoding.UTF8.GetBytes("{\"success\":true}"); res.OutputStream.Write(okBytes, 0, okBytes.Length);
                    return;
                }

                if (urlPath == "/data") {
                    res.ContentType = "application/json; charset=utf-8";
                    var obj = new { 
                        status = OverlayState.Status, 
                        current = OverlayState.CurrentPlaying, 
                        queue = OverlayState.Queue, 
                        uiConfig = _config.OverlayUIConfig,
                        toast = new { msg = OverlayState.LastToastMsg, time = OverlayState.LastToastTime },
                        accepting = _acceptingRequests,
                        playing = _isPlayingEnabled
                    };
                    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(obj)); res.OutputStream.Write(bytes, 0, bytes.Length);
                    return;
                }
                
                if (!string.IsNullOrEmpty(_webRootPath)) {
                    string servePath = urlPath == "/" ? "/index.html" : urlPath;
                    string filePath = Path.Combine(_webRootPath, servePath.TrimStart('/').Replace("/", "\\"));
                    if (!File.Exists(filePath)) { filePath = Path.Combine(_webRootPath, "index.html"); if (!File.Exists(filePath)) { res.StatusCode = 404; return; } }
                    string extension = Path.GetExtension(filePath).ToLower();
                    res.ContentType = extension switch { ".html" => "text/html; charset=utf-8", ".js" => "application/javascript", ".css" => "text/css", ".svg" => "image/svg+xml", ".png" => "image/png", ".json" => "application/json", _ => "application/octet-stream" };
                    byte[] fileBytes = File.ReadAllBytes(filePath); res.OutputStream.Write(fileBytes, 0, fileBytes.Length);
                    return;
                }
                res.StatusCode = 404;
            } catch { res.StatusCode = 500; } finally { res.Close(); }
        }

        static void SyncHUD(string status = null) {
            if (!_config.EnableHUD) return;
            if (status != null) OverlayState.Status = status;
            OverlayState.CurrentPlaying = _currentPlayingSong;
            lock(_queueLock) { OverlayState.Queue = new List<SongInfo>(_targetQueue); }
        }

        static void WriteLog(string message, ConsoleColor color = ConsoleColor.Gray) {
            lock (_consoleLock) {
                Console.ForegroundColor = color;
                Console.WriteLine(message);
                Console.ResetColor();
                _sysLogs.Add(new LogEntry { Time = DateTime.Now.ToString("HH:mm:ss"), Message = message, Color = color.ToString() });
                if (_sysLogs.Count > 100) _sysLogs.RemoveAt(0); 
            }
        }

        static void RestartNCMWithDebugPort() {
            try {
                var procs = Process.GetProcessesByName("cloudmusic");
                string exePath = "";
                
                if (procs.Length > 0) {
                    try { exePath = procs[0].MainModule.FileName; } catch { }
                    foreach(var p in procs) { try { p.Kill(); } catch { } }
                    Thread.Sleep(1000); 
                }
                
                if (string.IsNullOrEmpty(exePath)) {
                    string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    string defaultPath = Path.Combine(localAppData, "Netease", "CloudMusic", "cloudmusic.exe");
                    if (File.Exists(defaultPath)) exePath = defaultPath;
                    else {
                        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                        string pfPath = Path.Combine(programFiles, "Netease", "CloudMusic", "cloudmusic.exe");
                        if (File.Exists(pfPath)) exePath = pfPath;
                    }
                }

                if (!string.IsNullOrEmpty(exePath)) {
                    Process.Start(exePath, $"--remote-debugging-port={_config.CdpPort}");
                    WriteLog($">>> [CDP] 已自动寻找并带调试端口({_config.CdpPort})重启网易云音乐！", ConsoleColor.Green);
                    StartCDPRadar();
                } else {
                    WriteLog(">>> [错误] 找不到网易云安装路径，请先手动正常运行一次网易云！", ConsoleColor.Red);
                }
            } catch(Exception ex) {
                WriteLog($"[重启网易云失败] {ex.Message}", ConsoleColor.Red);
            }
        }

        static async Task<string> SendCDPCommandAndGetResultAsync(string script)
        {
            if (!_config.EnableCDP) return null;
            
            try {
                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(2);
                var jsonStr = await httpClient.GetStringAsync($"http://127.0.0.1:{_config.CdpPort}/json");
                var targets = JsonDocument.Parse(jsonStr).RootElement;
                
                string wsUrl = "";
                foreach (var t in targets.EnumerateArray()) {
                    if (t.GetProperty("type").GetString() == "page" && t.GetProperty("url").GetString().Contains("orpheus")) {
                        wsUrl = t.GetProperty("webSocketDebuggerUrl").GetString();
                        break;
                    }
                }
                if (string.IsNullOrEmpty(wsUrl)) {
                     foreach (var t in targets.EnumerateArray()) {
                        if (t.GetProperty("type").GetString() == "page") {
                            wsUrl = t.GetProperty("webSocketDebuggerUrl").GetString();
                            break;
                        }
                    }
                }

                if (string.IsNullOrEmpty(wsUrl)) return null;

                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None);
                
                var payload = new {
                    id = 1,
                    method = "Runtime.evaluate",
                    @params = new Dictionary<string, object> {
                        { "expression", script },
                        { "returnByValue", true },
                        { "awaitPromise", true }
                    }
                };
                
                string reqJson = JsonSerializer.Serialize(payload);
                var bytes = Encoding.UTF8.GetBytes(reqJson);
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                
                var buffer = new byte[8192];
                using var ms = new MemoryStream();
                WebSocketReceiveResult wsRes;
                do {
                    wsRes = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    ms.Write(buffer, 0, wsRes.Count);
                } while (!wsRes.EndOfMessage);
                
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
                
                string resJson = Encoding.UTF8.GetString(ms.ToArray());
                var doc = JsonDocument.Parse(resJson);
                
                if (doc.RootElement.TryGetProperty("result", out var outerRes) && 
                    outerRes.TryGetProperty("result", out var innerRes) && 
                    innerRes.TryGetProperty("value", out var val)) 
                {
                    if (val.ValueKind == JsonValueKind.String) return val.GetString();
                    return val.GetRawText();
                }
                
                return "true";
            } catch {
                return null;
            }
        }

        static string GetFiberStoreExtractJs() {
            return @"
                function _ensureStore() {
                    if (window._reduxStore) return true;
                    const root = window._fiberRoot || (document.querySelector('#root') && document.querySelector('#root')._reactRootContainer && document.querySelector('#root')._reactRootContainer._internalRoot);
                    if (!root) return false;
                    let queue = [root.current || root];
                    while (queue.length > 0) {
                        let node = queue.shift();
                        if (!node) continue;
                        if (node.memoizedProps && node.memoizedProps.store) { window._reduxStore = node.memoizedProps.store; return true; }
                        if (node.stateNode && node.stateNode.store) { window._reduxStore = node.stateNode.store; return true; }
                        let child = node.child;
                        while (child) { queue.push(child); child = child.sibling; }
                    }
                    return false;
                }
            ";
        }

        static async Task<bool> InsertNextSongViaCDP(string songId) {
            string js = GetFiberStoreExtractJs() + $@"
                if (_ensureStore()) {{
                    window._reduxStore.dispatch({{
                        type: 'async:action/doAction',
                        payload: {{
                            actionId: 'addToPlayList',
                            data: {{
                                resource: {{ id: String({songId}), duration: 0 }},
                                resourceType: 'track',
                                eventType: 'click'
                            }}
                        }}
                    }});
                    'ok';
                }} else {{
                    'no_store';
                }}
            ";
            var result = await SendCDPCommandAndGetResultAsync(js);
            if (result == "ok") {
                if (_config.ShowDebugLogs) WriteLog($"[CDP] addToPlayList 成功: songId={songId}", ConsoleColor.DarkGray);
                return true;
            } else {
                WriteLog($"[CDP] addToPlayList 失败: songId={songId}, result={result}", ConsoleColor.Red);
                return false;
            }
        }

        static async Task RemoveSongFromPlaylistViaCDP(string songId) {
            string js = GetFiberStoreExtractJs() + $@"
                if (_ensureStore()) {{
                    window._reduxStore.dispatch({{
                        type: 'async:action/doAction',
                        payload: {{ actionId: 'removeFromPlayingList', data: {{ id: String({songId}) }} }}
                    }});
                    window._reduxStore.dispatch({{
                        type: 'async:action/doAction',
                        payload: {{ actionId: 'removeFromPlayList', data: {{ id: String({songId}) }} }}
                    }});
                }}
            ";
            await SendCDPCommandAndGetResultAsync(js);
        }

        static async Task BiliQrLoginAsync()
        {
            _isQrLoggingIn = true; _qrCodeBase64 = ""; _qrLoginStatus = "正在向 B站请求二维码...";
            try {
                var handler = new HttpClientHandler { UseCookies = false, AutomaticDecompression = DecompressionMethods.All };
                using var client = new HttpClient(handler);
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                
                var genRes = await client.GetStringAsync("https://passport.bilibili.com/x/passport-login/web/qrcode/generate");
                var genDoc = JObject.Parse(genRes);
                string url = genDoc["data"]?["url"]?.ToString() ?? "";
                string qrcodeKey = genDoc["data"]?["qrcode_key"]?.ToString() ?? "";

                if (string.IsNullOrEmpty(url)) { _qrLoginStatus = "错误：无法获取二维码"; _isQrLoggingIn = false; return; }

                string qrApiUrl = $"https://api.qrserver.com/v1/create-qr-code/?size=256x256&data={Uri.EscapeDataString(url)}";
                var qrBytes = await client.GetByteArrayAsync(qrApiUrl);
                _qrCodeBase64 = "data:image/png;base64," + Convert.ToBase64String(qrBytes);
                _qrLoginStatus = "请使用手机 B站 APP 扫码";
                WriteLog(">>> [登录] 请扫码...", ConsoleColor.Cyan);
                
                bool loginSuccess = false; string rawCookie = ""; long uid = 0;
                
                for(int i=0; i<60; i++) 
                {
                    await Task.Delay(2000);
                    var pollRes = await client.GetStringAsync($"https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key={qrcodeKey}");
                    var pollDoc = JObject.Parse(pollRes);
                    int code = pollDoc["data"]?["code"]?.Value<int>() ?? -1;
                    
                    if (code == 86101) continue; 
                    if (code == 86090) { _qrLoginStatus = "已扫码，请在手机上点击确认"; continue; }
                    if (code == 86038) { _qrLoginStatus = "二维码已失效，请重新发起"; break; }
                    if (code == 0) 
                    {
                        _qrLoginStatus = "扫码成功！正在提取身份凭证...";
                        string crossDomainUrl = pollDoc["data"]?["url"]?.ToString() ?? "";
                        if (!string.IsNullOrEmpty(crossDomainUrl)) {
                            var uri = new Uri(crossDomainUrl);
                            var query = uri.Query.TrimStart('?').Split('&');
                            List<string> cookieList = new List<string>();
                            foreach (var p in query) {
                                var kv = p.Split(new[] { '=' }, 2);
                                if (kv.Length == 2) {
                                    string key = kv[0], value = kv[1]; 
                                    if (key == "DedeUserID" || key == "DedeUserID__ckMd5" || key == "SESSDATA" || key == "bili_jct") cookieList.Add($"{key}={value}");
                                    if (key == "DedeUserID") long.TryParse(value, out uid);
                                }
                            }
                            rawCookie = string.Join("; ", cookieList);
                        }
                        loginSuccess = true;
                        break;
                    }
                }

                if (!loginSuccess) { _isQrLoggingIn = false; return; }
                
                _config.BiliCookie = rawCookie;
                _config.BiliUid = uid;
                SaveConfig();
                
                _qrLoginStatus = "登录完成，请前连接直播间！";
                WriteLog($">>> [登录] 登录成功，UID: {uid}", ConsoleColor.Green);

            } catch (Exception ex) {
                _qrLoginStatus = $"错误: {ex.Message}";
            } finally {
                _isQrLoggingIn = false;
            }
        }

        static async Task<bool> ConnectToLiveRoom(long shortRoomId) {
            try {
                if (string.IsNullOrEmpty(_config.BiliCookie)) return false;
                WriteLog($">>> [连接] 正在尝试解析直播间 {shortRoomId} ...", ConsoleColor.Cyan);

                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                client.DefaultRequestHeaders.Add("Cookie", _config.BiliCookie);

                var roomInitRes = await client.GetStringAsync($"https://api.live.bilibili.com/room/v1/Room/room_init?id={shortRoomId}");
                var roomInitDoc = JObject.Parse(roomInitRes);
                
                if (roomInitDoc["code"]?.Value<int>() != 0) {
                    WriteLog($">>> [错误] 初始化房间失败: {roomInitDoc["msg"]}", ConsoleColor.Red);
                    return false;
                }

                long realRoomId = shortRoomId;
                if (roomInitDoc["data"]?.Type == JTokenType.Object) {
                    realRoomId = roomInitDoc["data"]["room_id"]?.Value<long>() ?? shortRoomId;
                }

                var danmuReqUrl = $"https://api.live.bilibili.com/xlive/web-room/v1/index/getDanmuInfo?id={realRoomId}&type=0";
                var danmuJson = await client.GetStringAsync(danmuReqUrl);
                
                if (danmuJson.Contains("-352")) {
                    var fallbackReq = new HttpRequestMessage(HttpMethod.Get, $"https://api.live.bilibili.com/room/v1/Danmu/getConf?room_id={realRoomId}&platform=pc&player=web");
                    fallbackReq.Headers.Add("Cookie", _config.BiliCookie);
                    fallbackReq.Headers.Add("Referer", $"https://live.bilibili.com/{shortRoomId}");
                    var fbRes = await client.SendAsync(fallbackReq);
                    danmuJson = await fbRes.Content.ReadAsStringAsync();
                }

                var danmuDoc = JObject.Parse(danmuJson);
                string token = danmuDoc["data"]?["token"]?.ToString() ?? "";

                string finalBuvid = "999E9060-EA3F-0F79-7BDC-A14879D11DCB95434infoc";
                var b3Match = System.Text.RegularExpressions.Regex.Match(_config.BiliCookie, @"buvid3=([^;]+)");
                if (b3Match.Success) finalBuvid = b3Match.Groups[1].Value;

                var authObj = new {
                    uid = _config.BiliUid, roomid = realRoomId, protover = 3, buvid = finalBuvid,
                    support_ack = true, queue_uuid = Guid.NewGuid().ToString("N").Substring(0, 8),
                    scene = "room", platform = "web", type = 2, key = token
                };

                _config.AuthJson = JsonSerializer.Serialize(authObj);
                _config.RoomId = shortRoomId; 
                SaveConfig();
                
                WriteLog($">>> [成功] 直播间 {shortRoomId} 凭证解析完毕，已发起 WebSocket 连接。", ConsoleColor.Green);
                RestartBiliConnection();
                return true;

            } catch (Exception ex) {
                WriteLog($">>> [异常] 连接直播间出错: {ex.Message}", ConsoleColor.Red);
                return false;
            }
        }

        static bool LoadConfig() {
            try {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
                if (!File.Exists(path)) return false;
                _config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path)) ?? new AppConfig();
                return true;
            } catch { return false; }
        }

        static void SaveConfig() {
            try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json"), JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true })); } catch { }
        }

        static bool HasPermission(UserInfo user, PermissionConfig perm) {
            if (_config.SuperUsers.Contains(user.Name)) return true;
            if (perm.AllowManager && user.IsManager) return true;
            if (perm.MinGuardType == -1) return false; 
            if (perm.MinGuardType > 0) return user.GuardType > 0 && user.GuardType <= perm.MinGuardType;
            if (perm.MinMedalLevel > 0 && user.MedalLevel >= perm.MinMedalLevel) {
                if (string.IsNullOrEmpty(perm.MedalName) || user.MedalName == perm.MedalName) return true;
            } else if (perm.MinMedalLevel == 0) return true; 
            return false;
        }

        static async Task<string> GetUserAvatarAsync(string uid) {
            if (string.IsNullOrEmpty(uid)) return "";
            if (_avatarCache.TryGetValue(uid, out string url)) return url;
            try {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
                var res = await client.GetStringAsync($"https://api.bilibili.com/x/web-interface/card?mid={uid}");
                var doc = JsonDocument.Parse(res);
                if (doc.RootElement.GetProperty("code").GetInt32() == 0) {
                    string face = doc.RootElement.GetProperty("data").GetProperty("card").GetProperty("face").GetString();
                    if (!string.IsNullOrEmpty(face)) {
                        if (face.StartsWith("http:")) face = face.Replace("http:", "https:");
                        face += "@64w_64h_1c.webp"; _avatarCache[uid] = face; return face;
                    }
                }
            } catch { }
            string fallback = $"https://api.dicebear.com/7.x/identicon/svg?seed={uid}";
            _avatarCache[uid] = fallback; return fallback;
        }

        static async Task TryRequestSong(UserInfo user, string keyword, string mode = "normal") {
            if (mode == "normal" && !HasPermission(user, _config.OrderPermission)) {
                WriteLog($"   |_ [拒绝] {user.Name} 弹幕点歌失败: 权限不足", ConsoleColor.DarkGray);
                return;
            }
            if (!_config.SuperUsers.Contains(user.Name) && _userCooldowns.TryGetValue(user.Uid, out DateTime last) && (DateTime.Now - last).TotalMinutes < _config.CooldownMinutes) {
                WriteLog($"   |_ [拒绝] {user.Name} 处于冷却中", ConsoleColor.DarkGray);
                return;
            }

            string avatar = await GetUserAvatarAsync(user.Uid);
            try {
                var res = await _neteaseApi.RequestAsync(CloudMusicApiProviders.Search, new Dictionary<string, object> { { "keywords", keyword }, { "limit", 1 } });
                var songs = res["result"]?["songs"] as JArray;
                if (songs == null || songs.Count == 0) {
                    WriteLog($"   |_ [失败] 未找到歌曲: \"{keyword}\"", ConsoleColor.Red);
                    return;
                }

                var s = songs[0];
                var newSong = new SongInfo { 
                    Id = s["id"]?.ToString(), SongName = s["name"]?.ToString(), 
                    ArtistName = string.Join("/", s["artists"]?.Select(a => (string)a["name"])), 
                    OrderedBy = user.Name, OrderedByUid = user.Uid, OrderedByAvatar = avatar 
                };
                
                bool shouldInsertToNCM = false;
                string insertSongId = null;

                lock (_queueLock) { 
                    _withdrawnSongIds.Remove(newSong.Id); // [新增] 若再次点歌，将其移出黑名单，让其恢复正常可播放状态
                    
                    if (mode == "play_now_drop" || mode == "play_now_keep") {
                        if (mode == "play_now_keep" && _currentPlayingSong.HasValue) {
                            _targetQueue.Insert(0, _currentPlayingSong.Value);
                            WriteLog($"[系统] 原播放歌曲已被顶回队列首位: {_currentPlayingSong.Value.SongName}", ConsoleColor.DarkGray);
                        } else if (mode == "play_now_drop" && _currentPlayingSong.HasValue) {
                            WriteLog($"[系统] 原播放歌曲已被凭空丢弃: {_currentPlayingSong.Value.SongName}", ConsoleColor.DarkGray);
                        }

                        _isPlayingEnabled = true;
                        Task.Run(() => ForcePlaySongAsync(newSong));
                    } else if (mode == "top") {
                        _targetQueue.Insert(0, newSong);
                        WriteLog($"[指令] {user.Name} 置顶点歌了: {newSong.SongName}", ConsoleColor.Green);
                        SyncHUD($"[置顶] {newSong.SongName}");
                        if (_isPlayingEnabled) {
                            shouldInsertToNCM = true;
                            insertSongId = newSong.Id;
                        }
                        MarkQueueDirty();
                    } else {
                        _targetQueue.Add(newSong);
                        WriteLog($"   |_ [入队] {newSong.FullTitle} (by {user.Name})", ConsoleColor.Green);
                        SyncHUD($"[入队] {newSong.SongName} ({user.Name})");
                        
                        if (_targetQueue.Count == 1 && _isPlayingEnabled) {
                            shouldInsertToNCM = true;
                            insertSongId = newSong.Id;
                        }
                        MarkQueueDirty();
                    }
                }
                
                if (shouldInsertToNCM && insertSongId != null) {
                    WriteLog($"[CDP] 点歌入队后立即 addToPlayList: {newSong.SongName} ({insertSongId})", ConsoleColor.Cyan);
                    await InsertNextSongViaCDP(insertSongId);
                }
                
                _userCooldowns[user.Uid] = DateTime.Now;
            } catch { }
        }

        static void TrySkipSong(UserInfo user) {
            bool canSkip = _config.SuperUsers.Contains(user.Name) || user.IsManager;
            if (!canSkip && _currentPlayingSong.HasValue) canSkip = (_currentPlayingSong.Value.OrderedByUid == user.Uid);
            if (canSkip) { 
                WriteLog($"   |_ [指令] {user.Name} 发起了切歌", ConsoleColor.Yellow);
                SyncHUD($"[切歌] 由 {user.Name} 发起"); 
                _ = TrySkipSongAsync();
            }
        }

        static async Task TrySkipSongAsync() {
            if (_config.EnableCDP) {
                string skipJs = GetFiberStoreExtractJs() + @"
                    if (_ensureStore()) {
                        window._reduxStore.dispatch({
                            type: 'async:action/doAction',
                            payload: { actionId: 'playNext', data: { eventType: 'click' } }
                        });
                    }
                ";
                var result = await SendCDPCommandAndGetResultAsync(skipJs);
                if (result != null) {
                    WriteLog("[CDP] 注入切歌指令成功！", ConsoleColor.Cyan);
                }
            }
        }

        static async Task MonitorLoopAsync() {
            while (_isMonitoring) {
                if (_isIntercepting) { await Task.Delay(100); continue; }
                
                try {
                    if (_config.EnableCDP) {
                        string js = GetFiberStoreExtractJs() + @"
                            function getPlaybackInfo() {
                                if (!_ensureStore()) return null;
                                try {
                                    const state = window._reduxStore.getState();
                                    const playingState = state.playing || {};
                                    const playingList = state.playingList?.curPlayingList || [];
                                    const currentId = playingState.resourceTrackId || playingState.onlineResourceId;
                                    const currentIndex = playingList.findIndex(item => String(item.id) === String(currentId));
                                    
                                    return JSON.stringify({
                                        currentId: currentId ? String(currentId) : null,
                                        nextId: (currentIndex !== -1 && playingList[currentIndex + 1]) ? String(playingList[currentIndex + 1].id) : null
                                    });
                                } catch (e) { return null; }
                            }
                            getPlaybackInfo();
                        ";
                        
                        var infoStr = await SendCDPCommandAndGetResultAsync(js);
                        if (!string.IsNullOrEmpty(infoStr) && infoStr != "null") {
                            var doc = JsonDocument.Parse(infoStr).RootElement;
                            string currentId = doc.TryGetProperty("currentId", out var c) && c.ValueKind != JsonValueKind.Null ? c.GetString() : null;
                            string nextId = doc.TryGetProperty("nextId", out var n) && n.ValueKind != JsonValueKind.Null ? n.GetString() : null;

                            if (currentId == null) { await Task.Delay(_config.MonitorIntervalMs); continue; }

                            bool stateChanged = false;
                            SongInfo? songToForcePlay = null;
                            string songToInsertAsNext = null;

                            lock (_queueLock) {
                                bool ncmTrackChanged = (currentId != _lastTrackTitle);
                                _lastTrackTitle = currentId;
                                
                                // MonitorLoop 现在作为备用方案，防雷达漏掉消息
                                if (ncmTrackChanged) {
                                    if (_withdrawnSongIds.Contains(currentId)) {
                                        WriteLog($"[防残歌系统/监控] 发现网易云播放了已被撤回/移除的歌曲，正在自动跳过: {currentId}", ConsoleColor.Yellow);
                                        _withdrawnSongIds.Remove(currentId);
                                        _ = TrySkipSongAsync();
                                    } else {
                                        if (_config.ShowDebugLogs) WriteLog($"[监控] 网易云切歌信号: newId={currentId}, 当前点播={_currentPlayingSong?.Id ?? "无"}, 队首={(_targetQueue.Count > 0 ? _targetQueue[0].Id : "空")}", ConsoleColor.DarkGray);
                                        
                                        if (_currentPlayingSong.HasValue && currentId == _currentPlayingSong.Value.Id) {
                                            // 正常
                                        }
                                        else if (_currentPlayingSong.HasValue && currentId != _currentPlayingSong.Value.Id) {
                                            if (_isPlayingEnabled && _targetQueue.Count > 0 && currentId == _targetQueue[0].Id) {
                                                WriteLog($"[监控] 自然衔接成功: {_targetQueue[0].SongName}", ConsoleColor.Green);
                                                _currentPlayingSong = _targetQueue[0];
                                                _targetQueue.RemoveAt(0);
                                                stateChanged = true;
                                            }
                                            else if (_isPlayingEnabled && _targetQueue.Count > 0) {
                                                if ((DateTime.Now - _lastForcePlayTime).TotalSeconds > 2.5) {
                                                    WriteLog($"[监控] 播完后切偏！期望={_targetQueue[0].SongName}({_targetQueue[0].Id})，实际={currentId}，强制纠正", ConsoleColor.Yellow);
                                                    songToForcePlay = _targetQueue[0];
                                                    _targetQueue.RemoveAt(0);
                                                }
                                            }
                                            else {
                                                WriteLog($"[监控] 点播列表已播完，清除当前播放状态", ConsoleColor.DarkGray);
                                                _currentPlayingSong = null;
                                                stateChanged = true;
                                            }
                                        }
                                        else if (!_currentPlayingSong.HasValue && _isPlayingEnabled && _targetQueue.Count > 0) {
                                            if (currentId == _targetQueue[0].Id) {
                                                WriteLog($"[监控] 检测到开始播放队首: {_targetQueue[0].SongName}", ConsoleColor.Green);
                                                _currentPlayingSong = _targetQueue[0];
                                                _targetQueue.RemoveAt(0);
                                                stateChanged = true;
                                            }
                                        }
                                    }
                                }
                                
                                if (!ncmTrackChanged && _currentPlayingSong.HasValue 
                                    && currentId != _currentPlayingSong.Value.Id
                                    && _isPlayingEnabled) {
                                    if ((DateTime.Now - _lastForcePlayTime).TotalSeconds > 5) {
                                        WriteLog($"[监控] 播放状态持续不一致：期望={_currentPlayingSong.Value.SongName}({_currentPlayingSong.Value.Id})，实际={currentId}，重新强制播放", ConsoleColor.Yellow);
                                        var retryTarget = _currentPlayingSong.Value;
                                        _currentPlayingSong = null;
                                        songToForcePlay = retryTarget;
                                    }
                                }

                                if (_isPlayingEnabled && _targetQueue.Count > 0 && songToForcePlay == null) {
                                    var expectedNext = _targetQueue[0];
                                    bool needInsert = false;
                                    
                                    if (_queueDirty) {
                                        needInsert = true;
                                        _queueDirty = false;
                                        if (_config.ShowDebugLogs) WriteLog($"[监控] 队列脏标记触发，重新同步下一首: {expectedNext.SongName}", ConsoleColor.DarkGray);
                                    }
                                    else if (nextId != expectedNext.Id) {
                                        if (_lastInsertedNextSongId != expectedNext.Id || (DateTime.Now - _lastInsertTime).TotalSeconds > 3) {
                                            needInsert = true;
                                            if (_config.ShowDebugLogs) WriteLog($"[监控] 预加载下一首: {expectedNext.SongName}({expectedNext.Id}), NCM下一首={nextId}", ConsoleColor.DarkGray);
                                        }
                                    } else {
                                        _lastInsertedNextSongId = expectedNext.Id;
                                    }
                                    
                                    if (needInsert) {
                                        songToInsertAsNext = expectedNext.Id;
                                        _lastInsertedNextSongId = expectedNext.Id;
                                        _lastInsertTime = DateTime.Now;
                                    }
                                } else if (_queueDirty) {
                                    _queueDirty = false; 
                                }
                            }

                            if (stateChanged) {
                                SyncHUD(_currentPlayingSong.HasValue ? $"[播放] {_currentPlayingSong.Value.SongName}" : "准备就绪");
                            }

                            if (songToForcePlay.HasValue) {
                                _lastForcePlayTime = DateTime.Now;
                                _ = Task.Run(() => ForcePlaySongAsync(songToForcePlay.Value));
                            }

                            if (songToInsertAsNext != null) {
                                await InsertNextSongViaCDP(songToInsertAsNext);
                            }
                        }
                    }
                } catch (Exception ex) {
                    if (_config.ShowDebugLogs) WriteLog($"[监控异常] {ex.Message}", ConsoleColor.Red);
                }
                
                await Task.Delay(_config.MonitorIntervalMs);
            }
        }

        static async Task ForcePlaySongAsync(SongInfo target) {
            _isIntercepting = true; 
            _expectedSong = target; 
            
            lock (_queueLock) {
                _currentPlayingSong = target;
                _lastTrackTitle = target.Id; 
            }
            
            SyncHUD($"[强制播放] {target.SongName}");
            WriteLog($"[插播] 正在执行强制播放: {target.FullTitle}", ConsoleColor.Magenta);
            
            if (_config.EnableCDP) {
                string jsPlayCmd = GetFiberStoreExtractJs() + $@"
                    if (_ensureStore()) {{
                        window._reduxStore.dispatch({{
                            type: 'async:action/doAction',
                            payload: {{
                                actionId: 'play',
                                data: {{
                                    resource: {{ id: String({target.Id}) }},
                                    resourceType: 'track',
                                    eventType: 'dblclick'
                                }}
                            }}
                        }});
                    }}
                ";
                var cdpSuccess = await SendCDPCommandAndGetResultAsync(jsPlayCmd);
                if (cdpSuccess != null) WriteLog($"[CDP] 强行插播 JS 注入成功！({target.Id})", ConsoleColor.Cyan);
                
                await Task.Delay(1500);

                string nextIdToInsert = null;
                lock (_queueLock) {
                    if (_isPlayingEnabled && _targetQueue.Count > 0) {
                        nextIdToInsert = _targetQueue[0].Id;
                        _lastInsertedNextSongId = nextIdToInsert;
                        _lastInsertTime = DateTime.Now;
                    }
                }
                
                if (nextIdToInsert != null) {
                    WriteLog($"[CDP] 强制播放后修正，预先添加下一首排队歌曲 ({nextIdToInsert})", ConsoleColor.DarkGray);
                    await InsertNextSongViaCDP(nextIdToInsert);
                }
            } 
            else {
                WriteLog($"[系统] CDP 调试未开启，无法强行切歌，请检查配置！", ConsoleColor.Red);
            }
            
            _isIntercepting = false; 
            SyncHUD($"[播放] {target.SongName}");
        }

        static void HandleDanmaku(byte[] bytes) {
            if (!_acceptingRequests) return;
            try {
                var json = Encoding.UTF8.GetString(bytes); using var doc = JsonDocument.Parse(json);
                string cmd = doc.RootElement.GetProperty("cmd").GetString() ?? "";
                
                if (cmd.StartsWith("DANMU_MSG")) {
                    var info = doc.RootElement.GetProperty("info"); string msg = info[1].GetString().Trim(); var userBase = info[2];
                    string rawUid = userBase[0].ValueKind == JsonValueKind.Number ? userBase[0].GetInt64().ToString() : userBase[0].ToString().Trim('"');
                    UserInfo u = new UserInfo { Uid = rawUid, Name = userBase[1].GetString(), IsManager = userBase[2].GetRawText() == "1", GuardType = info.GetArrayLength() > 7 ? info[7].GetInt32() : 0 };
                    var medal = info[3]; if (medal.GetArrayLength() >= 2) { u.MedalLevel = medal[0].GetInt32(); u.MedalName = medal[1].GetString(); }
                    
                    if (_config.ShowAllDanmaku) { WriteLog($"[弹幕] {u.Name}: {msg}", ConsoleColor.DarkGray); }

                    string lowerMsg = msg.ToLower();
                    
                    if (lowerMsg.Contains("test") || lowerMsg.Contains("ping") || lowerMsg.Contains("测试")) {
                        WriteLog($"[通讯] 收到来自 {u.Name} 的 Test 弹幕测试！(内容: {msg})", ConsoleColor.Cyan);
                        OverlayState.LastToastMsg = $"收到 {u.Name} 的连接测试";
                        OverlayState.LastToastTime = DateTime.Now.Ticks; 
                        return;
                    }

                    if (msg == "开启点歌" || msg == "关闭点歌") {
                        if (HasPermission(u, _config.ToggleAcceptPermission)) {
                            _acceptingRequests = (msg == "开启点歌");
                            WriteLog($"[指令] {u.Name} 通过弹幕{msg}", ConsoleColor.Yellow);
                            SyncHUD();
                        } else {
                            WriteLog($"   |_ [拒绝] {u.Name} 尝试{msg}，权限不足", ConsoleColor.DarkGray);
                        }
                        return;
                    }

                    if (msg.StartsWith("立即点歌")) {
                        if (HasPermission(u, _config.ForceControlPermission)) {
                            string kw = msg.Replace("立即点歌", "").Trim();
                            if (!string.IsNullOrEmpty(kw)) _ = Task.Run(() => TryRequestSong(u, kw, "play_now_drop"));
                        }
                        return;
                    }

                    if (msg.StartsWith("插队点歌")) {
                        if (HasPermission(u, _config.ForceControlPermission)) {
                            string kw = msg.Replace("插队点歌", "").Trim();
                            if (!string.IsNullOrEmpty(kw)) _ = Task.Run(() => TryRequestSong(u, kw, "play_now_keep"));
                        }
                        return;
                    }

                    if (msg.StartsWith("置顶点歌")) {
                        if (HasPermission(u, _config.PriorityPermission)) {
                            string kw = msg.Replace("置顶点歌", "").Trim();
                            if (!string.IsNullOrEmpty(kw)) _ = Task.Run(() => TryRequestSong(u, kw, "top"));
                        }
                        return;
                    }

                    if (msg.StartsWith("立即播放") || msg.StartsWith("强制播放")) {
                        if (HasPermission(u, _config.ForceControlPermission)) {
                            string kw = msg.Replace("立即播放", "").Replace("强制播放", "").Trim().ToLower();
                            lock (_queueLock) {
                                int idx = -1;
                                if (!string.IsNullOrEmpty(kw)) {
                                    idx = _targetQueue.FindIndex(s => s.SongName.ToLower().Contains(kw) || s.OrderedBy.ToLower().Contains(kw));
                                } else {
                                    idx = _targetQueue.FindLastIndex(s => s.OrderedByUid == u.Uid);
                                }

                                if (idx != -1) {
                                    var target = _targetQueue[idx];
                                    _targetQueue.RemoveAt(idx);
                                    
                                    if (_currentPlayingSong.HasValue) {
                                        WriteLog($"[系统] 原播放歌曲已被凭空丢弃: {_currentPlayingSong.Value.SongName}", ConsoleColor.DarkGray);
                                    }

                                    _isPlayingEnabled = true;
                                    Task.Run(() => ForcePlaySongAsync(target));
                                } else {
                                    WriteLog($"   |_ [失败] 无法在队列中找到需强制播放的歌: {kw}", ConsoleColor.Red);
                                }
                            }
                        }
                        return;
                    }

                    if (msg.StartsWith("插队")) {
                        if (HasPermission(u, _config.ForceControlPermission)) {
                            string kw = msg.Replace("插队", "").Trim().ToLower();
                            lock (_queueLock) {
                                int idx = -1;
                                if (!string.IsNullOrEmpty(kw)) {
                                    idx = _targetQueue.FindIndex(s => s.SongName.ToLower().Contains(kw) || s.OrderedBy.ToLower().Contains(kw));
                                } else {
                                    idx = _targetQueue.FindLastIndex(s => s.OrderedByUid == u.Uid);
                                }

                                if (idx != -1) {
                                    var target = _targetQueue[idx];
                                    _targetQueue.RemoveAt(idx);
                                    
                                    if (_currentPlayingSong.HasValue) {
                                        _targetQueue.Insert(0, _currentPlayingSong.Value);
                                        WriteLog($"[系统] 原播放歌曲已被顶回队列首位: {_currentPlayingSong.Value.SongName}", ConsoleColor.DarkGray);
                                    }

                                    _isPlayingEnabled = true;
                                    Task.Run(() => ForcePlaySongAsync(target));
                                } else {
                                    WriteLog($"   |_ [失败] 无法在队列中找到需插队播放的歌: {kw}", ConsoleColor.Red);
                                }
                            }
                        }
                        return;
                    }

                    if (msg.StartsWith("移除")) {
                        if (HasPermission(u, _config.ForceControlPermission)) {
                            string kw = msg.Substring(2).Trim().ToLower();
                            lock (_queueLock) {
                                int idx = -1;
                                if (!string.IsNullOrEmpty(kw)) {
                                    idx = _targetQueue.FindIndex(s => s.SongName.ToLower().Contains(kw) || s.OrderedBy.ToLower().Contains(kw));
                                } else {
                                    idx = _targetQueue.FindLastIndex(s => s.OrderedByUid == u.Uid);
                                }

                                if (idx != -1) {
                                    var target = _targetQueue[idx];
                                    _targetQueue.RemoveAt(idx);
                                    _withdrawnSongIds.Add(target.Id); // [新增] 加入撤回黑名单
                                    MarkQueueDirty(); 
                                    WriteLog($"[指令] {u.Name} 移除了待播歌曲 {target.SongName}", ConsoleColor.Yellow);
                                    SyncHUD($"[移除] {target.SongName}");
                                    _ = RemoveSongFromPlaylistViaCDP(target.Id); 
                                }
                            }
                        }
                        return;
                    }

                    if (msg.StartsWith("置顶") || msg.StartsWith("优先")) {
                        if (HasPermission(u, _config.PriorityPermission)) {
                            string kw = msg.Replace("置顶", "").Replace("优先", "").Trim().ToLower();
                            lock (_queueLock) {
                                int idx = -1;
                                if (!string.IsNullOrEmpty(kw)) {
                                    idx = _targetQueue.FindIndex(s => s.SongName.ToLower().Contains(kw) || s.OrderedBy.ToLower().Contains(kw));
                                } else {
                                    idx = _targetQueue.FindLastIndex(s => s.OrderedByUid == u.Uid);
                                }

                                if (idx > 0) { 
                                    var target = _targetQueue[idx];
                                    _targetQueue.RemoveAt(idx);
                                    _targetQueue.Insert(0, target);
                                    MarkQueueDirty(); 
                                    WriteLog($"[指令] {u.Name} 置顶了 {target.SongName}", ConsoleColor.Green);
                                    SyncHUD($"[置顶] {target.SongName}");
                                } else if (idx == -1) {
                                    WriteLog($"   |_ [失败] 置顶未找到目标: {kw}", ConsoleColor.Red);
                                }
                            }
                        }
                        return;
                    }

                    if (msg.StartsWith("撤回") || msg.StartsWith("取消")) {
                        if (HasPermission(u, _config.CancelPermission)) {
                            string kw = msg.Replace("撤回", "").Replace("取消", "").Trim().ToLower();
                            lock (_queueLock) {
                                int idx = -1;
                                if (!string.IsNullOrEmpty(kw)) {
                                    idx = _targetQueue.FindLastIndex(s => s.OrderedByUid == u.Uid && (s.SongName.ToLower().Contains(kw) || s.ArtistName.ToLower().Contains(kw)));
                                } else {
                                    idx = _targetQueue.FindLastIndex(s => s.OrderedByUid == u.Uid);
                                }

                                if (idx != -1) {
                                    var target = _targetQueue[idx];
                                    _targetQueue.RemoveAt(idx);
                                    _withdrawnSongIds.Add(target.Id); // [新增] 加入撤回黑名单
                                    MarkQueueDirty(); 
                                    WriteLog($"   |_ [撤回] 已移除待播歌曲: {target.SongName}", ConsoleColor.Yellow);
                                    SyncHUD($"[撤回] 成功"); 
                                    
                                    _ = RemoveSongFromPlaylistViaCDP(target.Id);
                                } 
                                else if (_currentPlayingSong.HasValue && _currentPlayingSong.Value.OrderedByUid == u.Uid && (string.IsNullOrEmpty(kw) || _currentPlayingSong.Value.SongName.ToLower().Contains(kw))) 
                                {
                                    var target = _currentPlayingSong.Value;
                                    WriteLog($"   |_ [撤回] 正在撤回当前播放的歌曲并自动切歌: {target.SongName}", ConsoleColor.Yellow);
                                    
                                    _currentPlayingSong = null;
                                    _ = TrySkipSongAsync();
                                    _ = RemoveSongFromPlaylistViaCDP(target.Id);
                                }
                            }
                        }
                        return;
                    }

                    if (msg.StartsWith("点歌")) { 
                        string kw = msg.Substring(2).Trim(); 
                        if (!string.IsNullOrEmpty(kw)) _ = Task.Run(() => TryRequestSong(u, kw, "normal")); 
                    }
                    else if (msg == "切歌" || msg == "跳过") TrySkipSong(u);
                }
            } catch { }
        }

        static async Task StartBiliConnection(CancellationToken ct) {
            if (string.IsNullOrWhiteSpace(_config.AuthJson)) return;
            while (!ct.IsCancellationRequested) {
                using var ws = new ClientWebSocket();
                try {
                    WriteLog(">>> [网络] 正在连接 B站弹幕服务器...", ConsoleColor.DarkGray);
                    await ws.ConnectAsync(new Uri("wss://broadcastlv.chat.bilibili.com/sub"), ct);
                    
                    byte[] packet = new byte[16 + _config.AuthJson.Length];
                    BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(0), packet.Length); BinaryPrimitives.WriteInt16BigEndian(packet.AsSpan(4), 16); BinaryPrimitives.WriteInt16BigEndian(packet.AsSpan(6), 1); BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(8), 7); BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(12), 1); Buffer.BlockCopy(Encoding.UTF8.GetBytes(_config.AuthJson), 0, packet, 16, _config.AuthJson.Length);
                    await ws.SendAsync(new ArraySegment<byte>(packet), WebSocketMessageType.Binary, true, CancellationToken.None);
                    
                    WriteLog(">>> [网络] 弹幕握手包已发送，监听开始。", ConsoleColor.Green);

                    _ = Task.Run(async () => { while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested) { byte[] p = new byte[16 + 15]; BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(0), p.Length); BinaryPrimitives.WriteInt16BigEndian(p.AsSpan(4), 16); BinaryPrimitives.WriteInt16BigEndian(p.AsSpan(6), 1); BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(8), 2); BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(12), 1); Buffer.BlockCopy(Encoding.UTF8.GetBytes("[object Object]"), 0, p, 16, 15); await ws.SendAsync(new ArraySegment<byte>(p), WebSocketMessageType.Binary, true, ct); await Task.Delay(30000, ct); } }, ct);
                    byte[] buffer = new byte[8192];
                    while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested) {
                        using var ms = new MemoryStream(); WebSocketReceiveResult res;
                        do { res = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct); ms.Write(buffer, 0, res.Count); } while (!res.EndOfMessage);
                        if (ms.Length > 0) {
                            ReadOnlySpan<byte> data = ms.ToArray();
                            while(data.Length >= 16) {
                                int pLen = BinaryPrimitives.ReadInt32BigEndian(data.Slice(0, 4));
                                int op = BinaryPrimitives.ReadInt32BigEndian(data.Slice(8, 4));
                                var payload = data.Slice(16, Math.Min(pLen, data.Length) - 16);
                                if (op == 5) {
                                    if (BinaryPrimitives.ReadInt16BigEndian(data.Slice(6, 2)) == 3) {
                                        using var cms = new MemoryStream(payload.ToArray()); using var bs = new BrotliStream(cms, CompressionMode.Decompress); using var outMs = new MemoryStream(); bs.CopyTo(outMs); HandleDanmaku(outMs.ToArray().Skip(16).ToArray());
                                    } else HandleDanmaku(payload.ToArray());
                                }
                                data = data.Slice(pLen);
                            }
                        }
                    }
                } catch(Exception e) { 
                    WriteLog($"[网络断开] {e.Message}，5秒后重试...", ConsoleColor.Red); 
                    if (!ct.IsCancellationRequested) await Task.Delay(5000, ct); 
                }
            }
        }
        static void RestartBiliConnection() { if (!string.IsNullOrWhiteSpace(_config.AuthJson)) { _biliCts.Cancel(); _biliCts = new CancellationTokenSource(); _ = Task.Run(() => StartBiliConnection(_biliCts.Token)); } }

        static CancellationTokenSource _radarCts = new CancellationTokenSource();
        static bool _radarRunning = false;

        static void StartCDPRadar() {
            if (_radarRunning) { _radarCts.Cancel(); _radarCts = new CancellationTokenSource(); }
            _radarRunning = true;
            _ = Task.Run(() => CDPRadarLoopAsync(_radarCts.Token));
        }

        static async Task CDPRadarLoopAsync(CancellationToken ct) {
            int consecutiveFailures = 0;
            
            while (!ct.IsCancellationRequested) {
                try {
                    string wsUrl = null;
                    for (int retry = 0; retry < 30 && !ct.IsCancellationRequested; retry++) {
                        await Task.Delay(2000, ct);
                        wsUrl = await GetCDPWebSocketUrl();
                        if (wsUrl != null) break;
                    }
                    if (wsUrl == null) {
                        WriteLog("[雷达] 30次重试后仍找不到网易云 CDP 目标，60秒后重试...", ConsoleColor.Red);
                        await Task.Delay(60000, ct);
                        continue;
                    }
                    
                    if (consecutiveFailures == 0) {
                        WriteLog("[雷达] 检测到网易云 CDP 目标，正在建立持久监听连接...", ConsoleColor.Cyan);
                    }
                    
                    using var ws = new ClientWebSocket();
                    ws.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
                    await ws.ConnectAsync(new Uri(wsUrl), ct);
                    
                    int cmdId = 1;
                    
                    await CDPSend(ws, cmdId++, "Runtime.enable", new { }, ct);
                    await CDPReadUntilId(ws, cmdId - 1, ct);
                    
                    await CDPSend(ws, cmdId++, "Runtime.addBinding", new { name = "__ncmRadarCallback" }, ct);
                    await CDPReadUntilId(ws, cmdId - 1, ct);
                    
                    string radarScript = GetFiberStoreExtractJs() + @"
                        (function() {
                            if (!_ensureStore()) { 
                                window.__ncmRadarCallback(JSON.stringify({ event: 'RADAR_INIT_FAIL', reason: 'no_store' }));
                                return; 
                            }
                            
                            const deployVersion = Date.now();
                            
                            if (window.__radarDeployed && window.__radarSubscribeAlive) {
                                window.__ncmRadarCallback(JSON.stringify({ event: 'RADAR_ALREADY_DEPLOYED' }));
                                return;
                            }
                            
                            window.__radarDeployed = true;
                            window.__radarSubscribeAlive = true;

                            const extractSongInfo = (id, list) => {
                                if (!id) return null;
                                const song = list.find(item => String(item.id) === String(id));
                                if (!song) return null;
                                return {
                                    id: String(song.id),
                                    name: song.track?.name || '未知歌曲',
                                    artist: song.track?.artists?.map(a => a.name).join('/') || '未知歌手',
                                    duration: song.track?.duration || 0
                                };
                            };

                            let state = window._reduxStore.getState();
                            let lastTrackId = state.playing?.resourceTrackId || state.playing?.onlineResourceId;

                            window.__ncmRadarCallback(JSON.stringify({ 
                                event: 'RADAR_INIT_OK', 
                                currentId: lastTrackId ? String(lastTrackId) : null 
                            }));

                            window._reduxStore.subscribe(() => {
                                try {
                                    state = window._reduxStore.getState();
                                    const currentTrackId = state.playing?.resourceTrackId || state.playing?.onlineResourceId;
                                    const curList = state.playingList?.curPlayingList || [];

                                    if (currentTrackId && currentTrackId !== lastTrackId) {
                                        const prevSong = extractSongInfo(lastTrackId, curList);
                                        const currentIndex = curList.findIndex(item => String(item.id) === String(currentTrackId));
                                        let currSong = null;
                                        let nextSong = null;

                                        if (currentIndex !== -1) {
                                            currSong = extractSongInfo(currentTrackId, curList);
                                            if (currentIndex + 1 < curList.length) {
                                                nextSong = extractSongInfo(curList[currentIndex + 1].id, curList);
                                            }
                                        } else {
                                            currSong = { id: String(currentTrackId), name: '(列表外)', artist: '', duration: 0 };
                                        }

                                        const payload = {
                                            event: 'TRACK_CHANGED',
                                            timestamp: Date.now(),
                                            previous: prevSong,
                                            current: currSong,
                                            next: nextSong
                                        };

                                        try {
                                            window.__ncmRadarCallback(JSON.stringify(payload));
                                        } catch(e) {}
                                        lastTrackId = currentTrackId;
                                    }
                                } catch(e) { }
                            });
                        })();
                    ";
                    
                    await CDPSend(ws, cmdId++, "Runtime.evaluate", new { 
                        expression = radarScript, 
                        returnByValue = true,
                        awaitPromise = false 
                    }, ct);
                    
                    WriteLog("[雷达] 挖树 + 切歌雷达脚本已注入，开始持久监听...", ConsoleColor.Green);
                    consecutiveFailures = 0;
                    
                    var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    _ = Task.Run(async () => {
                        while (!heartbeatCts.Token.IsCancellationRequested) {
                            try {
                                await Task.Delay(8000, heartbeatCts.Token);
                                if (ws.State == WebSocketState.Open) {
                                    int hbId = Interlocked.Increment(ref cmdId);
                                    await CDPSend(ws, hbId, "Runtime.evaluate", new { 
                                        expression = "1", 
                                        returnByValue = true 
                                    }, heartbeatCts.Token);
                                }
                            } catch { break; }
                        }
                    }, heartbeatCts.Token);
                    
                    var buffer = new byte[16384];
                    while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested) {
                        using var ms = new MemoryStream();
                        WebSocketReceiveResult wsRes;
                        do {
                            wsRes = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                            ms.Write(buffer, 0, wsRes.Count);
                        } while (!wsRes.EndOfMessage);
                        
                        if (wsRes.MessageType == WebSocketMessageType.Close) break;
                        
                        string msgJson = Encoding.UTF8.GetString(ms.ToArray());
                        HandleCDPMessage(msgJson);
                    }
                    
                    heartbeatCts.Cancel();
                    WriteLog("[雷达] CDP WebSocket 连接已断开，准备重连...", ConsoleColor.Yellow);
                    
                } catch (OperationCanceledException) {
                    break;
                } catch (Exception ex) {
                    consecutiveFailures++;
                    if (consecutiveFailures <= 3 || consecutiveFailures % 10 == 0) {
                        WriteLog($"[雷达] 连接异常(第{consecutiveFailures}次): {ex.Message}，重连中...", ConsoleColor.Red);
                    }
                    if (!ct.IsCancellationRequested) {
                        int delay = Math.Min(3000 * consecutiveFailures, 30000);
                        await Task.Delay(delay, ct);
                    }
                }
            }
            _radarRunning = false;
        }

        static async Task<string> GetCDPWebSocketUrl() {
            try {
                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(2);
                var jsonStr = await httpClient.GetStringAsync($"http://127.0.0.1:{_config.CdpPort}/json");
                var targets = JsonDocument.Parse(jsonStr).RootElement;
                
                foreach (var t in targets.EnumerateArray()) {
                    if (t.GetProperty("type").GetString() == "page" && t.GetProperty("url").GetString().Contains("orpheus")) {
                        return t.GetProperty("webSocketDebuggerUrl").GetString();
                    }
                }
                foreach (var t in targets.EnumerateArray()) {
                    if (t.GetProperty("type").GetString() == "page") {
                        return t.GetProperty("webSocketDebuggerUrl").GetString();
                    }
                }
            } catch { }
            return null;
        }

        static async Task CDPSend(ClientWebSocket ws, int id, string method, object @params, CancellationToken ct) {
            var payload = new Dictionary<string, object> { { "id", id }, { "method", method }, { "params", @params } };
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }

        static async Task CDPReadUntilId(ClientWebSocket ws, int targetId, CancellationToken ct) {
            var buffer = new byte[8192];
            for (int i = 0; i < 50; i++) {
                using var ms = new MemoryStream();
                WebSocketReceiveResult wsRes;
                do {
                    wsRes = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    ms.Write(buffer, 0, wsRes.Count);
                } while (!wsRes.EndOfMessage);
                
                string json = Encoding.UTF8.GetString(ms.ToArray());
                try {
                    var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("id", out var idProp) && idProp.GetInt32() == targetId) {
                        return; 
                    }
                    HandleCDPMessage(json);
                } catch { }
            }
        }

        static void HandleCDPMessage(string msgJson) {
            try {
                var doc = JsonDocument.Parse(msgJson);
                
                if (doc.RootElement.TryGetProperty("id", out _)) return;
                if (!doc.RootElement.TryGetProperty("method", out var methodProp)) return;
                string method = methodProp.GetString();
                
                if (method == "Runtime.bindingCalled") {
                    var paramsEl = doc.RootElement.GetProperty("params");
                    string bindingName = paramsEl.GetProperty("name").GetString();
                    
                    if (bindingName != "__ncmRadarCallback") return;
                    
                    string payloadStr = paramsEl.GetProperty("payload").GetString();
                    var payload = JsonDocument.Parse(payloadStr).RootElement;
                    string eventType = payload.GetProperty("event").GetString();
                    
                    switch (eventType) {
                        case "RADAR_INIT_OK":
                            string initId = payload.TryGetProperty("currentId", out var cid) && cid.ValueKind != JsonValueKind.Null ? cid.GetString() : "无";
                            WriteLog($"[雷达] ✅ 切歌雷达初始化成功！当前锚点 ID: {initId}", ConsoleColor.Green);
                            break;
                            
                        case "RADAR_INIT_FAIL":
                            string reason = payload.TryGetProperty("reason", out var r) ? r.GetString() : "未知";
                            WriteLog($"[雷达] ⚠️ 雷达初始化失败: {reason}，将自动重试", ConsoleColor.Yellow);
                            break;
                            
                        case "RADAR_ALREADY_DEPLOYED":
                            WriteLog("[雷达] 雷达已存在，跳过重复部署", ConsoleColor.DarkGray);
                            break;
                            
                        case "TRACK_CHANGED":
                            string prevName = "无", prevArtist = "", prevId = "";
                            string currName = "无", currArtist = "", currId = "";
                            string nextName = "无", nextArtist = "", nextId = "";
                            
                            if (payload.TryGetProperty("previous", out var prev) && prev.ValueKind != JsonValueKind.Null) {
                                prevName = prev.TryGetProperty("name", out var pn) ? pn.GetString() : "无";
                                prevArtist = prev.TryGetProperty("artist", out var pa) ? pa.GetString() : "";
                                prevId = prev.TryGetProperty("id", out var pi) ? pi.GetString() : "";
                            }
                            if (payload.TryGetProperty("current", out var curr) && curr.ValueKind != JsonValueKind.Null) {
                                currName = curr.TryGetProperty("name", out var cn) ? cn.GetString() : "无";
                                currArtist = curr.TryGetProperty("artist", out var ca) ? ca.GetString() : "";
                                currId = curr.TryGetProperty("id", out var ci) ? ci.GetString() : "";
                            }
                            if (payload.TryGetProperty("next", out var next) && next.ValueKind != JsonValueKind.Null) {
                                nextName = next.TryGetProperty("name", out var nn) ? nn.GetString() : "无";
                                nextArtist = next.TryGetProperty("artist", out var na) ? na.GetString() : "";
                                nextId = next.TryGetProperty("id", out var ni) ? ni.GetString() : "";
                            }
                            
                            WriteLog($"[雷达] 🔔 切歌信号！", ConsoleColor.Magenta);
                            WriteLog($"   ⏮️ 上一首: {prevArtist}{(string.IsNullOrEmpty(prevArtist) ? "" : " - ")}{prevName} ({prevId})", ConsoleColor.DarkGray);
                            WriteLog($"   ▶️ 正在播: {currArtist}{(string.IsNullOrEmpty(currArtist) ? "" : " - ")}{currName} ({currId})", ConsoleColor.White);
                            WriteLog($"   ⏭️ 下一首: {nextArtist}{(string.IsNullOrEmpty(nextArtist) ? "" : " - ")}{nextName} ({nextId})", ConsoleColor.DarkGray);

                            // ============================================
                            // ★★★ 核心修复：基于雷达信号实时同步队列状态 ★★★
                            // ============================================
                            bool stateChanged = false;
                            SongInfo? songToForcePlay = null;

                            lock (_queueLock) {
                                if (!string.IsNullOrEmpty(currId)) {
                                    // [新增] 检查当前播放的歌曲是否是已经被撤回/移除的失效残歌
                                    if (_withdrawnSongIds.Contains(currId)) {
                                        WriteLog($"[防残歌系统/雷达] 发现网易云播放了已被撤回的残余歌曲，正在自动跳过: {currName}", ConsoleColor.Yellow);
                                        _withdrawnSongIds.Remove(currId); // 移出黑名单
                                        _lastTrackTitle = currId; // 更新规避标记
                                        _ = TrySkipSongAsync(); // 直接跳过
                                    } 
                                    else {
                                        // 防止网易云可能触发重复当前歌曲的事件
                                        if (_currentPlayingSong.HasValue && currId == _currentPlayingSong.Value.Id) {
                                            // 正常播放中，无需动作
                                        }
                                        else if (_currentPlayingSong.HasValue && currId != _currentPlayingSong.Value.Id) {
                                            // A. 正在播的点播歌已结束 或被切走了
                                            if (_isPlayingEnabled && _targetQueue.Count > 0 && currId == _targetQueue[0].Id) {
                                                // A1. 完美衔接：切到的歌刚好是队列的下一首点歌
                                                WriteLog($"[状态同步] 自然衔接到下一首: {_targetQueue[0].SongName}", ConsoleColor.Green);
                                                _currentPlayingSong = _targetQueue[0];
                                                _targetQueue.RemoveAt(0);
                                                stateChanged = true;
                                            }
                                            else if (_isPlayingEnabled && _targetQueue.Count > 0) {
                                                // A2. 切偏了：切到的歌不是期望的下一首，强制纠正回去
                                                if ((DateTime.Now - _lastForcePlayTime).TotalSeconds > 2.5) {
                                                    WriteLog($"[状态同步] 播完后切偏！期望={_targetQueue[0].SongName}，实际={currName}，将强行纠正", ConsoleColor.Yellow);
                                                    songToForcePlay = _targetQueue[0];
                                                    _targetQueue.RemoveAt(0);
                                                }
                                            }
                                            else {
                                                // A3. 点播队列已空，恢复无点歌空闲状态
                                                WriteLog($"[状态同步] 点播列表已全数播完，回到空闲状态", ConsoleColor.DarkGray);
                                                _currentPlayingSong = null;
                                                stateChanged = true;
                                            }
                                        }
                                        else if (!_currentPlayingSong.HasValue && _isPlayingEnabled && _targetQueue.Count > 0) {
                                            // B. 当前没有点播显示在播，但突然切到了一首歌（网易云自动切歌，或者我们拖回了歌）
                                            if (currId == _targetQueue[0].Id) {
                                                WriteLog($"[状态同步] 自然衔接，开始播放队首点歌: {_targetQueue[0].SongName}", ConsoleColor.Green);
                                                _currentPlayingSong = _targetQueue[0];
                                                _targetQueue.RemoveAt(0);
                                                stateChanged = true;
                                            } else {
                                                // 切歌信号来了！当前空闲，代播有歌。
                                                // 需求2：只要有切歌信号而且代播有歌，立刻强制拉起代播列表！
                                                // 特殊情况排除：如果是刚刚拖拽退回引起的切歌，跳过拦截，让原歌单的歌放。
                                                if ((DateTime.Now - _lastPushToQueueTime).TotalSeconds < 2.5) {
                                                    WriteLog($"[状态同步] 这是拖拽退回触发的特殊切歌，已放行原歌单曲目...", ConsoleColor.DarkGray);
                                                } else {
                                                    WriteLog($"[状态同步] 捕捉到切歌信号！强制拉起代播列表首曲: {_targetQueue[0].SongName}", ConsoleColor.Magenta);
                                                    songToForcePlay = _targetQueue[0];
                                                    _targetQueue.RemoveAt(0);
                                                }
                                            }
                                        }
                                        
                                        _lastTrackTitle = currId; // 更新给 MonitorLoop 作为规避防撞标
                                    }
                                }
                            }

                            // 如果内部状态变更了，调用 SyncHUD 促发前端更新视图
                            if (stateChanged) {
                                SyncHUD(_currentPlayingSong.HasValue ? $"[播放] {_currentPlayingSong.Value.SongName}" : "准备就绪");
                            }

                            // 如果发现了需要强制播放的歌，启动强行播放逻辑 (这套逻辑内部已包含了添加下一首到接下来的功能)
                            if (songToForcePlay.HasValue) {
                                _lastForcePlayTime = DateTime.Now;
                                _ = Task.Run(() => ForcePlaySongAsync(songToForcePlay.Value));
                            }
                            
                            // 标记队列已改变，让原有的 MonitorLoop 在下次循环时预加载下一首歌曲
                            MarkQueueDirty();
                            
                            break;
                    }
                }
            } catch { }
        }
    }
}