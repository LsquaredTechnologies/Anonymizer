# /// script
# requires-python = ">=3.12"
# dependencies = [
#   "pdfplumber",
# ]
# ///
"""
Extracteur PDF fidèle à la logique C# (PdfTextExtractor.cs).
Bibliothèque : pdfplumber (coordonnées PDF natives, baseline = y0)

Hiérarchie  : Glyph → Sequence → Token → Sentence → Paragraph → Page → Document
Sortie       : texte linéaire + positions (x, y, w, h) de chaque Token
               + export JSON optionnel (<nom_pdf>_tokens.json)

Correspondances C# → Python
──────────────────────────────────────────────────────
letter.TextSequence          → char['mcid']
letter.StartBaseLine.Y       → char['y0']   (coord PDF, origine bas-gauche)
letter.GlyphRectangle        → x0,y0,x1,y1
letter.Font.IsBold           → 'Bold' in fontname
letter.Font.IsItalic         → 'Italic' in fontname
letter.Width                 → char['width']

Logique clé alignée sur le C# :
  • ExtractSequences : coupe sur changement de font (même baseline) OU gap > 3×width
  • ExtractTokens    : espaces séparent, apostrophes fusionnées, ponctuation = token seul
  • MergeHyphenated  : fusion mot-\n-mot (tiret de césure)
  • ExtractSentences : coupe sur changement de taille de font entre séquences
  • ExtractParagraphs: gap vertical, indentation, ponctuation terminale
"""

from __future__ import annotations

import json
import math
import os
import sys
from dataclasses import dataclass
from typing import Iterable, List, Optional

import pdfplumber


# ──────────────────────────────────────────────────────────────────────────────
# Modèle de données
# ──────────────────────────────────────────────────────────────────────────────

@dataclass
class Font:
    name: str
    size: float
    bold: bool = False
    italic: bool = False


@dataclass
class Rect:
    x: float   # left  (coord PDF : origine bas-gauche)
    y: float   # bottom
    w: float
    h: float

    @property
    def right(self) -> float:
        return self.x + self.w

    @property
    def top(self) -> float:
        return self.y + self.h


@dataclass
class Glyph:
    index: int
    char: str
    font: Font
    baseline_y: float   # y0 du char (bas du glyph, coord PDF)
    width: float
    bounds: Rect
    seq: int            # mcid = TextSequence


@dataclass
class Sequence:
    index: int
    glyphs: List[Glyph]


@dataclass
class Token:
    index: int
    glyphs: List[Glyph]

    def __str__(self) -> str:
        return "".join(g.char for g in self.glyphs)

    @property
    def bounds(self) -> Rect:
        if not self.glyphs:
            return Rect(0, 0, 0, 0)
        xs = [g.bounds.x for g in self.glyphs]
        ys = [g.bounds.y for g in self.glyphs]
        rights = [g.bounds.right for g in self.glyphs]
        tops = [g.bounds.top for g in self.glyphs]
        x = min(xs); y = min(ys)
        return Rect(x, y, max(rights) - x, max(tops) - y)

    @property
    def font_size(self) -> float:
        for g in self.glyphs:
            if not g.char.isspace():
                return g.font.size
        return self.glyphs[0].font.size if self.glyphs else 0.0

    @property
    def first_baseline_y(self) -> float:
        return self.glyphs[0].baseline_y if self.glyphs else 0.0

    @property
    def last_baseline_y(self) -> float:
        return self.glyphs[-1].baseline_y if self.glyphs else 0.0


@dataclass
class Sentence:
    index: int
    tokens: List[Token]

    @property
    def bounds(self) -> Rect:
        if not self.tokens:
            return Rect(0, 0, 0, 0)
        xs = [t.bounds.x for t in self.tokens]
        ys = [t.bounds.y for t in self.tokens]
        rights = [t.bounds.right for t in self.tokens]
        tops = [t.bounds.top for t in self.tokens]
        x = min(xs); y = min(ys)
        return Rect(x, y, max(rights) - x, max(tops) - y)

    @property
    def first_line_baseline_y(self) -> float:
        return self.tokens[0].first_baseline_y if self.tokens else 0.0

    @property
    def last_line_baseline_y(self) -> float:
        return self.tokens[-1].last_baseline_y if self.tokens else 0.0


@dataclass
class Paragraph:
    index: int
    sentences: List[Sentence]


@dataclass
class Page:
    index: int
    paragraphs: List[Paragraph]


@dataclass
class Document:
    pages: List[Page]


# ──────────────────────────────────────────────────────────────────────────────
# Helpers
# ──────────────────────────────────────────────────────────────────────────────

def _clean_char(ch: str) -> str:
    code = ord(ch)
    if 0xE000 <= code <= 0xF8FF:   # Private Use Area
        return ""
    if code < 32 and ch not in ("\n", "\r", "\t"):
        return ""
    return ch


def _parse_font(fontname: str, size: float) -> Font:
    name = fontname.split("+")[-1]          # strip subset prefix BCDEEE+
    bold = "Bold" in name
    italic = "Italic" in name or "Oblique" in name
    return Font(name, round(size, 1), bold, italic)


def _font_eq(a: Font, b: Font) -> bool:
    return (
        abs(a.size - b.size) < 0.01
        and a.bold == b.bold
        and a.italic == b.italic
        and a.name == b.name
    )


def _is_apostrophe(ch: str) -> bool:
    return ch in ("'", "\u2019")


def _is_punctuation(ch: str) -> bool:
    return len(ch) == 1 and (ch in ".,;:!?…''\"-–—" or ch in "()[]{}")


def _is_terminal_punct(ch: str) -> bool:
    return ch in (".", "…", "!", "?")


# ──────────────────────────────────────────────────────────────────────────────
# 1. ExtractGlyphs
# ──────────────────────────────────────────────────────────────────────────────

def extract_glyphs(page: pdfplumber.page.Page) -> List[Glyph]:
    """
    pdfplumber.char fields utilisés :
      text, fontname, size, x0, y0, x1, y1, width, mcid

    Système de coordonnées :
      pdfplumber expose AUSSI les coordonnées PDF natives (y0 = bas, y1 = haut).
      On utilise y0 comme baseline_y (équivalent StartBaseLine.Y du C#).
    """
    glyphs: List[Glyph] = []
    idx = 1

    for char in page.chars:
        ch_raw = char.get("text", "")
        ch = _clean_char(ch_raw)
        if not ch:
            continue

        fontname = char.get("fontname", "")
        size = float(char.get("size", 0.0))
        font = _parse_font(fontname, size)

        x0 = round(float(char["x0"]), 1)
        y0 = round(float(char["y0"]), 1)   # bas du glyph (coord PDF)
        x1 = round(float(char["x1"]), 1)
        y1 = round(float(char["y1"]), 1)   # haut du glyph (coord PDF)
        width = round(x1 - x0, 1)
        bounds = Rect(x0, y0, width, round(y1 - y0, 1))

        # mcid = TextSequence ; fallback sur -1 si absent
        seq = int(char.get("mcid") or -1)

        glyphs.append(Glyph(
            index=idx,
            char=ch,
            font=font,
            baseline_y=y0,
            width=width,
            bounds=bounds,
            seq=seq,
        ))
        idx += 1

    return glyphs


# ──────────────────────────────────────────────────────────────────────────────
# 2. ExtractSequences
# ──────────────────────────────────────────────────────────────────────────────

def extract_sequences(glyphs: List[Glyph]) -> List[Sequence]:
    """
    Coupe en séquences quand :
      - même baseline MAIS font différente (sameBaseline && !sameFont → coupe)
      - gap horizontal > 3× largeur du glyph précédent
    """
    if not glyphs:
        return []

    sequences: List[Sequence] = []
    buffer: List[Glyph] = []
    seq_index = 1
    prev: Optional[Glyph] = None

    def flush():
        nonlocal seq_index
        if buffer:
            sequences.append(Sequence(seq_index, buffer.copy()))
            seq_index += 1
            buffer.clear()

    for g in glyphs:
        if prev is None:
            buffer.append(g)
            prev = g
            continue

        same_baseline = abs(prev.baseline_y - g.baseline_y) < 0.01
        same_font = _font_eq(prev.font, g.font)

        if same_baseline and not same_font:
            flush()
        else:
            gap = abs(g.bounds.x - prev.bounds.right)
            threshold = prev.width * 3.0
            if gap > threshold:
                flush()

        buffer.append(g)
        prev = g

    flush()
    return sequences


# ──────────────────────────────────────────────────────────────────────────────
# 3. ExtractTokens + MergeHyphenatedLineBreaks
# ──────────────────────────────────────────────────────────────────────────────

def extract_tokens_from_sequences(sequences: List[Sequence]) -> List[Token]:
    buffer: List[Glyph] = []
    tokens: List[Token] = []
    token_index = 0

    def flush_buffer():
        nonlocal token_index
        if buffer:
            tokens.append(Token(token_index, buffer.copy()))
            token_index += 1
            buffer.clear()

    for seq in sequences:
        for g in seq.glyphs:
            ch = g.char

            if ch.isspace():
                flush_buffer()
                continue

            if _is_apostrophe(ch):
                if buffer:
                    buffer.append(g)
                    flush_buffer()
                elif tokens:
                    last = tokens[-1]
                    tokens[-1] = Token(last.index, last.glyphs + [g])
                else:
                    tokens.append(Token(token_index, [g]))
                    token_index += 1
                continue

            if _is_punctuation(ch):
                flush_buffer()
                tokens.append(Token(token_index, [g]))
                token_index += 1
                continue

            buffer.append(g)

        flush_buffer()  # fin de séquence

    tokens = _merge_hyphenated_line_breaks(tokens)
    for i, t in enumerate(tokens):
        tokens[i] = Token(i, t.glyphs)

    return tokens


def _is_hyphen_token(token: Token) -> bool:
    if len(token.glyphs) != 1:
        return False
    return token.glyphs[0].char in ("-", "\u2011")


def _has_surrounding_space(p: Token, h: Token, n: Token) -> bool:
    pg = p.glyphs[-1]; hg = h.glyphs[0]; ng = n.glyphs[0]
    avg_w = (pg.width + hg.width + ng.width) / 3.0
    thresh = max(avg_w * 0.6, 0.6)
    return (hg.bounds.x - pg.bounds.right) > thresh or (ng.bounds.x - hg.bounds.right) > thresh


def _merge_hyphenated_line_breaks(tokens: List[Token]) -> List[Token]:
    if len(tokens) < 3:
        return tokens

    result: List[Token] = []
    i = 0
    while i < len(tokens):
        if 0 < i < len(tokens) - 1 and _is_hyphen_token(tokens[i]):
            prev_t, hyph_t, next_t = tokens[i - 1], tokens[i], tokens[i + 1]
            prev_seq = prev_t.glyphs[-1].seq
            hyph_seq = hyph_t.glyphs[-1].seq
            next_seq = next_t.glyphs[0].seq

            # césure de fin de ligne
            if prev_seq == hyph_seq and hyph_seq != next_seq:
                if not _has_surrounding_space(prev_t, hyph_t, next_t):
                    merged = result[-1].glyphs + next_t.glyphs
                    result[-1] = Token(result[-1].index, merged)
                    i += 2
                    continue

            # tiret inline sans espaces sur même baseline
            all_same = (
                abs(prev_t.glyphs[-1].baseline_y - hyph_t.glyphs[0].baseline_y) <= 0.5
                and abs(next_t.glyphs[0].baseline_y - hyph_t.glyphs[0].baseline_y) <= 0.5
            )
            if all_same and not _has_surrounding_space(prev_t, hyph_t, next_t):
                merged = result[-1].glyphs + hyph_t.glyphs + next_t.glyphs
                result[-1] = Token(result[-1].index, merged)
                i += 2
                continue

        result.append(tokens[i])
        i += 1

    return result


# ──────────────────────────────────────────────────────────────────────────────
# 4. ExtractSentences
# ──────────────────────────────────────────────────────────────────────────────

def _should_split_on_font_change(prev_t: Token, curr_t: Token) -> bool:
    if not prev_t.glyphs or not curr_t.glyphs:
        return False
    prev_seq = prev_t.glyphs[1].seq if len(prev_t.glyphs) > 1 else prev_t.glyphs[0].seq
    curr_seq = curr_t.glyphs[1].seq if len(curr_t.glyphs) > 1 else curr_t.glyphs[0].seq
    if prev_seq == curr_seq:
        return False
    return abs(prev_t.font_size - curr_t.font_size) > 0.01


def _should_ignore_sentence(tokens: List[Token]) -> bool:
    has_page = any(str(t).upper() == "PAGE" for t in tokens)
    has_digit = any(any(ch.isdigit() for ch in str(t)) for t in tokens)
    return has_page and has_digit


def extract_sentences(tokens: List[Token]) -> List[Sentence]:
    buffer: List[Token] = []
    sentences: List[Sentence] = []
    sent_index = 0
    prev: Optional[Token] = None

    def flush():
        nonlocal sent_index
        if buffer and not _should_ignore_sentence(buffer):
            sentences.append(Sentence(sent_index, buffer.copy()))
            sent_index += 1
        buffer.clear()

    for t in tokens:
        if prev is not None and _should_split_on_font_change(prev, t):
            flush()
        buffer.append(t)
        if any(_is_terminal_punct(g.char) for g in t.glyphs):
            flush()
        prev = t

    flush()
    return sentences


# ──────────────────────────────────────────────────────────────────────────────
# 5. ExtractParagraphs
# ──────────────────────────────────────────────────────────────────────────────

def _are_same_line(s1: Sentence, s2: Sentence) -> bool:
    return s1.last_line_baseline_y == s2.first_line_baseline_y


def _has_terminal_punct_sentence(s: Sentence) -> bool:
    if not s.tokens or not s.tokens[-1].glyphs:
        return False
    return _is_terminal_punct(s.tokens[-1].glyphs[-1].char)


def _has_large_vertical_gap(s1: Sentence, s2: Sentence) -> bool:
    gap = abs(s1.last_line_baseline_y - s2.first_line_baseline_y)
    ref = s2.tokens[0].font_size if s2.tokens else 12.0
    return gap > ref * 1.25


def _should_start_new_paragraph(prev_s: Sentence, curr_s: Sentence) -> bool:
    if _are_same_line(prev_s, curr_s):
        return False
    if not _has_terminal_punct_sentence(prev_s):
        return True
    if _has_large_vertical_gap(prev_s, curr_s):
        return True
    start_diff = curr_s.tokens[0].bounds.x - prev_s.bounds.x
    first_glyph_w = curr_s.tokens[0].glyphs[0].width if curr_s.tokens[0].glyphs else 1.0
    if start_diff <= first_glyph_w * 1.5:
        return True
    return False


def extract_paragraphs(sentences: List[Sentence]) -> List[Paragraph]:
    if not sentences:
        return []
    buffer: List[Sentence] = []
    paragraphs: List[Paragraph] = []
    para_index = 0
    prev: Optional[Sentence] = None

    for s in sentences:
        if prev is not None and _should_start_new_paragraph(prev, s):
            if buffer:
                paragraphs.append(Paragraph(para_index, buffer.copy()))
                para_index += 1
            buffer.clear()
        buffer.append(s)
        prev = s

    if buffer:
        paragraphs.append(Paragraph(para_index, buffer))

    return paragraphs


# ──────────────────────────────────────────────────────────────────────────────
# 6. API publique
# ──────────────────────────────────────────────────────────────────────────────

def extract_document(pdf_path: str) -> Document:
    pages_out: List[Page] = []
    with pdfplumber.open(pdf_path) as pdf:
        for i, page in enumerate(pdf.pages):
            glyphs = extract_glyphs(page)
            sequences = extract_sequences(glyphs)
            tokens = extract_tokens_from_sequences(sequences)
            sentences = extract_sentences(tokens)
            paragraphs = extract_paragraphs(sentences)
            pages_out.append(Page(i + 1, paragraphs))
    return Document(pages_out)


# ──────────────────────────────────────────────────────────────────────────────
# 7. Formatage de la sortie
# ──────────────────────────────────────────────────────────────────────────────

def format_text_with_positions(document: Document) -> str:
    lines: List[str] = []
    token_records: List[str] = []

    for page in document.pages:
        lines.append(f"═══ Page {page.index} ═══")
        for para in page.paragraphs:
            for sent in para.sentences:
                words = []
                for tok in sent.tokens:
                    text = str(tok)
                    words.append(text)
                    b = tok.bounds
                    font = tok.glyphs[0].font if tok.glyphs else Font("?", 0)
                    token_records.append(
                        f"  p{page.index:02d}.§{para.index:03d}.s{sent.index:03d}.t{tok.index:04d}"
                        f"  {repr(text):<28}"
                        f"  x={b.x:7.1f}  y={b.y:7.1f}  w={b.w:6.1f}  h={b.h:5.1f}"
                        f"  {font.name:<28}  {font.size:5.1f} pt"
                        f"{'  BOLD' if font.bold else ''}{'  ITALIC' if font.italic else ''}"
                    )
                lines.append(" ".join(words))
            lines.append("")  # séparateur entre paragraphes

    col = 120
    lines += [
        "",
        "━" * col,
        "POSITIONS DES TOKENS",
        "━" * col,
        f"  {'réf':<30}  {'texte':<28}  {'x':>7}  {'y':>7}  {'w':>6}  {'h':>5}"
        f"  {'police':<28}  {'pt':>5}",
        "─" * col,
    ] + token_records

    return "\n".join(lines)


def export_tokens_json(document: Document) -> list:
    out = []
    for page in document.pages:
        for para in page.paragraphs:
            for sent in para.sentences:
                for tok in sent.tokens:
                    b = tok.bounds
                    font = tok.glyphs[0].font if tok.glyphs else Font("?", 0)
                    out.append({
                        "page": page.index,
                        "paragraph": para.index,
                        "sentence": sent.index,
                        "token": tok.index,
                        "text": str(tok),
                        "x": b.x,
                        "y": b.y,
                        "w": b.w,
                        "h": b.h,
                        "font_name": font.name,
                        "font_size": font.size,
                        "bold": font.bold,
                        "italic": font.italic,
                    })
    return out


# ──────────────────────────────────────────────────────────────────────────────
# Point d'entrée
# ──────────────────────────────────────────────────────────────────────────────

if __name__ == "__main__":
    pdf = r"C:/Users/lione/OneDrive/Documents/CV/Lionel/CV-LionelLalande.pdf"
    if len(sys.argv) > 1:
        pdf = sys.argv[1]

    doc = extract_document(pdf)

    # Sortie texte + positions (stdout)
    print(format_text_with_positions(doc))

    # Export JSON
    json_path = os.path.splitext(pdf)[0] + "_tokens.json"
    with open(json_path, "w", encoding="utf-8") as f:
        json.dump(export_tokens_json(doc), f, ensure_ascii=False, indent=2)
    print(f"\n→ JSON exporté : {json_path}", file=sys.stderr)
