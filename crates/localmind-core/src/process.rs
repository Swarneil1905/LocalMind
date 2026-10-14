//! Process creation for desktop background work.

use std::ffi::OsStr;
use std::process::Command;

/// Console executables must not create a terminal when launched by the GUI.
/// Keep this on the command itself so conversion into a Tokio command preserves
/// the Windows flags, while stdout and stderr remain available for diagnostics.
pub fn background_command(program: impl AsRef<OsStr>) -> Command {
    let mut command = Command::new(program);
    #[cfg(target_os = "windows")]
    {
        use std::os::windows::process::CommandExt;
        command.creation_flags(0x0800_0000); // CREATE_NO_WINDOW
    }
    command.stdin(std::process::Stdio::null());
    command
}

#[cfg(all(test, target_os = "windows"))]
mod tests {
    use super::*;

    fn console_probe_command() -> Command {
        let mut command = background_command(std::env::current_exe().unwrap());
        command.args([
            "--exact",
            "process::tests::console_probe",
            "--ignored",
            "--nocapture",
        ]);
        command
    }

    #[test]
    #[ignore = "fixture executed in a separate process by the tests below"]
    fn console_probe() {
        #[link(name = "kernel32")]
        extern "system" {
            fn GetConsoleWindow() -> *mut std::ffi::c_void;
        }
        // CREATE_NO_WINDOW can retain console I/O state, but must not allocate
        // a console window. Checking the code page would reject that valid case.
        assert!(
            unsafe { GetConsoleWindow() }.is_null(),
            "background child has a console window"
        );
        println!("background-child-stdout");
        eprintln!("background-child-stderr");
    }

    fn assert_console_free(output: std::process::Output) {
        assert!(output.status.success(), "{output:?}");
        assert!(String::from_utf8_lossy(&output.stdout).contains("background-child-stdout"));
        assert!(String::from_utf8_lossy(&output.stderr).contains("background-child-stderr"));
    }

    #[test]
    fn background_child_has_no_console_and_keeps_diagnostics() {
        assert_console_free(console_probe_command().output().unwrap());
    }

    #[tokio::test]
    async fn async_background_child_preserves_no_console_flag() {
        let mut command = tokio::process::Command::from(console_probe_command());
        assert_console_free(command.output().await.unwrap());
    }
}
