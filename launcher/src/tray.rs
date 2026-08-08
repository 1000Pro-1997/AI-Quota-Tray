//! 런처가 일하는 동안 트레이에 띄우는 회색 숫자 아이콘.
//!
//! Setup.exe를 눌렀을 때 아무 창도 안 뜨면 사용자는 실행이 실패한 줄 안다.
//! 그렇다고 매번 창을 띄우면 이미 설치된 사람에게는 성가시다. 그래서 트레이에
//! 작은 아이콘만 남기고, 진행 상황은 아이콘 위의 숫자와 툴팁으로 알린다.
//!
//! 숫자는 GDI로 그때그때 그린다. 미리 만든 .ico를 바꿔 끼우는 방법도 있지만
//! 그러면 퍼센트를 한 칸 단위로 보여줄 수 없다.
//!
//! 트레이 아이콘은 자기를 만든 스레드의 메시지 반복에 매여 있다. 그래서 창과
//! 마찬가지로 전용 스레드를 주고, 화면에는 보이지 않는 껍데기 창에 얹는다.

use std::ffi::OsStr;
use std::os::windows::ffi::OsStrExt;
use std::sync::Arc;
use std::sync::Mutex;
use std::sync::atomic::{AtomicBool, AtomicI32, Ordering};
use std::thread::{self, JoinHandle};
use windows_sys::Win32::Foundation::{HWND, LPARAM, LRESULT, POINT, RECT, WPARAM};
use windows_sys::Win32::Graphics::Gdi::*;
use windows_sys::Win32::System::LibraryLoader::GetModuleHandleW;
use windows_sys::Win32::UI::Shell::{
    NIF_ICON, NIF_MESSAGE, NIF_TIP, NIM_ADD, NIM_DELETE, NIM_MODIFY, NOTIFYICONDATAW,
    Shell_NotifyIconW,
};
use windows_sys::Win32::UI::WindowsAndMessaging::*;

/// 트레이 아이콘이 보내오는 알림을 받을 우리끼리의 메시지 번호.
const WM_TRAY: u32 = WM_USER + 1;
/// 상태가 바뀌었으니 아이콘을 다시 그리라는 신호.
const WM_REFRESH: u32 = WM_USER + 2;

/// 우클릭 메뉴 항목. 0은 "고른 것 없음"이라 1부터 쓴다.
const ID_OPEN: usize = 1;
const ID_FOLDER: usize = 2;
const ID_LOG: usize = 3;
const ID_QUIT: usize = 4;

/// 사용자가 아이콘으로 요청한 일.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum TrayCommand {
    /// 왼쪽 클릭. 앱을 띄우거나 앞으로 가져온다.
    Open,
    /// 설치 폴더 열기.
    OpenFolder,
    /// 로그 파일 열기.
    OpenLog,
    /// 런처를 그만둔다.
    Quit,
}

/// 아이콘과 일하는 쪽이 함께 보는 상태.
struct Shared {
    /// 아이콘에 그릴 숫자. 0~100. 음수면 실패 표시(느낌표)를 그린다.
    value: AtomicI32,
    /// 마우스를 올렸을 때 뜨는 글.
    tip: Mutex<String>,
    /// 사용자가 종료를 골랐는가.
    quit: AtomicBool,
    /// 쌓인 요청. 일하는 쪽이 가져가 비운다.
    commands: Mutex<Vec<TrayCommand>>,
}

/// 트레이 아이콘 손잡이. 떨어뜨리면 아이콘이 사라진다.
pub struct TrayIcon {
    shared: Arc<Shared>,
    hwnd: usize,
    thread: Option<JoinHandle<()>>,
}

impl TrayIcon {
    /// 아이콘을 띄운다. 못 띄워도 하던 일은 계속되어야 하므로 None을 준다.
    pub fn show(tip: &str, value: i32) -> Option<Self> {
        let shared = Arc::new(Shared {
            value: AtomicI32::new(value),
            tip: Mutex::new(tip.to_string()),
            quit: AtomicBool::new(false),
            commands: Mutex::new(Vec::new()),
        });

        let (tx, rx) = std::sync::mpsc::channel::<usize>();
        let worker = Arc::clone(&shared);

        let thread = thread::spawn(move || run_tray(worker, tx));

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

    /// 숫자와 툴팁을 함께 바꾼다. 0~100, 음수면 실패 표시.
    pub fn set(&self, value: i32, tip: &str) {
        self.shared
            .value
            .store(value.clamp(-1, 100), Ordering::Relaxed);
        if let Ok(mut slot) = self.shared.tip.lock() {
            *slot = tip.to_string();
        }
        unsafe {
            PostMessageW(self.hwnd as HWND, WM_REFRESH, 0, 0);
        }
    }

    /// 진행률을 퍼센트로 바꾼다. 툴팁 뒤에 숫자를 붙여 준다.
    pub fn set_percent(&self, label: &str, percent: f64) {
        let value = percent.clamp(0.0, 100.0) as i32;
        self.set(value, &format!("{label} {value}%"));
    }

    /// 사용자가 종료를 골랐는가. 오래 걸리는 일은 이걸 봐 가며 멈춰야 한다.
    pub fn quit_requested(&self) -> bool {
        self.shared.quit.load(Ordering::Relaxed)
    }

    /// 쌓인 요청을 가져가고 비운다.
    pub fn take_commands(&self) -> Vec<TrayCommand> {
        match self.shared.commands.lock() {
            Ok(mut list) => std::mem::take(&mut *list),
            Err(_) => Vec::new(),
        }
    }
}

impl Drop for TrayIcon {
    fn drop(&mut self) {
        unsafe {
            PostMessageW(self.hwnd as HWND, WM_CLOSE, 0, 0);
        }
        if let Some(handle) = self.thread.take() {
            let _ = handle.join();
        }
    }
}

/// 껍데기 창을 만들고 트레이에 아이콘을 얹은 뒤 메시지를 돌린다.
fn run_tray(shared: Arc<Shared>, tx: std::sync::mpsc::Sender<usize>) {
    unsafe {
        let instance = GetModuleHandleW(std::ptr::null());
        let class = to_wide("AiQuotaTrayLauncherTray");

        let mut wc: WNDCLASSW = std::mem::zeroed();
        wc.lpfnWndProc = Some(tray_proc);
        wc.hInstance = instance;
        wc.lpszClassName = class.as_ptr();
        RegisterClassW(&wc);

        // 크기 0으로 두어 화면에 드러나지 않게 한다. HWND_MESSAGE로 만들면
        // 아예 안 보이지만 그러면 우클릭 메뉴가 포커스를 못 받아 바로 닫힌다.
        let hwnd = CreateWindowExW(
            0,
            class.as_ptr(),
            to_wide("AI Quota Tray Launcher").as_ptr(),
            WS_OVERLAPPED,
            0,
            0,
            0,
            0,
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            instance,
            std::ptr::null(),
        );

        if hwnd.is_null() {
            let _ = tx.send(0);
            return;
        }

        SetWindowLongPtrW(hwnd, GWLP_USERDATA, Arc::into_raw(shared) as isize);

        let mut data = notify_data(hwnd);
        data.uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP;
        data.uCallbackMessage = WM_TRAY;
        fill_from_shared(hwnd, &mut data);

        let added = Shell_NotifyIconW(NIM_ADD, &data) != 0;
        // 아이콘 핸들은 셸이 복사해 가므로 우리 몫은 바로 돌려준다.
        if !data.hIcon.is_null() {
            DestroyIcon(data.hIcon);
        }

        if !added {
            let ptr = GetWindowLongPtrW(hwnd, GWLP_USERDATA) as *mut Shared;
            if !ptr.is_null() {
                drop(Arc::from_raw(ptr));
                SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0);
            }
            DestroyWindow(hwnd);
            let _ = tx.send(0);
            return;
        }

        let _ = tx.send(hwnd as usize);

        let mut msg: MSG = std::mem::zeroed();
        while GetMessageW(&mut msg, std::ptr::null_mut(), 0, 0) > 0 {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }

        // 지우지 않고 끝내면 마우스를 올릴 때까지 유령 아이콘이 남는다.
        let gone = notify_data(hwnd);
        Shell_NotifyIconW(NIM_DELETE, &gone);
    }
}

fn notify_data(hwnd: HWND) -> NOTIFYICONDATAW {
    let mut data: NOTIFYICONDATAW = unsafe { std::mem::zeroed() };
    data.cbSize = std::mem::size_of::<NOTIFYICONDATAW>() as u32;
    data.hWnd = hwnd;
    data.uID = 1;
    data
}

/// 지금 상태대로 아이콘 그림과 툴팁을 채운다. hIcon은 부른 쪽이 지워야 한다.
unsafe fn fill_from_shared(hwnd: HWND, data: &mut NOTIFYICONDATAW) {
    unsafe {
        let ptr = GetWindowLongPtrW(hwnd, GWLP_USERDATA) as *const Shared;
        if ptr.is_null() {
            return;
        }

        let value = (*ptr).value.load(Ordering::Relaxed);
        data.hIcon = draw_icon(value);

        let tip = (*ptr)
            .tip
            .lock()
            .map(|t| t.clone())
            .unwrap_or_else(|_| String::new());

        // szTip은 128칸이고 마지막은 널이어야 한다. 넘치면 잘라 넣는다.
        let wide = to_wide(&tip);
        let count = wide.len().min(data.szTip.len());
        data.szTip[..count].copy_from_slice(&wide[..count]);
        data.szTip[count.min(data.szTip.len() - 1)] = 0;
    }
}

/// 회색 숫자만 그린 아이콘을 만든다. 음수면 느낌표.
///
/// 트레이는 보통 16px이지만 고DPI에서는 더 크게 요구한다. 시스템이 알려주는
/// 크기로 그려야 흐려지지 않는다.
unsafe fn draw_icon(value: i32) -> HICON {
    unsafe {
        let size = GetSystemMetrics(SM_CXSMICON).max(16);

        let screen = GetDC(std::ptr::null_mut());
        let dc = CreateCompatibleDC(screen);

        // 32비트 DIB라야 배경을 투명하게 남길 수 있다.
        let mut info: BITMAPINFO = std::mem::zeroed();
        info.bmiHeader.biSize = std::mem::size_of::<BITMAPINFOHEADER>() as u32;
        info.bmiHeader.biWidth = size;
        // 음수로 두면 위에서 아래로 그려져 픽셀 좌표가 뒤집히지 않는다.
        info.bmiHeader.biHeight = -size;
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;
        info.bmiHeader.biCompression = BI_RGB;

        let mut bits: *mut core::ffi::c_void = std::ptr::null_mut();
        let bitmap = CreateDIBSection(
            dc,
            &info,
            DIB_RGB_COLORS,
            &mut bits,
            std::ptr::null_mut(),
            0,
        );
        if bitmap.is_null() || bits.is_null() {
            DeleteDC(dc);
            ReleaseDC(std::ptr::null_mut(), screen);
            return std::ptr::null_mut();
        }

        let old = SelectObject(dc, bitmap as _);

        // 회색 바탕을 깔고 그 위에 흰 숫자를 얹는다. 글자만 그리면 트레이가
        // 밝을 때 회색 숫자가 묻혀 버린다. 바탕이 있으면 어느 테마에서도 읽힌다.
        let pixels = bits as *mut u32;
        for i in 0..(size * size) as usize {
            *pixels.add(i) = 0;
        }
        let alpha = fill_rounded(pixels, size);

        let text = if value < 0 {
            "!".to_string()
        } else {
            value.to_string()
        };

        // 세 자리(100)까지 바탕 안에 들어가야 한다. 글자 높이를 기준으로 잡는
        // 값이라 자릿수가 늘면 가로로 먼저 넘친다. 그래서 자릿수마다 따로 준다.
        // 16px에서 실제로 그려 보고 고른 비율이니 함부로 올리면 숫자가 잘린다.
        let height = match text.chars().count() {
            1 => (size as f32 * 0.72) as i32,
            2 => (size as f32 * 0.56) as i32,
            _ => (size as f32 * 0.40) as i32,
        };

        let face = to_wide("Segoe UI");
        let font = CreateFontW(
            -height,
            0,
            0,
            0,
            FW_SEMIBOLD as i32,
            0,
            0,
            0,
            DEFAULT_CHARSET.into(),
            OUT_DEFAULT_PRECIS.into(),
            CLIP_DEFAULT_PRECIS.into(),
            // 트레이 아이콘은 작다. 안티앨리어싱이 없으면 숫자를 못 알아본다.
            ANTIALIASED_QUALITY.into(),
            (DEFAULT_PITCH | FF_DONTCARE) as u32,
            face.as_ptr(),
        );
        let old_font = SelectObject(dc, font as _);

        SetBkMode(dc, TRANSPARENT as i32);
        // 회색 바탕 위라 흰 글자가 가장 또렷하다. 앱 본체의 컬러 아이콘과
        // 헷갈리지 않게 색은 여전히 무채색으로만 쓴다.
        SetTextColor(dc, 0x00FF_FFFF);

        let mut rect = RECT {
            left: 0,
            top: 0,
            right: size,
            bottom: size,
        };
        let wide_text = to_wide(&text);
        DrawTextW(
            dc,
            wide_text.as_ptr(),
            text.chars().count() as i32,
            &mut rect,
            DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOCLIP,
        );

        // GDI 글자 그리기는 알파를 건드리지 않아 0인 채로 남는다. 그대로 두면
        // 통째로 투명해 아무것도 안 보인다. 바탕을 칠할 때 미리 넣어 둔
        // 알파를 되살려, 모서리 바깥만 투명하게 남긴다.
        restore_alpha(pixels, size, &alpha);

        SelectObject(dc, old_font);
        DeleteObject(font as _);

        // 32비트 알파를 쓸 때도 마스크 자리는 있어야 한다. 비어 있어도 된다.
        let mask = CreateBitmap(size, size, 1, 1, std::ptr::null());

        let mut icon_info: ICONINFO = std::mem::zeroed();
        icon_info.fIcon = 1;
        icon_info.hbmMask = mask;
        icon_info.hbmColor = bitmap;
        let icon = CreateIconIndirect(&icon_info);

        SelectObject(dc, old);
        DeleteObject(mask as _);
        DeleteObject(bitmap as _);
        DeleteDC(dc);
        ReleaseDC(std::ptr::null_mut(), screen);

        icon
    }
}

/// 모서리가 둥근 회색 바탕을 칠하고, 각 픽셀의 알파를 따로 돌려준다.
///
/// 알파를 배열로 빼 두는 까닭은 GDI가 글자를 그리며 알파를 0으로 뭉개기
/// 때문이다. 그림이 끝난 뒤 이 값으로 되살린다.
///
/// 모서리는 픽셀 안을 4×4로 잘라 얼마나 원 안에 드는지 세어 부드럽게 만든다.
/// 트레이 아이콘은 작아서 계단이 그대로 눈에 띈다.
unsafe fn fill_rounded(pixels: *mut u32, size: i32) -> Vec<u8> {
    let mut alpha = vec![0u8; (size * size) as usize];

    // 윈도우 트레이의 다른 아이콘들과 크기를 맞춘다. 꽉 채우면 혼자 커 보인다.
    let inset = (size as f32 * 0.06).round();
    let left = inset;
    let top = inset;
    let right = size as f32 - inset;
    let bottom = size as f32 - inset;
    let radius = (right - left) * 0.28;

    for y in 0..size {
        for x in 0..size {
            let mut covered = 0;

            // 픽셀 하나를 4×4로 잘라 각 조각의 한가운데가 안에 드는지 본다.
            for sy in 0..4 {
                for sx in 0..4 {
                    let px = x as f32 + (sx as f32 + 0.5) / 4.0;
                    let py = y as f32 + (sy as f32 + 0.5) / 4.0;

                    if px < left || px > right || py < top || py > bottom {
                        continue;
                    }

                    // 네 귀퉁이의 둥근 부분만 원으로 잘라 낸다.
                    let cx = if px < left + radius {
                        left + radius
                    } else if px > right - radius {
                        right - radius
                    } else {
                        px
                    };
                    let cy = if py < top + radius {
                        top + radius
                    } else if py > bottom - radius {
                        bottom - radius
                    } else {
                        py
                    };

                    let dx = px - cx;
                    let dy = py - cy;
                    if dx * dx + dy * dy <= radius * radius {
                        covered += 1;
                    }
                }
            }

            if covered == 0 {
                continue;
            }

            let a = (covered * 255 / 16) as u8;
            let index = (y * size + x) as usize;
            alpha[index] = a;

            // 앱 본체의 컬러 아이콘과 헷갈리지 않게 무채색 바탕으로 둔다.
            // 알파는 아래에서 되살리므로 여기서는 색만 넣는다.
            unsafe {
                *pixels.add(index) = 0x0060_6060;
            }
        }
    }

    alpha
}

/// 글자를 그리며 사라진 알파를 되살린다.
///
/// 바탕 밖(알파 0)에 글자가 삐져나갔다면 그 픽셀도 살려야 글자가 안 잘린다.
/// 자릿수에 맞춰 글자를 줄이므로 실제로 그런 일은 드물지만, 글꼴이 없어
/// 다른 것으로 대체될 때를 대비한다.
unsafe fn restore_alpha(pixels: *mut u32, size: i32, alpha: &[u8]) {
    unsafe {
        for i in 0..(size * size) as usize {
            let pixel = *pixels.add(i);
            let base = alpha[i];

            // 바탕보다 밝으면 글자가 얹힌 자리다. 바탕 밖이어도 불투명하게 둔다.
            let lit = pixel & 0x00FF_FFFF > 0x0060_6060;
            let a = if lit { 255 } else { base };

            if a == 0 {
                *pixels.add(i) = 0;
            } else {
                *pixels.add(i) = (pixel & 0x00FF_FFFF) | ((a as u32) << 24);
            }
        }
    }
}

unsafe extern "system" fn tray_proc(
    hwnd: HWND,
    msg: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    unsafe {
        match msg {
            WM_REFRESH => {
                let mut data = notify_data(hwnd);
                data.uFlags = NIF_ICON | NIF_TIP;
                fill_from_shared(hwnd, &mut data);
                Shell_NotifyIconW(NIM_MODIFY, &data);
                if !data.hIcon.is_null() {
                    DestroyIcon(data.hIcon);
                }
                0
            }
            WM_TRAY => {
                // 아래 16비트에 마우스 사건이 실려 온다.
                match (lparam as u32) & 0xFFFF {
                    WM_LBUTTONUP => push(hwnd, TrayCommand::Open),
                    WM_RBUTTONUP | WM_CONTEXTMENU => show_menu(hwnd),
                    _ => {}
                }
                0
            }
            WM_COMMAND => {
                match (wparam & 0xFFFF) as usize {
                    ID_OPEN => push(hwnd, TrayCommand::Open),
                    ID_FOLDER => push(hwnd, TrayCommand::OpenFolder),
                    ID_LOG => push(hwnd, TrayCommand::OpenLog),
                    ID_QUIT => {
                        let ptr = GetWindowLongPtrW(hwnd, GWLP_USERDATA) as *const Shared;
                        if !ptr.is_null() {
                            (*ptr).quit.store(true, Ordering::Relaxed);
                        }
                        push(hwnd, TrayCommand::Quit);
                    }
                    _ => {}
                }
                0
            }
            WM_CLOSE => {
                DestroyWindow(hwnd);
                0
            }
            WM_DESTROY => {
                let ptr = GetWindowLongPtrW(hwnd, GWLP_USERDATA) as *mut Shared;
                if !ptr.is_null() {
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

/// 요청을 쌓아 둔다. 일하는 쪽이 틈틈이 가져간다.
unsafe fn push(hwnd: HWND, command: TrayCommand) {
    unsafe {
        let ptr = GetWindowLongPtrW(hwnd, GWLP_USERDATA) as *const Shared;
        if ptr.is_null() {
            return;
        }
        if let Ok(mut list) = (*ptr).commands.lock() {
            list.push(command);
        }
    }
}

unsafe fn show_menu(hwnd: HWND) {
    unsafe {
        let menu = CreatePopupMenu();
        if menu.is_null() {
            return;
        }

        AppendMenuW(menu, MF_STRING, ID_OPEN, to_wide("앱 열기").as_ptr());
        AppendMenuW(menu, MF_SEPARATOR, 0, std::ptr::null());
        AppendMenuW(
            menu,
            MF_STRING,
            ID_FOLDER,
            to_wide("설치 폴더 열기").as_ptr(),
        );
        AppendMenuW(menu, MF_STRING, ID_LOG, to_wide("로그 보기").as_ptr());
        AppendMenuW(menu, MF_SEPARATOR, 0, std::ptr::null());
        AppendMenuW(menu, MF_STRING, ID_QUIT, to_wide("종료").as_ptr());

        let mut point = POINT { x: 0, y: 0 };
        GetCursorPos(&mut point);

        // 이 창을 앞으로 세우지 않으면 메뉴가 뜨자마자 닫힌다. 오래된 규칙이다.
        SetForegroundWindow(hwnd);
        TrackPopupMenu(
            menu,
            TPM_RIGHTBUTTON,
            point.x,
            point.y,
            0,
            hwnd,
            std::ptr::null(),
        );
        // 메뉴가 닫힌 뒤 남는 메시지를 흘려보낸다.
        PostMessageW(hwnd, WM_NULL, 0, 0);
        DestroyMenu(menu);
    }
}

fn to_wide(value: &str) -> Vec<u16> {
    OsStr::new(value).encode_wide().chain(Some(0)).collect()
}
