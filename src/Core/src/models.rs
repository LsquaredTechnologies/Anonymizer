// src/models.rs
use lopdf::ObjectId;

#[derive(Debug, Clone, PartialEq)]
pub struct BBox {
    pub x: f64,
    pub y: f64,
    pub width: f64,
    pub height: f64,
}

// Precisely locate the source byte of a letter in the content stream, to
// be able to actually erase it when writing (not just paint over it).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct SourceRef {
    pub op_index: usize,           // index in content.operations
    pub array_item: Option<usize>, // Some(i) if the operand is a TJ array, None if Tj/'/"
    pub byte_start: usize,         // offset in the bytes of the relevant PDF string
    pub byte_len: usize,           // number of bytes occupied by the code of this glyph (1 or 2)
}

#[derive(Debug, Clone, PartialEq)]
pub struct Letter {
    pub value: char,
    pub bbox: BBox,
    pub font_size: f64,
    pub baseline_y: f64, // position of the baseline (stable, independent of stems/descenders) — to be used for any grouping/alignment
    pub src: SourceRef,
}

#[derive(Debug, Clone)]
pub struct Word {
    pub text: String,
    pub bbox: BBox,
    pub font_size: f64,
    pub page_id: ObjectId,
    pub letters: Vec<Letter>, // Memorization of letters for light green tracing
    pub baseline_y: f64,
}

#[derive(Debug, Clone)]
pub struct Line {
    pub words: Vec<Word>,
    pub bbox: BBox,
    pub page_id: ObjectId,
    pub baseline_y: f64,
}

#[derive(Debug, Clone)]
pub struct Block {
    pub lines: Vec<Line>,
    pub bbox: BBox,
    pub page_id: ObjectId,
    pub baseline_y: f64, // baseline of the last added line, used to judge line spacing
}

#[derive(Debug, Clone)]
pub struct Paragraph {
    pub text: String,
    pub blocks: Vec<Block>,
    pub is_heading: bool,
}
