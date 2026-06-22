# /// script
# requires-python = ">=3.12"
# dependencies = [
#   "numpy",
#   "onnxruntime",
#   "opencv-python",
#   "PyMuPDF",
# ]
# ///
from __future__ import annotations

import os
import re
import sys
import math
from dataclasses import dataclass
from typing import Iterable, List, Dict

import fitz  # PyMuPDF

# Import du module de détection de visage (doit être présent dans le même dossier)
try:
    from face_detector import FaceDetector
except ImportError:
    FaceDetector = None
    print("Attention: Module 'face_detector' introuvable. La détection de visages sera ignorée.")


# ──────────────────────────────────────────────────────────────────────────────
# 1. CONFIGURATION ET REGEX (Issu de anonymize.py)
# ──────────────────────────────────────────────────────────────────────────────

REGEX_PATTERNS = [
    (r'\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b', "EMAIL"),
    (r'(?:\+33|0033|0)\s*[1-9](?:[\s.\-]?\d{2}){4}', "PHONE"),
    (r'Né[e]?\s+le\s+\d{1,2}\s+\w+\s+\d{4}', "BIRTHDATE"),
    (r'\b\d{1,3}\s+ans\b', "AGE"),
    (r'\b(marié|mariée|célibataire|divorcé|divorcée)\b', "MARITAL"),
    (r'\b\d+\s+enfant[s]?\b', "CHILDREN"),
    (r'\b\d{5}\s+[A-Za-zÀ-ÖØ-öø-ÿ\- ]+\b', "ADDRESS"),
    (r'\b\d{1,4}\s+(rue|avenue|av\.?|boulevard|bd\.?|impasse|chemin|route)\b.+', "ADDRESS"),
]


# ──────────────────────────────────────────────────────────────────────────────
# 2. MODÈLE DE DONNÉES ET EXTRACTION (Issu de caviardage.py / extraction.py)
# ──────────────────────────────────────────────────────────────────────────────

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
    seq: int

@dataclass
class Token:
    index: int
    glyphs: List[Glyph]

    def __str__(self) -> str:
        return "".join(g.char for g in self.glyphs)

@dataclass
class Sequence:
    index: int
    glyphs: List[Glyph]


def clean_char(ch: str) -> str:
    code = ord(ch)
    if 0xE000 <= code <= 0xF8FF:  # Private Use Area
        return ""
    if code < 32 and ch not in ("\n", "\r", "\t"):
        return ""
    return ch


def extract_glyphs(page: fitz.Page) -> Iterable[Glyph]:
    raw = page.get_text("rawdict")
    glyphs: List[Glyph] = []
    idx = 0
    seq = 0

    for block in raw.get("blocks", []):
        if "lines" not in block:
            continue
        for line in block["lines"]:
            seq += 1  # nouvelle séquence logique par ligne
            for span in line.get("spans", []):
                font = Font(span.get("font", ""), float(span.get("size", 0.0)))
                for ch_info in span.get("chars", []):
                    ch = clean_char(ch_info["c"])
                    if not ch:
                        continue
                    x0, y0, x1, y1 = ch_info["bbox"]
                    glyphs.append(
                        Glyph(
                            index=idx, char=ch, font=font,
                            baseline_y=y1, width=x1 - x0,
                            x=x0, y=y1, seq=seq
                        )
                    )
                    idx += 1
    return glyphs


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
        same_font = (g.font.name == prev.font.name and math.isclose(g.font.size, prev.font.size, abs_tol=0.1))

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


def group_glyphs_into_words(glyphs: List[Glyph]) -> List[List[Glyph]]:
    if not glyphs:
        return []
    words: List[List[Glyph]] = []
    buffer: List[Glyph] = []
    prev: Glyph | None = None

    for g in glyphs:
        if g.char.isspace():
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

            if same_baseline and same_font and gap < prev.width * 3.0:
                buffer.append(g)
            else:
                if buffer:
                    words.append(buffer)
                buffer = [g]
        prev = g

    if buffer:
        words.append(buffer)
    return words


def extract_tokens(page: fitz.Page) -> Dict[int, List[Token]]:
    sequences = list(extract_sequences(page))
    tokens_by_seq: Dict[int, List[Token]] = {}
    token_index = 0

    for seq in sequences:
        word_glyph_lists = group_glyphs_into_words(seq.glyphs)
        tokens = [Token(token_index + i, gl) for i, gl in enumerate(word_glyph_lists)]
        token_index += len(tokens)
        tokens_by_seq[seq.index] = tokens
    return tokens_by_seq


def get_page_clean_text(page: fitz.Page) -> str:
    """Reconstruit le texte de la page grâce à la logique robuste des tokens."""
    tokens_by_seq = extract_tokens(page)
    if not tokens_by_seq:
        return ""

    lines_out = []
    for seq_index in sorted(tokens_by_seq.keys()):
        tokens = tokens_by_seq[seq_index]
        if not tokens:
            continue
        # Tri horizontal par X pour garantir le sens de lecture
        tokens_sorted = sorted(tokens, key=lambda t: t.glyphs[0].x)
        text = " ".join(str(t) for t in tokens_sorted)
        text = " ".join(text.split())
        if text:
            lines_out.append(text)
    return "\n".join(lines_out)


# ──────────────────────────────────────────────────────────────────────────────
# 3. RECHERCHE DE PII ET CAVIARDAGE (Issu de anonymize.py)
# ──────────────────────────────────────────────────────────────────────────────

def get_full_name(text: str) -> str | None:
    lines = [l.strip() for l in text.split("\n") if l.strip()]
    if lines:
        first = lines[0]
        if re.search(r'[:;?!_*/\\]', first):
            return None
        if (1 < len(first.split()) <= 4 and not any(c.isdigit() for c in first)
            and not first.isupper() and len(first) > 3):
            return first.strip()
    return None


def extract_full_name_from_document(doc: fitz.Document) -> str | None:
    for page in doc:
        text = get_page_clean_text(page)
        name = get_full_name(text)
        if name:
            return name
    return None


def get_name_variants(full_name: str) -> List[str]:
    if not full_name:
        return []
    name = full_name.strip()
    variants = {name, name.lower(), name.replace(" ", "").lower(),
                " ".join(list(name.replace(" ", ""))).lower()}
    for p in name.split():
        if len(p) > 2:
            variants.update({p, p.lower(), " ".join(list(p.lower()))})
    return list(variants)


def redact_standard_text(page: fitz.Page, text: str, full_name: str | None, page_summary: dict):
    entities = []
    if full_name:
        for variant in get_name_variants(full_name):
            entities.append({"text": variant, "label": "NAME"})

    for pattern, label in REGEX_PATTERNS:
        for m in re.finditer(pattern, text, re.IGNORECASE):
            entities.append({"text": m.group(), "label": label})

    entities_sorted = sorted(entities, key=lambda x: len(x["text"]), reverse=True)
    seen = set()

    for ent in entities_sorted:
        if ent["text"] in seen:
            continue
        rects = page.search_for(ent["text"])
        for rect in rects:
            page.add_redact_annot(
                rect, text="[REDACTED]", fill=(0, 0, 0),
                text_color=(0, 0, 0), fontname="helv", fontsize=8
            )
            page_summary.setdefault(ent["label"], set()).add(ent["text"])
        seen.add(ent["text"])


def redact_urls_robust(page: fitz.Page, full_name: str | None, page_summary: dict):
    blocks = page.get_text("dict").get("blocks", [])
    name_parts = [full_name.lower().replace(" ", "")] + [p.lower() for p in full_name.split() if len(p) > 2] if full_name else []
    name_variants = get_name_variants(full_name) if full_name else []

    for b in blocks:
        for line in b.get("lines", []):
            line_text = "".join(span.get("text", "") for span in line.get("spans", [])).strip()
            line_lower = line_text.lower()
            if re.search(r'https?://|www\.|linkedin\.com/|github\.com/', line_lower):
                rect = fitz.Rect(line["bbox"])
                label = "NETWORK" if "linkedin.com" in line_lower or "github.com" in line_lower else "URL"
                if any(part in line_lower for part in name_parts) or any(v in line_lower for v in name_variants):
                    label = "URL_NAME"
                page.add_redact_annot(
                    rect, text="[REDACTED]", fill=(0, 0, 0),
                    text_color=(0, 0, 0), fontname="helv", fontsize=8
                )
                page_summary.setdefault(label, set()).add(line_text)


def redact_images_with_faces(doc: fitz.Document, page: fitz.Page, detector, page_summary: dict):
    if not detector:
        return
    for img_info in page.get_images(full=True):
        xref = img_info[0]
        try:
            image_bytes = doc.extract_image(xref)["image"]
            if detector.detect_faces(image_bytes):
                for rect in page.get_image_rects(xref):
                    page.add_redact_annot(rect, text="", fill=(0.5, 0.5, 0.5))
                    page_summary.setdefault("FACE_PHOTO", set()).add(f"Image XREF {xref}")
        except Exception:
            continue


def redact_page(doc: fitz.Document, page: fitz.Page, global_summary: dict, detector, full_name: str | None):
    # 1. Supprimer les liens interactifs
    for link in page.get_links():
        page.delete_link(link)

    # 2. Extraire le texte de manière robuste (Logique C# / Caviardage)
    text = get_page_clean_text(page)
    page_summary = {}

    # 3. Appliquer les différentes stratégies de caviardage
    redact_images_with_faces(doc, page, detector, page_summary)
    redact_standard_text(page, text, full_name, page_summary)
    redact_urls_robust(page, full_name, page_summary)

    # 4. Appliquer physiquement les caviardages
    page.apply_redactions(images=fitz.PDF_REDACT_IMAGE_REMOVE)

    for k, v in page_summary.items():
        global_summary.setdefault(k, set()).update(v)


# ──────────────────────────────────────────────────────────────────────────────
# 4. PROCESSUS PRINCIPAL
# ──────────────────────────────────────────────────────────────────────────────

def find_onnx_model(model_dir: str) -> str | None:
    if os.path.isfile(model_dir) and model_dir.endswith(".onnx"):
        return model_dir
    if os.path.isdir(model_dir):
        for f in os.listdir(model_dir):
            if f.endswith(".onnx"):
                return os.path.join(model_dir, f)
    return None


def anonymize(model_dir: str, pdf_file: str):
    model_path = find_onnx_model(os.path.join(model_dir, "face")) if model_dir else None
    detector = FaceDetector(model_path) if FaceDetector and model_path and os.path.exists(model_path) else None

    doc = fitz.open(pdf_file)
    global_summary = {}

    # Extraction du nom complet via la logique d'extraction robuste
    full_name = extract_full_name_from_document(doc) if len(doc) > 0 else None

    for page in doc:
        redact_page(doc, page, global_summary, detector, full_name)

    out = pdf_file.replace(".pdf", ".anon.pdf")
    doc.set_metadata({})  # Nettoyage des métadonnées du document
    doc.save(out, garbage=4, deflate=True, clean=True)
    doc.close()

    # Rapport de fin de traitement
    report = {k: sorted(list(v)) for k, v in global_summary.items()}
    total = sum(len(v) for v in report.values())

    print("━" * 60)
    print(f"Fichier source : {pdf_file}")
    print(f"Fichier généré : {out}")
    print(f"Nom détecté    : {full_name or 'Non détecté'}")
    print(f"Caviardage     : {total} élément(s) masqué(s) dans {len(report)} catégorie(s)")
    print("━" * 60)
    if report:
        for k, v in report.items():
            print(f"  {k:<12} {len(v):>2}  " + ", ".join(v))


if __name__ == "__main__":
    if len(sys.argv) < 3:
        print("Usage: python auto_anonymizer.py <chemin_vers_dossier_modele_onnx> <fichier.pdf>")
        sys.exit(1)

    model_directory = sys.argv[1]
    pdf_target = sys.argv[2]

    if not os.path.exists(pdf_target):
        print(f"Erreur: Le fichier PDF '{pdf_target}' est introuvable.")
        sys.exit(1)

    anonymize(model_directory, pdf_target)
