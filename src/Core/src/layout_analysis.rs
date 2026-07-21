// src/layout_analysis.rs
use crate::models::{Block, Line, Paragraph, Word, Letter};

// =========================================================================
// 1. PIPELINE PRINCIPAL : INTEGRATION PDFPIG
// =========================================================================

pub fn build_segmented_layout_pdfpig(pages_words_or_letters: Vec<Vec<Word>>) -> Vec<Paragraph> {
    let mut paragraphs = Vec::new();
    let mut global_font_sum = 0.0;
    let mut global_word_count = 0.0;
    let mut all_ordered_blocks = Vec::new();

    for words_on_page in pages_words_or_letters {
        if words_on_page.is_empty() {
            continue;
        }

        // On extrait le vrai page_id stocké dans le premier mot de la page
        let page_id = words_on_page[0].page_id;

        let mut page_letters = Vec::new();
        for word in words_on_page {
            global_font_sum += word.font_size;
            global_word_count += 1.0;
            page_letters.extend(word.letters);
        }

        // On transmet le page_id à l'extracteur de mots
        let exact_words = nearest_neighbour_word_extractor(page_letters, page_id);

        let text_blocks = docstrum_page_segmenter(exact_words);
        let ordered_blocks = unsupervised_reading_order_detector(text_blocks);

        all_ordered_blocks.extend(ordered_blocks);
    }

    let avg_font_size = if global_word_count > 0.0 {
        global_font_sum / global_word_count
    } else {
        10.0
    };

    if all_ordered_blocks.is_empty() {
        return paragraphs;
    }

    // =========================================================================
    // 2. RECONSTRUCTION DES PARAGRAPHES & OFFSETS (Maintien de votre logique NER)
    // =========================================================================
    let mut current_para_text = String::new();
    let mut current_para_blocks: Vec<Block> = Vec::new();

    for mut block in all_ordered_blocks {
        // Calcule les offsets et la taille de police moyenne du bloc
        let (block_text, block_avg_font) = annotate_block_offsets(&mut block);
        let is_heading = block_avg_font > (avg_font_size * 1.25);

        if is_heading {
            if !current_para_blocks.is_empty() {
                paragraphs.push(finalize_paragraph(
                    std::mem::take(&mut current_para_text),
                    std::mem::take(&mut current_para_blocks),
                    false,
                ));
            }
            paragraphs.push(finalize_paragraph(block_text, vec![block], true));
        } else {
            let ends_with_punctuation = block_text.trim().ends_with('.')
                || block_text.trim().ends_with(':')
                || block_text.trim().ends_with('!');

            let base = current_para_text.len();
            shift_block_offsets(&mut block, base);
            current_para_text.push_str(&block_text);
            current_para_blocks.push(block);

            if ends_with_punctuation {
                paragraphs.push(finalize_paragraph(
                    std::mem::take(&mut current_para_text),
                    std::mem::take(&mut current_para_blocks),
                    false,
                ));
            }
        }
    }

    if !current_para_blocks.is_empty() {
        paragraphs.push(finalize_paragraph(
            current_para_text,
            current_para_blocks,
            false,
        ));
    }

    paragraphs
}

// =========================================================================
// 3. SOUS-ALGORITHMES PDFPIG (PORTAGE RUST)
// =========================================================================

/// Équivalent de : NearestNeighbourWordExtractor.cs
fn nearest_neighbour_word_extractor(mut letters: Vec<Letter>, page_id: lopdf::ObjectId) -> Vec<Word> {
    if letters.is_empty() { return Vec::new(); }

    letters.sort_by(|a, b| {
        b.baseline_y.partial_cmp(&a.baseline_y).unwrap()
            .then(a.bbox.x.partial_cmp(&b.bbox.x).unwrap())
    });

    let mut words = Vec::new();
    let mut current_word_letters: Vec<Letter> = Vec::new();

    for letter in letters {
        if letter.value.is_whitespace() || letter.value == '\u{00a0}' {
            continue;
        }

        if current_word_letters.is_empty() {
            current_word_letters.push(letter);
            continue;
        }

        let last = current_word_letters.last().unwrap();
        let last_right = last.bbox.x + last.bbox.width;
        let horizontal_gap = letter.bbox.x - last_right;
        let vertical_diff = (letter.baseline_y - last.baseline_y).abs();

        let v_tolerance = last.font_size * 0.2;
        let word_space_threshold = last.font_size * 0.3;

        if vertical_diff < v_tolerance && horizontal_gap >= -2.0 && horizontal_gap < word_space_threshold {
            current_word_letters.push(letter);
        } else {
            // Passez le page_id ici
            words.push(build_word_from_letters(&current_word_letters, page_id));
            current_word_letters.clear();
            current_word_letters.push(letter);
        }
    }

    if !current_word_letters.is_empty() {
        // Et ici
        words.push(build_word_from_letters(&current_word_letters, page_id));
    }

    words
}

/// Équivalent de : DocstrumBoundingBoxes.cs
fn docstrum_page_segmenter(mut words: Vec<Word>) -> Vec<Block> {
    if words.is_empty() { return Vec::new(); }

    // 1. Regroupement des mots en lignes (Words -> Lines)
    words.sort_by(|a, b| b.baseline_y.partial_cmp(&a.baseline_y).unwrap());
    let mut lines: Vec<Line> = Vec::new();

    for word in words {
        let mut added = false;
        for line in lines.iter_mut() {
            let v_tolerance = word.font_size * 0.4;
            let inline = (word.baseline_y - line.baseline_y).abs() < v_tolerance;

            // Evite l'effondrement des colonnes : maximum 2.5 à 3.0 espaces de police d'écart horizontal
            let max_h_gap = word.font_size * 2.8;
            let no_column_gap = (word.bbox.x - (line.bbox.x + line.bbox.width)).abs() < max_h_gap;

            if inline && no_column_gap {
                let min_x = line.bbox.x.min(word.bbox.x);
                let max_x = (line.bbox.x + line.bbox.width).max(word.bbox.x + word.bbox.width);
                line.bbox.x = min_x;
                line.bbox.width = max_x - min_x;
                line.words.push(word.clone());
                added = true;
                break;
            }
        }
        if !added {
            lines.push(Line {
                bbox: word.bbox.clone(),
                page_id: word.page_id,
                baseline_y: word.baseline_y,
                words: vec![word],
            });
        }
    }

    // Assure que chaque ligne lise ses mots strictement de gauche à droite
    for line in &mut lines {
        line.words.sort_by(|a, b| a.bbox.x.partial_cmp(&b.bbox.x).unwrap());
    }

    // 2. Regroupement des lignes en Blocs (Lines -> Blocks)
    lines.sort_by(|a, b| b.baseline_y.partial_cmp(&a.baseline_y).unwrap());
    let mut blocks: Vec<Block> = Vec::new();

    for line in lines {
        let mut added = false;
        for block in blocks.iter_mut() {
            // L'interligne max basé sur la hauteur de la ligne (Docstrum standard)
            let max_line_spacing = line.bbox.height * 1.8;
            let close_y = (line.baseline_y - block.baseline_y).abs() < max_line_spacing;

            // On ne fusionne verticalement que s'il y a un chevauchement horizontal (même colonne)
            let x_overlap = line.bbox.x < (block.bbox.x + block.bbox.width)
                && (line.bbox.x + line.bbox.width) > block.bbox.x;

            if close_y && x_overlap {
                let min_x = block.bbox.x.min(line.bbox.x);
                let min_y = block.bbox.y.min(line.bbox.y);
                let max_x = (block.bbox.x + block.bbox.width).max(line.bbox.x + line.bbox.width);
                let max_y = (block.bbox.y + block.bbox.height).max(line.bbox.y + line.bbox.height);

                block.bbox.x = min_x;
                block.bbox.y = min_y;
                block.bbox.width = max_x - min_x;
                block.bbox.height = max_y - min_y;
                block.baseline_y = line.baseline_y;
                block.lines.push(line.clone());
                added = true;
                break;
            }
        }
        if !added {
            blocks.push(Block {
                bbox: line.bbox.clone(),
                page_id: line.page_id,
                baseline_y: line.baseline_y,
                lines: vec![line],
            });
        }
    }

    blocks
}

/// Équivalent de : UnsupervisedReadingOrderDetector.cs
fn unsupervised_reading_order_detector(mut blocks: Vec<Block>) -> Vec<Block> {
    // Analyse des blocs en mode multi-colonnes
    blocks.sort_by(|a, b| {
        // Si l'écart X entre deux blocs dépasse le seuil, ils appartiennent à deux colonnes distinctes
        let col_tolerance = 50.0;
        let x_diff = a.bbox.x - b.bbox.x;

        if x_diff.abs() > col_tolerance {
            // Colonne de gauche d'abord, puis colonne de droite
            a.bbox.x.partial_cmp(&b.bbox.x).unwrap()
        } else {
            // Même colonne : du haut vers le bas
            b.baseline_y.partial_cmp(&a.baseline_y).unwrap()
        }
    });
    blocks
}

// Helper pour générer une structure Word propre depuis la collection de ses structures Letter
fn build_word_from_letters(letters: &[Letter], page_id: lopdf::ObjectId) -> Word {
    let first = &letters[0];
    let mut text = String::new();
    let mut min_x = first.bbox.x;
    let mut max_x = first.bbox.x + first.bbox.width;
    let mut min_y = first.bbox.y;
    let mut max_y = first.bbox.y + first.bbox.height;

    for l in letters {
        text.push(l.value);
        min_x = min_x.min(l.bbox.x);
        max_x = max_x.max(l.bbox.x + l.bbox.width);
        min_y = min_y.min(l.bbox.y);
        max_y = max_y.max(l.bbox.y + l.bbox.height);
    }

    Word {
        text,
        bbox: crate::models::BBox {
            x: min_x,
            y: min_y,
            width: max_x - min_x,
            height: max_y - min_y,
        },
        font_size: first.font_size,
        page_id, // <--- On affecte directement le vrai ObjectId passé en paramètre
        baseline_y: first.baseline_y,
        letters: letters.to_vec(),
        para_char_range: None,
    }
}

// =========================================================================
// 4. FONCTIONS DE RECALAGE DES OFFSETS (Vos fonctions d'origine préservées)
// =========================================================================

fn annotate_block_offsets(block: &mut Block) -> (String, f64) {
    let mut text = String::new();
    let mut font_sum = 0.0;
    let mut word_count = 0.0;

    for line in &mut block.lines {
        for word in &mut line.words {
            let start = text.len();
            text.push_str(&word.text);
            let end = text.len();
            word.para_char_range = Some(start..end);
            text.push(' ');

            font_sum += word.font_size;
            word_count += 1.0;
        }
    }

    let avg_font = if word_count > 0.0 { font_sum / word_count } else { 10.0 };
    (text, avg_font)
}

fn shift_block_offsets(block: &mut Block, base: usize) {
    if base == 0 { return; }
    for line in &mut block.lines {
        for word in &mut line.words {
            if let Some(r) = word.para_char_range.take() {
                word.para_char_range = Some((r.start + base)..(r.end + base));
            }
        }
    }
}

fn finalize_paragraph(raw_text: String, mut blocks: Vec<Block>, is_heading: bool) -> Paragraph {
    let trimmed_start = raw_text.len() - raw_text.trim_start().len();
    let final_text = raw_text.trim().to_string();
    let final_len = final_text.len();

    for block in &mut blocks {
        for line in &mut block.lines {
            for word in &mut line.words {
                if let Some(r) = &word.para_char_range {
                    let mut start = r.start.saturating_sub(trimmed_start);
                    let mut end = r.end.saturating_sub(trimmed_start);
                    if start > final_len { start = final_len; }
                    if end > final_len { end = final_len; }
                    if end < start { end = start; }
                    word.para_char_range = Some(start..end);
                }
            }
        }
    }

    Paragraph { text: final_text, blocks, is_heading }
}
