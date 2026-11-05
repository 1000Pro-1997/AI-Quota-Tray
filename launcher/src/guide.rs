//! 첫 설치 전에 Windows 보안 예외를 먼저 안내하는 대화상자.
//!
//! 서명 없는 런처가 자기를 복사하고 시작 프로그램에 올리면 Defender의 행동 판정
//! (Behavior:Win32/Persistence.A!ml)이 런처·Run 값·시작 메뉴 바로가기를 통째로
//! 격리한다. 그러면 부팅 때 앱이 뜨지 않는데 사용자는 까닭을 알 길이 없다.
//! 그런 일을 하기 전에 멈춰서 설치 폴더를 제외 목록에 넣게 한다.
//!
//! 예외를 런처가 대신 넣지는 않는다. 서명 없는 프로그램이 관리자 권한으로
//! Defender 예외를 추가하는 것은 그 자체로 악성 코드의 전형이라 더 확실히 잡힌다.
//! 넣었는지 확인할 수도 없다(예외 목록은 관리자만 읽는다). 사용자의 답을 믿는다.

use std::ffi::OsStr;
use std::os::windows::ffi::OsStrExt;
use std::path::Path;
use windows_sys::Win32::Foundation::{GlobalFree, HANDLE, HWND, LPARAM, S_FALSE, S_OK, WPARAM};
use windows_sys::Win32::Globalization::GetUserDefaultUILanguage;
use windows_sys::Win32::System::DataExchange::{CloseClipboard, EmptyClipboard, OpenClipboard, SetClipboardData};
use windows_sys::Win32::System::Memory::{GMEM_MOVEABLE, GlobalAlloc, GlobalLock, GlobalUnlock};
use windows_sys::Win32::UI::Controls::{
    TASKDIALOG_BUTTON, TASKDIALOG_NOTIFICATIONS, TASKDIALOGCONFIG, TD_INFORMATION_ICON, TD_SHIELD_ICON,
    TDCBF_CANCEL_BUTTON, TDF_ALLOW_DIALOG_CANCELLATION, TDF_USE_COMMAND_LINKS, TDN_BUTTON_CLICKED,
    TaskDialogIndirect,
};
use windows_sys::Win32::UI::Shell::ShellExecuteW;
use windows_sys::Win32::UI::WindowsAndMessaging::SW_SHOWNORMAL;
use windows_sys::core::HRESULT;

/// 사용자가 고른 것.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Choice {
    /// 예외를 넣었다고 답했다.
    Confirmed,
    /// 예외 없이 그냥 설치한다.
    Skipped,
    /// 설치를 그만둔다.
    Cancelled,
}

const ID_OPEN: i32 = 100;
const ID_DONE: i32 = 101;
const ID_SKIP: i32 = 102;

/// Windows 보안의 "제외" 화면. 못 열면 한 단계 위인 설정 화면이라도 연다.
const EXCLUSIONS_URI: &str = "windowsdefender://exclusions/";
const THREAT_SETTINGS_URI: &str = "windowsdefender://threatsettings/";

/// windows-sys 0.59는 이 값을 Ole 피처에만 둔다. 숫자 하나에 피처를 늘리지 않는다.
const CF_UNICODETEXT: u32 = 13;

/// 한국어 Windows 표시 언어의 주 언어 번호.
const LANG_KOREAN: u16 = 0x12;

struct Texts {
    title: &'static str,
    instruction: &'static str,
    content: &'static str,
    open: &'static str,
    done: &'static str,
    skip: &'static str,
    footer: &'static str,
}

const KO: Texts = Texts {
    title: "AI Quota Tray 설치",
    instruction: "설치하기 전에 Windows 보안 예외를 추가해 주세요",
    content: "AI Quota Tray는 코드 서명이 없어서, Windows 보안이 자동 실행 등록을 악성 동작으로 \
              오인하고 런처를 격리할 수 있습니다. 그러면 Windows를 켤 때 앱이 뜨지 않습니다.\n\n\
              아래 폴더를 제외 목록에 넣으면 막히지 않습니다.\n",
    open: "예외 설정 열기\n폴더 경로를 복사하고 Windows 보안의 제외 화면을 엽니다.",
    done: "추가했습니다, 설치 계속\n예외를 넣은 뒤에 누르세요.",
    skip: "예외 없이 설치\nWindows 보안이 런처를 다시 격리할 수 있습니다.",
    footer: "제외 화면에서: 관리자 확인 [예] → [제외 사항 추가] → [폴더] → 폴더 칸에 Ctrl+V → [폴더 선택]",
};

const EN: Texts = Texts {
    title: "AI Quota Tray Setup",
    instruction: "Add a Windows Security exclusion before installing",
    content: "AI Quota Tray is not code-signed, so Windows Security may mistake its startup \
              registration for malware and quarantine the launcher. The app then won't start \
              with Windows.\n\nAdding the folder below to the exclusion list prevents this.\n",
    open: "Open exclusion settings\nCopies the folder path and opens the Exclusions page in Windows Security.",
    done: "I added it, continue\nPress this after adding the exclusion.",
    skip: "Install without an exclusion\nWindows Security may quarantine the launcher again.",
    footer: "On the Exclusions page: approve the admin prompt → Add an exclusion → Folder → \
             paste with Ctrl+V → Select Folder",
};

fn texts() -> &'static Texts {
    // 런처의 다른 문구는 한국어뿐이지만, 이 창은 설치를 멈춰 세우는 창이라
    // 읽지 못하면 다음으로 갈 수가 없다. 한국어가 아니면 영어로 보인다.
    if unsafe { GetUserDefaultUILanguage() } & 0x3FF == LANG_KOREAN {
        &KO
    } else {
        &EN
    }
}

/// 안내 창을 띄우고 답을 기다린다. 창을 못 띄우면 Err를 준다.
pub fn ask(dir: &Path) -> Result<Choice, String> {
    let t = texts();
    let dir_wide: Vec<u16> = dir.as_os_str().encode_wide().chain(Some(0)).collect();

    let title = to_wide(t.title);
    let instruction = to_wide(t.instruction);
    let content = to_wide(&format!("{}{}", t.content, dir.display()));
    let open = to_wide(t.open);
    let done = to_wide(t.done);
    let skip = to_wide(t.skip);
    let footer = to_wide(t.footer);

    let buttons = [
        TASKDIALOG_BUTTON { nButtonID: ID_OPEN, pszButtonText: open.as_ptr() },
        TASKDIALOG_BUTTON { nButtonID: ID_DONE, pszButtonText: done.as_ptr() },
        TASKDIALOG_BUTTON { nButtonID: ID_SKIP, pszButtonText: skip.as_ptr() },
    ];

    let mut config: TASKDIALOGCONFIG = unsafe { std::mem::zeroed() };
    config.cbSize = std::mem::size_of::<TASKDIALOGCONFIG>() as u32;
    config.dwFlags = TDF_USE_COMMAND_LINKS | TDF_ALLOW_DIALOG_CANCELLATION;
    config.dwCommonButtons = TDCBF_CANCEL_BUTTON;
    config.pszWindowTitle = title.as_ptr();
    config.Anonymous1.pszMainIcon = TD_SHIELD_ICON;
    config.pszMainInstruction = instruction.as_ptr();
    config.pszContent = content.as_ptr();
    config.cButtons = buttons.len() as u32;
    config.pButtons = buttons.as_ptr();
    config.nDefaultButton = ID_OPEN;
    config.Anonymous2.pszFooterIcon = TD_INFORMATION_ICON;
    config.pszFooter = footer.as_ptr();
    config.pfCallback = Some(on_notify);
    // 콜백이 클립보드에 넣을 경로. 창이 떠 있는 동안 이 함수의 지역이 살아 있다.
    config.lpCallbackData = &dir_wide as *const Vec<u16> as isize;

    let mut pressed = 0;
    let hr = unsafe {
        TaskDialogIndirect(&config, &mut pressed, std::ptr::null_mut(), std::ptr::null_mut())
    };
    if hr != S_OK {
        return Err(format!("예외 안내 창을 띄우지 못했습니다 (HRESULT {hr:#010x})"));
    }

    Ok(match pressed {
        ID_DONE => Choice::Confirmed,
        ID_SKIP => Choice::Skipped,
        _ => Choice::Cancelled,
    })
}

unsafe extern "system" fn on_notify(
    hwnd: HWND,
    msg: TASKDIALOG_NOTIFICATIONS,
    wparam: WPARAM,
    _lparam: LPARAM,
    data: isize,
) -> HRESULT {
    if msg == TDN_BUTTON_CLICKED && wparam as i32 == ID_OPEN {
        let dir = unsafe { &*(data as *const Vec<u16>) };
        copy_to_clipboard(hwnd, dir);
        open_exclusions();
        // 창은 그대로 둔다. 예외를 넣고 돌아와 "추가했습니다"를 눌러야 다음으로 간다.
        return S_FALSE;
    }
    S_OK
}

/// 폴더 고르기 창의 폴더 칸에 붙여넣게 경로를 클립보드에 둔다.
/// 실패해도 경로는 안내 창 본문에 보이니 손으로 옮길 수 있다.
fn copy_to_clipboard(owner: HWND, text: &[u16]) {
    unsafe {
        if OpenClipboard(owner) == 0 {
            return;
        }
        EmptyClipboard();
        let memory = GlobalAlloc(GMEM_MOVEABLE, text.len() * 2);
        if !memory.is_null() {
            let mut handed_over = false;
            let target = GlobalLock(memory) as *mut u16;
            if !target.is_null() {
                std::ptr::copy_nonoverlapping(text.as_ptr(), target, text.len());
                GlobalUnlock(memory);
                // 성공하면 메모리는 클립보드 것이 된다. 실패했을 때만 돌려준다.
                handed_over = !SetClipboardData(CF_UNICODETEXT, memory as HANDLE).is_null();
            }
            if !handed_over {
                GlobalFree(memory);
            }
        }
        CloseClipboard();
    }
}

fn open_exclusions() {
    // ShellExecute는 32 이하를 실패로 돌려준다.
    if shell_open(EXCLUSIONS_URI) <= 32 {
        shell_open(THREAT_SETTINGS_URI);
    }
}

fn shell_open(target: &str) -> isize {
    let verb = to_wide("open");
    let file = to_wide(target);
    unsafe {
        ShellExecuteW(
            std::ptr::null_mut(),
            verb.as_ptr(),
            file.as_ptr(),
            std::ptr::null(),
            std::ptr::null(),
            SW_SHOWNORMAL,
        ) as isize
    }
}

fn to_wide(value: &str) -> Vec<u16> {
    OsStr::new(value).encode_wide().chain(Some(0)).collect()
}
