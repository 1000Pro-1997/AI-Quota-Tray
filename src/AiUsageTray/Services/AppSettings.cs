using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiUsageTray.Services;

/// <summary>프로젝트 정보. 문의 창구를 여는 데 쓴다.</summary>
public static class AppInfo
{
    public const string Name = "AI Quota Tray";
    public const string Repository = "https://github.com/1000Pro-1997/AI-Quota-Tray";
    public const string IssuesUrl = Repository + "/issues";

    /// <summary>표시용 버전. 어셈블리에서 읽어 "1.0.0" 형태로.</summary>
    public static string Version =>
        System.Reflection.Assembly.GetExecutingAssembly()
            .GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";
}

/// <summary>사용량을 어느 쪽 기준으로 보여줄지.</summary>
public enum DisplayMode
{
    /// <summary>남은 양. "18% 남음"</summary>
    Remaining,

    /// <summary>쓴 양. "82% 사용"</summary>
    Used,
}

/// <summary>사용자 설정. %APPDATA%\AiUsageTray\settings.json 에 저장된다.</summary>
public sealed class AppSettings
{
    /// <summary>Claude 자격증명 파일 경로. 비우면 기본 위치를 자동 탐지한다.</summary>
    public string ClaudeCredentialsPath { get; set; } = "";

    /// <summary>Codex 세션 로그 폴더. 비우면 기본 위치를 자동 탐지한다.</summary>
    public string CodexSessionsPath { get; set; } = "";

    public bool ClaudeEnabled { get; set; } = true;
    public bool CodexEnabled { get; set; } = true;

    /// <summary>자동 새로고침 주기(초).</summary>
    public int RefreshIntervalSeconds { get; set; } = 600;

    public bool StartWithWindows { get; set; } = true;

    /// <summary>트레이 아이콘을 숨김 영역에서 꺼내 작업표시줄에 항상 보이게 한다.</summary>
    public bool ShowInTaskbar { get; set; } = true;

    /// <summary>작업표시줄 옆에 가로 막대로 사용량을 항상 띄운다.</summary>
    public bool ShowWidgetBar { get; set; } = true;

    /// <summary>설정 마법사를 이미 완료했는지.</summary>
    public bool SetupCompleted { get; set; }

    /// <summary>새 버전을 마지막으로 확인한 시각. 하루에 한 번만 묻는다.</summary>
    public DateTime LastUpdateCheck { get; set; } = DateTime.MinValue;

    /// <summary>UI 언어. 비우면 첫 실행 때 시스템 언어로 정한다.</summary>
    public string Language { get; set; } = "";

    /// <summary>숫자를 남은 양으로 볼지, 쓴 양으로 볼지.</summary>
    public DisplayMode DisplayMode { get; set; } = DisplayMode.Remaining;

    /// <summary>Claude 진행률 바 색. #RRGGBB.</summary>
    public string ClaudeColor { get; set; } = DefaultClaudeColor;

    /// <summary>Codex 진행률 바 색. #RRGGBB.</summary>
    public string CodexColor { get; set; } = DefaultCodexColor;

    public const string DefaultClaudeColor = "#F7A32D"; // 주황
    public const string DefaultCodexColor = "#58A6FF";  // 파랑

    /// <summary>공급자 이름으로 색을 찾는다. 모르는 이름이면 회색.</summary>
    public string ColorFor(string provider) => provider switch
    {
        "Claude" => ClaudeColor,
        "Codex" => CodexColor,
        _ => "#8B8B8B",
    };

    // ---- 경로 확정 ----

    [JsonIgnore]
    public string EffectiveClaudePath =>
        !string.IsNullOrWhiteSpace(ClaudeCredentialsPath) ? ClaudeCredentialsPath : DefaultClaudePath;

    [JsonIgnore]
    public string EffectiveCodexPath =>
        !string.IsNullOrWhiteSpace(CodexSessionsPath) ? CodexSessionsPath : DefaultCodexPath;

    public static string DefaultClaudePath =>
        Path.Combine(Home, ".claude", ".credentials.json");

    public static string DefaultCodexPath =>
        Path.Combine(Home, ".codex", "sessions");

    /// <summary>자동 업데이트 런처의 표준 설치 위치.</summary>
    public static string LauncherPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AI Quota Tray", "Launcher.exe");

    private static string Home =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // ---- 저장/불러오기 ----

    public static string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AiQuotaTray");

    public static string SettingsFile => Path.Combine(SettingsDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// 설치돼 있는 도구만 켠다. 첫 실행 때 한 번 부른다.
    /// 없는 도구를 켜두면 팝업이 오류로 가득 차 보인다.
    /// </summary>
    public void DetectInstalledTools()
    {
        ClaudeEnabled = File.Exists(EffectiveClaudePath);
        CodexEnabled = Directory.Exists(EffectiveCodexPath);

        // 둘 다 없으면 사용자가 나중에 켤 수 있도록 그대로 둔다.
        // 하나라도 켜졌으면 그 상태가 맞다.
        if (!ClaudeEnabled && !CodexEnabled)
        {
            ClaudeEnabled = true;
            CodexEnabled = true;
        }
    }

    /// <summary>
    /// 모든 설정을 처음 상태로 되돌린다. 언어와 경로, 색, 표기까지 전부.
    /// </summary>
    public void ResetAll()
    {
        var fresh = new AppSettings();

        ClaudeCredentialsPath = fresh.ClaudeCredentialsPath;
        CodexSessionsPath = fresh.CodexSessionsPath;
        RefreshIntervalSeconds = fresh.RefreshIntervalSeconds;
        StartWithWindows = fresh.StartWithWindows;
        ShowInTaskbar = fresh.ShowInTaskbar;
        ShowWidgetBar = fresh.ShowWidgetBar;
        DisplayMode = fresh.DisplayMode;
        ClaudeColor = fresh.ClaudeColor;
        CodexColor = fresh.CodexColor;

        // 언어는 시스템 언어로 되돌린다.
        Language = Strings.DetectSystemLanguage();

        // 도구는 지금 설치된 것에 맞춘다.
        DetectInstalledTools();
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                string json = File.ReadAllText(SettingsFile);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
                if (loaded is not null)
                {
                    // 너무 짧은 주기는 무의미한 부하만 만든다.
                    if (loaded.RefreshIntervalSeconds < 30) loaded.RefreshIntervalSeconds = 30;
                    return loaded;
                }
            }
        }
        catch
        {
            // 설정이 깨졌으면 기본값으로 시작한다. 사용자를 막지 않는다.
        }
        return new AppSettings();
    }

    /// <summary>
    /// 설정을 파일에 쓴다. 실패하면 이유를 돌려준다(성공이면 null).
    /// 저장이 조용히 실패하면 사용자가 다음 실행에서야 알게 되므로 감추지 않는다.
    /// </summary>
    public string? Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);

            // 쓰는 도중 죽어도 기존 설정이 남도록 임시 파일에 먼저 쓴다.
            string tmp = SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
            File.Move(tmp, SettingsFile, overwrite: true);

            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
