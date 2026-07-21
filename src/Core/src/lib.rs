// src/lib.rs
mod face_detector;
mod font_map;
mod layout_analysis;
mod markdown_generator;
mod models;
mod pdf_generator;
mod pdf_parser;
mod pii_ner_detector;
mod pii_regex_detector;

use face_detector::FaceDetector;
use lopdf::{Dictionary, Document, Object};
use pii_ner_detector::PiiNerDetector;
use pii_regex_detector::PiiRegexDetector;

use std::ffi::CStr;
use std::os::raw::c_char;
use std::path::Path;

/// Redact PII information from a PDF file using a hybrid approach (NER + Regex + Face Detection).
/// Returns an integer status code indicating the result of the operation:
///  0 = Success
/// -1 = One or more input pointers are null
/// -2 = UTF-8 conversion error on input strings
/// -3 = Internal error during pipeline execution
#[unsafe(no_mangle)]
pub extern "C" fn redact_pdf(
    c_input_path: *const c_char,
    c_models_dir: *const c_char,
    c_output_path: *const c_char,
) -> i32 {
    let result = std::panic::catch_unwind(|| {
        // Safety check for null pointers
        if c_input_path.is_null() || c_models_dir.is_null() || c_output_path.is_null() {
            return -1;
        }

        // Safe conversion of C strings (*const c_char) to Rust &str
        let input_path = unsafe {
            match CStr::from_ptr(c_input_path).to_str() {
                Ok(s) => s,
                Err(_) => return -2,
            }
        };

        let models_dir = unsafe {
            match CStr::from_ptr(c_models_dir).to_str() {
                Ok(s) => s,
                Err(_) => return -2,
            }
        };

        let output_path = unsafe {
            match CStr::from_ptr(c_output_path).to_str() {
                Ok(s) => s,
                Err(_) => return -2,
            }
        };

        // Execute the pipeline and capture errors
        match run_redaction_pipeline(input_path, models_dir, output_path) {
            Ok(_) => 0,
            Err(e) => {
                eprintln!("[Error] Pipeline execution failed: {}", e);
                -3
            }
        }
    });
    match result {
        Ok(code) => code,
        Err(_) => {
            eprintln!("[Error] Internal panic.");
            -4
        }
    }
}

// Internal logic of the global redaction pipeline
fn run_redaction_pipeline(
    input_path: &str,
    models_dir: &str,
    output_path: &str,
) -> Result<(), Box<dyn std::error::Error>> {
    let file_prefix = Path::new(input_path)
        .file_stem()
        .and_then(|s| s.to_str())
        .unwrap_or("document");

    let output_path_path = Path::new(output_path);

    println!("Loading the source file: {}...", input_path);
    let mut doc = Document::load(input_path)?;

    // Low-level extraction (Letters -> Physical Words)
    // On mémorise au passage le numéro de page (1-indexé) associé à chaque
    // ObjectId de page, pour pouvoir plus tard produire un export Markdown
    // lisible (séparateurs "Page N") sans avoir à reparcourir le document.
    let mut all_pages_words = Vec::new();
    let mut page_numbers: std::collections::HashMap<lopdf::ObjectId, usize> =
        std::collections::HashMap::new();
    for (page_index, page_id) in doc.page_iter().enumerate() {
        page_numbers.insert(page_id, page_index + 1);
        let words = pdf_parser::extract_words(&doc, page_id)?;
        all_pages_words.push(words);
    }

    println!("Semantic analysis...");
    let paragraphs = layout_analysis::build_segmented_layout_pdfpig(all_pages_words);

    println!("Initializing detection engines (Multi-agents)...");

    // NER Detector (ONNX Language Model)
    let mut ner_detector = PiiNerDetector::new(models_dir);

    // Deterministic Detector (Regex)
    let regex_detector = PiiRegexDetector::new();

    // Face Detector (UltraFace ONNX)
    let face_model_path = Path::new(models_dir)
        .join("face")
        .join("model.onnx");
    let mut face_detector = FaceDetector::new(&face_model_path, 0.7)?;

    println!("Analyzing text... (could take a while for large documents)");
    let mut redaction_targets = ner_detector.scan_pii(&paragraphs);
    let ner_count = redaction_targets.len();
    let mut regex_count = 0;
    for paragraph in &paragraphs {
        let regex_spans = regex_detector.analyze_text(&paragraph.text);
        if regex_spans.is_empty() {
            continue;
        }

        // Comparaison d'intervalles directe (offset connu à l'avance, voir
        // Word::para_char_range) : plus de curseur de recherche séquentiel,
        // donc plus de désynchronisation possible sur des tokens courts et
        // répétés (dates, numéros de téléphone...).
        for word in paragraph
            .blocks
            .iter()
            .flat_map(|b| &b.lines)
            .flat_map(|l| &l.words)
        {
            let Some(range) = &word.para_char_range else {
                continue;
            };

            let is_regex_pii = regex_spans
                .iter()
                .any(|span| range.start.max(span.start) < range.end.min(span.end));

            if is_regex_pii {
                let already_exists = redaction_targets.iter().any(|w| {
                    w.page_id == word.page_id && w.bbox == word.bbox && w.text == word.text
                });

                if !already_exists {
                    redaction_targets.push(word.clone());
                    regex_count += 1;
                }
            }
        }
    }
    println!(
        "      -> NER Engine   : {} occurrence(s) identified.",
        ner_count
    );
    println!(
        "      -> Regex Engine : {} new occurrence(s) added.",
        regex_count
    );

    println!("Analyzing graphic resources (Face Detection)...");
    let mut face_detected_total = 0;

    let object_ids: Vec<lopdf::ObjectId> = doc.objects.keys().copied().collect();
    for id in object_ids {
        if let Ok(Object::Stream(stream)) = doc.get_object_mut(id) {
            if is_image_stream(&stream.dict) {
                let _ = stream.decompress();
                let image_bytes = &stream.content;

                if face_detector.detect_faces(image_bytes) {
                    face_detected_total += 1;

                    stream.content = vec![];
                    stream
                        .dict
                        .set("Filter", Object::Name(b"FlateDecode".to_vec()));
                }
            }
        }
    }

    if face_detected_total > 0 {
        println!(
            "      -> Face Engine : {} image(s) containing faces purged.",
            face_detected_total
        );
    } else {
        println!("      -> Face Engine : No faces detected in images.");
    }

    let output_str = output_path_path
        .to_str()
        .ok_or("Invalid final output path")?;
    println!("Applying text redaction -> {}...", output_str);

    // Save by applying black masks on textual `Word` structures
    pdf_generator::apply_text_redaction(&mut doc, &redaction_targets)?;

    doc.save(output_str)?;

    // Génère en complément un export Markdown anonymisé (même liste de
    // cibles de caviardage que le PDF), à côté du fichier de sortie, ex:
    // "document.redacted.pdf" -> "document.redacted.md".
    println!("Generating redacted markdown export...");
    let markdown_content = markdown_generator::generate_markdown(
        file_prefix,
        &paragraphs,
        &redaction_targets,
        &page_numbers,
    );
    let markdown_path = output_path_path.with_extension("md");
    std::fs::write(&markdown_path, markdown_content)?;
    println!(
        "      -> Markdown export written to {}",
        markdown_path.display()
    );

    println!("\n[Success] Anonymization pipeline executed successfully.");
    Ok(())
}

/// Utility function to identify if a lopdf object dictionary corresponds to an image
fn is_image_stream(dict: &Dictionary) -> bool {
    if let Ok(Object::Name(type_name)) = dict.get(b"Type") {
        if type_name == b"XObject" {
            if let Ok(Object::Name(subtype_name)) = dict.get(b"Subtype") {
                return subtype_name == b"Image";
            }
        }
    }
    false
}
