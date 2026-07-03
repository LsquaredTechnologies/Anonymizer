// src/pdf_parser.rs
use crate::font_map::{self, FontDecoder};
use crate::models::{BBox, Letter, Word};
use lopdf::{Document, Object, ObjectId, content::Content};
use std::collections::HashMap;

fn multiply_matrices(m2: &[f64; 6], m1: &[f64; 6]) -> [f64; 6] {
    [
        m2[0] * m1[0] + m2[1] * m1[2],
        m2[0] * m1[1] + m2[1] * m1[3],
        m2[2] * m1[0] + m2[3] * m1[2],
        m2[2] * m1[1] + m2[3] * m1[3],
        m2[4] * m1[0] + m2[5] * m1[2] + m1[4],
        m2[4] * m1[1] + m2[5] * m1[3] + m1[5],
    ]
}

fn obj_to_f64(obj: &Object) -> f64 {
    match obj {
        Object::Integer(i) => *i as f64,
        Object::Real(f) => *f as f64,
        _ => 0.0,
    }
}

pub fn extract_words(
    doc: &Document,
    page_id: ObjectId,
) -> Result<Vec<Word>, Box<dyn std::error::Error>> {
    let content_data = doc.get_page_content(page_id)?;
    let content = Content::decode(&content_data)?;
    let decoders: HashMap<Vec<u8>, FontDecoder> = font_map::build_font_decoders(doc, page_id);

    let mut letters = Vec::new();
    let mut ctm = [1.0, 0.0, 0.0, 1.0, 0.0, 0.0];
    let mut ctm_stack = Vec::new();
    let mut tm = [1.0, 0.0, 0.0, 1.0, 0.0, 0.0];
    let mut lm = [1.0, 0.0, 0.0, 1.0, 0.0, 0.0];
    let mut font_size = 10.0;
    let mut current_font: Option<Vec<u8>> = None;

    for (op_index, op) in content.operations.iter().enumerate() {
        match op.operator.as_str() {
            "q" => ctm_stack.push(ctm),
            "Q" => {
                if let Some(old) = ctm_stack.pop() {
                    ctm = old;
                }
            }
            "cm" => {
                let m = [
                    obj_to_f64(&op.operands[0]),
                    obj_to_f64(&op.operands[1]),
                    obj_to_f64(&op.operands[2]),
                    obj_to_f64(&op.operands[3]),
                    obj_to_f64(&op.operands[4]),
                    obj_to_f64(&op.operands[5]),
                ];
                ctm = multiply_matrices(&m, &ctm);
            }
            "Tf" => {
                if let Some(Object::Name(name)) = op.operands.first() {
                    current_font = Some(name.clone());
                }
                if let Some(size) = op.operands.get(1).and_then(|o| o.as_float().ok()) {
                    font_size = size as f64;
                }
            }
            "BT" => {
                tm = [1.0, 0.0, 0.0, 1.0, 0.0, 0.0];
                lm = tm;
            }
            "Td" | "TD" => {
                let x = obj_to_f64(&op.operands[0]);
                let y = obj_to_f64(&op.operands[1]);
                lm = multiply_matrices(&[1.0, 0.0, 0.0, 1.0, x, y], &lm);
                tm = lm;
            }
            "Tm" => {
                tm = [
                    obj_to_f64(&op.operands[0]),
                    obj_to_f64(&op.operands[1]),
                    obj_to_f64(&op.operands[2]),
                    obj_to_f64(&op.operands[3]),
                    obj_to_f64(&op.operands[4]),
                    obj_to_f64(&op.operands[5]),
                ];
                lm = tm;
            }
            "T*" => {
                lm = multiply_matrices(&[1.0, 0.0, 0.0, 1.0, 0.0, -font_size], &lm);
                tm = lm;
            }
            "Tj" | "'" | "\"" => {
                if let Some(Object::String(bytes, _)) = op.operands.last() {
                    let decoder = current_font.as_ref().and_then(|f| decoders.get(f));
                    parse_letters(
                        bytes,
                        decoder,
                        &mut tm,
                        &ctm,
                        font_size,
                        &mut letters,
                        op_index,
                        None,
                    );
                }
            }
            "TJ" => {
                if let Some(Object::Array(arr)) = op.operands.first() {
                    let decoder = current_font.as_ref().and_then(|f| decoders.get(f));
                    for (item_index, item) in arr.iter().enumerate() {
                        match item {
                            Object::String(bytes, _) => parse_letters(
                                bytes,
                                decoder,
                                &mut tm,
                                &ctm,
                                font_size,
                                &mut letters,
                                op_index,
                                Some(item_index),
                            ),
                            Object::Integer(i) => {
                                tm = multiply_matrices(
                                    &[1.0, 0.0, 0.0, 1.0, -(*i as f64 / 1000.0) * font_size, 0.0],
                                    &tm,
                                );
                            }
                            Object::Real(f) => {
                                tm = multiply_matrices(
                                    &[1.0, 0.0, 0.0, 1.0, -(*f as f64 / 1000.0) * font_size, 0.0],
                                    &tm,
                                );
                            }
                            _ => {}
                        }
                    }
                }
            }
            _ => {}
        }
    }

    Ok(reconstruct_words(letters, page_id))
}

fn parse_letters(
    bytes: &[u8],
    decoder: Option<&FontDecoder>,
    tm: &mut [f64; 6],
    ctm: &[f64; 6],
    font_size: f64,
    letters: &mut Vec<Letter>,
    op_index: usize,
    array_item: Option<usize>,
) {
    // Decode via the actual font table (CID/GID -> Unicode + actual width).
    // (glyph, byte_offset, byte_length) to be able to precisely erase
    // each glyph later without affecting neighboring glyphs.
    let (glyphs, height_ratio, y_offset_ratio): (
        Vec<(font_map::GlyphInfo, usize, usize)>,
        f64,
        f64,
    ) = match decoder {
        Some(d) => (
            d.decode(bytes)
                .into_iter()
                .map(|(g, off)| (g, off, d.bytes_per_code))
                .collect(),
            d.height_ratio,
            d.y_offset_ratio,
        ),
        None => {
            let mut offset = 0usize;
            let list = String::from_utf8(bytes.to_vec())
                .unwrap_or_default()
                .chars()
                .map(|c| {
                    let len = c.len_utf8();
                    let start = offset;
                    offset += len;
                    (font_map::GlyphInfo { c, width: 0.45 }, start, len)
                })
                .collect();
            (list, 0.9, -0.2)
        }
    };

    for (g, byte_start, byte_len) in glyphs {
        let src = crate::models::SourceRef {
            op_index,
            array_item,
            byte_start,
            byte_len,
        };

        let m_abs = multiply_matrices(tm, ctm);
        if m_abs[0].abs() < 0.1 && m_abs[3].abs() < 0.1 {
            continue;
        }

        if (g.c.is_control() || g.c == '\u{200B}' || g.c == '\u{FEFF}') && g.c != '\u{0020}' {
            continue;
        }

        let width = font_size * g.width;
        let height = font_size * height_ratio;
        // Constant offset per font: the bottom of the box extends below the baseline (descenders),
        // identical for all letters of this font/size.
        let baseline_y = m_abs[5];
        let y = baseline_y + font_size * y_offset_ratio;

        letters.push(Letter {
            value: g.c,
            bbox: BBox {
                x: m_abs[4],
                y,
                width,
                height,
            },
            font_size,
            baseline_y,
            src,
        });
        *tm = multiply_matrices(&[1.0, 0.0, 0.0, 1.0, width, 0.0], tm);
    }
}

fn reconstruct_words(mut letters: Vec<Letter>, page_id: ObjectId) -> Vec<Word> {
    if letters.is_empty() {
        return vec![];
    }
    letters.sort_by(|a, b| {
        b.baseline_y
            .partial_cmp(&a.baseline_y)
            .unwrap()
            .then(a.bbox.x.partial_cmp(&b.bbox.x).unwrap())
    });

    let mut words = Vec::new();
    let mut current_word = String::new();
    let mut current_word_letters = Vec::new();
    let mut word_bbox: Option<BBox> = None;
    let mut last_letter: Option<Letter> = None;

    // Only true isolated punctuation, never letters (accented or not)
    let is_special = |c: char| c.is_ascii_punctuation();
    let mut saw_space = false;

    for letter in letters {
        if letter.value.is_whitespace() || letter.value == '\u{00a0}' {
            saw_space = true;
            continue;
        }

        // A true space in the text flow is a reliable word separator:
        // it takes precedence over any geometric heuristic (kerning, attached punctuation...).
        let is_new_word = if saw_space {
            true
        } else {
            match &last_letter {
                Some(last) => {
                    // Comparison on the baseline (stable), not on the visual bbox
                    // which varies with the stems/descenders of each glyph.
                    let vertical_dist = (letter.baseline_y - last.baseline_y).abs();
                    let horizontal_dist = letter.bbox.x - (last.bbox.x + last.bbox.width);

                    // Leniency only for attaching isolated punctuation to the current word,
                    // never for attaching a normal word to the previous one.
                    let lenient = is_special(letter.value);
                    let max_v = if lenient { letter.font_size * 0.8 } else { 6.0 };
                    // Expanded threshold: no need to be strict for detecting spaces,
                    // which prevents cutting a word in the middle due to kerning TJ.
                    let max_h = if lenient {
                        letter.font_size * 0.4
                    } else {
                        letter.font_size * 0.5
                    };

                    let is_overlapping_h =
                        horizontal_dist < max_h && horizontal_dist > -last.bbox.width;

                    vertical_dist > max_v || !is_overlapping_h
                }
                None => true,
            }
        };
        saw_space = false;

        if is_new_word && !current_word.is_empty() {
            if let Some(bbox) = word_bbox.take() {
                let trimmed = current_word.trim();
                if !trimmed.is_empty() {
                    words.push(Word {
                        text: trimmed.to_string(),
                        bbox,
                        font_size: last_letter.as_ref().unwrap().font_size,
                        page_id,
                        letters: current_word_letters.clone(), // Injection
                        baseline_y: current_word_letters[0].baseline_y,
                    });
                }
            }
            current_word.clear();
            current_word_letters.clear();
        }

        if current_word.is_empty() {
            word_bbox = Some(letter.bbox.clone());
        } else if let Some(ref mut bbox) = word_bbox {
            let min_x = bbox.x.min(letter.bbox.x);
            let max_x = (bbox.x + bbox.width).max(letter.bbox.x + letter.bbox.width);
            let min_y = bbox.y.min(letter.bbox.y);
            let max_y = (bbox.y + bbox.height).max(letter.bbox.y + letter.bbox.height);

            bbox.x = min_x;
            bbox.width = max_x - min_x;
            bbox.y = min_y;
            bbox.height = max_y - min_y;
        }

        current_word.push(letter.value);
        current_word_letters.push(letter.clone());
        last_letter = Some(letter);
    }

    if !current_word.is_empty() {
        if let Some(bbox) = word_bbox {
            let trimmed = current_word.trim();
            if !trimmed.is_empty() {
                words.push(Word {
                    text: trimmed.to_string(),
                    bbox,
                    font_size: last_letter.unwrap().font_size,
                    page_id,
                    baseline_y: current_word_letters[0].baseline_y,
                    letters: current_word_letters,
                });
            }
        }
    }

    words
}
