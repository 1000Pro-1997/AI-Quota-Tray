#![windows_subsystem = "windows"]

use reqwest::blocking::Client;
use serde::Deserialize;
use sha2::{Digest, Sha256};
use std::env;
use std::ffi::OsStr;
use std::fs::{self, File, OpenOptions};
use std::io::{Read, Write};
use std::os::windows::process::CommandExt;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::time::Duration;
use windows_sys::Win32::Foundation::{CloseHandle, ERROR_ALREADY_EXISTS, GetLastError, HANDLE};
use windows_sys::Win32::System::Threading::{CreateMutexW, OpenMutexW};
use windows_sys::Win32::UI::WindowsAndMessaging::{MB_ICONERROR, MB_OK, MessageBoxW};

const RELEASE_API: &str = "https://api.github.com/repos/1000Pro-1997/AI-Quota-Tray/releases/latest";
const ASSET_NAME: &str = "AiQuotaTray-standalone.exe";
const CREATE_NO_WINDOW: u32 = 0x0800_0000;

/// 뮤텍스를 "있는지만" 확인할 때 쓰는 최소 권한. windows-sys에서는
/// 파일 시스템 쪽에 묶여 있어 피처가 늘어나므로 값을 직접 적는다.
const SYNCHRONIZE: u32 = 0x0010_0000;

#[derive(Deserialize)]
struct Release {
    tag_name: String,
    #[serde(default)]
    body: String,
    #[serde(default)]
    draft: bool,
    #[serde(default)]
    prerelease: bool,
    assets: Vec<Asset>,
}

#[derive(Deserialize)]
struct Asset {
    name: String,
    browser_download_url: String,
    size: u64,
    digest: Option<String>,
}

fn main() {
    if let Err(error) = run() {
        log_line(&format!("launcher error: {error}"));
        if !app_path().exists() {
            show_error(&format!(
                "AI Quota Tray를 설치하거나 실행하지 못했습니다.\n\n{error}\n\n인터넷 연결을 확인한 뒤 다시 실행해 주세요."
            ));
        }
    }
}

fn run() -> Result<(), String> {
    let installed = launcher_path();
    let current = env::current_exe().map_err(|e| e.to_string())?;

    // 앱이 "받아뒀으니 지금 갈아끼워 달라"고 부른 경우.
    // 앱은 이 호출 직후 스스로 종료하므로, 잠금이 풀릴 때까지 기다렸다 교체한다.
    if env::args().skip(1).any(|a| a == "--apply-now") {
        return apply_now();
    }

    if !same_path(&current, &installed) {
        install_launcher(&current, &installed)?;
        start_hidden(&installed, &[])?;
        return Ok(());
    }

    fs::create_dir_all(install_dir()).map_err(|e| e.to_string())?;
    let Some(_mutex) = LauncherMutex::acquire() else {
        return Ok(());
    };

    apply_pending_update()?;

    if app_path().exists() {
        if should_launch_app() {
            start_hidden(&app_path(), &[])?;
        }
        // 앱은 즉시 띄우고, 확인과 다운로드는 이 런처 프로세스에서 뒤이어 수행한다.
        if let Err(error) = stage_latest_update() {
            log_line(&format!("background update skipped: {error}"));
        }
    } else {
        download_first_install()?;
        if should_launch_app() {
            start_hidden(&app_path(), &["--flyout"])?;
        }
    }

    Ok(())
}

fn install_dir() -> PathBuf {
    if let Some(path) = env::var_os("AI_QUOTA_TRAY_INSTALL_DIR") {
        return PathBuf::from(path);
    }
    PathBuf::from(env::var_os("LOCALAPPDATA").unwrap_or_default()).join("AI Quota Tray")
}

fn should_launch_app() -> bool {
    env::var_os("AI_QUOTA_TRAY_NO_LAUNCH").is_none()
}

fn launcher_path() -> PathBuf {
    install_dir().join("Launcher.exe")
}
fn app_path() -> PathBuf {
    install_dir().join("AiQuotaTray.exe")
}
fn pending_path() -> PathBuf {
    install_dir().join("AiQuotaTray.pending.exe")
}
fn partial_path() -> PathBuf {
    install_dir().join("AiQuotaTray.download")
}
fn version_path() -> PathBuf {
    install_dir().join("app-version.txt")
}
fn pending_version_path() -> PathBuf {
    install_dir().join("pending-version.txt")
}
fn log_path() -> PathBuf {
    install_dir().join("launcher.log")
}

struct LauncherMutex(HANDLE);

impl LauncherMutex {
    fn acquire() -> Option<Self> {
        let name = to_wide("Local\\AIQuotaTray.Launcher");
        let handle = unsafe { CreateMutexW(std::ptr::null(), 0, name.as_ptr()) };
        if handle.is_null() {
            return None;
        }
        if unsafe { GetLastError() } == ERROR_ALREADY_EXISTS {
            unsafe {
                CloseHandle(handle);
            }
            return None;
        }
        Some(Self(handle))
    }
}

impl Drop for LauncherMutex {
    fn drop(&mut self) {
        unsafe {
            CloseHandle(self.0);
        }
    }
}

fn install_launcher(source: &Path, target: &Path) -> Result<(), String> {
    fs::create_dir_all(install_dir()).map_err(|e| e.to_string())?;
    fs::copy(source, target).map_err(|e| format!("런처 설치 실패: {e}"))?;

    if env::var_os("AI_QUOTA_TRAY_SKIP_STARTUP").is_some() {
        return Ok(());
    }

    let value = format!("\"{}\"", target.display());
    let status = Command::new("reg.exe")
        .args([
            "add",
            r"HKCU\Software\Microsoft\Windows\CurrentVersion\Run",
            "/v",
            "AiQuotaTray",
            "/t",
            "REG_SZ",
            "/d",
            &value,
            "/f",
        ])
        .creation_flags(CREATE_NO_WINDOW)
        .status()
        .map_err(|e| format!("시작 프로그램 등록 실패: {e}"))?;
    if !status.success() {
        return Err("시작 프로그램을 등록하지 못했습니다.".into());
    }
    Ok(())
}

/// 실행 중인 앱이 끝나기를 기다렸다가 교체하고 다시 띄운다.
///
/// 실행 중인 exe는 Windows가 잠가서 rename이 실패한다. 앱이 완전히
/// 사라지는 시점을 밖에서 알 수 없으니 짧은 간격으로 되풀이해 본다.
fn apply_now() -> Result<(), String> {
    fs::create_dir_all(install_dir()).map_err(|e| e.to_string())?;

    if !pending_path().exists() {
        return Err("받아 둔 업데이트가 없습니다.".into());
    }

    // 앱이 완전히 사라진 뒤에 띄워야 한다. 앱은 SingleInstance 뮤텍스를 쥐고 있어,
    // 아직 살아 있는 동안 새 인스턴스를 띄우면 "이미 실행 중"으로 조용히 죽는다.
    // 최대 30초. 이보다 오래 안 끝나면 앱이 멈춘 것이라 봐야 한다.
    let mut exited = false;
    for _ in 0..60 {
        if !app_is_running() {
            exited = true;
            break;
        }
        std::thread::sleep(Duration::from_millis(500));
    }

    if !exited {
        log_line("app did not exit in time; applying anyway");
    }

    apply_pending_update()?;
    let _ = start_hidden(&app_path(), &[]);
    log_line("update applied on request");
    Ok(())
}

/// 앱이 아직 살아 있는가. 앱이 쥐는 단일 인스턴스 뮤텍스를 열어 본다.
///
/// 이름이 App.xaml.cs의 것과 어긋나면 기다리지 않고 지나가므로 함께 고쳐야 한다.
fn app_is_running() -> bool {
    // 이름은 App.xaml.cs의 것과 같아야 한다. 환경변수는 시험용 우회로다.
    let raw = env::var("AI_QUOTA_TRAY_MUTEX")
        .unwrap_or_else(|_| "AiQuotaTray.SingleInstance".to_string());
    let name = to_wide(&raw);
    let handle = unsafe { OpenMutexW(SYNCHRONIZE, 0, name.as_ptr()) };
    if handle.is_null() {
        return false;
    }
    unsafe {
        CloseHandle(handle);
    }
    true
}

fn apply_pending_update() -> Result<(), String> {
    let pending = pending_path();
    if !pending.exists() {
        return Ok(());
    }

    let app = app_path();
    let backup = install_dir().join("AiQuotaTray.previous.exe");
    let _ = fs::remove_file(&backup);

    if app.exists() {
        fs::rename(&app, &backup).map_err(|e| format!("기존 앱 백업 실패: {e}"))?;
    }

    if let Err(error) = fs::rename(&pending, &app) {
        if backup.exists() {
            let _ = fs::rename(&backup, &app);
        }
        return Err(format!("업데이트 적용 실패: {error}"));
    }

    if let Ok(version) = fs::read_to_string(pending_version_path()) {
        let _ = fs::write(version_path(), version);
    }
    let _ = fs::remove_file(pending_version_path());
    let _ = fs::remove_file(backup);
    log_line("pending update applied");
    Ok(())
}

fn download_first_install() -> Result<(), String> {
    let release = fetch_release()?;
    let asset = find_asset(&release)?;
    download_verified(asset, &release.body, &partial_path())?;
    fs::rename(partial_path(), app_path()).map_err(|e| format!("앱 설치 실패: {e}"))?;
    fs::write(version_path(), normalize_version(&release.tag_name)).map_err(|e| e.to_string())?;
    Ok(())
}

fn stage_latest_update() -> Result<(), String> {
    let release = fetch_release()?;
    let latest = normalize_version(&release.tag_name);
    let current = fs::read_to_string(version_path()).unwrap_or_else(|_| "0.0.0".into());
    if compare_versions(&latest, current.trim()) <= 0 {
        return Ok(());
    }

    let asset = find_asset(&release)?;
    download_verified(asset, &release.body, &partial_path())?;
    let _ = fs::remove_file(pending_path());
    fs::rename(partial_path(), pending_path()).map_err(|e| format!("업데이트 준비 실패: {e}"))?;
    fs::write(pending_version_path(), &latest).map_err(|e| e.to_string())?;
    log_line(&format!("update {latest} staged for next launch"));
    Ok(())
}

fn fetch_release() -> Result<Release, String> {
    let client = Client::builder()
        .timeout(Duration::from_secs(30))
        .build()
        .map_err(|e| e.to_string())?;
    let release: Release = client
        .get(RELEASE_API)
        .header("User-Agent", "AI-Quota-Tray-Launcher")
        .send()
        .and_then(|r| r.error_for_status())
        .map_err(|e| format!("릴리즈 확인 실패: {e}"))?
        .json()
        .map_err(|e| format!("릴리즈 정보 해석 실패: {e}"))?;
    if release.draft || release.prerelease {
        return Err("정식 릴리즈가 아닙니다.".into());
    }
    Ok(release)
}

fn find_asset(release: &Release) -> Result<&Asset, String> {
    release
        .assets
        .iter()
        .find(|a| a.name == ASSET_NAME)
        .ok_or_else(|| format!("{ASSET_NAME} 파일이 릴리즈에 없습니다."))
}

fn download_verified(asset: &Asset, body: &str, destination: &Path) -> Result<(), String> {
    let expected = expected_sha256(asset, body)
        .ok_or_else(|| "릴리즈에서 SHA256을 확인할 수 없습니다.".to_string())?;
    let client = Client::builder()
        .timeout(Duration::from_secs(180))
        .build()
        .map_err(|e| e.to_string())?;
    let mut response = client
        .get(&asset.browser_download_url)
        .header("User-Agent", "AI-Quota-Tray-Launcher")
        .send()
        .and_then(|r| r.error_for_status())
        .map_err(|e| format!("다운로드 실패: {e}"))?;

    let mut file = File::create(destination).map_err(|e| e.to_string())?;
    let mut hasher = Sha256::new();
    let mut total = 0u64;
    let mut buffer = [0u8; 64 * 1024];
    loop {
        let count = response.read(&mut buffer).map_err(|e| e.to_string())?;
        if count == 0 {
            break;
        }
        total += count as u64;
        if total > 250 * 1024 * 1024 {
            return Err("다운로드 파일이 너무 큽니다.".into());
        }
        hasher.update(&buffer[..count]);
        file.write_all(&buffer[..count])
            .map_err(|e| e.to_string())?;
    }
    file.flush().map_err(|e| e.to_string())?;

    if total != asset.size {
        return Err("다운로드 크기가 릴리즈 정보와 다릅니다.".into());
    }
    let actual = format!("{:x}", hasher.finalize());
    if !actual.eq_ignore_ascii_case(&expected) {
        let _ = fs::remove_file(destination);
        return Err("다운로드 SHA256 검증에 실패했습니다.".into());
    }
    Ok(())
}

fn expected_sha256(asset: &Asset, body: &str) -> Option<String> {
    if let Some(digest) = &asset.digest
        && let Some(value) = digest.strip_prefix("sha256:")
        && is_sha256(value)
    {
        return Some(value.to_string());
    }

    for line in body.lines() {
        if !line.contains(&asset.name) {
            continue;
        }
        for word in line.split_whitespace() {
            let clean = word.trim_matches(|c: char| !c.is_ascii_hexdigit());
            if is_sha256(clean) {
                return Some(clean.to_string());
            }
        }
    }
    None
}

fn is_sha256(value: &str) -> bool {
    value.len() == 64 && value.chars().all(|c| c.is_ascii_hexdigit())
}

fn normalize_version(tag: &str) -> String {
    tag.trim()
        .trim_start_matches(['v', 'V'])
        .split('-')
        .next()
        .unwrap_or("0.0.0")
        .to_string()
}

fn compare_versions(left: &str, right: &str) -> i32 {
    let parse = |s: &str| {
        let mut result = [0u64; 3];
        for (i, part) in s.split('.').take(3).enumerate() {
            result[i] = part.parse().unwrap_or(0);
        }
        result
    };
    parse(left).cmp(&parse(right)) as i32
}

fn start_hidden(path: &Path, args: &[&str]) -> Result<(), String> {
    Command::new(path)
        .args(args)
        .creation_flags(CREATE_NO_WINDOW)
        .spawn()
        .map(|_| ())
        .map_err(|e| format!("{} 실행 실패: {e}", path.display()))
}

fn same_path(left: &Path, right: &Path) -> bool {
    left.to_string_lossy()
        .eq_ignore_ascii_case(&right.to_string_lossy())
}

fn log_line(message: &str) {
    let _ = fs::create_dir_all(install_dir());
    if let Ok(mut file) = OpenOptions::new()
        .create(true)
        .append(true)
        .open(log_path())
    {
        let _ = writeln!(file, "{message}");
    }
}

fn show_error(message: &str) {
    let wide = to_wide(message);
    let title = to_wide("AI Quota Tray Launcher");
    unsafe {
        MessageBoxW(
            std::ptr::null_mut(),
            wide.as_ptr(),
            title.as_ptr(),
            MB_OK | MB_ICONERROR,
        );
    }
}

fn to_wide(value: &str) -> Vec<u16> {
    OsStr::new(value).encode_wide().chain(Some(0)).collect()
}

use std::os::windows::ffi::OsStrExt;

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn compares_semver_triplets() {
        assert!(compare_versions("1.2.0", "1.1.9") > 0);
        assert_eq!(compare_versions("1.1", "1.1.0"), 0);
        assert!(compare_versions("2.0.0", "10.0.0") < 0);
    }

    #[test]
    fn reads_hash_from_release_body() {
        let asset = Asset {
            name: ASSET_NAME.into(),
            browser_download_url: String::new(),
            size: 1,
            digest: None,
        };
        let hash = "6992b5c4bcaaafffcf97d690c1093a48f8b5645941e7625cc58b7f912852414f";
        assert_eq!(
            expected_sha256(&asset, &format!("{ASSET_NAME}  {hash}")),
            Some(hash.into())
        );
    }
}
