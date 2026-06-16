# /// script
# requires-python = ">=3.12"
# dependencies = [
#   "numpy",
#   "onnxruntime",
#   "opencv-python",
#   "PyMuPDF",
# ]
# ///
import sys
import re
import fitz
import os
from face_detector import FaceDetector


# -------------------------
#   REGEX PATTERNS
# -------------------------
REGEX_PATTERNS = [
    (r'\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b', "EMAIL"),
    (r'(?<!\d)\+\d{2}(?:[\s.\-]*\d){8,}(?!\d)', "PHONE"),
    (r'(?<!\d)0[1-9](?:[\s.\-]*\d){6,}(?!\d)', "PHONE"),
    (r'\b\d{1,2}\s+(janvier|février|mars|avril|mai|juin|juillet|août|septembre|octobre|novembre|décembre)\s+\d{4}\b', "DATE"),
    (r'\b\d{1,2}[\/\-]\d{1,2}[\/\-]\d{4}\b', "DATE"),
    (r'\b\d{1,3}\s*ans\b', "AGE"),
    (r'\b\d{1,4}\s+rue\s+[A-Za-zÀ-ÖØ-öø-ÿ\- ]+\b', "ADDRESS"),
    (r'\b\d{5} [A-Z][A-Za-zÀ-ÖØ-öø-ÿ\'\-]+(?: [A-Z][A-Za-zÀ-ÖØ-öø-ÿ\'\-]+)*\b', "CITY"),
    (r'\bmarié(e)?\b', "CIVIL_STATUS"),
    (r'\bdivorcé(e)?\b', "CIVIL_STATUS"),
    (r'\bveuf\b|\bveuve\b', "CIVIL_STATUS"),
    (r'\b(seul|célibataire)\b', "CIVIL_STATUS"),
    (r'\b[1-9]\d?\b\s+enfants?\b', "CHILDREN"),
]


# -------------------------
#   TEXT NORMALIZATION
# -------------------------
def normalize_text(text):
    # Supprime les espaces entre lettres : "L i o n e l" -> "Lionel"
    text = re.sub(r'(?<=\w)\s+(?=\w)', '', text)

    # Sépare les mots collés : ARCHITECTELOGICIEL -> ARCHITECTE LOGICIEL
    text = re.sub(r'([a-z])([A-Z])', r'\1 \2', text)
    text = re.sub(r'([A-Z]{2,})([A-Z][a-z])', r'\1 \2', text)

    # Sépare chiffres/lettres : 25rue -> 25 rue
    text = re.sub(r'(\d)([A-Za-z])', r'\1 \2', text)
    text = re.sub(r'([A-Za-z])(\d)', r'\1 \2', text)

    return text


# -------------------------
#   TEXT RECONSTRUCTION
# -------------------------
def extract_reconstructed_text(page):
    blocks = page.get_text("dict")["blocks"]
    out = []

    for b in blocks:
        if "lines" not in b:
            continue

        for line in b["lines"]:
            line_text = ""
            last_x = None

            for span in line["spans"]:
                text = span["text"]
                x0 = span["bbox"][0]

                for c in text:
                    # si saut horizontal → espace
                    if last_x is not None and abs(x0 - last_x) > 1:
                        line_text += " "

                    line_text += c
                    last_x = x0 + 1

            out.append(line_text)

    return "\n".join(out)


# -------------------------
#   NAME DETECTION
# -------------------------
def get_full_name(text):
    lines = [l.strip() for l in text.split("\n") if l.strip()]

    for line in lines[:30]:
        m = re.match(
            r'^([A-ZÉÈÀÂÎÔÙÜÇ][a-zA-Zéèàâêîôûùüç\-]+)\s+([A-ZÉÈÀÂÎÔÙÜÇ][A-ZÉÈÀÂÎÔÙÜÇ\-]+)$',
            line
        )
        if m:
            return line

    return None


# -------------------------
#   REDACTION ENGINE
# -------------------------
def redact_standard_text(page, text, full_name, page_summary):
    entities = []

    # Nom complet + parties
    if full_name:
        entities.append({"text": full_name, "label": "NAME"})
        for part in full_name.split():
            entities.append({"text": part, "label": "NAME"})

    # Regex
    for pattern, label in REGEX_PATTERNS:
        for m in re.finditer(pattern, text, re.IGNORECASE):
            entities.append({"text": m.group(), "label": label})

    # Tri par longueur
    entities_sorted = sorted(entities, key=lambda x: len(x["text"]), reverse=True)
    seen = set()

    page_text = page.get_text("text")
    page_text_lower = page_text.lower()

    for ent in entities_sorted:
        if ent["text"] in seen:
            continue

        rects = []
        for word in ent["text"].split():
            word_lower = word.lower()

            start = 0
            while True:
                idx = page_text_lower.find(word_lower, start)
                if idx == -1:
                    break

                exact = page_text[idx:idx+len(word)]
                rects.extend(page.search_for(exact))

                start = idx + len(word)

        for rect in rects:
            page.add_redact_annot(
                rect, text="[REDACTED]", fill=(0, 0, 0),
                text_color=(0, 0, 0), fontname="helv", fontsize=8
            )
            page_summary.setdefault(ent["label"], set()).add(ent["text"])

        seen.add(ent["text"])


# -------------------------
#   URL REDACTION
# -------------------------
def redact_urls_robust(page, full_name, page_summary):
    blocks = page.get_text("dict")["blocks"]
    name_parts = []

    if full_name:
        name_parts = [full_name.lower().replace(" ", "")] + [
            p.lower() for p in full_name.split() if len(p) > 2
        ]

    for b in blocks:
        if "lines" not in b:
            continue

        for line in b["lines"]:
            line_text = "".join(span["text"] for span in line["spans"]).strip()
            line_lower = line_text.lower()

            is_url = bool(re.search(r'https?://|www\.|linkedin\.com/|github\.com/', line_lower))
            if not is_url:
                continue

            rect = fitz.Rect(line["bbox"])
            label = "URL"

            if "linkedin.com" in line_lower or "github.com" in line_lower:
                label = "NETWORK"

            if name_parts and any(part in line_lower for part in name_parts):
                label = "URL_NAME"

            page.add_redact_annot(
                rect, text="[REDACTED]", fill=(0, 0, 0),
                text_color=(0, 0, 0), fontname="helv", fontsize=8
            )
            page_summary.setdefault(label, set()).add(line_text)


# -------------------------
#   FACE REDACTION
# -------------------------
def redact_images_with_faces(doc, page, detector, page_summary):
    for img_info in page.get_images(full=True):
        xref = img_info[0]

        try:
            base_image = doc.extract_image(xref)
            image_bytes = base_image["image"]
        except Exception:
            continue

        if detector.detect_faces(image_bytes):
            for rect in page.get_image_rects(xref):
                page.add_redact_annot(rect, text="", fill=(0.5, 0.5, 0.5))
                page_summary.setdefault("FACE_PHOTO", set()).add(f"Image XREF {xref}")


# -------------------------
#   PAGE PROCESSING
# -------------------------
def redact_page(doc, page, global_summary, detector, full_name):
    for link in page.get_links():
        page.delete_link(link)

    raw_text = extract_reconstructed_text(page)
    text = normalize_text(raw_text)

    page_summary = {}

    if detector:
        redact_images_with_faces(doc, page, detector, page_summary)

    redact_standard_text(page, text, full_name, page_summary)
    redact_urls_robust(page, full_name, page_summary)

    page.apply_redactions(images=fitz.PDF_REDACT_IMAGE_REMOVE)

    for k, v in page_summary.items():
        global_summary.setdefault(k, set()).update(v)


# -------------------------
#   MAIN
# -------------------------
def find_onnx_model(model_dir):
    if os.path.isfile(model_dir) and model_dir.endswith(".onnx"):
        return model_dir
    if os.path.isdir(model_dir):
        for f in os.listdir(model_dir):
            if f.endswith(".onnx"):
                return os.path.join(model_dir, f)
    return None


def anonymize(model_dir, pdf_file):
    model_path = find_onnx_model(os.path.join(model_dir, "faces"))

    detector = None
    if model_path and os.path.exists(model_path):
        detector = FaceDetector(model_path)
    else:
        print(f"WARNING: No .onnx model found in '{model_dir}'. Face detection disabled.")

    doc = fitz.open(pdf_file)
    global_summary = {}

    raw_text = extract_reconstructed_text(doc[0])
    first_page_text = normalize_text(raw_text)
    full_name = get_full_name(first_page_text)

    for page in doc:
        redact_page(doc, page, global_summary, detector, full_name)

    out = pdf_file.replace(".pdf", ".anon.pdf")

    doc.set_metadata({})
    doc.save(out, garbage=4, deflate=True, clean=True)
    doc.close()

    report = {k: sorted(list(v)) for k, v in global_summary.items()}
    total = sum(len(v) for v in report.values())

    print(f"Output : {out}")
    print(f"Name   : {full_name or 'not detected'}")
    print(f"Redacted: {total} item(s) across {len(report)} categories")
    if report:
        for k, v in report.items():
            print(f"  {k:<12} {len(v):>2}  " + ", ".join(v))


if __name__ == "__main__":
    if len(sys.argv) < 3:
        print("Usage: python anonymizer.py <model_dir> <file.pdf>")
        sys.exit(1)

    model_dir = sys.argv[1]
    pdf_file = sys.argv[2]

    if not os.path.exists(pdf_file):
        print(f"Error: PDF file '{pdf_file}' not found.")
        sys.exit(1)

    anonymize(model_dir, pdf_file)
