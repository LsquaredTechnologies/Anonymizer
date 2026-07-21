// Modèle de lettre d'entrée supposé (proche de PdfPig.Letter)
#[derive(Clone, Debug)]
pub struct PdfLetter {
    pub text: String,
    pub x: f64,
    pub y: f64, // baseline y
    pub width: f64,
    pub height: f64,
    pub font_size: f64,
}

use crate::models::{Word, BoundingBox}; // Adaptez selon vos modèles existants

pub struct NearestNeighbourWordExtractor;

impl NearestNeighbourWordExtractor {
    pub fn get_words(&self, mut letters: Vec<PdfLetter>) -> Vec<Word> {
        if letters.is_empty() { return Vec::new(); }

        // Tri initial de gauche à droite, puis de haut en bas
        letters.sort_by(|a, b| {
            b.y.partial_cmp(&a.y).unwrap()
                .then(a.x.partial_cmp(&b.x).unwrap())
        });

        let mut words = Vec::new();
        let mut current_words_letters: Vec<PdfLetter> = Vec::new();

        for letter in letters {
            if current_words_letters.is_empty() {
                current_words_letters.push(letter);
                continue;
            }

            let last = current_words_letters.last().unwrap();

            // Calcul de l'écart horizontal entre la fin de la dernière lettre et le début de la nouvelle
            let last_right = last.x + last.width;
            let horizontal_gap = letter.x - last_right;

            // Tolérance verticale pour rester sur la même ligne (PdfPig utilise environ 10-15% de la hauteur)
            let vertical_diff = (letter.y - last.y).abs();
            let v_tolerance = last.font_size * 0.2;

            // Seuil d'espace : si l'écart dépasse 30% de la taille de la police, on crée un mot
            let word_space_threshold = last.font_size * 0.3;

            if vertical_diff < v_tolerance && horizontal_gap >= 0.0 && horizontal_gap < word_space_threshold {
                current_words_letters.push(letter);
            } else {
                // Finalisation du mot courant
                words.push(Self::build_word_from_letters(&current_words_letters));
                current_words_letters.clear();
                current_words_letters.push(letter);
            }
        }

        if !current_words_letters.is_empty() {
            words.push(Self::build_word_from_letters(&current_words_letters));
        }

        words
    }

    fn build_word_from_letters(letters: &[PdfLetter]) -> Word {
        let first = &letters[0];
        let mut text = String::new();
        let mut min_x = first.x;
        let mut max_x = first.x + first.width;
        let mut min_y = first.y;
        let mut max_y = first.y + first.height;

        for l in letters {
            text.push_str(&l.text);
            min_x = min_x.min(l.x);
            max_x = max_x.max(l.x + l.width);
            min_y = min_y.min(l.y);
            max_y = max_y.max(l.y + l.height);
        }

        Word {
            text,
            bbox: BoundingBox {
                x: min_x,
                y: min_y,
                width: max_x - min_x,
                height: max_y - min_y,
            },
            baseline_y: first.y,
            font_size: first.font_size,
            page_id: 0, // À dynamiser selon votre contexte
            para_char_range: None,
        }
    }
}
