// src/pii_regex_detector.rs
use regex::Regex;

#[derive(Debug, Clone)]
pub struct RegexEntity {
    // pub label: String,
    pub start: usize,
    pub end: usize,
    // pub text: String,
    // pub score: f32,
}

pub struct PiiRegexDetector {
    email_regex: Regex,
    phone_regex: Regex,
    date_regex: Regex,
    url_regex: Regex,
    person_full_name_regex: Regex,
}

impl PiiRegexDetector {
    pub fn new() -> Self {
        Self {
            email_regex: Regex::new(r"(?i)\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b").unwrap(),
            phone_regex: Regex::new(r"(?:(?:\+|00)33|0)\s*[1-9](?:[\s.-]*\d+)+\b").unwrap(),
            date_regex: Regex::new(r"\b\d{2}[/-]\d{2}[/-]\d{4}\b").unwrap(),
            url_regex: Regex::new(r"(?i)https?://(www\.)?[-a-zA-Z0-9@:%._\+~#=]{1,256}\.[a-zA-Z0-9()]{1,6}\b([-a-zA-Z0-9()@:%_\+.~#?&//=]*)").unwrap(),
            person_full_name_regex: Regex::new(r"(?m)^(?:#+\s*)?[A-ZÀ-ÖØ-Ý][a-zà-öø-ÿ]+(?:[-'][A-ZÀ-ÖØ-Ý][a-zà-öø-ÿ]+)?[ \t\u00A0\u202F]+(?:[A-ZÀ-ÖØ-Ý]{3,}(?:[-'’][A-ZÀ-ÖØ-Ý]{2,})*|(?:[A-ZÀ-ÖØ-Ý][ \t\u00A0\u202F]+){2,}[A-ZÀ-ÖØ-Ý])\b").unwrap(),
        }
    }

    pub fn analyze_text(&self, text: &str) -> Vec<RegexEntity> {
        let mut entities = Vec::new();

        if text.trim().is_empty() {
            return entities;
        }

        // 1. Detect full names (NOM_PERSONNE) with a regex that matches capitalized first and last names, optionally preceded by hashtags or whitespace.
        for mat in self.person_full_name_regex.find_iter(text) {
            let mut start = mat.start();
            let end = mat.end();

            while start < end {
                if let Some(c) = text[start..].chars().next() {
                    if c == '#' || c.is_whitespace() {
                        start += c.len_utf8();
                    } else {
                        break;
                    }
                } else {
                    break;
                }
            }

            if end <= start {
                continue;
            }

            entities.push(RegexEntity {
                // label: "NOM_PERSONNE".to_string(),
                start,
                end,
                // text: text[start..end].to_string(),
                // score: 1.0,
            });
        }

        let standard_regexes = [
            (&self.email_regex, "EMAIL"),
            (&self.phone_regex, "PHONE"),
            (&self.date_regex, "DATE"),
            (&self.url_regex, "URL"),
        ];

        for (regex, _label) in standard_regexes {
            for mat in regex.find_iter(text) {
                entities.push(RegexEntity {
                    // label: label.to_string(),
                    start: mat.start(),
                    end: mat.end(),
                    // text: mat.as_str().to_string(),
                    // score: 1.0,
                });
            }
        }

        entities.sort_by_key(|e| e.start);
        entities
    }
}
