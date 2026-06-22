# /// script
# requires-python = ">=3.12"
# dependencies = [
#   "numpy",
#   "onnxruntime",
#   "opencv-python",
#   "PyMuPDF",
#   "pdfplumber",
#   "tokenizers",
# ]
# ///
from __future__ import annotations

import os
import re
import sys
import math
import json
from collections import defaultdict
from dataclasses import dataclass, field
from typing import Iterable, List, Dict, Tuple

import numpy as np
import onnxruntime as ort
from tokenizers import Tokenizer
import fitz  # PyMuPDF
import pdfplumber

# Import du module de détection de visage
try:
    from face_detector import FaceDetector
except ImportError:
    FaceDetector = None
    print("Attention: Module 'face_detector' introuvable. La détection de visages sera ignorée.")


# ──────────────────────────────────────────────────────────────────────────────
# 1. CONFIGURATION ET REGEX
# ──────────────────────────────────────────────────────────────────────────────

REGEX_PATTERNS = [
    # Téléphones français — groupes de 2 OU 3 chiffres, pas de saut de ligne
    (r'(?:\+33|0033|0)[ \t]*[1-9](?:[ \t.\-]?\d{2,3}){3,4}', "PHONE"),
    (r'\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b', "EMAIL"),
    (r'Né[e]?\s+le\s+\d{1,2}\s+\w+\s+\d{4}', "BIRTHDATE"),
    (r'\b\d{1,3}\s+ans\b', "AGE"),
    (r'\b(marié|mariée|célibataire|divorcé|divorcée)\b', "MARITAL"),
    (r'\b\d+\s+enfant[s]?\b', "CHILDREN"),
    (r'\b\d{5}\s+[A-Za-zÀ-ÖØ-öø-ÿ\- ]+\b', "ADDRESS"),
    (r'\b\d{1,4}\s+(rue|avenue|av\.?|boulevard|bd\.?|impasse|chemin|route)\b.+', "ADDRESS"),
]


# ──────────────────────────────────────────────────────────────────────────────
# 2. EXTRACTION PDFPLUMBER — mots → lignes → paragraphes avec bounding boxes
#
#  Rôle de pdfplumber :
#    - LIRE et EXTRAIRE tout le contenu textuel du PDF d'entrée
#    - Fournir les bounding boxes précises pour le caviardage
#    - Segmenter le texte en paragraphes par colonne (2 colonnes sur ce CV)
#
#  Rôle de PyMuPDF (fitz) :
#    - CRÉER le PDF de sortie anonymisé (apply_redactions, save)
#    - Poser les annotations de caviardage (add_redact_annot)
# ──────────────────────────────────────────────────────────────────────────────

# Séparateur de colonne : x < COL_SEP = colonne gauche, x >= COL_SEP = droite
COL_SEP = 430.0

# Gap vertical (en pts) entre deux lignes au-delà duquel on crée un nouveau paragraphe
PARA_GAP = 5.0

# Tolérance x pour regrouper les lettres d'un mot espacé (ex: "L i o n e l" → "Lionel")
WORD_X_TOLERANCE = 15


@dataclass
class PWord:
    """Mot avec sa bounding box (coordonnées pdfplumber : origine en haut à gauche)."""
    text: str
    x0: float
    top: float
    x1: float
    bottom: float


@dataclass
class PLine:
    """Ligne reconstituée : mots triés par x0, bbox englobante."""
    text: str
    words: List[PWord]
    x0: float
    top: float
    x1: float
    bottom: float


@dataclass
class PParagraph:
    """Paragraphe : plusieurs lignes consécutives sans gap > PARA_GAP."""
    text: str          # lignes jointes par "\n"
    lines: List[PLine]
    x0: float
    top: float
    x1: float
    bottom: float
    column: str        # "left" | "right" | "full"


def _words_to_lines(words: List[PWord], y_tolerance: float = 2.0) -> List[PLine]:
    """
    Regroupe les mots par ligne en utilisant leur coordonnée `top` arrondie.
    Les mots d'une même ligne ont le même `top` (± y_tolerance).
    """
    if not words:
        return []

    buckets: Dict[int, List[PWord]] = defaultdict(list)
    for w in words:
        key = round(w.top / y_tolerance) * int(y_tolerance)
        buckets[key].append(w)

    lines: List[PLine] = []
    for key in sorted(buckets.keys()):
        lw = sorted(buckets[key], key=lambda w: w.x0)
        line_text = " ".join(w.text for w in lw)
        lines.append(PLine(
            text=line_text, words=lw,
            x0=min(w.x0 for w in lw), top=min(w.top for w in lw),
            x1=max(w.x1 for w in lw), bottom=max(w.bottom for w in lw),
        ))
    return lines


def _lines_to_paragraphs(lines: List[PLine], gap: float, column: str) -> List[PParagraph]:
    """
    Regroupe les lignes en paragraphes : un nouveau paragraphe commence
    dès qu'un gap vertical > `gap` pts sépare deux lignes consécutives.
    """
    if not lines:
        return []

    paras: List[PParagraph] = []
    current: List[PLine] = [lines[0]]

    for line in lines[1:]:
        prev_bottom = current[-1].bottom
        if line.top - prev_bottom > gap:
            paras.append(_make_para(current, column))
            current = []
        current.append(line)

    if current:
        paras.append(_make_para(current, column))

    return paras


def _make_para(lines: List[PLine], column: str) -> PParagraph:
    text = "\n".join(l.text for l in lines)
    return PParagraph(
        text=text, lines=lines,
        x0=min(l.x0 for l in lines), top=min(l.top for l in lines),
        x1=max(l.x1 for l in lines), bottom=max(l.bottom for l in lines),
        column=column,
    )


def extract_paragraphs(pdf_path: str, page_number: int) -> List[PParagraph]:
    """
    Point d'entrée principal pdfplumber.
    Retourne les paragraphes de la page, segmentés par colonne puis par gap vertical.
    Utilise x_tolerance=WORD_X_TOLERANCE pour reconstituer les mots des polices explodées.
    """
    with pdfplumber.open(pdf_path) as pdf:
        page = pdf.pages[page_number]
        raw_words = page.extract_words(
            x_tolerance=WORD_X_TOLERANCE,
            y_tolerance=3,
            keep_blank_chars=False,
        )

    all_words = [
        PWord(text=w["text"].strip(), x0=w["x0"], top=w["top"],
              x1=w["x1"], bottom=w["bottom"])
        for w in raw_words if w["text"].strip()
    ]

    # Séparation en deux colonnes
    left_words  = [w for w in all_words if w.x1 <= COL_SEP]
    right_words = [w for w in all_words if w.x0 >  COL_SEP]

    left_lines  = _words_to_lines(left_words)
    right_lines = _words_to_lines(right_words)

    left_paras  = _lines_to_paragraphs(left_lines,  gap=PARA_GAP, column="left")
    right_paras = _lines_to_paragraphs(right_lines, gap=PARA_GAP, column="right")

    return left_paras + right_paras


# ──────────────────────────────────────────────────────────────────────────────
# 3. MODÈLE DE DONNÉES PyMuPDF (conservé tel quel — sert uniquement au PDF de sortie)
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
    y: float
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
    if 0xE000 <= code <= 0xF8FF:
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
            seq += 1
            for span in line.get("spans", []):
                font = Font(span.get("font", ""), float(span.get("size", 0.0)))
                for ch_info in span.get("chars", []):
                    ch = clean_char(ch_info["c"])
                    if not ch:
                        continue
                    x0, y0, x1, y1 = ch_info["bbox"]
                    glyphs.append(Glyph(
                        index=idx, char=ch, font=font,
                        baseline_y=y1, width=x1 - x0,
                        x=x0, y=y1, seq=seq
                    ))
                    idx += 1
    return glyphs


# ──────────────────────────────────────────────────────────────────────────────
# 4. INTERFACE MODÈLE ONNX TEXT-NER PII
# ──────────────────────────────────────────────────────────────────────────────

def load_labels_from_config(config_path: str) -> List[str]:
    if not os.path.exists(config_path):
        raise FileNotFoundError(f"config.json introuvable : {config_path}")
    with open(config_path, "r", encoding="utf-8") as f:
        cfg = json.load(f)
    if "id2label" in cfg:
        id2label = cfg["id2label"]
        return [id2label[str(i)] for i in sorted(map(int, id2label.keys()))]
    raise RuntimeError("Impossible de trouver 'id2label' dans config.json.")


def load_pii_model(models_root: str):
    pii_dir = os.path.join(models_root, "pii")
    tokenizer_path = os.path.join(pii_dir, "tokenizer.json")
    model_path     = os.path.join(pii_dir, "model.onnx")
    config_path    = os.path.join(pii_dir, "config.json")

    if not (os.path.exists(model_path) and os.path.exists(tokenizer_path) and os.path.exists(config_path)):
        print(f"Information : Modèle textuel PII non trouvé dans {pii_dir}. Pipeline NER désactivé.")
        return None, None, None

    tokenizer = Tokenizer.from_file(tokenizer_path)
    from tokenizers.pre_tokenizers import Whitespace
    tokenizer.pre_tokenizer = Whitespace()

    labels  = load_labels_from_config(config_path)
    session = ort.InferenceSession(model_path, providers=["CPUExecutionProvider"])
    return tokenizer, labels, session


def group_entities(tokens: List[str], predictions: List[str]) -> List[Dict[str, str]]:
    entities = []
    current  = None

    for tok, label in zip(tokens, predictions):
        if label.startswith("B-"):
            if current:
                entities.append(current)
            current = {"type": label[2:], "text": tok}
        elif label.startswith("I-") and current:
            if tok.startswith("##"):
                current["text"] += tok[2:]
            elif tok.startswith(" "):
                current["text"] += tok
            else:
                current["text"] += " " + tok
        else:
            if current:
                entities.append(current)
            current = None

    if current:
        entities.append(current)

    for ent in entities:
        ent["text"] = ent["text"].replace(" ##", "").replace("##", "").strip()
    return entities


def extract_pii_via_onnx(text: str, tokenizer, labels, session) -> List[Dict[str, str]]:
    if not session or not text.strip():
        return []
    encoded       = tokenizer.encode(text)
    input_ids     = np.array([encoded.ids], dtype=np.int64)
    attention_mask = np.ones_like(input_ids, dtype=np.int64)
    outputs       = session.run(None, {"input_ids": input_ids, "attention_mask": attention_mask})
    pred_ids      = outputs[0].argmax(axis=-1)[0]
    pred_labels   = [labels[i] for i in pred_ids]
    return group_entities(encoded.tokens, pred_labels)


# ──────────────────────────────────────────────────────────────────────────────
# 5. CAVIARDAGE
# ──────────────────────────────────────────────────────────────────────────────

def _is_unwanted(val: str) -> bool:
    v = val.lower().strip()
    return v in (".net", "net", "[cls]", "[sep]", "[unk]", "[pad]") or len(val) <= 1


def _find_span_rect_in_line(line: PLine, span_text: str) -> fitz.Rect | None:
    """
    Cherche une séquence de mots contigus dans une ligne pdfplumber.
    Retourne le fitz.Rect englobant, ou None.
    """
    span_tokens = span_text.split()
    n = len(span_tokens)
    m = len(line.words)

    for i in range(m - n + 1):
        window = line.words[i:i + n]
        if all(
            wt.text.strip(".,;:!?\"'()[]").lower() == st.strip(".,;:!?\"'()[]").lower()
            for wt, st in zip(window, span_tokens)
        ):
            return fitz.Rect(window[0].x0, window[0].top,
                             window[-1].x1, window[-1].bottom)
    return None


def _redact_entity(page: fitz.Page, search_text: str, label: str,
                   paragraphs: List[PParagraph], page_summary: dict):
    """
    Caviarde search_text avec une stratégie à deux niveaux :
      1. page.search_for() PyMuPDF (exact, rapide).
      2. Fallback matching mot-à-mot sur les lignes pdfplumber (robuste aux polices
         explodées et aux espaces insécables dans les numéros de téléphone).
    """
    rects = page.search_for(search_text)
    if rects:
        for rect in rects:
            page.add_redact_annot(rect, text="[REDACTED]", fill=(0, 0, 0),
                                  text_color=(0, 0, 0), fontname="helv", fontsize=8)
        page_summary.setdefault(label, set()).add(search_text)
        return

    # Fallback pdfplumber : matching dans chaque ligne
    found = False
    for para in paragraphs:
        for line in para.lines:
            rect = _find_span_rect_in_line(line, search_text)
            if rect:
                page.add_redact_annot(rect, text="[REDACTED]", fill=(0, 0, 0),
                                      text_color=(0, 0, 0), fontname="helv", fontsize=8)
                page_summary.setdefault(label, set()).add(search_text)
                found = True

    if found:
        return

    # Dernier recours : matching token par token sur les lignes (numéros fragmentés)
    span_tokens = search_text.split()
    if len(span_tokens) > 1:
        pattern = r'[ \t]+'.join(re.escape(t) for t in span_tokens)
        for para in paragraphs:
            for line in para.lines:
                if re.search(pattern, line.text, re.IGNORECASE):
                    matched = []
                    remaining = list(span_tokens)
                    for w in line.words:
                        if remaining and w.text.lower() == remaining[0].lower():
                            matched.append(w)
                            remaining.pop(0)
                    if not remaining and matched:
                        rect = fitz.Rect(matched[0].x0, matched[0].top,
                                         matched[-1].x1, matched[-1].bottom)
                        page.add_redact_annot(rect, text="[REDACTED]", fill=(0, 0, 0),
                                              text_color=(0, 0, 0), fontname="helv", fontsize=8)
                        page_summary.setdefault(label, set()).add(search_text)


def redact_standard_text(page: fitz.Page, paragraphs: List[PParagraph],
                         pii_model_components: tuple, page_summary: dict,
                         debug: bool = False):
    """
    Détecte et caviarde les PII textuels.
    NER ONNX et regex opèrent sur CHAQUE PARAGRAPHE extrait par pdfplumber.
    """
    entities: List[Dict[str, str]] = []
    tokenizer, labels, session = pii_model_components

    for para in paragraphs:
        para_text = para.text.strip()
        if not para_text:
            continue

        para_entities: List[Dict[str, str]] = []

        # --- NER ONNX sur ce paragraphe ---
        if session:
            try:
                onnx_pii = extract_pii_via_onnx(para_text, tokenizer, labels, session)
                for ent in onnx_pii:
                    if ent["type"] in ("ORG", "LOC", "MISC"):
                        continue
                    val = ent["text"].strip()
                    if not _is_unwanted(val):
                        para_entities.append({"text": val, "label": ent["type"]})
            except Exception as e:
                print(f"Erreur NER para page {page.number}: {e}", file=sys.stderr)

        # --- Regex sur ce paragraphe ---
        for pattern, label in REGEX_PATTERNS:
            for m in re.finditer(pattern, para_text, re.IGNORECASE):
                val = m.group().strip()
                if not _is_unwanted(val):
                    para_entities.append({"text": val, "label": label})

        if debug and para_entities:
            print(f"\n  [DEBUG para col={para.column} top={para.top:.0f}]")
            print(f"    TEXTE : {repr(para_text[:120])}")
            for e in para_entities:
                print(f"    ENTITÉ [{e['label']}] → {repr(e['text'])}")

        entities.extend(para_entities)

    # Dédoublonnage par longueur décroissante
    entities_sorted = sorted(entities, key=lambda x: len(x["text"]), reverse=True)
    seen: set = set()

    for ent in entities_sorted:
        search_text = ent["text"].strip()
        if not search_text or search_text in seen:
            continue
        _redact_entity(page, search_text, ent["label"], paragraphs, page_summary)
        seen.add(search_text)


def redact_urls_robust(page: fitz.Page, page_summary: dict):
    blocks = page.get_text("dict").get("blocks", [])
    for b in blocks:
        for line in b.get("lines", []):
            line_text = "".join(span.get("text", "") for span in line.get("spans", [])).strip()
            line_lower = line_text.lower()
            if re.search(r'https?://|www\.|linkedin\.com/|github\.com/', line_lower):
                rect  = fitz.Rect(line["bbox"])
                label = "NETWORK" if ("linkedin.com" in line_lower or "github.com" in line_lower) else "URL"
                page.add_redact_annot(rect, text="[REDACTED]", fill=(0, 0, 0),
                                      text_color=(0, 0, 0), fontname="helv", fontsize=8)
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


def debug_paragraphs(pdf_path: str, page_number: int, pii_model_components: tuple):
    """
    Affiche tous les paragraphes extraits par pdfplumber et les entités NER/regex détectées.
    """
    paragraphs = extract_paragraphs(pdf_path, page_number)
    tokenizer, labels, session = pii_model_components

    print("━" * 70)
    print(f"DEBUG PARAGRAPHES — page {page_number} — {len(paragraphs)} paragraphes")
    print("━" * 70)

    for i, para in enumerate(paragraphs):
        para_text = para.text.strip()
        if not para_text:
            continue

        entities: List[Dict[str, str]] = []

        if session:
            try:
                onnx_pii = extract_pii_via_onnx(para_text, tokenizer, labels, session)
                for ent in onnx_pii:
                    if ent["type"] in ("ORG", "LOC", "MISC"):
                        continue
                    val = ent["text"].strip()
                    if not _is_unwanted(val):
                        entities.append({"text": val, "label": ent["type"], "src": "NER"})
            except Exception as e:
                pass

        for pattern, label in REGEX_PATTERNS:
            for m in re.finditer(pattern, para_text, re.IGNORECASE):
                val = m.group().strip()
                if not _is_unwanted(val):
                    entities.append({"text": val, "label": label, "src": "REGEX"})

        ner_flag = " ◀ PII" if entities else ""
        print(f"\n[Para {i:03d} | col={para.column:5s} | top={para.top:.0f}–{para.bottom:.0f}]{ner_flag}")
        for line in para.lines:
            print(f"  │ {line.text[:110]}")
        if entities:
            for e in entities:
                print(f"  └─ [{e['src']:5s}][{e['label']:10s}] {repr(e['text'])}")

    print("━" * 70)


def redact_page(doc: fitz.Document, page: fitz.Page, pdf_path: str,
                global_summary: dict, detector, pii_model_components: tuple,
                debug: bool = False):
    for link in page.get_links():
        page.delete_link(link)

    # pdfplumber : extraction et segmentation (source de vérité pour le texte)
    paragraphs = extract_paragraphs(pdf_path, page.number)

    page_summary = {}

    redact_images_with_faces(doc, page, detector, page_summary)
    redact_standard_text(page, paragraphs, pii_model_components, page_summary, debug=debug)
    redact_urls_robust(page, page_summary)

    # PyMuPDF : brûlage physique (création du PDF de sortie)
    page.apply_redactions(images=fitz.PDF_REDACT_IMAGE_REMOVE)

    for k, v in page_summary.items():
        global_summary.setdefault(k, set()).update(v)


# ──────────────────────────────────────────────────────────────────────────────
# 6. EXECUTION PRINCIPALE
# ──────────────────────────────────────────────────────────────────────────────

def find_onnx_model(model_dir: str) -> str | None:
    if os.path.isfile(model_dir) and model_dir.endswith(".onnx"):
        return model_dir
    if os.path.isdir(model_dir):
        for f in os.listdir(model_dir):
            if f.endswith(".onnx"):
                return os.path.join(model_dir, f)
    return None


def anonymize(model_dir: str, pdf_file: str, debug: bool = False):
    model_path = find_onnx_model(os.path.join(model_dir, "face")) if model_dir else None
    detector   = FaceDetector(model_path) if FaceDetector and model_path and os.path.exists(model_path) else None

    tokenizer, labels, session = (None, None, None)
    if model_dir:
        try:
            tokenizer, labels, session = load_pii_model(model_dir)
            if session:
                print("→ Pipeline ONNX Text-PII (NER) initialisé avec succès.")
        except Exception as e:
            print(f"Attention: Impossible de charger le modèle de PII textuel : {e}", file=sys.stderr)

    pii_model_components = (tokenizer, labels, session)

    if debug:
        debug_paragraphs(pdf_file, 0, pii_model_components)

    doc = fitz.open(pdf_file)
    global_summary = {}

    for page in doc:
        redact_page(doc, page, pdf_file, global_summary, detector, pii_model_components, debug=debug)

    out = pdf_file.replace(".pdf", ".anon.pdf")
    doc.set_metadata({})
    doc.save(out, garbage=4, deflate=True, clean=True)
    doc.close()

    report = {k: sorted(list(v)) for k, v in global_summary.items()}
    total  = sum(len(v) for v in report.values())

    print("━" * 60)
    print(f"Fichier source : {pdf_file}")
    print(f"Fichier généré : {out}")
    print(f"Caviardage     : {total} élément(s) masqué(s) dans {len(report)} catégorie(s)")
    print("━" * 60)
    if report:
        for k, v in report.items():
            print(f"  {k:<15} {len(v):>2}  " + ", ".join(v))


if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser(description="Anonymisation PDF")
    parser.add_argument("model_dir", help="Répertoire racine des modèles ONNX")
    parser.add_argument("pdf_file",  help="Fichier PDF à anonymiser")
    parser.add_argument("--debug",   action="store_true", help="Affiche les paragraphes et entités détectées")
    args = parser.parse_args()

    if not os.path.exists(args.pdf_file):
        print(f"Erreur: Le fichier PDF '{args.pdf_file}' est introuvable.")
        sys.exit(1)

    anonymize(args.model_dir, args.pdf_file, debug=args.debug)
