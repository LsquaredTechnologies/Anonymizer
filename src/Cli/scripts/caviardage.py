# /// script
# requires-python = ">=3.12"
# dependencies = [
#   "PyMuPDF",
# ]
# ///
from __future__ import annotations

import math
from dataclasses import dataclass
from typing import Iterable, List, Dict

import fitz  # PyMuPDF


# ---------- Modèle proche de ton C# ----------

@dataclass
class Font:
    name: str
    size: float


@dataclass
class Glyph:
    index: int
    char: str
    font: Font
    baseline_y: float
    width: float
    x: float
    y: float  # bas du glyph
    seq: int  # séquence logique (approximation du TextSequence)


@dataclass
class Token:
    index: int
    glyphs: List[Glyph]

    def __str__(self) -> str:
        return "".join(g.char for g in self.glyphs)


# ---------- Helpers de nettoyage ----------

def clean_char(ch: str) -> str:
    code = ord(ch)

    # Private Use Area (  etc.)
    if 0xE000 <= code <= 0xF8FF:
        return ""

    # Contrôles (sauf espace, tab, newline)
    if code < 32 and ch not in ("\n", "\r", "\t"):
        return ""

    return ch


def is_whitespace(ch: str) -> bool:
    return ch.isspace()


def is_punctuation(ch: str) -> bool:
    return len(ch) == 1 and (ch in ".,;:!?…'’\"-–—" or ch in "()[]{}")


def is_apostrophe(ch: str) -> bool:
    return ch in ("'", "’")


# ---------- Extraction des glyphes (équivalent ExtractGlyphs) ----------

def extract_glyphs(page: fitz.Page) -> Iterable[Glyph]:
    raw = page.get_text("rawdict")
    glyphs: List[Glyph] = []
    idx = 0
    seq = 0

    for block in raw["blocks"]:
        if "lines" not in block:
            continue
        for line in block["lines"]:
            seq += 1  # nouvelle séquence logique par ligne
            for span in line["spans"]:
                font_name = span.get("font", "")
                font_size = float(span.get("size", 0.0))
                font = Font(font_name, font_size)

                for ch_info in span.get("chars", []):
                    ch_raw = ch_info["c"]
                    ch = clean_char(ch_raw)
                    if not ch:
                        continue

                    x0, y0, x1, y1 = ch_info["bbox"]
                    width = x1 - x0
                    baseline_y = y1  # bas du glyph (approx)

                    glyphs.append(
                        Glyph(
                            index=idx,
                            char=ch,
                            font=font,
                            baseline_y=baseline_y,
                            width=width,
                            x=x0,
                            y=y1,
                            seq=seq,
                        )
                    )
                    idx += 1

    return glyphs


# ---------- Séquences (par ligne) ----------

@dataclass
class Sequence:
    index: int
    glyphs: List[Glyph]


def extract_sequences(page: fitz.Page) -> Iterable[Sequence]:
    glyphs = list(extract_glyphs(page))
    if not glyphs:
        return []

    sequences: List[Sequence] = []
    buffer: List[Glyph] = []
    seq_index = 0
    prev: Glyph | None = None

    for g in glyphs:
        if prev is None:
            buffer.append(g)
            prev = g
            continue

        same_seq = g.seq == prev.seq
        same_baseline = math.isclose(g.baseline_y, prev.baseline_y, abs_tol=0.5)
        same_font = (
            g.font.name == prev.font.name
            and math.isclose(g.font.size, prev.font.size, abs_tol=0.1)
        )

        if same_seq and same_baseline and same_font:
            buffer.append(g)
        else:
            if buffer:
                sequences.append(Sequence(seq_index, buffer.copy()))
                seq_index += 1
                buffer.clear()
            buffer.append(g)

        prev = g

    if buffer:
        sequences.append(Sequence(seq_index, buffer.copy()))

    return sequences


# ---------- Regroupement des glyphes en mots (par séquence/ligne) ----------

def group_glyphs_into_words(glyphs: List[Glyph]) -> List[List[Glyph]]:
    """
    Reproduit la logique C# :
    - même baseline
    - même font
    - gap horizontal < threshold => même mot
    - les espaces séparent les mots
    """
    if not glyphs:
        return []

    words: List[List[Glyph]] = []
    buffer: List[Glyph] = []
    prev: Glyph | None = None

    for g in glyphs:
        ch = g.char

        # Si espace → fin de mot courant
        if is_whitespace(ch):
            if buffer:
                words.append(buffer)
                buffer = []
            prev = None
            continue

        if prev is None:
            buffer.append(g)
        else:
            same_baseline = abs(g.baseline_y - prev.baseline_y) < 0.5
            same_font = (g.font.name == prev.font.name and abs(g.font.size - prev.font.size) < 0.1)
            gap = abs(g.x - (prev.x + prev.width))
            threshold = prev.width * 3.0

            if same_baseline and same_font and gap < threshold:
                buffer.append(g)
            else:
                if buffer:
                    words.append(buffer)
                buffer = [g]

        prev = g

    if buffer:
        words.append(buffer)

    return words


# ---------- Tokens (équivalent ExtractTokens + MergeHyphenatedLineBreaks) ----------

def extract_tokens(page: fitz.Page) -> Dict[int, List[Token]]:
    """
    Retourne un dict : seq (ligne) -> liste de tokens (mots + ponctuation)
    """
    sequences = list(extract_sequences(page))
    tokens_by_seq: Dict[int, List[Token]] = {}
    token_index = 0

    for seq in sequences:
        # Regrouper les glyphes de cette séquence en mots
        word_glyph_lists = group_glyphs_into_words(seq.glyphs)
        tokens: List[Token] = []

        for glyph_list in word_glyph_lists:
            # Chaque groupe de glyphes devient un token "mot"
            tokens.append(Token(token_index, glyph_list))
            token_index += 1

        # On pourrait ici ajouter la ponctuation comme tokens séparés
        # en inspectant les glyphes, mais pour ton CV, les mots suffisent.

        tokens_by_seq[seq.index] = tokens

    return tokens_by_seq


# ---------- Extraction texte linéaire à partir des tokens ----------

def extract_clean_text(pdf_path: str) -> str:
    doc = fitz.open(pdf_path)
    lines_out: List[str] = []

    for page in doc:
        tokens_by_seq = extract_tokens(page)
        if not tokens_by_seq:
            continue

        # seq = ligne logique (on respecte les lignes du PDF)
        for seq_index in sorted(tokens_by_seq.keys()):
            tokens = tokens_by_seq[seq_index]
            if not tokens:
                continue

            # tri horizontal par x
            tokens_sorted = sorted(tokens, key=lambda t: t.glyphs[0].x)
            text = " ".join(str(t) for t in tokens_sorted)
            text = " ".join(text.split())
            if text:
                lines_out.append(text)

    doc.close()
    return "\n".join(lines_out)


if __name__ == "__main__":
    pdf = r"C:/Users/lione/OneDrive/Documents/CV/Lionel/CV-LionelLalande.pdf"
    text = extract_clean_text(pdf)
    print(text)
