fn main() {
    println!("cargo:rerun-if-changed=resources/mizuauto.ico");
    println!("cargo:rerun-if-changed=resources/app.manifest");
    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("windows") {
        winres::WindowsResource::new()
            .set_icon("resources/mizuauto.ico")
            .set_manifest_file("resources/app.manifest")
            .set("OriginalFilename", "mizuauto.exe")
            .set("ProductName", "Mizuauto")
            .compile()
            .expect("Could not embed Mizuauto icon and UAC manifest");
    }
}
