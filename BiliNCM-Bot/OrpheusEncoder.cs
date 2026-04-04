using System;
using System.Text;
using System.Text.Json;

public class OrpheusEncoder
{
    // 定义数据结构，方便后续扩展
    public class OrpheusCommand
    {
        public string type { get; set; } = "song";
        public string id { get; set; }
        public string cmd { get; set; } = "play";
    }

    /// <summary>
    /// 将歌曲 ID 转换为加密的 orpheus 协议链接
    /// </summary>
    /// <param name="songId">歌曲的 ID 字符串</param>
    /// <returns>完整的 orpheus:// 链接</returns>
    public static string EncodeSongId(string songId)
    {
        if (string.IsNullOrEmpty(songId)) return string.Empty;

        // 1. 构建对象
        var payload = new OrpheusCommand { id = songId };

        // 2. 序列化为 JSON 字符串
        string jsonString = JsonSerializer.Serialize(payload);

        // 3. 将 JSON 字符串转为 UTF-8 字节数组
        byte[] bytes = Encoding.UTF8.GetBytes(jsonString);

        // 4. 转为 Base64 编码
        string base64Payload = Convert.ToBase64String(bytes);

        // 5. 拼接协议头
        return $"orpheus://{base64Payload}";
    }
}