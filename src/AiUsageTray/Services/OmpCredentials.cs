using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AiUsageTray.Services;

/// <summary>
/// omp(코딩 에이전트)가 ChatGPT 구독으로 Codex 모델을 쓰면 ~/.codex에는 아무것도
/// 남지 않는다. 로그도 토큰 갱신도 omp 쪽 agent.db에서만 일어나 Codex CLI의 토큰은
/// 만료된 채 남는다. 같은 계정의 사용량 API를 부르려고 omp가 관리하는 토큰을 빌린다.
/// 읽기만 한다. 갱신은 omp가 하므로 여기서 리프레시 토큰을 쓰면 omp 로그인이 풀린다.
/// </summary>
internal static class OmpCredentials
{
    private static string DatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".omp", "agent", "agent.db");

    /// <summary>아직 만료되지 않은 openai-codex 토큰 중 가장 오래 쓸 수 있는 것.</summary>
    public static (string Token, string Account)? ReadCodex()
    {
        string path = DatabasePath;
        if (!File.Exists(path)) return null;

        (string Token, string Account)? best = null;
        long bestExpires = 0;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        foreach (string json in Query(path,
                     "SELECT data FROM auth_credentials WHERE provider = 'openai-codex' " +
                     "AND credential_type = 'oauth' AND disabled_cause IS NULL"))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string token = root.TryGetProperty("access", out var a) && a.ValueKind == JsonValueKind.String
                    ? a.GetString() ?? "" : "";
                long expires = root.TryGetProperty("expires", out var e) && e.ValueKind == JsonValueKind.Number
                    ? e.GetInt64() : 0;
                if (token.Length == 0 || expires <= now || expires <= bestExpires) continue;

                string account = root.TryGetProperty("accountId", out var acc) && acc.ValueKind == JsonValueKind.String
                    ? acc.GetString() ?? "" : "";
                best = (token, account);
                bestExpires = expires;
            }
            catch (JsonException)
            {
                // omp가 저장 형식을 바꿨다면 이 출처만 건너뛴다.
            }
        }

        return best;
    }

    // Windows 10부터 System32에 들어 있는 SQLite를 쓴다. NuGet 패키지를 넣으면
    // 네이티브 DLL이 딸려 와 단일 exe 배포가 번거로워진다.
    private const string Lib = "winsqlite3";
    private const int SQLITE_OK = 0;
    private const int SQLITE_ROW = 100;
    private const int SQLITE_OPEN_READONLY = 0x1;

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_close(IntPtr db);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_busy_timeout(IntPtr db, int ms);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr stmt, IntPtr tail);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_step(IntPtr stmt);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_finalize(IntPtr stmt);

    /// <summary>첫 열을 문자열로 모은다. 실패는 모두 빈 결과다. 다른 출처로 물러서면 된다.</summary>
    private static string[] Query(string path, string sql)
    {
        var rows = new System.Collections.Generic.List<string>();
        IntPtr db = IntPtr.Zero;
        try
        {
            if (sqlite3_open_v2(Utf8z(path), out db, SQLITE_OPEN_READONLY, IntPtr.Zero) != SQLITE_OK)
                return Array.Empty<string>();
            // omp가 쓰는 중이면 잠깐 기다린다. 오래 붙잡으면 조회 전체가 늦어진다.
            sqlite3_busy_timeout(db, 500);

            if (sqlite3_prepare_v2(db, Utf8z(sql), -1, out var stmt, IntPtr.Zero) != SQLITE_OK)
                return Array.Empty<string>();
            try
            {
                while (sqlite3_step(stmt) == SQLITE_ROW)
                {
                    string? text = Marshal.PtrToStringUTF8(sqlite3_column_text(stmt, 0));
                    if (!string.IsNullOrEmpty(text)) rows.Add(text);
                }
            }
            finally
            {
                sqlite3_finalize(stmt);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return Array.Empty<string>();
        }
        finally
        {
            if (db != IntPtr.Zero) sqlite3_close(db);
        }

        return rows.ToArray();
    }

    private static byte[] Utf8z(string s) => Encoding.UTF8.GetBytes(s + "\0");
}
