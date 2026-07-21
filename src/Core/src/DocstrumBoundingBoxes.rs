use crate::models::{Block, Line};

pub struct DocstrumBoundingBoxes;

impl DocstrumBoundingBoxes {
    pub fn get_blocks(&self, words: Vec<Word>) -> Vec<Block> {
        if words.is_empty() { return Vec::new(); }

        // Étape 1 : Regroupement des mots en lignes physiques
        let lines = self.words_to_lines(words);

        // Étape 2 : Regroupement des lignes en blocs géométriques (Paragraphes structurels)
        let mut blocks: Vec<Block> = Vec::new();

        for line in lines {
            let mut added = false;
            for block in blocks.iter_mut() {
                // Seuil Docstrum pour l'interligne (généralement 1.5 à 2x la hauteur de la ligne)
                let max_line_spacing = line.bbox.height * 1.8;
                let close_y = (line.baseline_y - block.baseline_y).abs() < max_line_spacing;

                // Vérification stricte du chevauchement horizontal (évite de fusionner 2 colonnes distinctes)
                let x_overlap = line.bbox.x < (block.bbox.x + block.bbox.width)
                    && (line.bbox.x + line.bbox.width) > block.bbox.x;

                if close_y && x_overlap {
                    block.bbox.x = block.bbox.x.min(line.bbox.x);
                    block.bbox.y = block.bbox.y.min(line.bbox.y);
                    block.bbox.width = (block.bbox.x + block.bbox.width).max(line.bbox.x + line.bbox.width) - block.bbox.x;
                    block.bbox.height = (block.bbox.y + block.bbox.height).max(line.bbox.y + line.bbox.height) - block.bbox.y;
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

    fn words_to_lines(&self, mut words: Vec<Word>) -> Vec<Line> {
        // Tri de haut en bas strict pour l'analyse par balayage
        words.sort_by(|a, b| b.baseline_y.partial_cmp(&a.baseline_y).unwrap());

        let mut lines: Vec<Line> = Vec::new();

        for word in words {
            let mut added = false;
            for line in lines.iter_mut() {
                let v_tolerance = word.font_size * 0.4;
                let inline = (word.baseline_y - line.baseline_y).abs() < v_tolerance;

                // Docstrum : On ne fusionne dans la ligne que si la distance X n'est pas un gouffre (seuil colonne)
                let max_h_gap = word.font_size * 3.0;
                let no_column_gap = (word.bbox.x - (line.bbox.x + line.bbox.width)).abs() < max_h_gap;

                if inline && no_column_gap {
                    line.bbox.x = line.bbox.x.min(word.bbox.x);
                    line.bbox.width = (line.bbox.x + line.bbox.width).max(word.bbox.x + word.bbox.width) - line.bbox.x;
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

        // Tri des mots à l'intérieur de chaque ligne de gauche à droite
        for line in &mut lines {
            line.words.sort_by(|a, b| a.bbox.x.partial_cmp(&b.bbox.x).unwrap());
        }

        lines
    }
}
