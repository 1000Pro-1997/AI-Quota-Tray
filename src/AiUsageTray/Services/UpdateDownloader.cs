using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace AiUsageTray.Services;

/// <summary>다운로드 진행 상황. 크기를 모르면 Total이 0이다.</summary>
public readonly record struct DownloadProgress(long Received, long Total)
{
    /// <summary>0~100. 전체 크기를 모르면 0을 돌려준다.</summary>
    public double Percent => Total > 0 ? Math.Clamp(Received * 100.0 / Total, 0, 100) : 0;
}

/// <summary>
/// 새 버전 exe를 받아 설치 폴더에 준비해 둔다.
///
/// 실행 중인 exe는 Windows가 잠그기 때문에 그 자리에 바로 덮어쓸 수 없다.
/// 그래서 받은 파일을 pending 이름으로 두고, 교체는 런처에게 맡긴다.
/// 런처가 하는 일과 같은 규칙(파일명·해시 검증)을 따라야 서로 맞물린다.
/// </summary>
public sealed class UpdateDownloader
{
    /// <summary>런처와 같은 설치 폴더를 봐야 한다. 여기가 어긋나면 교체가 안 일어난다.</summary>
    public static string InstallDir =>
        Environment.GetEnvironmentVariable("AI_QUOTA_TRAY_INSTALL_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AI Quota Tray");

    public static string LauncherPath => Path.Combine(InstallDir, "Launcher.exe");
    public static string PendingPath => Path.Combine(InstallDir, "AiQuotaTray.pending.exe");
    public static string PendingVersionPath => Path.Combine(InstallDir, "pending-version.txt");
    private static string PartialPath => Path.Combine(InstallDir, "AiQuotaTray.download");

    /// <summary>내려받다 만 파일이 무한정 커지지 않게 막는다. 런처와 같은 상한.</summary>
    private const long MaxBytes = 250L * 1024 * 1024;

    private readonly HttpClient _http;

    public UpdateDownloader(HttpClient http) => _http = http;

    /// <summary>런처가 설치되어 있는가. 없으면 받아도 교체해 줄 사람이 없다.</summary>
    public static bool LauncherInstalled => File.Exists(LauncherPath);

    /// <summary>
    /// 런처가 없으면 릴리스에서 받아 앉힌다.
    ///
    /// 사용자가 자립형 exe만 손으로 내려받아 쓰는 경우가 있다. 그때도 앱 안에서
    /// 업데이트가 끝나도록, 교체를 맡을 런처를 스스로 마련한다. 셋업 파일을
    /// 따로 받게 하지 않으려는 것이다. 실패해도 업데이트 자체는 진행한다.
    /// </summary>
    public async Task<bool> EnsureLauncherAsync(UpdateInfo info, CancellationToken ct = default)
    {
        if (LauncherInstalled) return true;
        if (info.LauncherUrl.Length == 0) return false;

        try
        {
            Directory.CreateDirectory(InstallDir);

            using var req = new HttpRequestMessage(HttpMethod.Get, info.LauncherUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", "AiQuotaTray");

            using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
            res.EnsureSuccessStatusCode();

            byte[] bytes = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

            // 런처는 2MB 남짓이다. 이보다 크면 받아온 것이 런처가 아니다.
            if (bytes.Length == 0 || bytes.Length > 32 * 1024 * 1024) return false;

            await File.WriteAllBytesAsync(LauncherPath, bytes, ct).ConfigureAwait(false);

            // 파일만 되살리면 반쪽이다. 시작 프로그램 등록은 런처가 설치될 때
            // 함께 들어가는데, 사용자가 런처를 지웠다면 그 등록도 이미 깨졌거나
            // 없는 파일을 가리키고 있다. 부팅해도 아무것도 안 뜨는 상태가 된다.
            RegisterStartup();
            return true;
        }
        catch
        {
            // 런처를 못 갖췄을 뿐이다. 받아 둔 업데이트는 다음에 쓰일 수 있다.
            return false;
        }
    }

    /// <summary>런처를 Windows 시작 프로그램에 등록한다. 런처가 하는 것과 같은 규칙.</summary>
    private static void RegisterStartup()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);

            // 값이 이미 런처를 제대로 가리키면 건드리지 않는다. 사용자가 일부러
            // 자동 시작을 꺼 둔 것일 수도 있어, 없을 때만 새로 넣는다.
            if (key?.GetValue("AiQuotaTray") is string existing &&
                existing.Contains("Launcher.exe", StringComparison.OrdinalIgnoreCase))
                return;

            key?.SetValue("AiQuotaTray", $"\"{LauncherPath}\"");
        }
        catch
        {
            // 등록에 실패해도 런처 자체는 되살렸다. 앱에서 업데이트는 여전히 된다.
        }
    }

    /// <summary>이미 받아 둔 새 버전이 있는가.</summary>
    public static bool PendingReady => File.Exists(PendingPath);

    /// <summary>
    /// 새 버전을 받아 pending 자리에 둔다. 성공하면 재시작만 하면 된다.
    /// </summary>
    /// <exception cref="InvalidOperationException">해시가 어긋나거나 크기가 다를 때.</exception>
    public async Task DownloadAsync(
        UpdateInfo info,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct = default)
    {
        if (!info.CanDownload)
            throw new InvalidOperationException(Strings.Get("update.noAsset"));

        Directory.CreateDirectory(InstallDir);

        // 지난번에 받다 만 것이 남아 있을 수 있다.
        TryDelete(PartialPath);

        try
        {
            string actual = await FetchToFileAsync(info, progress, ct).ConfigureAwait(false);

            if (!string.Equals(actual, info.AssetSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(Strings.Get("update.hashMismatch"));

            // 검증을 통과한 것만 pending으로 올린다. 중간 상태를 런처가 보면 안 된다.
            TryDelete(PendingPath);
            File.Move(PartialPath, PendingPath);

            await File.WriteAllTextAsync(
                PendingVersionPath,
                info.Latest?.ToString() ?? "",
                ct).ConfigureAwait(false);
        }
        catch
        {
            // 실패한 찌꺼기를 남기면 다음 시도에서 헷갈린다.
            TryDelete(PartialPath);
            throw;
        }
    }

    /// <summary>내려받으면서 해시를 함께 계산한다. 파일을 두 번 읽지 않으려는 것이다.</summary>
    private async Task<string> FetchToFileAsync(
        UpdateInfo info,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, info.AssetUrl);
        req.Headers.TryAddWithoutValidation("User-Agent", "AiQuotaTray");

        using var res = await _http
            .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        res.EnsureSuccessStatusCode();

        long total = res.Content.Headers.ContentLength ?? info.AssetSize;

        using var sha = SHA256.Create();
        await using var source = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = new FileStream(
            PartialPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 64 * 1024, useAsync: true);

        var buffer = new byte[64 * 1024];
        long received = 0;

        // 너무 자주 알리면 UI가 갱신에만 매달린다. 눈에 보일 만큼만 올린다.
        long lastReported = 0;

        while (true)
        {
            int count = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (count == 0) break;

            received += count;
            if (received > MaxBytes)
                throw new InvalidOperationException(Strings.Get("update.tooLarge"));

            sha.TransformBlock(buffer, 0, count, null, 0);
            await target.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);

            if (received - lastReported >= 256 * 1024 || received == total)
            {
                lastReported = received;
                progress?.Report(new DownloadProgress(received, total));
            }
        }

        sha.TransformFinalBlock([], 0, 0);
        await target.FlushAsync(ct).ConfigureAwait(false);

        if (info.AssetSize > 0 && received != info.AssetSize)
            throw new InvalidOperationException(Strings.Get("update.sizeMismatch"));

        progress?.Report(new DownloadProgress(received, total));
        return Convert.ToHexString(sha.Hash ?? []).ToLowerInvariant();
    }

    /// <summary>
    /// 런처에게 교체를 맡기고 이 앱을 끝낸다.
    ///
    /// 런처는 이 프로세스가 사라지기를 기다렸다가 pending을 제자리에 옮기고
    /// 새 exe를 띄운다. 자기가 자기를 덮어쓸 수 없으니 이 방법뿐이다.
    /// </summary>
    public static bool RestartToApply()
    {
        if (!LauncherInstalled || !PendingReady) return false;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = LauncherPath,
                Arguments = "--apply-now",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 지우지 못해도 치명적이지 않다. 다음 단계에서 덮어쓰면 된다.
        }
    }
}
