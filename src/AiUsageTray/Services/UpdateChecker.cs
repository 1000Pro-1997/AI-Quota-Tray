using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AiUsageTray.Services;

/// <summary>업데이트 확인 결과.</summary>
public sealed class UpdateInfo
{
    /// <summary>릴리스에 붙은 버전. 예: 1.2.0</summary>
    public Version? Latest { get; init; }

    /// <summary>사람이 읽는 태그. 예: v1.2.0</summary>
    public string TagName { get; init; } = "";

    /// <summary>릴리스 페이지 주소.</summary>
    public string PageUrl { get; init; } = "";

    /// <summary>지금 버전보다 새 것이 있는가.</summary>
    public bool HasUpdate { get; init; }

    /// <summary>확인에 실패한 사유. 성공이면 null.</summary>
    public string? Error { get; init; }

    /// <summary>자립형 exe의 내려받기 주소. 릴리스에 없으면 빈 문자열.</summary>
    public string AssetUrl { get; init; } = "";

    /// <summary>자립형 exe의 크기(바이트). 진행률 계산에 쓴다.</summary>
    public long AssetSize { get; init; }

    /// <summary>기대되는 SHA256. 검증에 쓴다. 못 구했으면 빈 문자열.</summary>
    public string AssetSha256 { get; init; } = "";

    /// <summary>런처 내려받기 주소. 릴리스에 없으면 빈 문자열.</summary>
    public string LauncherUrl { get; init; } = "";

    /// <summary>런처 파일의 크기(바이트). 잘못된 응답을 거르는 데 쓴다.</summary>
    public long LauncherSize { get; init; }

    /// <summary>기대되는 런처 SHA256. 검증할 수 없으면 빈 문자열.</summary>
    public string LauncherSha256 { get; init; } = "";

    /// <summary>앱 안에서 바로 받을 수 있는가. 주소와 해시가 모두 있어야 한다.</summary>
    public bool CanDownload =>
        HasUpdate && AssetUrl.Length > 0 && AssetSha256.Length == 64;

    public static UpdateInfo Failed(string reason) => new() { Error = reason };
}

/// <summary>
/// GitHub Releases에서 새 버전이 있는지 확인한다.
///
/// 공개 저장소라 인증이 필요 없다. 다운로드는 하지 않고,
/// 사용자가 누르면 브라우저로 릴리스 페이지를 연다.
/// </summary>
public sealed class UpdateChecker
{
    /// <summary>런처가 받아가는 것과 같은 파일. 이름이 어긋나면 자동 설치가 끊긴다.</summary>
    public const string AssetName = "AiQuotaTray-standalone.exe";

    /// <summary>업데이트를 갈아끼워 줄 런처. 앱이 자기 자신을 덮어쓸 수 없어 필요하다.</summary>
    public const string LauncherAssetName = "AI-Quota-Tray.exe";

    private const string LatestReleaseApi =
        "https://api.github.com/repos/1000Pro-1997/AI-Quota-Tray/releases/latest";

    private readonly HttpClient _http;

    public UpdateChecker(HttpClient http)
    {
        _http = http;
    }

    /// <summary>마지막으로 확인한 결과. 아직 확인 전이면 null.</summary>
    public UpdateInfo? Last { get; private set; }

    /// <summary>지금 실행 중인 버전.</summary>
    public static Version Current
    {
        get
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? new Version(1, 0, 0) : new Version(v.Major, v.Minor, v.Build);
        }
    }

    /// <summary>지금 바로 확인한다. 설정의 버튼에서 부른다.</summary>
    public async Task<UpdateInfo> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);

            // GitHub API는 User-Agent를 요구한다.
            req.Headers.TryAddWithoutValidation("User-Agent", "AiQuotaTray");
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);

            // 릴리스를 아직 하나도 올리지 않았으면 404가 온다. 오류가 아니다.
            if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
                return Last = new UpdateInfo { HasUpdate = false };

            if (!res.IsSuccessStatusCode)
                return Last = UpdateInfo.Failed(Strings.Get("update.failed"));

            string body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            return Last = Parse(body);
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException)
        {
            return Last = UpdateInfo.Failed(Strings.Get("error.network"));
        }
        catch (Exception ex)
        {
            return Last = UpdateInfo.Failed(ex.Message);
        }
    }

    private static UpdateInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // 초안이나 사전 배포판은 건너뛴다.
        if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
            return new UpdateInfo { HasUpdate = false };

        if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True)
            return new UpdateInfo { HasUpdate = false };

        string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        string page = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";

        var latest = ParseVersion(tag);
        if (latest is null)
            return new UpdateInfo { TagName = tag, PageUrl = page, HasUpdate = false };

        string body = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        var (url, size, sha) = FindAsset(root, body);
        var (launcher, launcherSize, launcherSha) = FindLauncherAsset(root, body);

        return new UpdateInfo
        {
            Latest = latest,
            TagName = tag,
            PageUrl = string.IsNullOrEmpty(page) ? AppInfo.Repository + "/releases" : page,
            HasUpdate = latest > Current,
            AssetUrl = url,
            AssetSize = size,
            AssetSha256 = sha,
            LauncherUrl = launcher,
            LauncherSize = launcherSize,
            LauncherSha256 = launcherSha,
        };
    }

    /// <summary>런처도 실행 파일이므로 앱 본체와 똑같이 크기와 해시를 확인한다.</summary>
    private static (string Url, long Size, string Sha) FindLauncherAsset(JsonElement root, string body)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return ("", 0, "");

        foreach (var a in assets.EnumerateArray())
        {
            if (!a.TryGetProperty("name", out var n) || n.GetString() != LauncherAssetName) continue;
            string url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
            long size = a.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number
                ? s.GetInt64() : 0;
            return (url, size, AssetSha(a, body, LauncherAssetName));
        }
        return ("", 0, "");
    }

    /// <summary>릴리스에서 자립형 exe와 그 SHA256을 찾는다. 런처가 쓰는 규칙과 같다.</summary>
    private static (string Url, long Size, string Sha) FindAsset(JsonElement root, string body)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return ("", 0, "");

        foreach (var a in assets.EnumerateArray())
        {
            if (!a.TryGetProperty("name", out var n) || n.GetString() != AssetName) continue;

            string url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
            long size = a.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number
                ? s.GetInt64() : 0;

            string sha = AssetSha(a, body, AssetName);

            return (url, size, sha);
        }

        return ("", 0, "");
    }

    /// <summary>릴리스 본문에서 해당 파일 이름이 있는 줄의 SHA256을 찾는다.</summary>
    private static string AssetSha(JsonElement asset, string body, string assetName)
    {
        if (asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String &&
            d.GetString() is { } raw && raw.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            string value = raw[7..];
            if (IsSha256(value)) return value;
        }
        return ShaFromBody(body, assetName);
    }

    private static string ShaFromBody(string body, string assetName)
    {
        foreach (string line in body.Split('\n'))
        {
            if (!line.Contains(assetName, StringComparison.Ordinal)) continue;

            foreach (string word in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                string clean = word.Trim('`', '*', '|', '(', ')', '[', ']', ':', ',', '.');
                if (IsSha256(clean)) return clean;
            }
        }
        return "";
    }

    private static bool IsSha256(string v) =>
        v.Length == 64 && v.All(char.IsAsciiHexDigit);

    /// <summary>"v1.2.0", "1.2", "1.2.0-beta" 같은 태그에서 숫자만 뽑는다.</summary>
    private static Version? ParseVersion(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;

        string s = tag.Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s[1..];

        // 뒤에 붙은 -beta 같은 꼬리표를 떼어낸다.
        int dash = s.IndexOf('-');
        if (dash > 0) s = s[..dash];

        var parts = s.Split('.');
        if (parts.Length == 0) return null;

        int[] numbers = new int[3];
        for (int i = 0; i < 3; i++)
        {
            if (i >= parts.Length) { numbers[i] = 0; continue; }
            if (!int.TryParse(parts[i], out numbers[i])) return null;
        }

        return new Version(numbers[0], numbers[1], numbers[2]);
    }
}
