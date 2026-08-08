//! 첫 설치 때 보여 주는 작은 진행 창.
//!
//! 자립형 앱은 160MB가 넘어 느린 회선에서는 수십 초가 걸린다. 그동안 아무것도
//! 뜨지 않으면 사용자는 실행이 실패한 줄 안다. 창 하나로 그 침묵을 없앤다.
//!
//! 다운로드는 이 창과 다른 스레드에서 돈다. 창은 자기 스레드에서 메시지를
//! 돌리며 살아 있고, 진행률만 원자적으로 주고받는다. 외부 GUI 크레이트를
//! 쓰지 않는 이유는 런처가 작아야 의미가 있기 때문이다.

use std::ffi::OsStr;
use std::os::windows::ffi::OsStrExt;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
use std::thread::{self, JoinHandle};
use windows_sys::Win32::Foundation::{HWND, LPARAM, LRESULT, WPARAM};
use windows_sys::Win32::Graphics::Gdi::{
    CreateFontW, CreateSolidBrush, DEFAULT_CHARSET, DEFAULT_PITCH, FF_DONTCARE, FW_NORMAL, HBRUSH,
    UpdateWindow,
};
use windows_sys::Win32::UI::Controls::{PBM_SETBARCOLOR, PBM_SETBKCOLOR, PBM_SETPOS, PBM_SETRANGE32};
use windows_sys::Win32::System::LibraryLoader::GetModuleHandleW;
use windows_sys::Win32::UI::WindowsAndMessaging::*;

/// 창과 다운로드 스레드가 함께 보는 상태.
struct Shared {
    /// 0~1000. 정수로 두어야 원자적으로 주고받을 수 있다.
    permille: AtomicU32,
    /// 사용자가 창을 닫았는가. 다운로드를 멈추는 신호로 쓴다.
    cancelled: AtomicBool,
}

/// 진행 창 손잡이. 떨어뜨리면 창이 닫힌다.
pub struct ProgressWindow {
    shared: Arc<Shared>,
    hwnd: usize,
    thread: Option<JoinHandle<()>>,
}

const WM_TICK: u32 = WM_USER + 1;
const BAR_ID: usize = 1001;

const LABEL_ID: usize = 1002;

/// 오른쪽 정렬 STATIC. windows-sys 0.59는 이 값을 내주지 않아 직접 적는다.
const SS_RIGHT: u32 = 0x0000_0002;

/// exe에 박힌 아이콘의 번호. winresource가 첫 아이콘을 1번으로 넣는다.
/// MAKEINTRESOURCE와 같은 뜻이다 — 낮은 워드에 번호를 담은 가짜 포인터.
const ICON_RESOURCE_ID: *const u16 = 1 as *const u16;

/// 막대 색. COLORREF는 0x00BBGGRR 순서라 iOS 파랑(#007AFF)이 뒤집혀 적힌다.
const ACCENT: u32 = 0x00FF_7A00;
/// 막대 바탕. 창의 흰색과 구분되는 옅은 회색.
const TRACK: u32 = 0x00EF_EF_EF;

impl ProgressWindow {
    /// 창을 띄운다. 창을 만들지 못해도 설치는 계속되어야 하므로 None을 준다.
    pub fn open(title: &str, message: &str) -> Option<Self> {
        let shared = Arc::new(Shared {
            permille: AtomicU32::new(0),
            cancelled: AtomicBool::new(false),
        });

        let (tx, rx) = std::sync::mpsc::channel::<usize>();
        let worker = Arc::clone(&shared);
        let title = title.to_string();
        let message = message.to_string();

        // 창은 만든 스레드에서만 메시지를 받을 수 있다. 전용 스레드를 준다.
        let thread = thread::spawn(move || run_window(&title, &message, worker, tx));

        // 창이 만들어지기를 잠시 기다린다. 실패하면 0이 온다.
        let hwnd = rx.recv().unwrap_or(0);
        if hwnd == 0 {
            return None;
        }

        Some(Self {
            shared,
            hwnd,
            thread: Some(thread),
        })
    }

    /// 0.0~1.0. 창에 곧바로 반영된다.
    pub fn set(&self, fraction: f64) {
        let value = (fraction.clamp(0.0, 1.0) * 1000.0) as u32;
        self.shared.permille.store(value, Ordering::Relaxed);
        unsafe {
            PostMessageW(self.hwnd as HWND, WM_TICK, 0, 0);
        }
    }

    /// 사용자가 창을 닫았는가. 그러면 받기를 그만두어야 한다.
    pub fn cancelled(&self) -> bool {
        self.shared.cancelled.load(Ordering::Relaxed)
    }
}

impl Drop for ProgressWindow {
    fn drop(&mut self) {
        unsafe {
            PostMessageW(self.hwnd as HWND, WM_CLOSE, 0, 0);
        }
        if let Some(handle) = self.thread.take() {
            let _ = handle.join();
        }
    }
}

/// 창을 만들고 메시지를 돌린다. 이 함수가 끝나면 창도 사라진다.
fn run_window(
    title: &str,
    message: &str,
    shared: Arc<Shared>,
    tx: std::sync::mpsc::Sender<usize>,
) {
    unsafe {
        let instance = GetModuleHandleW(std::ptr::null());
        let class = to_wide("AiQuotaTrayProgress");

        let mut wc: WNDCLASSW = std::mem::zeroed();
        wc.lpfnWndProc = Some(window_proc);
        wc.hInstance = instance;
        wc.lpszClassName = class.as_ptr();
        wc.hCursor = LoadCursorW(std::ptr::null_mut(), IDC_ARROW);
        wc.hbrBackground = CreateSolidBrush(0x00FF_FFFF) as HBRUSH;

        // 아이콘은 이름이 아니라 번호로 찾아야 한다. winresource가 넣어 주는
        // RT_GROUP_ICON은 이름 없이 1번으로만 들어가서, "IDI_ICON1" 같은
        // 문자열로 부르면 조용히 null이 돌아온다. 그래서 여태 제목 표시줄과
        // 작업 표시줄이 비어 있었다.
        wc.hIcon = LoadIconW(instance, ICON_RESOURCE_ID);
        RegisterClassW(&wc);

        // 화면 한가운데. 작업 표시줄을 가리지 않을 만큼 작게.
        let (w, h) = (420, 172);
        let sw = GetSystemMetrics(SM_CXSCREEN);
        let sh = GetSystemMetrics(SM_CYSCREEN);

        let hwnd = CreateWindowExW(
            0,
            class.as_ptr(),
            to_wide(title).as_ptr(),
            // 최소화·최대화는 뺀다. 닫기만 남겨 취소로 쓴다.
            WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU,
            (sw - w) / 2,
            (sh - h) / 2,
            w,
            h,
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            instance,
            std::ptr::null(),
        );

        if hwnd.is_null() {
            let _ = tx.send(0);
            return;
        }

        // 지정하지 않으면 90년대 비트맵 글꼴이 나온다. 창 하나 띄우는 값에
        // 견주어 글꼴 한 줄이 인상을 가장 크게 바꾼다.
        let font = CreateFontW(
            -15, 0, 0, 0, FW_NORMAL as i32, 0, 0, 0,
            DEFAULT_CHARSET as u32, 0, 0, 0,
            (DEFAULT_PITCH | FF_DONTCARE) as u32,
            to_wide("Segoe UI").as_ptr(),
        );

        // 상태 문구.
        let text = CreateWindowExW(
            0,
            to_wide("STATIC").as_ptr(),
            to_wide(message).as_ptr(),
            WS_CHILD | WS_VISIBLE,
            20,
            18,
            370,
            40,
            hwnd,
            std::ptr::null_mut(),
            instance,
            std::ptr::null(),
        );
        SendMessageW(text, WM_SETFONT, font as WPARAM, 1);

        // 진행 막대. 공용 컨트롤이라 별도 초기화 없이 쓸 수 있다.
        let bar = CreateWindowExW(
            0,
            to_wide("msctls_progress32").as_ptr(),
            std::ptr::null(),
            WS_CHILD | WS_VISIBLE,
            20,
            70,
            370,
            10,
            hwnd,
            BAR_ID as _,
            instance,
            std::ptr::null(),
        );
        // 0~1000으로 잡아야 소수점 단위 움직임이 보인다.
        SendMessageW(bar, PBM_SETRANGE32, 0, 1000);

        // 색을 직접 주면 테마가 꺼져 막대가 납작해진다. 여기서는 그편이
        // 낫다 — 기본 테마의 초록 그라데이션보다 단색이 차분하다.
        SendMessageW(bar, PBM_SETBARCOLOR, 0, ACCENT as LPARAM);
        SendMessageW(bar, PBM_SETBKCOLOR, 0, TRACK as LPARAM);

        // 퍼센트. 막대만 있으면 얼마나 남았는지 눈대중해야 한다.
        let label = CreateWindowExW(
            0,
            to_wide("STATIC").as_ptr(),
            to_wide("0%").as_ptr(),
            WS_CHILD | WS_VISIBLE | SS_RIGHT,
            20,
            90,
            370,
            20,
            hwnd,
            LABEL_ID as _,
            instance,
            std::ptr::null(),
        );
        SendMessageW(label, WM_SETFONT, font as WPARAM, 1);

        // 글꼴은 따로 지우지 않는다. 이 창은 설치가 끝나면 프로세스와 함께
        // 사라져서, 회수하자고 창 구조에 자리를 더 내는 값이 안 나온다.
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, Arc::into_raw(shared) as isize);
        ShowWindow(hwnd, SW_SHOW);
        UpdateWindow(hwnd);

        let _ = tx.send(hwnd as usize);

        let mut msg: MSG = std::mem::zeroed();
        while GetMessageW(&mut msg, std::ptr::null_mut(), 0, 0) > 0 {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }
}

unsafe extern "system" fn window_proc(
    hwnd: HWND,
    msg: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    unsafe {
        match msg {
            WM_TICK => {
                let ptr = GetWindowLongPtrW(hwnd, GWLP_USERDATA) as *const Shared;
                if !ptr.is_null() {
                    let value = (*ptr).permille.load(Ordering::Relaxed);
                    let bar = GetDlgItem(hwnd, BAR_ID as i32);
                    if !bar.is_null() {
                        SendMessageW(bar, PBM_SETPOS, value as WPARAM, 0);
                    }

                    let label = GetDlgItem(hwnd, LABEL_ID as i32);
                    if !label.is_null() {
                        let text = to_wide(&format!("{}%", value / 10));
                        SetWindowTextW(label, text.as_ptr());
                    }
                }
                0
            }
            // 창을 닫으면 받기를 그만둔다는 뜻이다.
            WM_CLOSE => {
                let ptr = GetWindowLongPtrW(hwnd, GWLP_USERDATA) as *const Shared;
                if !ptr.is_null() {
                    (*ptr).cancelled.store(true, Ordering::Relaxed);
                }
                DestroyWindow(hwnd);
                0
            }
            WM_DESTROY => {
                let ptr = GetWindowLongPtrW(hwnd, GWLP_USERDATA) as *mut Shared;
                if !ptr.is_null() {
                    // 창이 쥐고 있던 몫을 돌려준다.
                    drop(Arc::from_raw(ptr));
                    SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0);
                }
                PostQuitMessage(0);
                0
            }
            _ => DefWindowProcW(hwnd, msg, wparam, lparam),
        }
    }
}

fn to_wide(value: &str) -> Vec<u16> {
    OsStr::new(value).encode_wide().chain(Some(0)).collect()
}
