using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace SpireReplay.SpireReplayCode;

public static class ReplayCode
{
    public const int MaxJsonBytes = 32 * 1024 * 1024;
    public const int MaxCodeChars = 12 * 1024 * 1024;
    private static readonly JsonSerializerOptions Compact = new(ReplayJson.Options)
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    public static string Export(RunRecording run)
    {
        RecordingLocation.NormalizeArchitect(run);
        Validate(run);
        byte[] json = PortableRun.Encode(run, Compact);
        if (json.Length > MaxJsonBytes) throw new InvalidDataException("记录过大，无法生成复盘码");
        using var output = new MemoryStream();
        using (var zip = new BrotliStream(output, CompressionLevel.SmallestSize, true)) zip.Write(json);
        byte[] data = output.ToArray();
        string code = "SPR2." + Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_') + "." + Convert.ToHexString(SHA256.HashData(data));
        if (code.Length > MaxCodeChars) throw new InvalidDataException("复盘码过长");
        return code;
    }
    public static RunRecording Import(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > MaxCodeChars) throw new InvalidDataException("复盘码为空或过长");
        code = string.Concat(code.Where(c => !char.IsWhiteSpace(c)));
        var parts = code.Split('.');
        if (parts.Length != 3 || parts[0] is not ("SPR1" or "SPR2")) throw new InvalidDataException("复盘码格式或版本不支持（支持 SPR1 / SPR2）");
        try
        {
            string payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
            byte[] bytes = Convert.FromBase64String(payload);
            if (parts[2].Length != 64 || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(parts[2], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("复盘码校验失败，可能复制不完整或已损坏");
            using var compressed = new MemoryStream(bytes);
            using var zip = new BrotliStream(compressed, CompressionMode.Decompress);
            using var json = new MemoryStream();
            byte[] buffer = new byte[8192]; int count;
            while ((count = zip.Read(buffer)) != 0)
            {
                if (json.Length + count > MaxJsonBytes) throw new InvalidDataException("复盘码解压后的记录过大");
                json.Write(buffer, 0, count);
            }
            byte[] decoded = json.ToArray();
            if (parts[0] == "SPR2")
            {
                var portable = System.Text.Json.Nodes.JsonNode.Parse(decoded) as System.Text.Json.Nodes.JsonObject ?? throw new InvalidDataException("复盘记录格式无效");
                PortableRun.Restore(portable);
                decoded = JsonSerializer.SerializeToUtf8Bytes(portable, Compact);
            }
            var run = JsonSerializer.Deserialize<RunRecording>(decoded, Compact) ?? throw new InvalidDataException("复盘码没有对局数据");
            Validate(run);
            CancelledSelectionFilter.Clean(run);
            RecordingLocation.NormalizeArchitect(run);
            return run;
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentException)
        { throw new InvalidDataException("复盘码内容无效：" + e.Message, e); }
    }
    public static void Validate(RunRecording run)
    {
        if (run.FormatVersion != 1 || string.IsNullOrWhiteSpace(run.RunId) || run.RunId.Length > 1024 ||
            string.IsNullOrWhiteSpace(run.Seed) || string.IsNullOrWhiteSpace(run.CharacterId) ||
            run.Areas == null || run.Areas.Count is < 1 or > 30 || run.Areas.Any(a => a == null || string.IsNullOrEmpty(a.Id)) ||
            run.Rooms == null || run.Rooms.Count is < 1 or > 2000 || run.Rooms.Any(r => r == null || r.Events == null))
            throw new InvalidDataException("复盘记录缺少必要信息或数据格式不支持");
        if (run.Rooms.GroupBy(r => (r.Act, r.Floor, r.RoomId)).Any(g => g.Count() != 1))
            throw new InvalidDataException("复盘记录包含重复房间");
        long events = 0;
        foreach (var room in run.Rooms)
        {
            if (room.Events.Any(e => e == null || string.IsNullOrEmpty(e.Type))) throw new InvalidDataException("房间操作数据无效");
            events += room.Events.Count;
            if (room.Battle is { } battle)
            {
                if (battle.Events == null || battle.Events.Any(e => e == null || string.IsNullOrEmpty(e.Type)) ||
                    battle.Act != room.Act || battle.TotalFloor != room.Floor || battle.RoomId != room.RoomId)
                    throw new InvalidDataException("战斗与所属房间不一致");
                events += battle.Events.Count;
            }
        }
        if (events > 250000) throw new InvalidDataException("复盘操作数量过多");
    }
}
