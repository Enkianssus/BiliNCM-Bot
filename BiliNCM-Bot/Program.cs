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
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Velopack;
using Velopack.Sources;

namespace BiliNetEaseIntegratedApp
{
    class Program
    {
        #region Win32 API & Constants
        [DllImport("user32.dll")]
        private static extern bool EnumThreadWindows(int dwThreadId, EnumThreadDelegate lpfn, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, uint dwExtraInfo);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private delegate bool EnumThreadDelegate(IntPtr hWnd, IntPtr lParam);

        private const byte VK_CONTROL = 0x11;
        private const byte VK_MENU = 0x12; 
        private const byte VK_RIGHT = 0x27;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
        #endregion

        #region 配置与数据模型
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
            public bool ShowDebugLogs { get; set; } = false;
            
            // 新增功能：是否在日志中打印收到的所有弹幕（默认关闭）
            public bool ShowAllDanmaku { get; set; } = false;

            public bool EnableHUD { get; set; } = true; 
            public int RestoreDelayMs { get; set; } = 800; 
            public int MonitorIntervalMs { get; set; } = 300; 
            public List<string> SuperUsers { get; set; } = new List<string>(); 
            public PermissionConfig OrderPermission { get; set; } = new PermissionConfig { MinMedalLevel = 5 };
            public PermissionConfig SkipPermission { get; set; } = new PermissionConfig { AllowManager = true, MinMedalLevel = 5 };
            public PermissionConfig PriorityPermission { get; set; } = new PermissionConfig { MinGuardType = 3 };
            public PermissionConfig CancelPermission { get; set; } = new PermissionConfig { MinMedalLevel = 5 };
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
        #endregion

        #region 全局状态 & Web API数据
        static AppConfig _config = new AppConfig();
        static CloudMusicApi _neteaseApi = new CloudMusicApi();
        static List<SongInfo> _targetQueue = new List<SongInfo>(); 
        static readonly object _queueLock = new object();
        
        static ConcurrentDictionary<string, DateTime> _userCooldowns = new ConcurrentDictionary<string, DateTime>();
        static ConcurrentDictionary<string, string> _avatarCache = new ConcurrentDictionary<string, string>();
        
        static string? _lastTrackTitle = null; 
        static SongInfo? _currentPlayingSong = null; 
        static SongInfo? _expectedSong = null; 
        static bool _isMonitoring = true;
        static bool _isIntercepting = false;
        static bool _acceptingRequests = true; 
        static IntPtr _cachedHandle = IntPtr.Zero;
        static int[] _cachedPids = new int[0];
        
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
        #endregion

        static async Task Main(string[] args)
        {
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

            Thread monitorThread = new Thread(MonitorLoop);
            monitorThread.Priority = ThreadPriority.Highest;
            monitorThread.IsBackground = true;
            monitorThread.Start();

            if (_config.RoomId > 0 && !string.IsNullOrEmpty(_config.BiliCookie)) {
                WriteLog($">>> [系统] 检测到历史房间配置，自动尝试连接直播间: {_config.RoomId}", ConsoleColor.Yellow);
                _ = Task.Run(() => ConnectToLiveRoom(_config.RoomId));
            }

            try { Process.Start(new ProcessStartInfo { FileName = "http://localhost:5555/", UseShellExecute = true }); } catch { }

            await Task.Delay(Timeout.Infinite);
        }

        #region HTTP WebServer & Admin API 模块
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

                if (urlPath == "/api/exit" && req.HttpMethod == "POST") {
                    WriteLog(">>> [系统] 收到前端彻底关闭指令，正在退出...", ConsoleColor.Red);
                    res.ContentType = "application/json; charset=utf-8";
                    byte[] ebytes = Encoding.UTF8.GetBytes("{\"success\":true}");
                    res.OutputStream.Write(ebytes, 0, ebytes.Length);
                    res.Close();
                    Environment.Exit(0);
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
                        toast = new { msg = OverlayState.LastToastMsg, time = OverlayState.LastToastTime }
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
        #endregion

        #region Bilibili 扫码、房间连接逻辑
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
                
                _qrLoginStatus = "登录完成，请前往“运行状态”连接直播间！";
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
        #endregion
        
        #region 底层监控、音量控制与核心拦截
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

        static async Task TryRequestSong(UserInfo user, string keyword) {
            if (!HasPermission(user, _config.OrderPermission)) {
                WriteLog($"   |_ [拒绝] {user.Name} 权限不足", ConsoleColor.DarkGray);
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
                
                lock (_queueLock) { _targetQueue.Add(newSong); }
                _userCooldowns[user.Uid] = DateTime.Now;
                WriteLog($"   |_ [入队] {newSong.FullTitle} (by {user.Name})", ConsoleColor.Green);
                SyncHUD($"[入队] {newSong.SongName} ({user.Name})");
            } catch { }
        }

        static void TrySkipSong(UserInfo user) {
            bool canSkip = _config.SuperUsers.Contains(user.Name) || user.IsManager;
            if (!canSkip && _currentPlayingSong.HasValue) canSkip = (_currentPlayingSong.Value.OrderedByUid == user.Uid);
            if (canSkip) { 
                WriteLog($"   |_ [指令] {user.Name} 发起了切歌", ConsoleColor.Yellow);
                SyncHUD($"[切歌] 由 {user.Name} 发起"); SimulateNextTrackShortcut(); 
            }
        }

        static void SimulateNextTrackShortcut() {
            keybd_event(VK_CONTROL, 0, 0, 0); keybd_event(VK_MENU, 0, 0, 0); keybd_event(VK_RIGHT, 0, 0, 0);
            keybd_event(VK_RIGHT, 0, KEYEVENTF_KEYUP, 0); keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, 0); keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, 0);
        }

        static void TrySetPriority(UserInfo user) {
            if (!HasPermission(user, _config.PriorityPermission)) return;
            lock (_queueLock) {
                int lastIdx = _targetQueue.FindLastIndex(s => s.OrderedByUid == user.Uid);
                if (lastIdx > 0) {
                    var song = _targetQueue[lastIdx]; _targetQueue.RemoveAt(lastIdx); _targetQueue.Insert(0, song);
                    WriteLog($"   |_ [置顶] 已将 {song.SongName} 移至队首", ConsoleColor.Green);
                    SyncHUD($"[置顶] {song.SongName}");
                }
            }
        }

        static void TryCancelSong(UserInfo user) {
            if (!HasPermission(user, _config.CancelPermission)) return;
            lock (_queueLock) {
                int lastIdx = _targetQueue.FindLastIndex(s => s.OrderedByUid == user.Uid);
                if (lastIdx != -1) { 
                    var song = _targetQueue[lastIdx];
                    _targetQueue.RemoveAt(lastIdx); 
                    WriteLog($"   |_ [撤回] 已移除 {song.SongName}", ConsoleColor.Yellow);
                    SyncHUD($"[撤回] 成功"); 
                }
            }
        }

        static void MonitorLoop() {
            Stopwatch refreshSw = Stopwatch.StartNew();
            while (_isMonitoring) {
                if (_isIntercepting) { Thread.Sleep(100); continue; }
                try {
                    if (!IsWindow(_cachedHandle) || refreshSw.ElapsedMilliseconds > 2000) {
                        var procs = Process.GetProcessesByName("cloudmusic").Concat(Process.GetProcessesByName("NetEase Cloud Music")).ToArray();
                        if (procs.Length == 0) { _cachedHandle = IntPtr.Zero; Thread.Sleep(1000); continue; }
                        _cachedPids = procs.Select(p => p.Id).Distinct().ToArray();
                        foreach (var p in procs) {
                            IntPtr h = IntPtr.Zero;
                            foreach (ProcessThread t in p.Threads) {
                                EnumThreadWindows(t.Id, (hw, lp) => {
                                    StringBuilder sb = new StringBuilder(256); GetWindowText(hw, sb, 256);
                                    if (!string.IsNullOrEmpty(sb.ToString()) && !sb.ToString().Contains("桌面歌词")) {
                                        if (sb.ToString().Contains(" - ")) { h = hw; return false; }
                                        if (sb.ToString().Contains("网易云") && h == IntPtr.Zero) h = hw;
                                    }
                                    return true;
                                }, IntPtr.Zero);
                                if (h != IntPtr.Zero) break;
                            }
                            if (h != IntPtr.Zero) { _cachedHandle = h; break; }
                        }
                        refreshSw.Restart();
                    }
                    if (_cachedHandle == IntPtr.Zero) { Thread.Sleep(200); continue; }

                    StringBuilder sb = new StringBuilder(256); GetWindowText(_cachedHandle, sb, 256); string title = sb.ToString();
                    if (string.IsNullOrEmpty(title) || title == "网易云音乐" || title == "NetEase Cloud Music") { Thread.Sleep(2); continue; }

                    if (_lastTrackTitle == null) { _lastTrackTitle = title; continue; }
                    if (title != _lastTrackTitle) {
                        SongInfo? target = null; bool cleared = false;
                        lock (_queueLock) {
                            if (_targetQueue.Count > 0) {
                                if (_expectedSong.HasValue && IsMatch(title, _expectedSong.Value)) { _expectedSong = null; _lastTrackTitle = title; continue; }
                                if (IsMatch(title, _targetQueue[0])) { _currentPlayingSong = _targetQueue[0]; _targetQueue.RemoveAt(0); _lastTrackTitle = title; SyncHUD($"[播放] {_currentPlayingSong.Value.SongName}"); continue; }
                                target = _targetQueue[0]; _targetQueue.RemoveAt(0);
                            } else {
                                if (_currentPlayingSong.HasValue && !IsMatch(title, _currentPlayingSong.Value)) { _currentPlayingSong = null; cleared = true; }
                            }
                        }
                        if (target.HasValue) ExecuteIntercept(_cachedPids, _cachedHandle, title, target.Value); else if (cleared) SyncHUD("准备就绪");
                        _lastTrackTitle = title;
                    }
                } catch { }
                Thread.Sleep(_config.MonitorIntervalMs);
            }
        }

        static bool IsMatch(string title, SongInfo song) {
            string low = title.ToLower(); return low.Contains(song.SongName.ToLower()) && song.ArtistName.Split('/').Any(a => low.Contains(a.Trim().ToLower()));
        }

        static void SetProcessMuteGroup(int[] pids, bool mute, string offendingTitle) {
            try {
                MMDeviceEnumerator devEnum = new MMDeviceEnumerator();
                var devices = devEnum.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                HashSet<uint> pidSet = new HashSet<uint>(pids.Select(p => (uint)p));
                foreach (var device in devices) {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (int i = 0; i < sessions.Count; i++) {
                        var s = sessions[i];
                        if ((s.GetProcessID != 0 && pidSet.Contains(s.GetProcessID)) || (s.DisplayName != null && (s.DisplayName.Contains("网易云") || s.DisplayName.Contains("netease")))) {
                            if (mute) { 
                                s.SimpleAudioVolume.Mute = true; 
                                s.SimpleAudioVolume.Volume = 0.0f; 
                            } else { 
                                s.SimpleAudioVolume.Mute = false; 
                                for (float v = 0.2f; v <= 1.0f; v += 0.2f) { 
                                    s.SimpleAudioVolume.Volume = v; 
                                    Thread.Sleep(20); 
                                }
                            }
                        }
                    }
                }
            } catch { }
        }

        static void ExecuteIntercept(int[] pids, IntPtr handle, string offendingTitle, SongInfo target) {
            _isIntercepting = true; _expectedSong = target; _currentPlayingSong = target;
            SyncHUD($"[拦截] 正在跳转: {target.SongName}");
            WriteLog($"[拦截] 切歌音量压制 -> 播放: {target.FullTitle}", ConsoleColor.Magenta);
            
            SetProcessMuteGroup(pids, true, offendingTitle);
            
            try { Process.Start(new ProcessStartInfo { FileName = "orpheus://" + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{{\"type\":\"song\",\"id\":\"{target.Id}\",\"cmd\":\"play\"}}")), UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden }); } catch { }
            
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 4000) {
                StringBuilder sb = new StringBuilder(256); GetWindowText(handle, sb, 256);
                string t = sb.ToString(); if (!string.IsNullOrEmpty(t) && t != "网易云音乐" && IsMatch(t, target)) { _lastTrackTitle = t; break; }
                Thread.Sleep(50);
            }
            
            Thread.Sleep(_config.RestoreDelayMs); 
            SetProcessMuteGroup(pids, false, "");
            
            _isIntercepting = false; SyncHUD($"[播放] {target.SongName}");
            WriteLog($"[恢复] 音量已恢复", ConsoleColor.Magenta);
        }

        static void HandleDanmaku(byte[] bytes) {
            if (!_acceptingRequests) return;
            try {
                var json = Encoding.UTF8.GetString(bytes); using var doc = JsonDocument.Parse(json);
                string cmd = doc.RootElement.GetProperty("cmd").GetString() ?? "";
                
                // 【核心修复】：不再用 "=="，而是用 StartsWith 兼容 B站最新的 "DANMU_MSG:4:0:2:2:2:0" 格式后缀
                if (cmd.StartsWith("DANMU_MSG")) {
                    var info = doc.RootElement.GetProperty("info"); string msg = info[1].GetString().Trim(); var userBase = info[2];
                    string rawUid = userBase[0].ValueKind == JsonValueKind.Number ? userBase[0].GetInt64().ToString() : userBase[0].ToString().Trim('"');
                    UserInfo u = new UserInfo { Uid = rawUid, Name = userBase[1].GetString(), IsManager = userBase[2].GetRawText() == "1", GuardType = info.GetArrayLength() > 7 ? info[7].GetInt32() : 0 };
                    var medal = info[3]; if (medal.GetArrayLength() >= 2) { u.MedalLevel = medal[0].GetInt32(); u.MedalName = medal[1].GetString(); }
                    
                    // 新增：全局弹幕监控输出（用于在日志中查看所有人发送的任何弹幕）
                    if (_config.ShowAllDanmaku) {
                        WriteLog($"[弹幕] {u.Name}: {msg}", ConsoleColor.DarkGray);
                    }

                    string lowerMsg = msg.ToLower();
                    if (lowerMsg.Contains("test") || lowerMsg.Contains("测试")) {
                        WriteLog($"[通讯] 收到来自 {u.Name} 的弹幕测试！(内容: {msg})", ConsoleColor.Cyan);
                        OverlayState.LastToastMsg = $"收到 {u.Name} 的连接测试";
                        OverlayState.LastToastTime = DateTime.Now.Ticks; 
                        return;
                    }

                    if (msg.StartsWith("点歌")) { string kw = msg.Substring(2).Trim(); if (!string.IsNullOrEmpty(kw)) _ = Task.Run(() => TryRequestSong(u, kw)); }
                    else if (msg == "切歌" || msg == "跳过") TrySkipSong(u);
                    else if (msg == "置顶") TrySetPriority(u); else if (msg == "撤回" || msg == "取消") TryCancelSong(u);
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
        #endregion
    }
}