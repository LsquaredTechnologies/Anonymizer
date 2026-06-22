# /// script
# requires-python = ">=3.12"
# dependencies = [
#   "numpy",
#   "onnxruntime",
#   "opencv-python",
#   "PyMuPDF",
#   "tokenizers",
# ]
# ///
from __future__ import annotations

import os
import re
import sys
import math
import json
from dataclasses import dataclass
from typing import Iterable, List, Dict

import numpy as np
import onnxruntime as ort
from tokenizers import Tokenizer
import fitz  # PyMuPDF

# Import du module de détection de visage (doit être présent dans le même dossier)
try:
    from face_detector import FaceDetector
except ImportError:
    FaceDetector = None
    print("Attention: Module 'face_detector' introuvable. La détection de visages sera ignorée.")


# ──────────────────────────────────────────────────────────────────────────────
# 1. CONFIGURATION ET REGEX
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
# 2. MODÈLE DE DONNÉES ET EXTRACTION GÉOMÉTRIQUE
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
    tokens_by_seq = extract_tokens(page)
    if not tokens_by_seq:
        return ""

    lines_out = []
    for seq_index in sorted(tokens_by_seq.keys()):
        tokens = tokens_by_seq[seq_index]
        if not tokens:
            continue
        tokens_sorted = sorted(tokens, key=lambda t: t.glyphs[0].x)
        text = " ".join(str(t) for t in tokens_sorted)
        text = " ".join(text.split())
        if text:
            lines_out.append(text)
    return "\n".join(lines_out)


# ──────────────────────────────────────────────────────────────────────────────
# 3. INTERFACE MODÈLE ONNX TEXT-NER PII
# ──────────────────────────────────────────────────────────────────────────────

def load_labels_from_config(config_path: str) -> List[str]:
    if not os.path.exists(config_path):
        raise FileNotFoundError(f"config.json introuvable : {config_path}")

    with open(config_path, "r", encoding="utf-8") as f:
        cfg = json.load(f)

    if "id2label" in cfg:
        id2label = cfg["id2label"]
        labels = [id2label[str(i)] for i in sorted(map(int, id2label.keys()))]
        return labels

    raise RuntimeError("Impossible de trouver 'id2label' dans config.json.")


def load_pii_model(models_root: str):
    pii_dir = os.path.join(models_root, "pii")
    tokenizer_path = os.path.join(pii_dir, "tokenizer.json")
    model_path = os.path.join(pii_dir, "model.onnx")
    config_path = os.path.join(pii_dir, "config.json")

    if not (os.path.exists(model_path) and os.path.exists(tokenizer_path) and os.path.exists(config_path)):
        print(f"Information : Modèle textuel PII non trouvé dans {pii_dir}. Pipeline NER désactivé.")
        return None, None, None

    tokenizer = Tokenizer.from_file(tokenizer_path)
    from tokenizers.pre_tokenizers import Whitespace
    tokenizer.pre_tokenizer = Whitespace()

    labels = load_labels_from_config(config_path)
    session = ort.InferenceSession(model_path, providers=["CPUExecutionProvider"])

    return tokenizer, labels, session


def group_entities(tokens: List[str], predictions: List[str]) -> List[Dict[str, str]]:
    entities = []
    current = None

    for tok, label in zip(tokens, predictions):
        if label.startswith("B-"):
            if current:
                entities.append(current)
            current = {"type": label[2:], "text": tok}
        elif label.startswith("I-") and current:
            if tok.startswith("##"):
                current["text"] += tok[2:]
            elif tok.startswith(" "):
                current["text"] += tok.replace(" ", " ")
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

    encoded = tokenizer.encode(text)
    input_ids = np.array([encoded.ids], dtype=np.int64)
    attention_mask = np.ones_like(input_ids, dtype=np.int64)

    ort_inputs = {
        "input_ids": input_ids,
        "attention_mask": attention_mask
    }

    outputs = session.run(None, ort_inputs)
    logits = outputs[0]

    pred_ids = logits.argmax(axis=-1)[0]
    pred_labels = [labels[i] for i in pred_ids]

    return group_entities(encoded.tokens, pred_labels)


# ──────────────────────────────────────────────────────────────────────────────
# 4. RECHERCHE DE PII ET CAVIARDAGE SÉCURISÉ
# ──────────────────────────────────────────────────────────────────────────────

def redact_standard_text(page: fitz.Page, text: str, pii_model_components: tuple, page_summary: dict):
    entities = []

    # 1. Stratégie IA (ONNX Text-PII NER) - Scanne tout le document sans a priori géographique
    tokenizer, labels, session = pii_model_components
    if session:
        try:
            onnx_pii = extract_pii_via_onnx(text, tokenizer, labels, session)
            for ent in onnx_pii:
                # Exclusion explicite des étiquettes non désirées ou erronées
                if ent["type"] in ("ORG", "LOC", "MISC"):
                    continue

                val = ent["text"].strip()

                # Ignorer ".net" ou "net" isolés s'ils sont faussement prédits comme PII
                if val.lower() in (".net", "net"):
                    continue

                if len(val) > 1 and val not in ("[CLS]", "[SEP]", "[UNK]", "[PAD]"):
                    entities.append({"text": val, "label": ent["type"]})
        except Exception as e:
            print(f"Erreur d'inférence ONNX PII sur la page {page.number}: {e}", file=sys.stderr)

    # 2. Stratégie Déterministe (Expressions régulières classiques)
    for pattern, label in REGEX_PATTERNS:
        for m in re.finditer(pattern, text, re.IGNORECASE):
            val = m.group().strip()

            # Sécurité supplémentaire contre le caviardage de ".net" ou ".Net" seul comme e-mail
            if val.lower() in (".net", "net"):
                continue

            entities.append({"text": val, "label": label})

    # Tri par longueur décroissante pour éviter le masquage partiel de sous-mots
    entities_sorted = sorted(entities, key=lambda x: len(x["text"]), reverse=True)
    seen = set()

    for ent in entities_sorted:
        search_text = ent["text"].strip()
        if not search_text or search_text in seen:
            continue

        rects = page.search_for(search_text)
        for rect in rects:
            page.add_redact_annot(
                rect, text="[REDACTED]", fill=(0, 0, 0),
                text_color=(0, 0, 0), fontname="helv", fontsize=8
            )
            page_summary.setdefault(ent["label"], set()).add(search_text)
        seen.add(search_text)


def redact_urls_robust(page: fitz.Page, page_summary: dict):
    blocks = page.get_text("dict").get("blocks", [])
    for b in blocks:
        for line in b.get("lines", []):
            line_text = "".join(span.get("text", "") for span in line.get("spans", [])).strip()
            line_lower = line_text.lower()

            # Détection des réseaux et urls standard
            if re.search(r'https?://|www\.|linkedin\.com/|github\.com/', line_lower):
                rect = fitz.Rect(line["bbox"])
                label = "NETWORK" if "linkedin.com" in line_lower or "github.com" in line_lower else "URL"
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


def redact_page(doc: fitz.Document, page: fitz.Page, global_summary: dict, detector, pii_model_components: tuple):
    # Nettoyage des hyperliens natifs du PDF
    for link in page.get_links():
        page.delete_link(link)

    text = get_page_clean_text(page)
    page_summary = {}

    # Exécution des processus de masquage
    redact_images_with_faces(doc, page, detector, page_summary)
    redact_standard_text(page, text, pii_model_components, page_summary)
    redact_urls_robust(page, page_summary)

    # Brûlage physique des zones de texte ciblées
    page.apply_redactions(images=fitz.PDF_REDACT_IMAGE_REMOVE)

    for k, v in page_summary.items():
        global_summary.setdefault(k, set()).update(v)


# ──────────────────────────────────────────────────────────────────────────────
# 5. EXECUTION PRINCIPALE
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
    # Chargement du modèle de détection de visages
    model_path = find_onnx_model(os.path.join(model_dir, "face")) if model_dir else None
    detector = FaceDetector(model_path) if FaceDetector and model_path and os.path.exists(model_path) else None

    # Chargement unique du modèle ONNX de PII Textuel (NER)
    tokenizer, labels, session = (None, None, None)
    if model_dir:
        try:
            tokenizer, labels, session = load_pii_model(model_dir)
            if session:
                print("→ Pipeline ONNX Text-PII (NER) initialisé avec succès.")
        except Exception as e:
            print(f"Attention: Impossible de charger le modèle de PII textuel : {e}", file=sys.stderr)

    pii_model_components = (tokenizer, labels, session)

    doc = fitz.open(pdf_file)
    global_summary = {}

    # Parcours global et traitement aveugle de la position géographique des entités
    for page in doc:
        redact_page(doc, page, global_summary, detector, pii_model_components)

    out = pdf_file.replace(".pdf", ".anon.pdf")
    doc.set_metadata({})
    doc.save(out, garbage=4, deflate=True, clean=True)
    doc.close()

    # Consolidé du rapport final
    report = {k: sorted(list(v)) for k, v in global_summary.items()}
    total = sum(len(v) for v in report.values())

    print("━" * 60)
    print(f"Fichier source : {pdf_file}")
    print(f"Fichier généré : {out}")
    print(f"Caviardage     : {total} élément(s) masqué(s) dans {len(report)} catégorie(s)")
    print("━" * 60)
    if report:
        for k, v in report.items():
            print(f"  {k:<15} {len(v):>2}  " + ", ".join(v))


if __name__ == "__main__":
    if len(sys.argv) < 3:
        print("Usage: python anon.py <chemin_vers_racine_modeles_onnx> <fichier.pdf>")
        sys.exit(1)

    model_directory = sys.argv[1]
    pdf_target = sys.argv[2]

    if not os.path.exists(pdf_target):
        print(f"Erreur: Le fichier PDF '{pdf_target}' est introuvable.")
        sys.exit(1)

    anonymize(model_directory, pdf_target)
