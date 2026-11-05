// 런처 exe에 아이콘과 매니페스트를 박는다.
// 아이콘이 없으면 사용자가 릴리스에서 받은 파일이 기본 exe 그림으로 보인다.
//
// 매니페스트는 빠지면 안 된다. 첫 설치 때 띄우는 예외 안내 창(TaskDialogIndirect)은
// 공용 컨트롤 6판에만 있어서, 매니페스트 없이 빌드하면 진입점을 찾지 못해
// exe가 아예 뜨지 않는다. 그래서 아이콘과 달리 실패하면 빌드를 멈춘다.
const ICON: &str = "../src/AiUsageTray/Assets/app.ico";

const MANIFEST: &str = r#"<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
  <dependency>
    <dependentAssembly>
      <assemblyIdentity type="win32" name="Microsoft.Windows.Common-Controls" version="6.0.0.0"
                        processorArchitecture="*" publicKeyToken="6595b64144ccf1df" language="*"/>
    </dependentAssembly>
  </dependency>
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
    <security>
      <requestedPrivileges>
        <requestedExecutionLevel level="asInvoker" uiAccess="false"/>
      </requestedPrivileges>
    </security>
  </trustInfo>
</assembly>
"#;

fn main() {
    let mut res = winresource::WindowsResource::new();
    res.set_manifest(MANIFEST);

    if std::path::Path::new(ICON).exists() {
        res.set_icon(ICON);
    } else {
        // 아이콘을 못 찾아도 빌드는 계속한다. 없으면 기본 그림이 될 뿐이다.
        println!("cargo:warning=app.ico를 찾지 못해 아이콘 없이 빌드한다.");
    }
    res.set("FileDescription", "AI Quota Tray Setup");
    res.set("ProductName", "AI Quota Tray");
    res.set("LegalCopyright", "MIT License");

    if let Err(error) = res.compile() {
        panic!("리소스를 넣지 못했다. 매니페스트 없이는 런처가 뜨지 않는다: {error}");
    }
}
