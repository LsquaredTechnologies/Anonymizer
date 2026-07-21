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

// =========================================================================
// ALGORITHME LOGIQUE : NEAREST NEIGHBOUR WORD EXTRACTOR (PdfPig adaptation)
// =========================================================================
fn reconstruct_words(mut letters: Vec<Letter>, page_id: ObjectId) -> Vec<Word> {
    if letters.is_empty() {
        return vec![];
    }

    // 1. Tri initial "Flou" : On trie d'abord par Y de haut en bas
    letters.sort_by(|a, b| b.baseline_y.partial_cmp(&a.baseline_y).unwrap());

    let mut words = Vec::new();
    let mut current_word_letters: Vec<Letter> = Vec::new();

    for letter in letters {
        // Ignorer les espaces physiques du flux PDF (on calcule les espaces géométriquement)
        if letter.value.is_whitespace() || letter.value == '\u{00a0}' {
            continue;
        }

        if current_word_letters.is_empty() {
            current_word_letters.push(letter);
            continue;
        }

        let last = current_word_letters.last().unwrap();

        // Distance géométrique locale entre la fin de la dernière lettre et le début de celle-ci
        let last_right = last.bbox.x + last.bbox.width;
        let horizontal_gap = letter.bbox.x - last_right;
        let vertical_diff = (letter.baseline_y - last.baseline_y).abs();

        // Seuils de tolérance proportionnels à la taille de la police
        let v_tolerance = last.font_size * 0.2;         // Alignement vertical sur la même ligne
        let word_space_threshold = last.font_size * 0.3; // Seuil d'un espace mot (30% de la hauteur de police)

        // Si la lettre est alignée verticalement et que le gap horizontal est inférieur au seuil,
        // alors elle appartient au même mot. Le seuil négatif (-2.0) gère les légers chevauchements/italiques.
        if vertical_diff < v_tolerance && horizontal_gap >= -2.0 && horizontal_gap < word_space_threshold {
            current_word_letters.push(letter);
        } else {
            // Clôture du mot courant avant d'entamer le suivant
            if let Some(word) = build_word_from_extracted_letters(&current_word_letters, page_id) {
                words.push(word);
            }
            current_word_letters.clear();
            current_word_letters.push(letter);
        }
    }

    // Traitement du dernier mot restant
    if !current_word_letters.is_empty() {
        if let Some(word) = build_word_from_extracted_letters(&current_word_letters, page_id) {
            words.push(word);
        }
    }

    // Étape CRUCIALE : Une fois tous les mots capturés sur la page entière,
    // on effectue un tri stable par ligne (Y) et colonne (X) pour renvoyer un flux cohérent à l'analyseur
    words.sort_by(|a, b| {
        b.baseline_y.partial_cmp(&a.baseline_y).unwrap()
            .then(a.bbox.x.partial_cmp(&b.bbox.x).unwrap())
    });

    words
}

// Générateur de structure de mot unifié
fn build_word_from_extracted_letters(letters: &[Letter], page_id: ObjectId) -> Option<Word> {
    if letters.is_empty() { return None; }

    // S'assurer que les lettres à l'intérieur du mot sont triées de gauche à droite
    let mut sorted_letters = letters.to_vec();
    sorted_letters.sort_by(|a, b| a.bbox.x.partial_cmp(&b.bbox.x).unwrap());

    let first = &sorted_letters[0];
    let mut text = String::new();
    let mut min_x = first.bbox.x;
    let mut max_x = first.bbox.x + first.bbox.width;
    let mut min_y = first.bbox.y;
    let mut max_y = first.bbox.y + first.bbox.height;

    for l in &sorted_letters {
        text.push(l.value);
        min_x = min_x.min(l.bbox.x);
        max_x = max_x.max(l.bbox.x + l.bbox.width);
        min_y = min_y.min(l.bbox.y);
        max_y = max_y.max(l.bbox.y + l.bbox.height);
    }

    let trimmed = text.trim();
    if trimmed.is_empty() {
        return None;
    }

    Some(Word {
        text: trimmed.to_string(),
        bbox: BBox {
            x: min_x,
            y: min_y,
            width: max_x - min_x,
            height: max_y - min_y,
        },
        font_size: first.font_size,
        page_id,
        baseline_y: first.baseline_y,
        letters: sorted_letters,
        para_char_range: None,
    })
}
