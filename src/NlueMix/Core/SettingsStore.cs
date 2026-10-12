using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NlueMix.Core;

/// <summary>设置持久化：%LOCALAPPDATA%\NlueMix\settings.json。</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NlueMix");

    public static string FilePath => Path.Combine(Dir, "settings.json");

    /// <summary>
    /// 风险提示文案的版本号。
    ///
    /// 只要权限清单 / 已知问题 / 安全风险有实质改动，就把这个号加一 ——
    /// 所有用户（包括点过"永久关闭"的）会被重新要求确认一次。
    /// 这是信息披露该有的样子：内容变了，同意就失效。
    /// </summary>
    public const string NoticeVersion = "2026-10-12.1";

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (loaded is not null)
                {
                    loaded.TrustedPhones ??= new List<string>();
                    loaded.RecentSpeakers ??= new List<string>();
                    if (string.IsNullOrWhiteSpace(loaded.PairCode) || loaded.PairCode.Length != 4)
                        loaded.PairCode = Random.Shared.Next(1000, 9999).ToString();
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("读取设置失败，使用默认值: " + ex.Message);
        }

        var fresh = new AppSettings { PairCode = Random.Shared.Next(1000, 9999).ToString() };
        return fresh;
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn("保存设置失败: " + ex.Message);
        }
    }
}
