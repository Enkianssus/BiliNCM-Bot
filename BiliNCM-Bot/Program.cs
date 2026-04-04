using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
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
using System.Drawing;

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

        private delegate bool EnumThreadDelegate(IntPtr hWnd, IntPtr lParam);

        private const byte VK_CONTROL = 0x11;
        private const byte VK_MENU = 0x12; 
        private const byte VK_RIGHT = 0x27;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        #endregion

        #region 配置与权限模型
        class PermissionConfig {
            public bool AllowManager { get; set; } = true;    
            public int MinGuardType { get; set; } = 0;        
            public int MinMedalLevel { get; set; } = 0;       
            public string MedalName { get; set; } = "";       

            public string GetSummary() {
                string guard = MinGuardType switch { 1 => "总督", 2 => "提督", 3 => "舰长", _ => "无" };
                return $"[房管:{(AllowManager ? "开" : "关")} | 航海:{guard} | 灯牌:{(MinGuardType == 0 ? MinMedalLevel + "+" : "跳过")}]";
            }
        }

        class AppConfig {
            public string AuthJson { get; set; } = "";
            public int CooldownMinutes { get; set; } = 0;
            public bool ShowDebugLogs { get; set; } = false;
            public bool EnableHUD { get; set; } = true; 
            public int RestoreDelayMs { get; set; } = 800; 
            public int MonitorIntervalMs { get; set; } = 300; 
            public List<string> SuperUsers { get; set; } = new List<string>(); 
            public PermissionConfig OrderPermission { get; set; } = new PermissionConfig { MinMedalLevel = 5 };
            public PermissionConfig SkipPermission { get; set; } = new PermissionConfig { AllowManager = true, MinMedalLevel = 5 };
            public PermissionConfig PriorityPermission { get; set; } = new PermissionConfig { MinGuardType = 3 };
            public PermissionConfig CancelPermission { get; set; } = new PermissionConfig { MinMedalLevel = 5 };
        }

        public struct UserInfo {
            public string Name;
            public string Uid;
            public bool IsManager;
            public int GuardType; 
            public int MedalLevel;
            public string MedalName;
        }

        public struct SongInfo {
            public string Id;
            public string SongName;
            public string ArtistName;
            public string OrderedBy; 
            public string OrderedByUid; 
            public string FullTitle => $"{ArtistName} - {SongName}";
        }
        #endregion

        #region 全局状态
        static AppConfig _config = new AppConfig();
        static CloudMusicApi _neteaseApi = new CloudMusicApi();
        static List<SongInfo> _targetQueue = new List<SongInfo>(); 
        static readonly object _queueLock = new object();
        
        static ConcurrentDictionary<string, DateTime> _userCooldowns = new ConcurrentDictionary<string, DateTime>();
        
        static string? _lastTrackTitle = null; 
        static SongInfo? _currentPlayingSong = null; 
        static SongInfo? _expectedSong = null; 
        static bool _isMonitoring = true;
        static bool _isIntercepting = false;
        static bool _acceptingRequests = true; 
        static IntPtr _cachedHandle = IntPtr.Zero;
        static int[] _cachedPids = new int[0];
        static string _currentRoomId = "未知";
        
        static CancellationTokenSource _biliCts = new CancellationTokenSource();
        static readonly object _consoleLock = new object();
        #endregion

        static async Task Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.Title = "Bili 网易云点歌机 - 管理终端";
            
            lock (_consoleLock)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("====================================================");
                Console.WriteLine("    Bili 网易云点歌机");
                Console.WriteLine("====================================================");
                Console.ResetColor();
            }

            if (!LoadConfig() || string.IsNullOrWhiteSpace(_config.AuthJson)) {
                WriteLog(">>> [初始化] 检测到配置缺失，请先完成设置。", ConsoleColor.Yellow);
                while (string.IsNullOrWhiteSpace(_config.AuthJson)) {
                    UpdateAuthJson();
                }
            }

            if (_config.EnableHUD) {
                DebugHUD.Launch("Bili点歌机 实时歌单");
                SyncHUD("点歌机已就绪");
            }

            ParseRoomId();
            _neteaseApi.Cookies.Add(new Cookie("os", "pc", "/", ".music.163.com"));
            _neteaseApi.Cookies.Add(new Cookie("appver", "2.10.6", "/", ".music.163.com"));

            Thread monitorThread = new Thread(MonitorLoop);
            monitorThread.Priority = ThreadPriority.Highest;
            monitorThread.IsBackground = true;
            monitorThread.Start();

            RestartBiliConnection();

            while (true)
            {
                PrintMenu();
                string input = Console.ReadLine()?.Trim().ToUpper() ?? "";
                Console.WriteLine();
                switch (input)
                {
                    case "1": UpdateAuthJson(); RestartBiliConnection(); break;
                    case "2": UpdateCooldown(); break;
                    case "3": 
                        _acceptingRequests = !_acceptingRequests;
                        SyncHUD(_acceptingRequests ? "点歌功能已开启" : "点歌功能已暂停");
                        WriteLog($">>> [系统] 点歌功能目前已 {(_acceptingRequests ? "开启" : "关闭")}", _acceptingRequests ? ConsoleColor.Green : ConsoleColor.Red);
                        break;
                    case "4": ShowQueue(); break;
                    case "5": ClearQueue(); break;
                    case "6": SaveConfig(); WriteLog(">>> [系统] 配置已保存。", ConsoleColor.Yellow); break;
                    case "7":
                        _config.ShowDebugLogs = !_config.ShowDebugLogs;
                        WriteLog($">>> [系统] 拦截调试日志目前已 {(_config.ShowDebugLogs ? "开启" : "关闭")}", _config.ShowDebugLogs ? ConsoleColor.Green : ConsoleColor.Red);
                        break;
                    case "8": UpdatePermissions(); break;
                    case "9": UpdateGeneralSettings(); break;
                    case "H": 
                        _config.EnableHUD = !_config.EnableHUD;
                        if (_config.EnableHUD) {
                            DebugHUD.Launch("Bili点歌机 实时歌单");
                            SyncHUD("HUD 已恢复");
                        } else {
                            WriteLog(">>> [系统] HUD 已关闭 (需重启程序生效)。", ConsoleColor.Yellow);
                        }
                        SaveConfig();
                        break;
                    case "0": Environment.Exit(0); break;
                }
            }
        }

        #region HUD 同步助手
        static void SyncHUD(string status = null, Color? color = null) {
            if (!_config.EnableHUD) return;
            List<string> displayQueue = new List<string>();
            lock(_queueLock) {
                displayQueue = _targetQueue.Select(s => $"{s.ArtistName} - {s.SongName} [{s.OrderedBy}]").ToList();
            }
            DebugHUD.Update(displayQueue, status, color);
        }
        #endregion

        #region UI 与 基础工具
        static void WriteLog(string message, ConsoleColor color = ConsoleColor.Gray) {
            lock (_consoleLock) {
                Console.ForegroundColor = color;
                Console.WriteLine(message);
                Console.ResetColor();
            }
        }

        static void PrintMenu() {
            lock (_consoleLock) {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("------------------- [ 管理菜单 ] -------------------");
                Console.ResetColor();
                Console.Write("房间: "); Console.ForegroundColor = ConsoleColor.White; Console.Write($"{_currentRoomId,-10}"); Console.ResetColor();
                Console.Write(" | 状态: ");
                if (_acceptingRequests) { Console.ForegroundColor = ConsoleColor.Green; Console.Write("● 接受点歌"); }
                else { Console.ForegroundColor = ConsoleColor.Red; Console.Write("○ 停止点歌"); }
                Console.ResetColor();
                int qCount = 0; lock(_queueLock) { qCount = _targetQueue.Count; }
                Console.Write(" | 队列: "); Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine($"{qCount} 首"); Console.ResetColor();
                
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine(" [1]修改AuthJson  [2]修改冷却  [3]开关点歌  [7]调试开关  [H]开关HUD");
                Console.WriteLine(" [4]查看歌单      [5]清空歌单  [6]保存配置  [8]权限设置  [9]通用设置  [0]退出");
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write("请输入指令 > "); Console.ResetColor();
            }
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
        #endregion

        #region 业务逻辑方法
        static async Task TryRequestSong(UserInfo user, string keyword) {
            if (!HasPermission(user, _config.OrderPermission)) {
                WriteLog($"   |_ [拒绝] 用户 {user.Name} 权限不足", ConsoleColor.DarkGray);
                return;
            }
            if (!_config.SuperUsers.Contains(user.Name) && _userCooldowns.TryGetValue(user.Uid, out DateTime last) && (DateTime.Now - last).TotalMinutes < _config.CooldownMinutes) {
                WriteLog($"   |_ [拒绝] 用户 {user.Name} 处于冷却中", ConsoleColor.DarkGray);
                return;
            }

            try {
                var res = await _neteaseApi.RequestAsync(CloudMusicApiProviders.Search, new Dictionary<string, object> { { "keywords", keyword }, { "limit", 1 } });
                var songs = res["result"]?["songs"] as JArray;
                if (songs == null || songs.Count == 0) {
                    WriteLog($"   |_ [失败] 未找到歌曲: \"{keyword}\"", ConsoleColor.Red);
                    SyncHUD($"未找到歌曲: {keyword}", Color.Red);
                    return;
                }

                var s = songs[0];
                var newSong = new SongInfo { 
                    Id = s["id"]?.ToString(), 
                    SongName = s["name"]?.ToString(), 
                    ArtistName = string.Join("/", s["artists"]?.Select(a => (string)a["name"])), 
                    OrderedBy = user.Name, 
                    OrderedByUid = user.Uid // 这里存的是处理后的 UID
                };
                
                lock (_queueLock) { _targetQueue.Add(newSong); }
                _userCooldowns[user.Uid] = DateTime.Now;
                
                WriteLog($"   |_ [已入队] {newSong.FullTitle}", ConsoleColor.Green);
                SyncHUD($"[入队] {newSong.SongName} ({user.Name})", Color.Lime);
            } catch (Exception ex) {
                WriteLog($"   |_ [异常] 搜索出错: {ex.Message}", ConsoleColor.Red);
            }
        }

        static void TrySkipSong(UserInfo user) {
            bool canSkip = _config.SuperUsers.Contains(user.Name) || user.IsManager;
            if (!canSkip && _currentPlayingSong.HasValue) {
                var cur = _currentPlayingSong.Value;
                // 使用 Uid 或 名字匹配（增加容错）
                canSkip = (cur.OrderedByUid == user.Uid || cur.OrderedBy == user.Name);
            }

            if (canSkip) {
                WriteLog($">>> [指令] {user.Name} 发起了切歌", ConsoleColor.Yellow);
                SyncHUD($"[切歌] 由 {user.Name} 发起", Color.Orange);
                SimulateNextTrackShortcut();
            } else {
                WriteLog($"   |_ [拒绝] 只有点歌人、房管或超级用户可切歌", ConsoleColor.DarkGray);
                SyncHUD("切歌失败: 权限不足", Color.Red);
            }
        }

        static void SimulateNextTrackShortcut() {
            keybd_event(VK_CONTROL, 0, 0, 0); keybd_event(VK_MENU, 0, 0, 0); keybd_event(VK_RIGHT, 0, 0, 0);
            keybd_event(VK_RIGHT, 0, KEYEVENTF_KEYUP, 0); keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, 0); keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, 0);
        }

        static void TrySetPriority(UserInfo user) {
            if (!HasPermission(user, _config.PriorityPermission)) {
                 WriteLog($"   |_ [拒绝] 用户 {user.Name} 权限不足", ConsoleColor.DarkGray);
                 SyncHUD("置顶失败: 权限不足", Color.Red);
                 return;
            }

            lock (_queueLock) {
                int lastIdx = _targetQueue.FindLastIndex(s => s.OrderedByUid == user.Uid || s.OrderedBy == user.Name);
                if (lastIdx > 0) {
                    var song = _targetQueue[lastIdx]; _targetQueue.RemoveAt(lastIdx); _targetQueue.Insert(0, song);
                    WriteLog($">>> [置顶] 已将 {user.Name} 点播的《{song.SongName}》移至队首", ConsoleColor.Green);
                    SyncHUD($"[置顶] {song.SongName} (由 {user.Name})", Color.Gold);
                } else if (lastIdx == 0) {
                    WriteLog($"   |_ [提示] 该用户的歌曲已在队首", ConsoleColor.DarkGray);
                    SyncHUD("置顶失败: 歌曲已在队首", Color.Orange);
                } else {
                    WriteLog($"   |_ [失败] 队列中未找到 {user.Name} 的待播歌曲", ConsoleColor.DarkGray);
                    SyncHUD("置顶失败: 队列中无您的歌曲", Color.Red);
                }
            }
        }

        static void TryCancelSong(UserInfo user) {
            if (!HasPermission(user, _config.CancelPermission)) {
                 WriteLog($"   |_ [拒绝] 用户 {user.Name} 权限不足", ConsoleColor.DarkGray);
                 SyncHUD("撤回失败: 权限不足", Color.Red);
                 return;
            }

            lock (_queueLock) {
                int lastIdx = _targetQueue.FindLastIndex(s => s.OrderedByUid == user.Uid || s.OrderedBy == user.Name);
                if (lastIdx != -1) {
                    var song = _targetQueue[lastIdx]; _targetQueue.RemoveAt(lastIdx);
                    WriteLog($">>> [撤回] 已移除 {user.Name} 最近点播的《{song.SongName}》", ConsoleColor.Yellow);
                    SyncHUD($"[撤回] {user.Name} 移除了歌曲", Color.Yellow);
                } else {
                    WriteLog($"   |_ [失败] 队列中未找到 {user.Name} 的待播歌曲", ConsoleColor.DarkGray);
                    SyncHUD("撤回失败: 未找到待播歌曲", Color.Red);
                }
            }
        }

        static void ClearQueue() { 
            lock (_queueLock) { _targetQueue.Clear(); } 
            WriteLog(">>> [系统] 歌单已清空。", ConsoleColor.Yellow); 
            SyncHUD("歌单已清空", Color.Yellow);
        }
        #endregion

        #region 核心监控循环
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
                            IntPtr h = FindRealMainWindow(p);
                            if (h != IntPtr.Zero) { _cachedHandle = h; break; }
                        }
                        refreshSw.Restart();
                    }
                    if (_cachedHandle == IntPtr.Zero) { Thread.Sleep(200); continue; }

                    StringBuilder sb = new StringBuilder(256);
                    GetWindowText(_cachedHandle, sb, 256);
                    string title = sb.ToString();

                    if (string.IsNullOrEmpty(title) || title == "网易云音乐" || title == "NetEase Cloud Music") { Thread.Sleep(2); continue; }

                    if (_lastTrackTitle == null) { _lastTrackTitle = title; continue; }

                    if (title != _lastTrackTitle) {
                        SongInfo? target = null;
                        lock (_queueLock) {
                            if (_targetQueue.Count > 0) {
                                if (_expectedSong.HasValue && IsMatch(title, _expectedSong.Value)) { _expectedSong = null; _lastTrackTitle = title; continue; }
                                if (IsMatch(title, _targetQueue[0])) {
                                    _currentPlayingSong = _targetQueue[0]; _targetQueue.RemoveAt(0); _lastTrackTitle = title; 
                                    SyncHUD($"[播放] {_currentPlayingSong.Value.SongName}", Color.Cyan);
                                    continue;
                                }
                                target = _targetQueue[0]; _targetQueue.RemoveAt(0);
                            } else _currentPlayingSong = null;
                        }
                        if (target.HasValue) ExecuteIntercept(_cachedPids, _cachedHandle, title, target.Value);
                        _lastTrackTitle = title;
                    }
                } catch { }
                Thread.Sleep(_config.MonitorIntervalMs);
            }
        }

        static void ExecuteIntercept(int[] pids, IntPtr handle, string offendingTitle, SongInfo target) {
            _isIntercepting = true; _expectedSong = target; _currentPlayingSong = target;
            SyncHUD($"[拦截] 正在跳转: {target.SongName}", Color.LightBlue);

            SetProcessMuteGroup(pids, true, offendingTitle);
            string uri = EncodeSongId(target.Id);
            try { Process.Start(new ProcessStartInfo { FileName = uri, UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden }); } catch { }

            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 4000) {
                StringBuilder sb = new StringBuilder(256); GetWindowText(handle, sb, 256);
                string t = sb.ToString();
                if (!string.IsNullOrEmpty(t) && t != "网易云音乐" && IsMatch(t, target)) { _lastTrackTitle = t; break; }
                Thread.Sleep(50);
            }

            Thread.Sleep(_config.RestoreDelayMs); 
            SetProcessMuteGroup(pids, false, "");
            Thread.Sleep(200); 
            _isIntercepting = false;
            SyncHUD($"[播放] {target.SongName}", Color.Cyan);
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
                            if (mute) { s.SimpleAudioVolume.Mute = true; s.SimpleAudioVolume.Volume = 0.0f; }
                            else { 
                                s.SimpleAudioVolume.Mute = false; 
                                for (float v = 0.2f; v <= 1.0f; v += 0.2f) { s.SimpleAudioVolume.Volume = v; Thread.Sleep(20); }
                            }
                        }
                    }
                }
            } catch { }
        }
        #endregion

        #region 底层协议与配置
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

        static void UpdateAuthJson() {
            Console.WriteLine("\n请输入 AuthJson:");
            string input = Console.ReadLine()?.Trim() ?? "";
            if (string.IsNullOrEmpty(input)) return;
            try {
                try { byte[] b = Convert.FromBase64String(input); input = Encoding.UTF8.GetString(b); } catch { }
                int f = input.IndexOf('{'), l = input.LastIndexOf('}');
                if (f != -1 && l > f) input = input.Substring(f, l - f + 1);
                JObject.Parse(input); _config.AuthJson = input; ParseRoomId(); SaveConfig();
                WriteLog(">>> [成功] AuthJson 已更新。", ConsoleColor.Green);
            } catch { WriteLog(">>> [错误] 解析失败。", ConsoleColor.Red); }
        }

        static void UpdateCooldown() {
            Console.Write($"请输入冷却时间 (当前: {_config.CooldownMinutes} 分): ");
            if (int.TryParse(Console.ReadLine(), out int min)) { _config.CooldownMinutes = min; SaveConfig(); }
        }

        static void ShowQueue() {
            lock (_queueLock) {
                WriteLog("--- 当前待播放歌单 ---", ConsoleColor.Yellow);
                if (_targetQueue.Count == 0) Console.WriteLine("(空)");
                else for(int i=0; i<_targetQueue.Count; i++) Console.WriteLine($"{i+1}. {_targetQueue[i].FullTitle} ({_targetQueue[i].OrderedBy})");
            }
        }

        static bool IsMatch(string title, SongInfo song) {
            string low = title.ToLower();
            return low.Contains(song.SongName.ToLower()) && song.ArtistName.Split('/').Any(a => low.Contains(a.Trim().ToLower()));
        }

        static async Task SendPacket(ClientWebSocket ws, byte[] payload, int op) {
            byte[] header = new byte[16];
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), payload.Length + 16);
            BinaryPrimitives.WriteInt16BigEndian(header.AsSpan(4), 16);
            BinaryPrimitives.WriteInt16BigEndian(header.AsSpan(6), 1);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8), op);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(12), 1);
            byte[] packet = new byte[payload.Length + 16];
            Buffer.BlockCopy(header, 0, packet, 0, 16);
            Buffer.BlockCopy(payload, 0, packet, 16, payload.Length);
            await ws.SendAsync(new ArraySegment<byte>(packet), WebSocketMessageType.Binary, true, CancellationToken.None);
        }

        static void ProcessPacket(ReadOnlySpan<byte> data) {
            if (data.Length < 16) return;
            int pLen = BinaryPrimitives.ReadInt32BigEndian(data.Slice(0, 4));
            short ver = BinaryPrimitives.ReadInt16BigEndian(data.Slice(6, 2));
            int op = BinaryPrimitives.ReadInt32BigEndian(data.Slice(8, 4));
            var payload = data.Slice(16, Math.Min(pLen, data.Length) - 16);
            if (op == 5) {
                if (ver == 3) DecompressAndProcess(payload);
                else if (ver == 0) HandleDanmaku(payload.ToArray());
            } else if (op == 8) WriteLog($">>> [成功] 已进入直播间 [{_currentRoomId}]。", ConsoleColor.Green);
            if (data.Length > pLen) ProcessPacket(data.Slice(pLen));
        }

        static void DecompressAndProcess(ReadOnlySpan<byte> compressed) {
            try {
                using var ms = new MemoryStream(compressed.ToArray());
                using var bs = new BrotliStream(ms, CompressionMode.Decompress);
                using var outMs = new MemoryStream(); bs.CopyTo(outMs); ProcessPacket(outMs.ToArray());
            } catch { }
        }

        static string EncodeSongId(string id) {
            return "orpheus://" + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{{\"type\":\"song\",\"id\":\"{id}\",\"cmd\":\"play\"}}"));
        }

        static void UpdatePermissions() {
            WriteLog("\n--- 权限设置管理 ---", ConsoleColor.Cyan);
            Console.WriteLine($"1.点歌权限: {_config.OrderPermission.GetSummary()}");
            Console.WriteLine($"2.切歌权限: {_config.SkipPermission.GetSummary()}");
            Console.WriteLine($"3.置顶权限: {_config.PriorityPermission.GetSummary()}");
            Console.WriteLine($"4.撤回权限: {_config.CancelPermission.GetSummary()}");
            Console.Write("请选择项 (1-4): ");

            string choice = Console.ReadLine();
            PermissionConfig target = choice switch { 
                "1" => _config.OrderPermission, 
                "2" => _config.SkipPermission, 
                "3" => _config.PriorityPermission, 
                "4" => _config.CancelPermission, 
                _ => null 
            };
            
            if (target == null) return;
            
            Console.WriteLine("\nA. 房管特权");
            Console.WriteLine("B. 航海等级要求 (1总督 2提督 3舰长 0无)");
            Console.WriteLine("C. 灯牌等级要求");
            Console.Write("请选择 (A/B/C): ");
            string sub = Console.ReadLine()?.ToUpper();
            
            if (sub == "A") target.AllowManager = !target.AllowManager;
            else if (sub == "B") { Console.Write("输入航海等级: "); if(int.TryParse(Console.ReadLine(), out int g)) target.MinGuardType = g; }
            else if (sub == "C") { Console.Write("输入灯牌等级: "); if(int.TryParse(Console.ReadLine(), out int l)) target.MinMedalLevel = l; }
            SaveConfig();
        }

        static void UpdateGeneralSettings() {
            WriteLog("\n--- 通用设置管理 ---", ConsoleColor.Cyan);
            Console.WriteLine($"1. 拦截恢复延迟 (当前: {_config.RestoreDelayMs}ms)");
            Console.WriteLine($"2. 监控扫描频率 (当前: {_config.MonitorIntervalMs}ms)");
            Console.WriteLine($"3. 超级用户名单: [ {string.Join(", ", _config.SuperUsers)} ]");
            Console.WriteLine("0. 返回主菜单");
            Console.Write("请选择项 (1-3): ");

            string choice = Console.ReadLine();
            if (choice == "1") {
                Console.Write("请输入新的恢复延迟 (ms): ");
                if (int.TryParse(Console.ReadLine(), out int ms)) { _config.RestoreDelayMs = ms; SaveConfig(); }
            } else if (choice == "2") {
                Console.Write("请输入新的监控扫描频率 (ms): ");
                if (int.TryParse(Console.ReadLine(), out int ms)) { _config.MonitorIntervalMs = ms; SaveConfig(); }
            } else if (choice == "3") {
                Console.WriteLine("\nA. 添加  B. 删除  C. 清空");
                string sub = Console.ReadLine()?.ToUpper();
                if (sub == "A") { Console.Write("输入名: "); string n = Console.ReadLine()?.Trim(); if(!string.IsNullOrEmpty(n)) _config.SuperUsers.Add(n); }
                else if (sub == "B") { Console.Write("输入名: "); string n = Console.ReadLine()?.Trim(); _config.SuperUsers.Remove(n); }
                else if (sub == "C") _config.SuperUsers.Clear();
                SaveConfig();
            }
        }

        static IntPtr FindRealMainWindow(Process proc) {
            IntPtr best = IntPtr.Zero;
            try {
                foreach (ProcessThread t in proc.Threads) {
                    EnumThreadWindows(t.Id, (hWnd, lp) => {
                        StringBuilder sb = new StringBuilder(256); GetWindowText(hWnd, sb, 256);
                        string s = sb.ToString();
                        if (!string.IsNullOrEmpty(s) && !s.Contains("桌面歌词")) {
                            if (s.Contains(" - ")) { best = hWnd; return false; }
                            if (s.Contains("网易云") && best == IntPtr.Zero) best = hWnd;
                        }
                        return true;
                    }, IntPtr.Zero);
                    if (best != IntPtr.Zero) break;
                }
            } catch { }
            return best;
        }

        static void HandleDanmaku(byte[] bytes) {
            if (!_acceptingRequests) return;
            try {
                var json = Encoding.UTF8.GetString(bytes);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.GetProperty("cmd").GetString() == "DANMU_MSG") {
                    var info = doc.RootElement.GetProperty("info"); string msg = info[1].GetString().Trim();
                    var userBase = info[2];
                    
                    // 核心修复：确保 UID 是纯数字字符串，避免格式干扰
                    string rawUid = userBase[0].ValueKind == JsonValueKind.Number 
                        ? userBase[0].GetInt64().ToString() 
                        : userBase[0].ToString().Trim('"');

                    UserInfo u = new UserInfo { 
                        Uid = rawUid, 
                        Name = userBase[1].GetString(), 
                        IsManager = userBase[2].GetRawText() == "1", 
                        GuardType = info.GetArrayLength() > 7 ? info[7].GetInt32() : 0 
                    };

                    var medal = info[3]; 
                    if (medal.GetArrayLength() >= 2) { 
                        u.MedalLevel = medal[0].GetInt32(); 
                        u.MedalName = medal[1].GetString(); 
                    }

                    if (msg.StartsWith("点歌")) { 
                        string kw = msg.Substring(2).Trim(); 
                        if (!string.IsNullOrEmpty(kw)) _ = Task.Run(() => TryRequestSong(u, kw)); 
                    }
                    else if (msg == "切歌" || msg == "跳过") TrySkipSong(u);
                    else if (msg == "置顶") TrySetPriority(u);
                    else if (msg == "撤回" || msg == "取消") TryCancelSong(u);
                }
            } catch { }
        }

        static async Task StartBiliConnection(CancellationToken ct) {
            if (string.IsNullOrWhiteSpace(_config.AuthJson)) return;
            while (!ct.IsCancellationRequested) {
                using var ws = new ClientWebSocket();
                try {
                    await ws.ConnectAsync(new Uri("wss://broadcastlv.chat.bilibili.com/sub"), ct);
                    await SendPacket(ws, Encoding.UTF8.GetBytes(_config.AuthJson), 7);
                    _ = Task.Run(async () => { while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested) { await SendPacket(ws, Encoding.UTF8.GetBytes("[object Object]"), 2); await Task.Delay(30000, ct); } }, ct);
                    byte[] buffer = new byte[8192];
                    while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested) {
                        using var ms = new MemoryStream(); WebSocketReceiveResult res;
                        do { res = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct); ms.Write(buffer, 0, res.Count); } while (!res.EndOfMessage);
                        if (ms.Length > 0) ProcessPacket(ms.ToArray());
                    }
                } catch { if (!ct.IsCancellationRequested) await Task.Delay(5000, ct); }
            }
        }

        static void ParseRoomId() { try { _currentRoomId = JObject.Parse(_config.AuthJson)["roomid"]?.ToString() ?? "未知"; } catch { } }
        static void RestartBiliConnection() { if (!string.IsNullOrWhiteSpace(_config.AuthJson)) { _biliCts.Cancel(); _biliCts = new CancellationTokenSource(); _ = Task.Run(() => StartBiliConnection(_biliCts.Token)); } }
        #endregion
    }
}