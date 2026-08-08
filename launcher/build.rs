// 런처 exe에 앱과 같은 아이콘을 박는다.
// 아이콘이 없으면 사용자가 릴리스에서 받은 파일이 기본 exe 그림으로 보인다.
fn main() {
    if !std::path::Path::new("../src/AiUsageTray/Assets/app.ico").exists() {
        // 아이콘을 못 찾아도 빌드는 계속한다. 없으면 기본 그림이 될 뿐이다.
        println!("cargo:warning=app.ico를 찾지 못해 아이콘 없이 빌드한다.");
        return;
    }

    let mut res = winresource::WindowsResource::new();
    res.set_icon("../src/AiUsageTray/Assets/app.ico");
    res.set("FileDescription", "AI Quota Tray Setup");
    res.set("ProductName", "AI Quota Tray");
    res.set("LegalCopyright", "MIT License");

    if let Err(error) = res.compile() {
        println!("cargo:warning=아이콘을 넣지 못했다: {error}");
    }
}
