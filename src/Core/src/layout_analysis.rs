// src/layout_analysis.rs
use crate::models::{Block, Line, Paragraph, Word};

pub fn build_segmented_layout(pages_words: Vec<Vec<Word>>) -> Vec<Paragraph> {
    let mut all_blocks = Vec::new();
    let mut global_font_sum = 0.0;
    let mut global_word_count = 0.0;

    for words in pages_words {
        if words.is_empty() {
            continue;
        }
        for w in &words {
            global_font_sum += w.font_size;
            global_word_count += 1.0;
        }

        let lines = words_to_lines(words);
        let blocks = lines_to_blocks(lines);
        all_blocks.extend(blocks);
    }

    let avg_font_size = if global_word_count > 0.0 {
        global_font_sum / global_word_count
    } else {
        10.0
    };
    let mut paragraphs = Vec::new();
    if all_blocks.is_empty() {
        return paragraphs;
    }

    let mut current_para_text = String::new();
    let mut current_para_blocks = Vec::new();

    for block in all_blocks {
        let mut block_text = String::new();
        let mut block_font_sum = 0.0;
        let mut block_word_count = 0.0;

        for line in &block.lines {
            for word in &line.words {
                block_text.push_str(&word.text);
                block_text.push(' ');
                block_font_sum += word.font_size;
                block_word_count += 1.0;
            }
        }
        let block_avg_font = if block_word_count > 0.0 {
            block_font_sum / block_word_count
        } else {
            10.0
        };
        let is_heading = block_avg_font > (avg_font_size * 1.25);

        if is_heading {
            if !current_para_blocks.is_empty() {
                paragraphs.push(Paragraph {
                    text: current_para_text.trim().to_string(),
                    blocks: current_para_blocks.clone(),
                    is_heading: false,
                });
                current_para_text.clear();
                current_para_blocks.clear();
            }
            paragraphs.push(Paragraph {
                text: block_text.trim().to_string(),
                blocks: vec![block],
                is_heading: true,
            });
        } else {
            let ends_with_punctuation = block_text.trim().ends_with('.')
                || block_text.trim().ends_with(':')
                || block_text.trim().ends_with('!');
            current_para_text.push_str(&block_text);
            current_para_blocks.push(block);

            if ends_with_punctuation {
                paragraphs.push(Paragraph {
                    text: current_para_text.trim().to_string(),
                    blocks: current_para_blocks.clone(),
                    is_heading: false,
                });
                current_para_text.clear();
                current_para_blocks.clear();
            }
        }
    }

    if !current_para_blocks.is_empty() {
        paragraphs.push(Paragraph {
            text: current_para_text.trim().to_string(),
            blocks: current_para_blocks,
            is_heading: false,
        });
    }

    paragraphs
}

fn words_to_lines(mut words: Vec<Word>) -> Vec<Line> {
    words.sort_by(|a, b| {
        b.baseline_y
            .partial_cmp(&a.baseline_y)
            .unwrap()
            .then(a.bbox.x.partial_cmp(&b.bbox.x).unwrap())
    });
    let mut lines: Vec<Line> = Vec::new();

    let is_special_str = |s: &str| {
        if s.len() != 1 {
            return false;
        }
        let c = s.chars().next().unwrap();
        c.is_ascii_punctuation()
    };

    for word in words {
        let mut added = false;
        for line in lines.iter_mut() {
            // Lenient alignment to integrate isolated punctuation into the current line
            let lenient = is_special_str(&word.text);
            let max_v = if lenient { word.font_size * 0.8 } else { 6.0 };

            // Comparison on the baseline (stable), not on the visual bbox which
            // varies according to the stems/descenders of the word (a "g" and a "T" do not have the
            // same bbox.y even though they are on the same line).
            let inline = (word.baseline_y - line.baseline_y).abs() < max_v;
            let no_column_gap =
                (word.bbox.x - (line.bbox.x + line.bbox.width)).abs() < (word.font_size * 2.5);

            if inline && no_column_gap {
                // Physical fusion of BBoxes in the line (purely visual, the reference baseline
                // of the line itself does not change)
                let min_x = line.bbox.x.min(word.bbox.x);
                let max_x = (line.bbox.x + line.bbox.width).max(word.bbox.x + word.bbox.width);
                let min_y = line.bbox.y.min(word.bbox.y);
                let max_y = (line.bbox.y + line.bbox.height).max(word.bbox.y + word.bbox.height);

                line.bbox.x = min_x;
                line.bbox.width = max_x - min_x;
                line.bbox.y = min_y;
                line.bbox.height = max_y - min_y;

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
    lines
}

fn lines_to_blocks(mut lines: Vec<Line>) -> Vec<Block> {
    lines.sort_by(|a, b| b.baseline_y.partial_cmp(&a.baseline_y).unwrap());
    let mut blocks: Vec<Block> = Vec::new();

    for line in lines {
        let mut added = false;
        for block in blocks.iter_mut() {
            // Distance to the baseline of the LAST added line (actual line spacing),
            // rather than to block.bbox.y which drifts with cumulative stems/descenders.
            let close_y = (line.baseline_y - block.baseline_y).abs() < 18.0;
            // Slight horizontal margin to capture punctuation shifted at the end of the block
            let x_overlap = line.bbox.x < (block.bbox.x + block.bbox.width + 10.0)
                && (line.bbox.x + line.bbox.width) > (block.bbox.x - 10.0);

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
