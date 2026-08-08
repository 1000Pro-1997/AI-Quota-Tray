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
use windows_sys::Win32::Graphics::Gdi::{CreateSolidBrush, HBRUSH, UpdateWindow};
use windows_sys::Win32::UI::Controls::{PBM_SETPOS, PBM_SETRANGE32};
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
        wc.hIcon = LoadIconW(instance, to_wide("IDI_ICON1").as_ptr());
        RegisterClassW(&wc);

        // 화면 한가운데. 작업 표시줄을 가리지 않을 만큼 작게.
        let (w, h) = (420, 150);
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

        // 상태 문구.
        CreateWindowExW(
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

        // 진행 막대. 공용 컨트롤이라 별도 초기화 없이 쓸 수 있다.
        let bar = CreateWindowExW(
            0,
            to_wide("msctls_progress32").as_ptr(),
            std::ptr::null(),
            WS_CHILD | WS_VISIBLE,
            20,
            68,
            370,
            22,
            hwnd,
            BAR_ID as _,
            instance,
            std::ptr::null(),
        );
        // 0~1000으로 잡아야 소수점 단위 움직임이 보인다.
        SendMessageW(bar, PBM_SETRANGE32, 0, 1000);

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
