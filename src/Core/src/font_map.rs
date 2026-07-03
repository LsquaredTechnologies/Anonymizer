// src/font_map.rs
// Decode character codes from a PDF stream (Tj/TJ) into real Unicode characters,
// relying on the cmap table of the actually embedded TrueType font.
// Necessary because many PDF generators (browsers, LibreOffice, etc.)
// subset fonts with an Identity-H encoding: the stream codes are arbitrary glyph
// indices (GID), NOT ASCII/Unicode codes. Interpreting them as raw UTF-8 produces
// random text and silently drops any glyph whose code represents an invalid UTF-8
// byte (common: accents, ligatures, space...).
use lopdf::{Dictionary, Document, Object};
use std::collections::HashMap;

pub struct FontDecoder {
    pub bytes_per_code: usize, // 1 for simple font, 2 for Type0/Identity-H
    pub code_to_char: HashMap<u32, char>,
    pub code_to_width: HashMap<u32, f64>, // font_size ratio (horizontal advance, varies per glyph: legitimate)
    pub default_width: f64,
    // Height/offset: constants on ALL characters in the font (global ascender/descender),
    // not the actual bounding box of each glyph. A comma, an "x", and an "É" should
    // produce boxes of the same height aligned on the same line — like
    // pdftotext/pdfplumber — rather than the actual visual bounding box of each
    // glyphe qui varie par nature (jambages, hampes, ponctuation basse...).
    pub height_ratio: f64,
    pub y_offset_ratio: f64,
}

pub struct GlyphInfo {
    pub c: char,
    pub width: f64, // font_size ratio
}

impl FontDecoder {
    /// Returns (glyph, offset_in_bytes) for each resolved code.
    /// Important: a code absent from code_to_char is silently skipped, so
    /// the offset CANNOT be deduced afterwards by simple position in the
    /// result — we return it explicitly here, calculated during the only pass
    /// that truly knows the position of each code in `bytes`.
    pub fn decode(&self, bytes: &[u8]) -> Vec<(GlyphInfo, usize)> {
        let mut out = Vec::new();
        let mut i = 0;
        while i + self.bytes_per_code <= bytes.len() {
            let code = if self.bytes_per_code == 2 {
                ((bytes[i] as u32) << 8) | bytes[i + 1] as u32
            } else {
                bytes[i] as u32
            };
            if let Some(&c) = self.code_to_char.get(&code) {
                let w = self
                    .code_to_width
                    .get(&code)
                    .copied()
                    .unwrap_or(self.default_width);
                out.push((GlyphInfo { c, width: w }, i));
            }
            // If the code is absent from the mapping, we silently skip THIS glyph
            // (better than a wrong character), rather than failing the entire run.
            i += self.bytes_per_code;
        }
        out
    }
}

/// Builds a decoder for each font referenced by the page.
pub fn build_font_decoders(doc: &Document, page_id: (u32, u16)) -> HashMap<Vec<u8>, FontDecoder> {
    let mut decoders = HashMap::new();
    let fonts = doc.get_page_fonts(page_id);

    for (name, font_dict) in fonts {
        if let Some(decoder) = build_one_decoder(doc, font_dict) {
            decoders.insert(name, decoder);
        }
    }
    decoders
}

fn build_one_decoder(doc: &Document, font_dict: &Dictionary) -> Option<FontDecoder> {
    let subtype = font_dict.get(b"Subtype").ok()?.as_name_str().ok()?;

    if subtype == "Type0" {
        // Composite font (CID), almost always Identity-H => 2 bytes per glyph.
        let descendants = font_dict.get(b"DescendantFonts").ok()?.as_array().ok()?;
        let desc_font_dict = resolve_dict(doc, descendants.first()?)?;
        let font_descriptor = resolve_dict(doc, desc_font_dict.get(b"FontDescriptor").ok()?)?;

        // The embedded font gives us the actual widths (hmtx table), whether or not we have
        // a ToUnicode CMap for the characters. We parse it in all cases.
        let font_file = font_descriptor.get(b"FontFile2").ok()?;
        let stream = doc
            .get_object(font_file.as_reference().ok()?)
            .and_then(Object::as_stream)
            .ok()?;
        let data = stream.decompressed_content().ok()?;
        let face = ttf_parser::Face::parse(&data, 0).ok()?;
        let units_per_em = face.units_per_em() as f64;

        let width_for_gid = |gid: u16| -> Option<f64> {
            face.glyph_hor_advance(ttf_parser::GlyphId(gid))
                .map(|adv| adv as f64 / units_per_em)
        };

        // 1) Priority to the ToUnicode CMap if it exists for the characters (standard mechanism,
        //    the most reliable), but widths are always taken from the font.
        if let Ok(tounicode_ref) = font_dict.get(b"ToUnicode") {
            if let Ok(stream) = doc
                .get_object(tounicode_ref.as_reference().ok()?)
                .and_then(Object::as_stream)
            {
                if let Ok(content) = stream.decompressed_content() {
                    let map = parse_tounicode_cmap(&content);
                    if !map.is_empty() {
                        let mut code_to_width = HashMap::new();
                        for &code in map.keys() {
                            // CIDToGIDMap Identity implicit: CID == GID for these subsets.
                            if let Some(w) = width_for_gid(code as u16) {
                                code_to_width.insert(code, w);
                            }
                        }
                        let (height_ratio, y_offset_ratio) =
                            ink_metrics(&face, map.keys().copied(), units_per_em);
                        return Some(FontDecoder {
                            bytes_per_code: 2,
                            code_to_char: map,
                            code_to_width,
                            default_width: 0.5,
                            height_ratio,
                            y_offset_ratio,
                        });
                    }
                }
            }
        }

        // 2) Otherwise, reconstruction from the cmap table of the embedded TrueType font
        //    (CIDToGIDMap Identity implicit: CID == GID for these subsets).
        let mut gid_to_char = HashMap::new();
        let mut code_to_width = HashMap::new();
        // We test all reasonable Unicode code points and see which
        // glyph the font associates with them, then we invert (gid -> char).
        for cp in 0x20u32..=0x2FFFu32 {
            if let Some(ch) = char::from_u32(cp) {
                if let Some(gid) = face.glyph_index(ch) {
                    gid_to_char.entry(gid.0 as u32).or_insert(ch);
                    if let Some(w) = width_for_gid(gid.0) {
                        code_to_width.entry(gid.0 as u32).or_insert(w);
                    }
                }
            }
        }
        let (height_ratio, y_offset_ratio) =
            ink_metrics(&face, gid_to_char.keys().copied(), units_per_em);
        return Some(FontDecoder {
            bytes_per_code: 2,
            code_to_char: gid_to_char,
            code_to_width,
            default_width: 0.5,
            height_ratio,
            y_offset_ratio,
        });
    } else {
        // Simple font (1 byte per character). Default WinAnsiEncoding:
        // essentially identical to Latin-1 in the useful range.
        let mut code_to_char = HashMap::new();
        for b in 0x20u32..=0xFFu32 {
            if let Some(c) = char::from_u32(b) {
                code_to_char.insert(b, c);
            }
        }

        // Actual widths via the /Widths array + /FirstChar from the font dictionary
        // (expressed in 1/1000 of an em in the PDF, as for standard fonts).
        let mut code_to_width = HashMap::new();
        if let (Ok(first_char), Ok(widths)) = (
            font_dict.get(b"FirstChar").and_then(Object::as_i64),
            font_dict.get(b"Widths").and_then(Object::as_array),
        ) {
            for (i, w) in widths.iter().enumerate() {
                if let Ok(w) = w.as_float() {
                    code_to_width.insert(first_char as u32 + i as u32, w as f64 / 1000.0);
                }
            }
        }
        // No embedded font used here for the height: reasonable default values
        // (simple font = mostly decorative/icon glyphs in this document, not critical).
        return Some(FontDecoder {
            bytes_per_code: 1,
            code_to_char,
            code_to_width,
            default_width: 0.5,
            height_ratio: 0.9,
            y_offset_ratio: -0.2,
        });
    }
}

/// Uniform height/offset for the entire font, calibrated on the ink actually
/// used by this subset (max of the top, min of the bottom among the present glyphs) —
/// not on the declarative ascender/descender metrics of the font (hhea table),
/// often inflated for line spacing and therefore much too high.
fn ink_metrics(
    face: &ttf_parser::Face,
    gids: impl Iterator<Item = u32>,
    units_per_em: f64,
) -> (f64, f64) {
    let mut min_y = i16::MAX;
    let mut max_y = i16::MIN;
    let mut found = false;
    for gid in gids {
        if let Some(r) = face.glyph_bounding_box(ttf_parser::GlyphId(gid as u16)) {
            min_y = min_y.min(r.y_min);
            max_y = max_y.max(r.y_max);
            found = true;
        }
    }
    if !found {
        return (0.7, 0.0);
    }
    (
        (max_y - min_y) as f64 / units_per_em,
        min_y as f64 / units_per_em,
    )
}

fn resolve_dict<'a>(doc: &'a Document, obj: &'a Object) -> Option<&'a Dictionary> {
    match obj {
        Object::Reference(id) => doc.get_dictionary(*id).ok(),
        Object::Dictionary(d) => Some(d),
        _ => None,
    }
}

/// Minimal parser for a CMap ToUnicode (bfchar / bfrange sections in hexadecimal).
fn parse_tounicode_cmap(content: &[u8]) -> HashMap<u32, char> {
    let text = String::from_utf8_lossy(content);
    let mut map = HashMap::new();

    for block in text.split("beginbfchar").skip(1) {
        let block = block.split("endbfchar").next().unwrap_or("");
        for line in block.lines() {
            let hexes: Vec<&str> = line
                .split(|c| c == '<' || c == '>')
                .filter(|s| !s.trim().is_empty())
                .collect();
            if hexes.len() >= 2 {
                if let (Ok(src), Ok(dst)) = (
                    u32::from_str_radix(hexes[0], 16),
                    u32::from_str_radix(&hexes[1][..hexes[1].len().min(4)], 16),
                ) {
                    if let Some(c) = char::from_u32(dst) {
                        map.insert(src, c);
                    }
                }
            }
        }
    }
    for block in text.split("beginbfrange").skip(1) {
        let block = block.split("endbfrange").next().unwrap_or("");
        for line in block.lines() {
            let hexes: Vec<&str> = line
                .split(|c| c == '<' || c == '>')
                .filter(|s| !s.trim().is_empty())
                .collect();
            if hexes.len() >= 3 {
                if let (Ok(lo), Ok(hi), Ok(dst)) = (
                    u32::from_str_radix(hexes[0], 16),
                    u32::from_str_radix(hexes[1], 16),
                    u32::from_str_radix(&hexes[2][..hexes[2].len().min(4)], 16),
                ) {
                    for (offset, code) in (lo..=hi).enumerate() {
                        if let Some(c) = char::from_u32(dst + offset as u32) {
                            map.insert(code, c);
                        }
                    }
                }
            }
        }
    }
    map
}
