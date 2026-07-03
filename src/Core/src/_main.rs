// src/main.rs
// Test file to create a demo program.
mod models;

use std::env;
use std::ffi::CString;

fn main() {
    // DEBUG
    // let mut detector = PiiDetector::new("../Anonymizer/models");
    // match detector.find_pii("Salut Lionel ! GitHub : http://github.com/LionelLalande ") {
    //     Ok(spans) => println!("PII found: {:?}", spans),
    //     Err(e) => eprintln!("find_pii ERROR: {e}"),
    // }

    // 1. Handling the 3 arguments required by the new pipeline
    let args: Vec<String> = env::args().collect();
    if args.len() < 4 {
        eprintln!(
            "Usage: {} <source_file.pdf> <models_directory> <output_file.pdf>",
            args[0]
        );
        std::process::exit(1);
    }

    let input_path = &args[1];
    let models_dir = &args[2];
    let output_path = &args[3];

    println!("--- [Test CLI] Launching the pipeline via the FFI interface ---");

    // 2. Simulation of C# behavior (Conversion to C-compatible strings for P/Invoke)
    let c_input_path = CString::new(input_path.as_str()).expect("Failed to convert input_path");
    let c_models_dir = CString::new(models_dir.as_str()).expect("Failed to convert models_dir");
    let c_output_path = CString::new(output_path.as_str()).expect("Failed to convert output_path");

    // 3. Call the function from your library (lib.rs)
    let status = redact::redact_pdf(
        c_input_path.as_ptr(),
        c_models_dir.as_ptr(),
        c_output_path.as_ptr(),
    );

    // 4. Handling the integer return code
    match status {
        0 => println!("\n[Success CLI] The pipeline returned code 0."),
        -1 => {
            eprintln!("\n[Error CLI] Code -1: One of the passed pointers was null.");
            std::process::exit(1);
        }
        -2 => {
            eprintln!("\n[Error CLI] Code -2: UTF-8 conversion error in the arguments.");
            std::process::exit(1);
        }
        -3 => {
            eprintln!(
                "\n[Error CLI] Code -3: An internal error occurred while processing the PDF."
            );
            std::process::exit(1);
        }
        _ => {
            eprintln!("\n[Error CLI] Unknown return code: {}", status);
            std::process::exit(1);
        }
    }
}
