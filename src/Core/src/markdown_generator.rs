// src/markdown_generator.rs
// Construit un export Markdown du document reflétant le caviardage appliqué
// au PDF : tout mot signalé comme donnée personnelle (par le NER, le Regex ou
// tout autre détecteur en amont) est masqué par des blocs pleins, tout en
// conservant la structure de lecture (titres, paragraphes, pages) pour
// permettre une relecture sans jamais exposer la donnée d'origine.
use crate::models::{Paragraph, Word};
use lopdf::ObjectId;
use std::collections::{HashMap, HashSet};

/// Construit une clé d'identité stable pour un `Word`, en reprenant la même
/// comparaison structurelle (page + bbox + texte) déjà utilisée ailleurs dans
/// le pipeline (voir `run_redaction_pipeline`), car `Word` ne dérive pas
/// `PartialEq`/`Hash`.
fn word_key(w: &Word) -> String {
    format!(
        "{}-{}-{:.4}-{:.4}-{:.4}-{:.4}-{}",
        w.page_id.0, w.page_id.1, w.bbox.x, w.bbox.y, w.bbox.width, w.bbox.height, w.text
    )
}

/// Remplace les caractères visibles d'un mot caviardé par des blocs pleins,
/// en conservant approximativement la longueur d'origine (nombre de
/// caractères Unicode) afin que la forme de la phrase reste lisible sans
/// révéler la donnée.
fn mask_word(word: &str) -> String {
    "█".repeat(word.chars().count().max(1))
}

/// Génère le contenu Markdown complet du document.
///
/// - `paragraphs` : la mise en page déjà segmentée (titres/paragraphes).
/// - `redactions` : les mots identifiés comme PII (NER + Regex), exactement
///   la même liste que celle utilisée par `pdf_generator::apply_text_redaction`.
/// - `page_numbers` : correspondance ObjectId de page -> numéro de page
///   (1-indexé), pour insérer des séparateurs de page lisibles.
pub fn generate_markdown(
    document_name: &str,
    paragraphs: &[Paragraph],
    redactions: &[Word],
    page_numbers: &HashMap<ObjectId, usize>,
) -> String {
    let redacted_keys: HashSet<String> = redactions.iter().map(word_key).collect();

    let mut out = String::new();
    out.push_str(&format!("# {} (anonymisé)\n\n", document_name));
    out.push_str(
        "_Export généré automatiquement à partir du pipeline d'anonymisation. \
         Les zones identifiées comme données personnelles ont été masquées (█)._\n\n---\n\n",
    );

    let mut current_page: Option<usize> = None;

    for paragraph in paragraphs {
        // Détermine la page du paragraphe à partir de son premier bloc, et
        // insère un séparateur de page à chaque changement.
        if let Some(first_block) = paragraph.blocks.first() {
            if let Some(&page_no) = page_numbers.get(&first_block.page_id) {
                if current_page != Some(page_no) {
                    if current_page.is_some() {
                        out.push_str("\n---\n\n");
                    }
                    out.push_str(&format!("*Page {}*\n\n", page_no));
                    current_page = Some(page_no);
                }
            }
        }

        let mut rendered = String::new();
        for block in &paragraph.blocks {
            for line in &block.lines {
                for word in &line.words {
                    if !rendered.is_empty() {
                        rendered.push(' ');
                    }
                    if redacted_keys.contains(&word_key(word)) {
                        rendered.push_str(&mask_word(&word.text));
                    } else {
                        rendered.push_str(&word.text);
                    }
                }
            }
        }
        let rendered = rendered.trim();
        if rendered.is_empty() {
            continue;
        }

        if paragraph.is_heading {
            out.push_str("## ");
            out.push_str(rendered);
            out.push_str("\n\n");
        } else {
            out.push_str(rendered);
            out.push_str("\n\n");
        }
    }

    out
}
