// src/pii_ner_detector.rs
use ort::session::Session;
use ort::value::Value;
use std::fs;
use std::ops::Range;
use std::path::Path;
use tokenizers::Tokenizer;

use crate::models::{Paragraph, Word};

#[derive(Debug, Clone)]
struct RawEntity {
    label: String,
    start: usize,
    end: usize,
    score: f32,
}

pub struct PiiNerDetector {
    session: Option<Session>,
    tokenizer: Option<Tokenizer>,
    labels: Vec<String>,
    max_seq_len: usize,
    expects_token_type_ids: bool,
}

impl PiiNerDetector {
    pub fn new(models_directory: &str) -> Self {
        let model_dir = Path::new(models_directory).join("pii");
        let onnx_file = model_dir.join("model.onnx");
        let tokenizer_file = model_dir.join("tokenizer.json");
        let config_file = model_dir.join("config.json");

        if !tokenizer_file.exists() {
            panic!("Tokenizer introuvable : {:?}", tokenizer_file);
        }
        if !onnx_file.exists() {
            panic!("Modèle ONNX introuvable : {:?}", onnx_file);
        }
        if !config_file.exists() {
            panic!("Fichier de configuration introuvable : {:?}", config_file);
        }

        let session = Session::builder()
            .unwrap()
            .commit_from_file(onnx_file)
            .unwrap();
        let tokenizer = Tokenizer::from_file(tokenizer_file).unwrap();

        // Some models (RoBERTa/CamemBERT-like) do not expose
        // the `token_type_ids` input, unlike a "classic" BERT.
        // We only call it if the model actually declares it,
        // otherwise ONNX Runtime returns "Invalid input name: token_type_ids".
        let expects_token_type_ids = session
            .inputs()
            .iter()
            .any(|input| input.name() == "token_type_ids");

        if cfg!(debug_assertions) {
            let input_names: Vec<&str> = session.inputs().iter().map(|i| i.name()).collect();
            eprintln!("[PiiNerDetector] ONNX model inputs: {:?}", input_names);
        }

        let config_content =
            fs::read_to_string(config_file).expect("Impossible de lire config.json");
        let config: serde_json::Value =
            serde_json::from_str(&config_content).expect("JSON invalide");

        let mut labels = Vec::new();
        if let Some(id2label) = config.get("id2label").and_then(|v| v.as_object()) {
            let mut sorted_keys: Vec<usize> = id2label
                .keys()
                .filter_map(|k| k.parse::<usize>().ok())
                .collect();
            sorted_keys.sort_unstable();

            for key in sorted_keys {
                if let Some(label_str) = id2label.get(&key.to_string()).and_then(|v| v.as_str()) {
                    labels.push(label_str.to_string());
                }
            }
        }

        if labels.is_empty() {
            eprintln!(
                "[PiiNerDetector] Warning: no labels loaded from config.json \
                 (key 'id2label' missing, empty, or malformed)."
            );
        } else if cfg!(debug_assertions) {
            // Useful for diagnosing an unexpected label schema
            // (e.g., flat labels "PER" instead of "B-PER"/"I-PER").
            eprintln!("[PiiNerDetector] Labels loaded: {:?}", labels);
        }

        Self {
            session: Some(session),
            tokenizer: Some(tokenizer),
            labels,
            max_seq_len: 512,
            expects_token_type_ids,
        }
    }

    pub fn scan_pii(&mut self, paragraphs: &[Paragraph]) -> Vec<Word> {
        let mut words_to_redact = Vec::new();

        for paragraph in paragraphs {
            let pii_spans = self.find_pii(&paragraph.text).unwrap_or_else(|e| {
                eprintln!("[PiiDetector] Erreur find_pii : {e}");
                Vec::new()
            });

            if pii_spans.is_empty() {
                continue;
            }

            // Chaque mot connaît déjà sa position exacte dans `paragraph.text`
            // (calculée une seule fois par layout_analysis) : simple test de
            // chevauchement d'intervalles, aucune recherche de sous-chaîne.
            for word in paragraph
                .blocks
                .iter()
                .flat_map(|b| &b.lines)
                .flat_map(|l| &l.words)
            {
                let Some(range) = &word.para_char_range else {
                    continue;
                };

                let is_pii = pii_spans
                    .iter()
                    .any(|span| range.start.max(span.start) < range.end.min(span.end));

                if is_pii {
                    words_to_redact.push(word.clone());
                }
            }
        }

        words_to_redact
    }

    pub fn find_pii(
        &mut self,
        text: &str,
    ) -> Result<Vec<Range<usize>>, Box<dyn std::error::Error>> {
        if text.trim().is_empty() {
            return Ok(Vec::new());
        }

        let tokenizer = self.tokenizer.as_ref().unwrap();
        let session = self.session.as_mut().unwrap();

        let encoding = tokenizer
            .encode(text, true)
            .map_err(|e| Box::<dyn std::error::Error>::from(e.to_string()))?;

        let all_input_ids = encoding.get_ids();
        let all_attention_mask = encoding.get_attention_mask();
        let all_type_ids = encoding.get_type_ids();
        let all_tokens = encoding.get_tokens();
        let all_special_mask = encoding.get_special_tokens_mask();

        // Some versions of `tokenizers` return offsets in terms of
        // Unicode characters rather than bytes. Since Rust strings
        // are indexed by bytes, we secure the conversion to avoid
        // a panic or incorrect slicing on accented text
        // ("Léa", "François"...).
        let byte_offsets = to_byte_offsets(text, encoding.get_offsets());

        let total_tokens = all_input_ids.len();
        let max_len = self.max_seq_len;
        let num_labels = self.labels.len();
        let mut chunk_entities = Vec::new();

        for i in (0..total_tokens).step_by(max_len) {
            let current_len = std::cmp::min(max_len, total_tokens - i);

            let mut input_ids: Vec<i64> = all_input_ids[i..i + current_len]
                .iter()
                .map(|&x| x as i64)
                .collect();
            let mut attention_mask: Vec<i64> = all_attention_mask[i..i + current_len]
                .iter()
                .map(|&x| x as i64)
                .collect();
            let mut type_ids: Vec<i64> = all_type_ids[i..i + current_len]
                .iter()
                .map(|&x| x as i64)
                .collect();

            let offsets = &byte_offsets[i..i + current_len];
            let tokens = &all_tokens[i..i + current_len];
            let special_mask = &all_special_mask[i..i + current_len];

            if current_len < max_len {
                input_ids.resize(max_len, 0);
                attention_mask.resize(max_len, 0);
                type_ids.resize(max_len, 0);
            }

            let input_tensor = Value::from_array(([1, max_len], input_ids))?;
            let attention_tensor = Value::from_array(([1, max_len], attention_mask))?;

            let outputs = if self.expects_token_type_ids {
                let type_ids_tensor = Value::from_array(([1, max_len], type_ids))?;
                session.run(ort::inputs![
                    "input_ids" => input_tensor,
                    "attention_mask" => attention_tensor,
                    "token_type_ids" => type_ids_tensor
                ])?
            } else {
                session.run(ort::inputs![
                    "input_ids" => input_tensor,
                    "attention_mask" => attention_tensor
                ])?
            };

            // NB: if the ONNX model exposes multiple named outputs,
            // ensure that the output at index 0 is indeed the token-by-token
            // classification logits (and not a pooler_output or
            // something else) — a confusion here would also produce a silent "no
            // entity" result.
            let (_shape, data) = outputs[0].try_extract_tensor::<f32>()?;

            let (preds, confs) = decode_predictions(data, max_len, num_labels);

            let is_special = |idx: usize| -> bool {
                if idx < special_mask.len() && special_mask[idx] == 1 {
                    return true;
                }
                if idx < offsets.len() {
                    return offsets[idx].0 == 0
                        && offsets[idx].1 == 0
                        && tokens[idx].starts_with('[');
                }
                false
            };

            let mut current: Option<RawEntity> = None;
            let mut current_score_sum = 0.0f32;
            let mut current_count = 0usize;

            for t in 0..current_len {
                if std::env::var("PII_DEBUG").is_ok() {
                    let raw_label = self
                        .labels
                        .get(preds[t])
                        .map(|s| s.as_str())
                        .unwrap_or("<hors limites>");
                    eprintln!(
                        "[PII_DEBUG] t={t:>3} token={:<15?} offset={:?} special={} pred_idx={} label={:<25} conf={:.3}",
                        tokens[t],
                        offsets[t],
                        is_special(t),
                        preds[t],
                        raw_label,
                        confs[t]
                    );
                }

                if is_special(t) {
                    continue;
                }

                // A token whose offset directly touches that of the
                // previous one is a subword of the same word (e.g., "Lion"/"##el"):
                // we extend the current entity without revalidating the label,
                // only the first subword of the word counts.
                if current.is_some()
                    && t > 0
                    && !is_special(t - 1)
                    && offsets[t - 1].1 == offsets[t].0
                {
                    if let Some(ref mut ent) = current {
                        ent.end = ent.end.max(offsets[t].1);
                        current_score_sum += confs[t];
                        current_count += 1;
                        continue;
                    }
                }

                let p = preds[t];
                let label = if p < self.labels.len() {
                    self.labels[p].as_str()
                } else {
                    "O"
                };

                if label == "O" {
                    finalize_entity(
                        &mut current,
                        current_score_sum,
                        current_count,
                        text,
                        &mut chunk_entities,
                    );
                    continue;
                }

                let (bio, tag) = split_bio(label);

                if tag == "ORG" {
                    finalize_entity(
                        &mut current,
                        current_score_sum,
                        current_count,
                        text,
                        &mut chunk_entities,
                    );
                    continue;
                }

                let off = offsets[t];
                if off.0 == 0 && off.1 == 0 && tokens[t].starts_with('[') {
                    continue;
                }

                let matches_current = current.as_ref().map_or(false, |e| e.label == tag);

                if bio == "B" || !matches_current {
                    let mut start_idx = t;
                    while start_idx > 0 && !is_special(start_idx - 1) {
                        if offsets[start_idx - 1].1 == offsets[start_idx].0 {
                            start_idx -= 1;
                        } else {
                            break;
                        }
                    }

                    finalize_entity(
                        &mut current,
                        current_score_sum,
                        current_count,
                        text,
                        &mut chunk_entities,
                    );

                    current = Some(RawEntity {
                        label: tag.to_string(),
                        start: offsets[start_idx].0,
                        end: off.1,
                        score: confs[t],
                    });
                    current_score_sum = confs[t];
                    current_count = 1;
                } else if let Some(ref mut ent) = current {
                    ent.end = ent.end.max(off.1);
                    current_score_sum += confs[t];
                    current_count += 1;
                }
            }
            finalize_entity(
                &mut current,
                current_score_sum,
                current_count,
                text,
                &mut chunk_entities,
            );
        }

        let merged_entities = merge_adjacent_entities(chunk_entities, text);
        Ok(merged_entities
            .into_iter()
            .map(|e| e.start..e.end)
            .collect())
    }
}

/// Splits a label of the form "B-PER" / "I-PER" into (bio, tag).
/// Also accepts the '_' separator ("B_PER"). If the model does not follow an
/// explicit BIO scheme (flat label, e.g., "PER" alone), each
/// occurrence is treated as the beginning of an entity ("B"): the merging of
/// adjacent words of the same type is still handled by `merge_adjacent_entities`.
fn split_bio(label: &str) -> (&str, &str) {
    for sep in ['-', '_'] {
        if let Some((bio, tag)) = label.split_once(sep) {
            if bio == "B" || bio == "I" {
                return (bio, tag);
            }
        }
    }
    ("B", label)
}

/// Calculates for each token the predicted class (argmax) and its confidence
/// (softmax) from the raw logits tensor returned by ONNX Runtime.
fn decode_predictions(data: &[f32], max_len: usize, num_labels: usize) -> (Vec<usize>, Vec<f32>) {
    let mut preds = vec![0usize; max_len];
    let mut confs = vec![0.0f32; max_len];

    if num_labels == 0 {
        return (preds, confs);
    }

    for t in 0..max_len {
        let start_idx = t * num_labels;
        let end_idx = start_idx + num_labels;
        if end_idx > data.len() {
            break;
        }
        let token_logits = &data[start_idx..end_idx];

        let (argmax, &max_val) = token_logits
            .iter()
            .enumerate()
            .max_by(|(_, a), (_, b)| a.partial_cmp(b).unwrap_or(std::cmp::Ordering::Equal))
            .unwrap();

        let mut sum_exp = 0.0f32;
        let mut exps = vec![0.0f32; num_labels];
        for (l, &logit) in token_logits.iter().enumerate() {
            let e = (logit - max_val).exp();
            exps[l] = e;
            sum_exp += e;
        }

        preds[t] = argmax;
        confs[t] = exps[argmax] / sum_exp;
    }

    (preds, confs)
}

/// Converts the offsets returned by the tokenizer into valid byte offsets
/// for `text`. If an offset already corresponds to a valid byte boundary,
/// it is kept as is; otherwise, it is interpreted as a character count and
/// converted using a character-to-byte mapping table.
fn to_byte_offsets(text: &str, raw_offsets: &[(usize, usize)]) -> Vec<(usize, usize)> {
    let mut char_to_byte: Vec<usize> = text.char_indices().map(|(b, _)| b).collect();
    char_to_byte.push(text.len());

    let convert = |pos: usize| -> usize {
        if pos <= text.len() && text.is_char_boundary(pos) {
            pos
        } else {
            char_to_byte.get(pos).copied().unwrap_or(text.len())
        }
    };

    raw_offsets
        .iter()
        .map(|&(s, e)| {
            let s = convert(s);
            let e = convert(e).max(s);
            (s, e)
        })
        .collect()
}

fn finalize_entity(
    current: &mut Option<RawEntity>,
    score_sum: f32,
    count: usize,
    text: &str,
    out: &mut Vec<RawEntity>,
) {
    if let Some(mut ent) = current.take() {
        normalize_entity_span(text, &mut ent.start, &mut ent.end);
        if ent.end > ent.start {
            ent.score = score_sum / (count as f32).max(1.0);
            out.push(ent);
        }
    }
}

fn normalize_entity_span(text: &str, start: &mut usize, end: &mut usize) {
    if *start >= text.len() || *end <= *start {
        return;
    }
    if *end > text.len() {
        *end = text.len();
    }

    while *start < *end {
        match text[*start..].chars().next() {
            Some(c) if c.is_whitespace() => *start += c.len_utf8(),
            _ => break,
        }
    }
    while *end > *start {
        match text[..*end].chars().next_back() {
            Some(c) if c.is_whitespace() => *end -= c.len_utf8(),
            _ => break,
        }
    }
    while *end > *start {
        match text[..*end].chars().next_back() {
            Some(c) if c.is_ascii_punctuation() => *end -= c.len_utf8(),
            _ => break,
        }
    }
    while *start < *end {
        match text[*start..].chars().next() {
            Some(c) if c.is_ascii_punctuation() => *start += c.len_utf8(),
            _ => break,
        }
    }
}

fn merge_adjacent_entities(entities: Vec<RawEntity>, text: &str) -> Vec<RawEntity> {
    if entities.len() <= 1 {
        return entities;
    }

    let mut merged = Vec::new();
    let mut current = entities[0].clone();

    for next in entities.into_iter().skip(1) {
        let only_spaces = current.label == next.label
            && next.start >= current.end
            && next.start <= text.len()
            && text[current.end..next.start]
                .chars()
                .all(char::is_whitespace);

        if only_spaces {
            current.end = next.end;
            normalize_entity_span(text, &mut current.start, &mut current.end);
            current.score = (current.score + next.score) / 2.0;
            continue;
        }

        merged.push(current);
        current = next;
    }
    merged.push(current);
    merged
}
