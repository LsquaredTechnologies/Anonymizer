// src/pdf_generator.rs
use crate::models::{Paragraph, Word};
use lopdf::{
    Document, Object, ObjectId,
    content::{Content, Operation},
};

pub fn apply_text_redaction(
    doc: &mut Document,
    redactions: &[Word],
) -> Result<(), Box<dyn std::error::Error>> {
    let page_ids: Vec<ObjectId> = doc.page_iter().collect();

    for page_id in page_ids {
        let page_redactions: Vec<&Word> =
            redactions.iter().filter(|w| w.page_id == page_id).collect();
        if page_redactions.is_empty() {
            continue;
        }

        let content_data = doc.get_page_content(page_id)?;
        let mut content = Content::decode(&content_data)?;

        // 1. Actual erasure of the source text
        for word in &page_redactions {
            for letter in &word.letters {
                let src = &letter.src;
                let Some(op) = content.operations.get_mut(src.op_index) else {
                    continue;
                };
                let string_obj = match src.array_item {
                    Some(idx) => match op.operands.first_mut() {
                        Some(Object::Array(arr)) => arr.get_mut(idx),
                        _ => None,
                    },
                    None => op.operands.last_mut(),
                };
                if let Some(Object::String(bytes, _)) = string_obj {
                    for b in bytes.iter_mut().skip(src.byte_start).take(src.byte_len) {
                        *b = 0;
                    }
                }
            }
        }

        // 2. Visual masking (white box with black border)
        content.operations.insert(0, Operation::new("q", vec![]));
        content.operations.push(Operation::new("Q", vec![]));

        for word in &page_redactions {
            let bbox = &word.bbox;
            content.operations.push(Operation::new("q", vec![]));
            content
                .operations
                .push(Operation::new("w", vec![Object::Real(1.0)]));
            content.operations.push(Operation::new(
                "rg",
                vec![Object::Real(1.0), Object::Real(1.0), Object::Real(1.0)],
            ));
            content.operations.push(Operation::new(
                "RG",
                vec![Object::Real(0.0), Object::Real(0.0), Object::Real(0.0)],
            ));
            content.operations.push(Operation::new(
                "re",
                vec![
                    Object::Real((bbox.x - 1.0) as f32),
                    Object::Real((bbox.y - 1.0) as f32),
                    Object::Real((bbox.width + 2.0) as f32),
                    Object::Real((bbox.height + 2.0) as f32),
                ],
            ));
            content.operations.push(Operation::new("B", vec![]));
            content.operations.push(Operation::new("Q", vec![]));
        }

        doc.change_page_content(page_id, content.encode()?)?;
    }

    Ok(())
}

// keep for DEBUG
pub fn write_layout_debug(
    doc_path: &str,
    paragraphs: &[Paragraph],
    out_path: &str,
) -> Result<(), Box<dyn std::error::Error>> {
    let mut doc = Document::load(doc_path)?;
    let page_ids: Vec<ObjectId> = doc.page_iter().collect();

    for page_id in page_ids {
        let content_data = doc.get_page_content(page_id)?;
        let mut content = Content::decode(&content_data)?;

        content.operations.insert(0, Operation::new("q", vec![]));
        content.operations.push(Operation::new("Q", vec![]));

        let mut has_drawings = false;

        for para in paragraphs {
            for block in &para.blocks {
                if block.page_id != page_id {
                    continue;
                }
                has_drawings = true;

                // 1. RED BLOCKS (Paragraphs) - Expanded by 3.5 pixels outward
                let color = if para.is_heading {
                    vec![Object::Real(0.7), Object::Real(0.0), Object::Real(1.0)] // Violet
                } else {
                    vec![Object::Real(1.0), Object::Real(0.0), Object::Real(0.0)] // Red
                };

                content.operations.push(Operation::new("q", vec![]));
                content
                    .operations
                    .push(Operation::new("w", vec![Object::Real(1.0)]));
                content.operations.push(Operation::new("RG", color));
                content.operations.push(Operation::new(
                    "re",
                    vec![
                        Object::Real((block.bbox.x - 3.5) as f32),
                        Object::Real((block.bbox.y - 3.5) as f32),
                        Object::Real((block.bbox.width + 7.0) as f32),
                        Object::Real((block.bbox.height + 7.0) as f32),
                    ],
                ));
                content.operations.push(Operation::new("S", vec![]));
                content.operations.push(Operation::new("Q", vec![]));

                for line in &block.lines {
                    for word in &line.words {
                        // 2. BLUE BLOCKS (Words) - Expanded by 1.5 pixels outward
                        content.operations.push(Operation::new("q", vec![]));
                        content
                            .operations
                            .push(Operation::new("w", vec![Object::Real(0.5)]));
                        content.operations.push(Operation::new(
                            "RG",
                            vec![Object::Real(0.0), Object::Real(0.0), Object::Real(1.0)],
                        ));
                        content.operations.push(Operation::new(
                            "re",
                            vec![
                                Object::Real((word.bbox.x - 1.5) as f32),
                                Object::Real((word.bbox.y - 1.5) as f32),
                                Object::Real((word.bbox.width + 3.0) as f32),
                                Object::Real((word.bbox.height + 3.0) as f32),
                            ],
                        ));
                        content.operations.push(Operation::new("S", vec![]));
                        content.operations.push(Operation::new("Q", vec![]));

                        // 3. LIGHT GREEN BLOCKS (Letters) - Exact pixel-perfect coordinates
                        for letter in &word.letters {
                            content.operations.push(Operation::new("q", vec![]));
                            content
                                .operations
                                .push(Operation::new("w", vec![Object::Real(0.3)]));
                            content.operations.push(Operation::new(
                                "RG",
                                vec![Object::Real(0.3), Object::Real(0.8), Object::Real(0.3)],
                            )); // Light green
                            content.operations.push(Operation::new(
                                "re",
                                vec![
                                    Object::Real(letter.bbox.x as f32),
                                    Object::Real(letter.bbox.y as f32),
                                    Object::Real(letter.bbox.width as f32),
                                    Object::Real(letter.bbox.height as f32),
                                ],
                            ));
                            content.operations.push(Operation::new("S", vec![]));
                            content.operations.push(Operation::new("Q", vec![]));
                        }
                    }
                }
            }
        }

        if has_drawings {
            doc.change_page_content(page_id, content.encode()?)?;
        }
    }

    doc.save(out_path)?;
    Ok(())
}
